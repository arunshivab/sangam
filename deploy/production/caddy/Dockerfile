# rc.9 (ASVS V1.14.5): Caddy, to run as an ordinary user (1000) with no Linux capabilities and no-new-privileges.
# The official binary carries a file capability (cap_net_bind_service) so it can open ports 80 and 443 without root;
# a kernel refuses to run such a binary once privileges may not be gained. A plain copy drops the capability; the
# compose file opens the low ports to unprivileged users inside Caddy's own network namespace instead.
# Pinned by digest (V10.3.2); Dependabot proposes new digests.
FROM caddy:2@sha256:504a44d7d74bd2bc28a66afc4774912fc21223ca8a67734af1c54ab2f999b489
RUN cp /usr/bin/caddy /usr/bin/caddy.plain \
    && mv /usr/bin/caddy.plain /usr/bin/caddy \
    && chown -R 1000:1000 /data /config
USER 1000:1000
