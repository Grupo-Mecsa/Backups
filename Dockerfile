# syntax=docker/dockerfile:1

# ---------- Compilación ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar primero (capa cacheable mientras no cambien los .csproj)
COPY Directory.Build.props ./
COPY src/Backup.Domain/Backup.Domain.csproj src/Backup.Domain/
COPY src/Backup.Application/Backup.Application.csproj src/Backup.Application/
COPY src/Backup.Infrastructure/Backup.Infrastructure.csproj src/Backup.Infrastructure/
COPY src/Backup.Providers.Databases/Backup.Providers.Databases.csproj src/Backup.Providers.Databases/
COPY src/Backup.Providers.Cloud/Backup.Providers.Cloud.csproj src/Backup.Providers.Cloud/
COPY src/Backup.Providers.Files/Backup.Providers.Files.csproj src/Backup.Providers.Files/
COPY src/Backup.Web/Backup.Web.csproj src/Backup.Web/
RUN dotnet restore src/Backup.Web/Backup.Web.csproj

COPY src/ src/
# Sin --no-restore: desde el SDK 10.0.4xx el pack con blazor.web.js (Microsoft.AspNetCore.App.Internal.Assets)
# solo se resuelve en el restore que hace publish; con --no-restore la app responde 404 a /_framework/blazor.web.js.
RUN dotnet publish src/Backup.Web/Backup.Web.csproj -c Release -o /app /p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Versión mayor de pg_dump: debe ser >= a la de tus servidores PostgreSQL.
ARG PG_MAJOR=17
ARG INSTALL_MONGO_TOOLS=true
ARG MONGO_TOOLS_VERSION=100.12.0

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl gnupg tzdata postgresql-common default-mysql-client; \
    /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y; \
    apt-get install -y --no-install-recommends "postgresql-client-${PG_MAJOR}"; \
    if [ "$INSTALL_MONGO_TOOLS" = "true" ]; then \
        . /etc/os-release; \
        distro="${ID}$(echo "$VERSION_ID" | tr -d .)"; \
        arch="$(dpkg --print-architecture)"; [ "$arch" = "amd64" ] && arch="x86_64"; \
        url="https://fastdl.mongodb.org/tools/db/mongodb-database-tools-${distro}-${arch}-${MONGO_TOOLS_VERSION}.deb"; \
        if curl -fsSL "$url" -o /tmp/mongo-tools.deb; then \
            apt-get install -y --no-install-recommends /tmp/mongo-tools.deb; rm /tmp/mongo-tools.deb; \
        else echo "AVISO: mongodump no disponible para ${distro}/${arch}; el origen MongoDB no funcionará."; fi; \
    fi; \
    apt-get purge -y --auto-remove gnupg; \
    rm -rf /var/lib/apt/lists/*; \
    pg_dump --version; mysqldump --version; command -v mongodump || true

WORKDIR /app
COPY --from=build /app .

RUN mkdir -p /data /backups /tmp/backup-work && chown -R "$APP_UID" /data /backups /tmp/backup-work

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Backup__DataDirectory=/data \
    Backup__WorkingDirectory=/tmp/backup-work \
    DOTNET_gcServer=0

VOLUME ["/data"]
EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Backup.Web.dll"]
