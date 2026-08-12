# 参与开发

## 准备环境

需要 .NET 10 SDK。Docker 仅在验证容器镜像、Nginx 与 Docker 运维链路时需要。

```bash
dotnet restore Ncpm.slnx
dotnet test Ncpm.slnx -c Release --no-restore
```

本地运行：

```bash
dotnet run --project src/Ncpm.Web/Ncpm.Web.csproj
```

本地环境没有 Nginx 或 Docker 时，面板页面可开发，但 `/health/ready` 会按设计报告未就绪。

## 变更约定

1. 先阅读 [AGENTS.md](AGENTS.md) 中的安全与发布不变量。
2. 一个变更同时包含行为实现、输入校验、错误提示和测试。
3. 配置与证书写入使用原子替换；Nginx 发布必须保留验证与回滚闭环。
4. 不提交 `data/`、`.env`、证书、私钥、DNS/通知 Token 或真实域名配置。
5. PR 描述列出验证命令、受影响配置、是否需要重启及回滚方式。

## 完成标准

- `dotnet test Ncpm.slnx -c Release --no-restore` 通过且零警告。
- Docker/部署变更通过镜像构建；涉及实际反代或证书时按[上线检查清单](docs/PRODUCTION-CHECKLIST.md)验收。
- 文档和 `.env.example` 与代码一致。
