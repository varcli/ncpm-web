# NCPM Docker Deployment

.NET Nginx Container & Proxy Management Panel

## Quick Start

### 1. Prepare directory

```bash
mkdir -p ncpm-data
cd ncpm-data
```

### 2. Download files

```bash
# Download docker-compose.yml
curl -O https://raw.githubusercontent.com/your-repo/ncpm/main/deploy/docker-compose.yml

# Download environment config
curl -O https://raw.githubusercontent.com/your-repo/ncpm/main/deploy/.env.example
cp .env.example .env
```

### 3. Start services

```bash
docker compose up -d
```

### 4. Access panel

Open browser: `http://your-server-ip:8098`

Default login:
- Username: `admin`
- Password: `admin123`

**Please change password immediately after first login!**

## Directory Structure

```
ncpm-data/
├── docker-compose.yml
├── .env
└── data/
    ├── config/
    │   ├── config.yml          # Application config
    │   ├── docker-hosts.yml    # Docker host connections
    │   ├── proxy-hosts/        # Proxy host configs
    │   ├── users.yml           # User accounts
    │   └── certificates/       # Certificate configs
    ├── nginx/
    │   ├── generated/          # Generated Nginx configs
    │   └── active/             # Active Nginx configs
    ├── certs/                  # SSL certificates
    ├── secrets/                # Sensitive data
    ├── logs/                   # Application logs
    ├── audit/                  # Audit logs
    └── backups/                # Configuration backups
```

## Configuration

### Environment Variables

Edit `.env` file:

```bash
# Docker host connection
Docker__Host=unix:///var/run/docker.sock

# For remote Docker
Docker__Host=tcp://192.168.1.100:2375

# Panel port
Panel__Port=8098
```

### Add Remote Docker Host

1. Access panel at `http://your-server:8098`
2. Go to Docker > Hosts
3. Click "Add Host"
4. Fill in connection details:
   - Name: My Remote Server
   - Type: TCP or SSH
   - Host: 192.168.1.100
   - Port: 2375 (TCP) or 22 (SSH)
5. Click "Test" to verify connection
6. Click "Submit" to save

## Backup & Restore

### Backup

```bash
# Stop services
docker compose down

# Backup data directory
tar -czf ncpm-backup-$(date +%Y%m%d).tar.gz data/

# Start services
docker compose up -d
```

### Restore

```bash
# Stop services
docker compose down

# Restore data directory
tar -xzf ncpm-backup-YYYYMMDD.tar.gz

# Start services
docker compose up -d
```

## Upgrade

```bash
# Pull latest images
docker compose pull

# Restart services
docker compose up -d
```

## Security Recommendations

1. **Change default password** immediately after first login
2. **Use Docker Socket Proxy** instead of direct socket mount
3. **Enable HTTPS** for production use
4. **Restrict panel access** to trusted networks
5. **Regular backups** of configuration data

## Troubleshooting

### Container not starting

```bash
# Check logs
docker compose logs ncpm-panel

# Check container status
docker compose ps
```

### Cannot connect to Docker

```bash
# Test Docker connection
docker exec ncpm-panel docker info

# Check Docker socket permissions
ls -la /var/run/docker.sock
```

### Nginx configuration errors

```bash
# Check Nginx config
docker exec ncpm-panel nginx -t

# View Nginx logs
docker exec ncpm-panel cat /var/log/nginx/error.log
```

## Advanced Configuration

### Use Docker Socket Proxy

For better security, use Docker Socket Proxy:

1. Uncomment `docker-proxy` service in `docker-compose.yml`
2. Update panel environment:
   ```yaml
   environment:
     - Docker__Host=tcp://docker-proxy:2375
   ```
3. Remove Docker socket mount from panel service
4. Restart services

### Custom Nginx Configuration

To add custom Nginx settings:

1. Edit `data/config/config.yml`
2. Update Nginx section
3. Restart panel or wait for config reload

### SSL/TLS Certificate

1. Go to Certificates page
2. Upload certificate files or configure ACME
3. Enable HTTPS in proxy host settings
