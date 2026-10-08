# Sangam - one image recipe for every host, and a migrator (PR-10, SGM-307).
#   docker build --build-arg PROJECT=Sangam.Identity.Server -t sangam/identity .
#   docker build --target migrator -t sangam/migrator .
#   docker build --build-arg PROJECT_DIR=samples --build-arg PROJECT=Imagiqa.Web -t sangam/demo .   (D-I)
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY . .
ARG PROJECT=Sangam.Identity.Server
ARG PROJECT_DIR=src
RUN dotnet publish "${PROJECT_DIR}/${PROJECT}/${PROJECT}.csproj" -c Release -o /out/app

FROM build AS bundle
# dotnet-ef is pinned in the repository's tool manifest (.config/dotnet-tools.json); a global install
# is not enough, because "dotnet ef" insists on the manifest's tool once one is declared.
RUN dotnet tool restore
RUN dotnet ef migrations bundle --project src/Sangam.Identity.Infrastructure --startup-project src/Sangam.Identity.Infrastructure -o /out/efbundle

# Runs once per release with the schema owner's connection string, then exits:
#   docker run --rm -e CONNECTION="..." sangam/migrator
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime-base
# Npgsql loads libgssapi_krb5 at start-up and logs an error when it is missing (V-03). R7: every build also takes the
# Ubuntu security updates published since the base image (docs/security/dependency-scan.md).
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM runtime-base AS migrator
COPY --from=bundle /out/efbundle /efbundle
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec /efbundle --connection \"$CONNECTION\""]

FROM runtime-base AS app
ARG PROJECT=Sangam.Identity.Server
# The base image already listens on 8080 through ASPNETCORE_HTTP_PORTS; setting ASPNETCORE_URLS too
# only produced an "Overriding HTTP_PORTS" warning (V-04).
ENV SANGAM_PROJECT=${PROJECT} \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
WORKDIR /app
COPY --from=build /out/app .
USER $APP_UID
EXPOSE 8080
# R7: Docker (and Compose) mark the container unhealthy when /health/live stops answering 200. bash's /dev/tcp makes
# the request, so no curl is added to the image; the port is the first of ASPNETCORE_HTTP_PORTS.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_HTTP_PORTS%%[;,]*} && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SANGAM_PROJECT.dll\""]
