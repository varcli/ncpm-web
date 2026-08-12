# NCPM Docker 部署

镜像将 .NET 面板、Nginx、Docker CLI、Compose 插件和固定版本 acme.sh 打包在同一容器。入口先执行 `nginx -t`，再启动 Nginx 与面板。

## 启动

```bash
cp deploy/.env.example .env
docker compose up -d --build
docker compose ps
```

面板地址为 `http://服务器地址:8098`。首次密码有三种来源，按优先级排列：

1. `NCPM_ADMIN_PASSWORD_FILE` 指向挂载的 Docker/Kubernetes Secret；
2. `.env` 中的 `NCPM_ADMIN_PASSWORD`；
3. 留空后随机生成到 `/app/data/secrets/initial-admin-password`。

```bash
docker exec ncpm-panel cat /app/data/secrets/initial-admin-password
```

初始化账号会强制改密，完成后该文件自动删除。不存在固定默认密码。

## 日期镜像与升级

```bash
NCPM_VERSION=20260813 docker compose pull
NCPM_VERSION=20260813 docker compose up -d
```

主标签为上海时区 `yyyyMMdd`，另有 `sha-*` 标签用于精确回滚。升级前备份完整数据目录；配置与 Data Protection keys 必须一起恢复。

```bash
docker compose stop panel
tar -czf ncpm-backup-$(date +%Y%m%d).tar.gz /opt/ncpm/data
docker compose start panel
```

## 数据路径与 Compose

在 `.env` 使用绝对路径：

```dotenv
NCPM_DATA_PATH=/opt/ncpm/data
NCPM_COMPOSE_HOST_PATH=/opt/ncpm/data/compose
```

面板管理的 Compose YAML 使用相对 bind mount 时必须设置 `NCPM_COMPOSE_HOST_PATH`。Docker daemon 在宿主机解析路径，容器内 `/app/data/compose` 对宿主机不可见；命名卷不受影响。

## 反代与证书

1. 域名 A/AAAA 指向服务器，开放 80/443。
2. 普通域名使用 HTTP-01；通配符证书必须使用 DNS-01。
3. 首次真实签发前先选择 Let's Encrypt Staging。
4. 发布会先校验候选配置和证书/私钥，再执行 `nginx -t` 与 reload；失败自动恢复上一版本。

HTTP-01 文件位于 `/app/data/certbot`。DNS-01 支持 Cloudflare、阿里云 DNS、DNSPod、AWS Route 53、Azure DNS、DigitalOcean、GoDaddy、Namecheap、Porkbun、Linode、Vultr。DNS API 凭据使用 Data Protection 加密后保存在 `data/secrets`，不会以明文写入证书 YAML 或命令参数。

TCP/UDP 代理还必须在 `docker-compose.yml` 的 `ports` 显式发布监听端口。

## Docker 权限与远程节点

默认挂载可读写 `/var/run/docker.sock`，这是容器/Compose 运维所必需，也等同较高宿主机权限。仅向可信管理员开放，或改接限制 API 的 Socket Proxy。

远程 Docker 支持 HTTP(S)。生产环境使用 HTTPS/mTLS：在 Docker 主机页面配置 CA、客户端证书与私钥的容器内只读路径。SSH 传输当前不支持，明文 2375 不应暴露到公网。

## 面板前置反代

面板位于负载均衡/Nginx/Caddy 后时，在系统配置填写直接代理的 IP 或 CIDR，并重启容器。只有可信来源的 `X-Forwarded-For`、`X-Forwarded-Proto` 会生效；不要把 `0.0.0.0/0` 加入可信代理。

## 运维检查

```bash
docker compose logs -f panel
docker exec ncpm-panel nginx -t
curl -f http://127.0.0.1:8098/health/live
curl -f http://127.0.0.1:8098/health/ready
```

- `/health/live` 只检查进程。
- `/health/ready` 检查配置和 Nginx；Docker 不可用时为 Degraded，不会把暂时的 daemon 故障误判成面板进程崩溃。

完整验收见[上线检查清单](../docs/PRODUCTION-CHECKLIST.md)。OIDC 当前仅为配置草稿，生产环境保持关闭。
