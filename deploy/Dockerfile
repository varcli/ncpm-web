# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY global.json Directory.Build.props ./
COPY src/Ncpm.Web/Ncpm.Web.csproj src/Ncpm.Web/
RUN dotnet restore src/Ncpm.Web/Ncpm.Web.csproj

# Copy everything and build
COPY src/Ncpm.Web/ src/Ncpm.Web/
RUN dotnet publish src/Ncpm.Web/Ncpm.Web.csproj -c Release --no-restore -o /app/publish

# The Blazor Server bootstrap script is supplied by the SDK-matched internal
# assets package. Some Linux SDK builds retain it in the package cache instead
# of copying it to the publish web root, which leaves the panel page blank.
# Install that exact restored version so the runtime image is self-contained.
RUN BLAZOR_SERVER_JS="$(find /root/.nuget/packages/microsoft.aspnetcore.app.internal.assets \
        -type f -path '*/_framework/blazor.server.js' -print -quit)" && \
    test -n "${BLAZOR_SERVER_JS}" && \
    install -D -m 0644 "${BLAZOR_SERVER_JS}" \
        /app/publish/wwwroot/_framework/blazor.server.js && \
    test -s /app/publish/wwwroot/_framework/blazor.server.js

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG ACME_SH_VERSION=3.1.4
ARG ACME_SH_SHA256=e5f8e187bbf5251e0cd8891f2622daab9850366bd17bea9f92c2fe2ee091fd32

# Install nginx with stream module, curl, and the docker CLI + compose plugin.
# The docker CLI lets the panel drive `docker compose` against the host daemon
# through the mounted socket; compose is invoked as a subprocess, not re-implemented.
# The distro id is read from the base image rather than hardcoded: the .NET runtime
# images have moved between Debian and Ubuntu, and download.docker.com keeps a
# separate repository per distro, so a wrong pairing 404s and fails apt-get update.
RUN apt-get update && apt-get install -y --no-install-recommends \
        nginx libnginx-mod-stream curl ca-certificates gnupg openssl && \
    install -m 0755 -d /etc/apt/keyrings && \
    DISTRO_ID="$(. /etc/os-release && echo "$ID")" && \
    DISTRO_CODENAME="$(. /etc/os-release && echo "$VERSION_CODENAME")" && \
    curl -fsSL --retry 5 --retry-delay 3 --retry-all-errors --connect-timeout 15 --max-time 120 \
        "https://download.docker.com/linux/${DISTRO_ID}/gpg" -o /etc/apt/keyrings/docker.asc && \
    chmod a+r /etc/apt/keyrings/docker.asc && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DISTRO_ID} ${DISTRO_CODENAME} stable" \
        > /etc/apt/sources.list.d/docker.list && \
    echo 'Acquire::Retries "5";' > /etc/apt/apt.conf.d/80-retries && \
    apt-get update && apt-get install -y --no-install-recommends \
        docker-ce-cli docker-compose-plugin && \
    rm -rf /var/lib/apt/lists/*

# DNS-01 providers are maintained by acme.sh. Pin the release instead of
# executing its online installer; NCPM drives issuance and renewal itself.
# The checksum below is what makes retrying safe: a truncated or substituted
# tarball fails sha256sum rather than being unpacked.
RUN mkdir -p /opt/acme.sh && \
    curl -fsSL --retry 5 --retry-delay 3 --retry-all-errors --connect-timeout 15 --max-time 300 \
        "https://github.com/acmesh-official/acme.sh/archive/refs/tags/${ACME_SH_VERSION}.tar.gz" \
        -o /tmp/acme-sh.tar.gz && \
    echo "${ACME_SH_SHA256}  /tmp/acme-sh.tar.gz" | sha256sum -c - && \
    tar -xzf /tmp/acme-sh.tar.gz --strip-components=1 -C /opt/acme.sh && \
    chmod 0755 /opt/acme.sh/acme.sh && \
    rm -f /tmp/acme-sh.tar.gz

# Create directories
RUN mkdir -p /app/data/config \
    /app/data/config/proxy-hosts \
    /app/data/config/discovered-hosts \
    /app/data/config/certificates \
    /app/data/compose \
    /app/data/nginx/generated \
    /app/data/nginx/active \
    /app/data/nginx/stream \
    /app/data/certs \
    /app/data/certbot \
    /app/data/secrets \
    /app/data/logs \
    /app/data/logs/nginx \
    /app/data/audit \
    /app/data/backups

# Copy nginx config
COPY deploy/nginx.conf /etc/nginx/nginx.conf
COPY deploy/default.conf /etc/nginx/conf.d/default.conf
COPY deploy/entrypoint.sh /usr/local/bin/ncpm-entrypoint
RUN chmod 0755 /usr/local/bin/ncpm-entrypoint

# Copy application
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8098
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Expose ports
# 8098: Panel WebUI
# 80: HTTP
# 443: HTTPS
EXPOSE 8098 80 443

# Health check
HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD curl -fsS -o /dev/null http://localhost:8098/health && \
        curl -fsS -o /dev/null http://localhost:8098/_framework/blazor.server.js || exit 1

# Start nginx first, then keep the panel as PID 1 so container signals and exit
# codes follow the managed application.
ENTRYPOINT ["/usr/local/bin/ncpm-entrypoint"]
