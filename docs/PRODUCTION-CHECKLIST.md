# NCPM 上线检查清单

NCPM 当前适合单节点、自托管环境。上线前逐项确认；OIDC 登录暂未实现，不应启用。

## 1. 部署与身份

- [ ] 使用日期标签 `yyyyMMdd` 或不可变 `sha-*`，不要长期依赖浮动 `latest`。
- [ ] `NCPM_DATA_PATH` 使用绝对路径并纳入备份。
- [ ] 首次密码来自 Docker Secret（`NCPM_ADMIN_PASSWORD_FILE`）或读取 `data/secrets/initial-admin-password`。
- [ ] 首次登录已完成强制改密，初始化密码文件已自动删除。
- [ ] `Security.RequireAuth=true`，8098 管理端口只向管理网或 VPN 开放。
- [ ] 如面板前还有反代，只在 `Panel.TrustedProxies` 填写直接代理 IP/CIDR，重启后验证真实来源 IP。

## 2. Docker 权限

- [ ] 已接受 `/var/run/docker.sock` 等同宿主机高权限的风险，或改用最小权限 Socket Proxy。
- [ ] 远程 Docker 不使用公网明文 2375；使用 HTTPS/mTLS，并挂载只读 CA、客户端证书和私钥。
- [ ] Compose 使用相对 bind mount 时，`NCPM_COMPOSE_HOST_PATH` 指向宿主机真实的 `data/compose`。
- [ ] 测试容器列表、启动、停止、重启、日志、指标和 Compose up/down。

## 3. 反向代理

- [ ] 80/443 端口无冲突，云安全组和主机防火墙已放行。
- [ ] 创建测试 HTTP 代理，发布后 `nginx -t` 成功，上游、WebSocket/SSE 与真实请求头符合预期。
- [ ] 故意提交一份无效候选配置，确认线上旧配置仍继续服务。
- [ ] TCP/UDP 代理端口已在 Compose `ports` 显式发布。
- [ ] ACL 与限流使用真实客户端 IP 验证，不能用伪造 `X-Forwarded-For` 绕过。

## 4. SSL / ACME

- [ ] 普通域名 HTTP-01：A/AAAA 已指向服务器，公网可访问 80，challenge 未被外层 CDN/代理拦截。
- [ ] 通配符证书使用 DNS-01，DNS API Token 采用最小区域/记录权限。
- [ ] 先用 Let's Encrypt Staging 验证，再切换生产目录，避免触发速率限制。
- [ ] 签发后证书 SAN、有效期、证书链与私钥配对正确，Nginx 已 reload。
- [ ] `data/secrets/acme-dns` 中凭据为加密载荷，日志和配置 YAML 不含明文 API Token。
- [ ] 手工触发一次续期并验证失败通知；自动续期任务启动后每 12 小时巡检，临期证书会续期。

## 5. 可观测性与恢复

- [ ] `GET /health/live` 返回 200；`GET /health/ready` 显示 Nginx 就绪，Docker 中断时为 Degraded。
- [ ] 日志目录有容量/保留策略，通知渠道测试成功且不会重复投递。
- [ ] 完整备份 `data/`，并在另一临时目录验证恢复：配置、Data Protection keys、证书和 DNS 凭据必须一起恢复。
- [ ] 记录当前镜像 `sha-*` 标签；升级失败时可回退镜像并恢复升级前 `data/` 快照。

## 6. 发布门禁

- [ ] `dotnet test Ncpm.slnx -c Release --no-restore` 通过且零警告。
- [ ] CI 的 .NET 测试与 Docker 镜像构建通过。
- [ ] 多架构发布包含 `linux/amd64`、`linux/arm64`、SBOM 和 provenance。
- [ ] 未提交 `.env`、初始化密码、用户文件、会话、私钥或真实 API Token。
