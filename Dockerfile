# Sangam - one image recipe for every host, and a migrator (PR-10, SGM-307).
#   docker build --build-arg PROJECT=Sangam.Identity.Server -t sangam/identity .
#   docker build --target migrator -t sangam/migrator .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY . .
ARG PROJECT=Sangam.Identity.Server
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /out/app

FROM build AS bundle
# dotnet-ef is pinned in the repository's tool manifest (.config/dotnet-tools.json); a global install
# is not enough, because "dotnet ef" insists on the manifest's tool once one is declared.
RUN dotnet tool restore
RUN dotnet ef migrations bundle --project src/Sangam.Identity.Infrastructure --startup-project src/Sangam.Identity.Infrastructure -o /out/efbundle

# Runs once per release with the schema owner's connection string, then exits:
#   docker run --rm -e CONNECTION="..." sangam/migrator
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime-base
# Npgsql loads libgssapi_krb5 at start-up and logs an error when it is missing (V-03).
RUN apt-get update \
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
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SANGAM_PROJECT.dll\""]
