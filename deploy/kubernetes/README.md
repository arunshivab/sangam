# Sangam on Kubernetes, highly available (R7, PR-33, CAP-106)

This folder runs Sangam on Kubernetes with every host replicated. The default deployment is still the single VM in
`deploy/production`, and that stays the pilot's. This is the path to take when one VM is no longer enough. It was
proven on a local three-node cluster ([Prove it locally](#prove-it-locally)), not yet on a production cluster.

```
deploy/kubernetes/
  base/              the deployment: namespace, settings, four hosts (2 replicas each), migrator job, ingress, policies
  overlays/kind/     the same, on a local kind cluster, with a test database, an Anjal stand-in and Traefik
```

## What makes Sangam safe to replicate

| Concern | How it is handled |
|---|---|
| Sessions and sign-in state | Kept in PostgreSQL (`user_sessions`) and in cookies protected by the shared Data Protection key ring, which is in the database and encrypted with the key-ring certificate. Any replica serves any request. |
| Tokens | OpenIddict stores authorizations and tokens in PostgreSQL. Every identity replica signs with the same certificates (secret `sangam-identity`). |
| Background work: SCIM, webhooks, back-channel logout, membership expiry, account purge, alerts, audit archive, SIEM | Every replica runs the workers. Each round takes a PostgreSQL advisory lock (`ClusterLock`, R7) and is skipped where another replica holds it, so nothing is delivered, purged or alerted twice. If a replica dies, its lock goes with its connection. |
| Lockout of an address with no account | Counted in PostgreSQL (`unknown_address_attempts`, R7), like a real account's lockout. If each replica counted on its own, the lockout would come after more tries for an unknown address than for a real account, and that difference would itself reveal which addresses have accounts. |
| Blazor Server (portal, consoles) | A browser's circuit lives in one pod. The ingress pins each browser to a pod with a cookie (`sangam_affinity`). If that pod goes, the browser reconnects to another pod and the person stays signed in, because the authentication cookie is valid on every replica. |
| Rate limits | Kept per replica, in memory, so the effective limit is the configured limit × replicas. The account lockout is shared, because it is in the database. |
| Monitoring | Replicas of one host report under the host's name: counters add up; gauges show the latest replica. |
| Database | Not part of this folder. Use a managed PostgreSQL 16+ with a standby, or an operator such as CloudNativePG with synchronous replication. Sangam needs one read-write endpoint. |

## Install

Prerequisites:

- A cluster with nodes in at least two failure domains.
- An ingress controller with WebSockets and cookie affinity: Traefik as written; for another controller, replace the
  `traefik.ingress.kubernetes.io/*` annotations.
- cert-manager or another source for the TLS secret `sangam-tls`.
- A ReadWriteMany storage class for the audit archive.
- PostgreSQL as above.

1. **Images.** Build and push them from the repository root, with the same commands as
   `deploy/production/README.md`, tagged `1.0.0-rc.2`: `sangam/identity`, `sangam/portal`, `sangam/admin`,
   `sangam/partner`, `sangam/migrator`. Point the image names at your registry with a kustomize `images:` entry.
2. **Secrets.** Each file of `deploy/production/secrets` becomes a key of a Kubernetes secret. The hosts read them
   from `/run/secrets`, exactly as on the VM:
   - `sangam-shared`: `ConnectionStrings__Sangam` (application role), `Sangam__Anjal__ApiKey`,
     `keyring_current.pfx`, `Sangam__DataProtection__Certificates__0__Password`.
   - `sangam-identity`:
     - `signing_current.pfx`, `encryption_current.pfx`, `Sangam__Certificates__Signing__0__Password`,
       `Sangam__Certificates__Encryption__0__Password`;
     - `audit_archive.crt`;
     - `Sangam__Clients__portal__Secret`, `Sangam__Clients__admin__Secret`, `Sangam__Clients__partner__Secret`.
   - `sangam-portal`: `Sangam__Portal__ClientSecret`. `sangam-admin`: `Sangam__Admin__ClientSecret`.
     `sangam-partner`: `Sangam__Partner__ClientSecret`.
   - `sangam-migrator`: `CONNECTION` (the schema owner's connection string).

   `overlays/kind/make-secrets.sh` shows every command.
3. **Settings.** Copy `base/config.yaml` into your overlay and fill in the `SET-…` values, the same ones as
   `deploy/production/docker-compose.yml`. Set `Sangam__ForwardedHeaders__KnownNetworks` to your pod network.
4. **Migrate, then roll out.** Apply the overlay. The `sangam-migrate-1-0-0-rc-2` job runs the migrations. Each release
   gets a new job name, so the job runs before the new hosts take traffic:

   ```sh
   kubectl apply -k overlays/<yours>
   kubectl -n sangam wait --for=condition=complete job/sangam-migrate-1-0-0-rc-2 --timeout=10m
   kubectl -n sangam rollout status deploy/identity deploy/portal deploy/admin deploy/partner
   ```

   On a first install the hosts restart until the job has created the schema. This is expected; after that they
   start normally.

5. **HSTS.** The hosts send HSTS themselves (30 days). For the year-long, sub-domain HSTS that Caddy sends on the VM,
   add it at the ingress, for example with a Traefik `headers` middleware (`stsSeconds: 31536000`,
   `stsIncludeSubdomains: true`).

Each Deployment:

- runs 2 replicas, spread across nodes, with a PodDisruptionBudget of `minAvailable: 1`;
- updates with `maxUnavailable: 0`;
- has startup, liveness (`/health/live`) and readiness (`/health/ready`, which includes the database) probes;
- runs as a non-root user with no privilege escalation, a read-only root file system and all capabilities dropped,
  and meets the namespace's Pod Security Standard *restricted*.

The NetworkPolicy admits traffic only from the ingress controller's namespace.

## Not covered yet

- **The database's own high availability** depends on the PostgreSQL service chosen (above).
- **Backups.** The VM's backup and restore-drill scripts (`deploy/production/backup`) run on the VM. On Kubernetes,
  use the database service's backups, plus a CronJob that copies the audit archive volume. The monitoring page's
  backup status (`Sangam__Monitoring__StatusDirectory`) is not set here.
- **The breached-password list.** It needs a read-only volume with `pwned-passwords.bin`, mounted at
  `/var/lib/sangam/pwned` on every host, before `Sangam__Passwords__BreachCheck__Enabled` is turned on.
- **The demo (imagiQa) and Anjal** are not part of this folder.
- **Horizontal autoscaling.** Not configured. Two replicas give availability, not scale; add an HPA when load
  testing (SGM-504) shows the need.

## Prove it locally

This needs Docker, kind and kubectl, plus Python with Playwright for the browser checks. The four host images and
the migrator must be built as `sangam/*:1.0.0-rc.2`.

```sh
cd deploy/kubernetes/overlays/kind
kind create cluster --config kind-cluster.yaml
for i in identity portal admin partner migrator; do kind load docker-image sangam/$i:1.0.0-rc.2 --name sangam; done
bash make-secrets.sh
kubectl apply -k .
python3 ha-proof.py      # sign-in, failover, rolling restart, node loss, one worker at a time
```

`ha-proof.py` checks the following and prints a line for each:

1. Every Sangam pod is ready, with replicas on different worker nodes.
2. A person registers (the code is read from the Anjal stand-in), signs in, and opens the portal and the account
   page.
3. Each identity pod in turn is deleted, and each time the person stays signed in and can sign in again.
4. The portal pod serving the person's circuit is deleted, and the page comes back, still signed in.
5. Every host is restarted in a rolling update, one host after another, while requests run. None of the requests fails.
6. A worker node is stopped (`docker stop`). Within about a minute every host answers again from the other node.
7. A background round's advisory lock is never held by two database sessions at once. PostgreSQL's locks are
   sampled 20 times a second for a minute. The rounds are short, so they are seen only a few times; the unit tests in
   `ClusterLockTests` prove the exclusion itself.

The R7 results are in the release evidence, under `kubernetes/ha-proof.txt`.
