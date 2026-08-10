# NCPM（.NET Nginx Container & Proxy Management Panel）

A lightweight container & reverse proxy management panel.

一个轻量级的容器与反向代理管理面板。

## 项目简介

NCPM 面向个人服务器、家庭实验室和小型自托管环境，通过简洁的 Web 界面完成三类高频工作：

1. **反向代理**：将域名或路径反向代理到本机、局域网或 Docker 服务
2. **SSL 证书**：自动申请、续期和加载 HTTPS 证书
3. **Docker 管理**：查看并执行常用的 Docker 容器运维操作

## 核心特性

### 反向代理
- 按域名匹配 HTTP/HTTPS 请求
- 支持 WebSocket、SSE、gRPC 等流式转发
- HTTP 自动跳转 HTTPS
- 路由启用、停用、修改和即时生效
- 基础访问日志

### SSL 证书
- 导入已有 PEM/PFX 证书
- 通过 ACME（HTTP-01）自动申请和续期
- 证书更新后无中断加载
- 续期失败重试与面板告警

### Docker 管理
- 查看容器列表、状态、镜像、端口和详情
- 启动、停止和重启容器
- 实时查看容器日志
- 查看 CPU、内存、网络等实时指标

## 技术栈

- **后端**：.NET 10 / ASP.NET Core
- **前端**：Blazor Server + Ant Design Blazor + ProLayout
- **代理**：Nginx
- **容器**：Docker.DotNet
- **配置**：YamlDotNet（YAML 配置文件）

## 快速开始

### 环境要求

- .NET 10 SDK（开发环境）
- Docker 和 Docker Compose（部署环境）

### 本地开发

```bash
# 克隆项目
git clone <repository-url>
cd n-panel

# 运行项目
cd src/Ncpm.Web
dotnet run
```

访问 `http://localhost:8098` 即可打开管理面板。

### Docker 部署

```bash
# 克隆项目
git clone <repository-url>
cd ncpm

# 启动服务
docker-compose up -d
```

访问 `http://your-server-ip:8098` 即可打开管理面板。

默认登录信息：
- 用户名：`admin`
- 密码：`admin123`

**请首次登录后立即修改密码！**

## 项目结构

```
n-panel/
├── src/
│   └── Ncpm.Web/              # 应用源代码
│       ├── Services/          # 业务服务层
│       │   ├── AuthService.cs
│       │   ├── ConfigService.cs
│       │   ├── DockerService.cs
│       │   ├── NginxService.cs
│       │   └── ...
│       ├── Pages/             # Blazor 页面
│       │   ├── Dashboard.razor
│       │   ├── Proxy/
│       │   ├── Docker/
│       │   ├── Certificates/
│       │   └── Settings/
│       ├── Layouts/           # 布局组件
│       └── wwwroot/           # 静态资源
├── deploy/
│   ├── Dockerfile
│   ├── nginx.conf             # Nginx 主配置
│   ├── default.conf           # Nginx 默认站点配置
│   └── data/                  # 持久化数据目录
├── docker-compose.yml
├── Ncpm.slnx                  # 解决方案文件
└── README.md
```

## 配置说明

### 应用配置

编辑 `src/Ncpm.Web/appsettings.json`：

```json
{
  "Docker": {
    "Host": "unix:///var/run/docker.sock"
  },
  "Nginx": {
    "ConfigPath": "/etc/nginx",
    "GeneratedPath": "/app/data/nginx/generated",
    "ActivePath": "/app/data/nginx/active"
  },
  "Logging": {
    "Level": "Information",
    "Path": "/app/data/logs"
  }
}
```

### 数据目录

所有持久化数据存储在 `deploy/data/` 目录：

```
data/
├── config/            # 应用配置文件
│   ├── proxy-hosts/   # 代理主机配置
│   └── certificates/  # 证书策略配置
├── nginx/
│   ├── generated/     # 生成的 Nginx 配置
│   └── active/        # 活跃的 Nginx 配置
├── certs/             # SSL 证书文件
├── certbot/           # ACME challenge 文件
├── secrets/           # 敏感信息
├── logs/              # 应用日志
├── audit/             # 审计日志
└── backups/           # 配置备份
```

## 安全建议

1. **修改默认密码**：首次登录后立即修改管理员密码
2. **使用 Docker Socket Proxy**：生产环境建议使用受限的 Docker Socket Proxy
3. **启用 HTTPS**：为管理面板配置 SSL 证书
4. **限制访问**：仅允许受信任的网络访问管理端口
5. **定期备份**：定期备份 `data/` 目录

## 开发路线

### Phase 0：技术原型 ✅
- 建立 .NET 10 解决方案
- 验证 Blazor + AntDesign 骨架
- 验证 Docker 和 Nginx 集成

### Phase 1：MVP（进行中）
- 首次启动、管理员登录和系统设置
- 代理主机 CRUD、配置校验、发布与回滚
- 手动证书导入与 HTTP-01 自动证书
- 容器列表、详情、启停、重启、日志和指标

### Phase 2：增强
- Docker Label 自动发现路由
- DNS-01、泛域名和多证书
- 多上游、健康检查和负载均衡
- 远程 Docker 节点

## 许可证

待定

## 参考资料

- [Nginx 反向代理模块](https://nginx.org/en/docs/http/ngx_http_proxy_module.html)
- [Docker Compose 文档](https://docs.docker.com/compose/)
- [Docker daemon socket 安全建议](https://docs.docker.com/engine/security/protect-access/)
- [Let's Encrypt Challenge Types](https://letsencrypt.org/docs/challenge-types/)
