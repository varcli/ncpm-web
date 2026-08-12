# NCPM

NCPM（Nginx Container & Proxy Management Panel）是面向个人服务器、家庭实验室和小型自托管环境的单节点运维面板，核心是反向代理、自动 SSL 证书和 Docker/Compose 管理。

## 当前能力

- HTTP/HTTPS、TCP、UDP 与静态文件代理，多上游负载均衡、WebSocket/SSE/gRPC、访问日志和健康检查。
- Nginx 候选配置校验、`nginx -t`、原子发布、快照、失败回滚和热重载。
- PEM/PFX 导入，ACME HTTP-01 与 DNS-01 签发、通配符证书、无人值守续期和失败通知。
- DNS-01 支持 Cloudflare、阿里云 DNS、DNSPod、AWS Route 53、Azure DNS、DigitalOcean、GoDaddy、Namecheap、Porkbun、Linode 和 Vultr。
- Docker 多主机、HTTPS/mTLS、容器/镜像/网络/卷运维、日志、指标、清理和 Compose 项目管理。
- Docker Label 自动发现、ACL、动态限流、通知、就绪检查和日期版本 Docker 镜像。

当前发布目标是单节点生产使用。OIDC 仅保留配置草稿，尚未接入登录链路；生产环境使用本地管理员认证。

## Docker 快速部署

```bash
git clone https://github.com/varcli/ncpm-web.git
cd ncpm-web
cp deploy/.env.example .env
docker compose up -d --build
```

面板地址：`http://服务器地址:8098`。

首次启动不再使用固定默认密码。若 `.env` 未设置 `NCPM_ADMIN_PASSWORD`/`NCPM_ADMIN_PASSWORD_FILE`，读取随机生成的凭据：

```bash
docker exec ncpm-panel cat /app/data/secrets/initial-admin-password
```

登录后会被强制进入“账号安全”修改密码，成功后初始化密码文件自动删除，已有会话全部撤销。

生产部署和升级请阅读 [Docker 部署说明](deploy/README.md) 与[上线检查清单](docs/PRODUCTION-CHECKLIST.md)。

## 镜像版本

GitHub Actions 发布 `linux/amd64`、`linux/arm64` 镜像，主版本使用上海时区日期 `yyyyMMdd`，同时生成不可变 `sha-*` 标签：

```bash
NCPM_VERSION=20260813 docker compose pull
NCPM_VERSION=20260813 docker compose up -d
```

## 数据与安全

默认持久化目录是 `deploy/data`，生产环境建议在 `.env` 使用绝对路径 `NCPM_DATA_PATH=/opt/ncpm/data`。需要整体备份：

```text
data/
├── config/       # 系统、代理、证书策略和 Docker 主机配置
├── compose/      # 面板管理的 Compose 项目
├── nginx/        # 生成、激活、stream 配置
├── certs/        # 证书链与私钥
├── certbot/      # HTTP-01 challenge
├── secrets/      # Data Protection keys、会话摘要、ACME/DNS 凭据
├── logs/
├── audit/
└── backups/
```

DNS 与通知 Token 使用持久化 Data Protection key 加密；会话只保存 SHA-256 摘要；敏感文件在 Linux 上限制为所有者访问。恢复时必须同时恢复完整 `secrets/`，否则历史加密配置无法解密。

挂载 `/var/run/docker.sock` 等同授予面板较高的宿主机权限。只向可信管理员开放面板，生产环境优先使用受限 Socket Proxy；远程 Docker 使用 HTTPS/mTLS，不要暴露公网明文 2375。

如果面板位于另一层反向代理后，只在“系统配置 → 可信反向代理”填写直接代理的 IP/CIDR。未列入信任的 `X-Forwarded-*` 会被忽略，防止伪造来源绕过 ACL 与限流。

## 健康检查

- `/health/live`：面板进程存活。
- `/health`、`/health/ready`：配置可读且 Nginx 可达；Docker 全部不可用时报告 Degraded。

Docker 镜像的 HEALTHCHECK 使用就绪检查。

## 本地开发

需要 .NET 10 SDK：

```bash
dotnet restore Ncpm.slnx
dotnet test Ncpm.slnx -c Release --no-restore
dotnet run --project src/Ncpm.Web/Ncpm.Web.csproj
```

本地没有 Nginx 或 Docker 时，Web 项目仍可编译和开发，但就绪检查不会显示 Healthy。

开发约束见 [AGENTS.md](AGENTS.md)，贡献流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 技术栈

- .NET 10 / ASP.NET Core / Blazor Server
- Ant Design Blazor / ProLayout
- Nginx
- Docker.DotNet + Docker Compose CLI
- YamlDotNet / Serilog / Certes / acme.sh

## 项目结构

```text
src/Ncpm.Web/             应用与 WebUI
tests/Ncpm.Web.Tests/     核心安全和配置测试
deploy/                   Dockerfile、Nginx 与部署说明
.github/workflows/        CI 与多架构镜像发布
docs/                     上线与运维文档
```

## 许可证

待定。
