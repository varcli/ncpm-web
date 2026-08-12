# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY src/Ncpm.Web/Ncpm.Web.csproj src/Ncpm.Web/
RUN dotnet restore src/Ncpm.Web/Ncpm.Web.csproj

# Copy everything and build
COPY src/Ncpm.Web/ src/Ncpm.Web/
RUN dotnet publish src/Ncpm.Web/Ncpm.Web.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install nginx with stream module, curl, and the docker CLI + compose plugin.
# The docker CLI lets the panel drive `docker compose` against the host daemon
# through the mounted socket; compose is invoked as a subprocess, not re-implemented.
RUN apt-get update && apt-get install -y --no-install-recommends \
        nginx libnginx-mod-stream curl ca-certificates gnupg && \
    install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc && \
    chmod a+r /etc/apt/keyrings/docker.asc && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
        > /etc/apt/sources.list.d/docker.list && \
    apt-get update && apt-get install -y --no-install-recommends \
        docker-ce-cli docker-compose-plugin && \
    rm -rf /var/lib/apt/lists/*

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
    /app/data/audit \
    /app/data/backups

# Copy nginx config
COPY deploy/nginx.conf /etc/nginx/nginx.conf
COPY deploy/default.conf /etc/nginx/conf.d/default.conf

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
    CMD curl -f http://localhost:8098/health || exit 1

# Start application
ENTRYPOINT ["dotnet", "Ncpm.Web.dll"]
