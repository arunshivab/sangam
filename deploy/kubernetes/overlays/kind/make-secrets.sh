#!/usr/bin/env bash
# Test-only secrets for the local proof: a CA and a *.sangam.test certificate, token and key-ring certificates, client
# secrets and a database password, all generated here. Never use these anywhere else.
set -euo pipefail
K=${KUBECTL:-kubectl}
D=$(mktemp -d)
trap 'rm -rf "$D"' EXIT
cd "$D"
pass() { openssl rand -hex 24; }
PW=$(pass)
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=Sangam test CA" -keyout ca.key -out ca.crt 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=sangam.test" -keyout tls.key -out tls.csr 2>/dev/null
printf 'subjectAltName=DNS:id.sangam.test,DNS:account.sangam.test,DNS:admin.sangam.test,DNS:partners.sangam.test\nextendedKeyUsage=serverAuth\n' > ext
openssl x509 -req -in tls.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 30 -extfile ext -out tls.crt 2>/dev/null
for n in signing encryption keyring; do
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=Sangam test $n" -keyout $n.key -out $n.crt 2>/dev/null
  openssl pkcs12 -export -inkey $n.key -in $n.crt -passout pass:"$PW" -out ${n}_current.pfx
done
openssl req -x509 -newkey rsa:3072 -nodes -days 365 -subj "/CN=Sangam test audit archive" -keyout audit.key -out audit_archive.crt 2>/dev/null
DBPW=$(pass); PORTAL=$(pass); ADMIN=$(pass); PARTNER=$(pass)
CONN="Host=postgres.sangam-data.svc.cluster.local;Database=sangam;Username=sangam;Password=$DBPW"
# The system bundle of the image plus the test CA, for SSL_CERT_FILE in the pods.
cat /etc/ssl/certs/ca-certificates.crt ca.crt > bundle.crt

$K create namespace sangam --dry-run=client -o yaml | $K apply -f -
$K create namespace sangam-data --dry-run=client -o yaml | $K apply -f -
$K -n sangam-data create secret generic postgres --from-literal=password="$DBPW"
$K -n sangam create secret tls sangam-tls --cert=tls.crt --key=tls.key
$K -n sangam create configmap sangam-test-ca --from-file=bundle.crt --from-file=ca.crt
$K -n sangam create secret generic sangam-migrator --from-literal=CONNECTION="$CONN"
$K -n sangam create secret generic sangam-shared \
  --from-literal=ConnectionStrings__Sangam="$CONN" \
  --from-literal=Sangam__Anjal__ApiKey="anjal-test-key-$(pass)" \
  --from-literal=Sangam__PasswordHashing__Pepper="$(openssl rand -base64 32)" \
  --from-file=keyring_current.pfx \
  --from-literal=Sangam__DataProtection__Certificates__0__Password="$PW"
$K -n sangam create secret generic sangam-identity \
  --from-file=signing_current.pfx --from-file=encryption_current.pfx --from-file=audit_archive.crt \
  --from-literal=Sangam__Certificates__Signing__0__Password="$PW" \
  --from-literal=Sangam__Certificates__Encryption__0__Password="$PW" \
  --from-literal=Sangam__Clients__portal__Secret="$PORTAL" \
  --from-literal=Sangam__Clients__admin__Secret="$ADMIN" \
  --from-literal=Sangam__Clients__partner__Secret="$PARTNER"
$K -n sangam create secret generic sangam-portal --from-literal=Sangam__Portal__ClientSecret="$PORTAL"
$K -n sangam create secret generic sangam-admin --from-literal=Sangam__Admin__ClientSecret="$ADMIN"
$K -n sangam create secret generic sangam-partner --from-literal=Sangam__Partner__ClientSecret="$PARTNER"
echo "Secrets created. The test CA is in configmap sangam/sangam-test-ca (ca.crt)."
