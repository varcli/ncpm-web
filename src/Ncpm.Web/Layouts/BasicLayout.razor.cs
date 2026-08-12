using AntDesign.Extensions.Localization;
using AntDesign.ProLayout;
using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.Net.Http.Json;

namespace Ncpm.Layouts
{
    public partial class BasicLayout : LayoutComponentBase
    {
        private MenuDataItem[] _menuData = [];

        [Inject] private ReuseTabsService TabService { get; set; } = default!;

        protected override void OnInitialized()
        {
            _menuData = new[] {
                new MenuDataItem
                {
                    Path = "/",
                    Name = "仪表盘",
                    Key = "dashboard",
                    Icon = "dashboard",
                },
                new MenuDataItem
                {
                    Name = "Docker",
                    Key = "docker",
                    Icon = "container",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/docker/hosts",
                            Name = "主机管理",
                            Key = "docker-hosts",
                            Icon = "cloud-server",
                        },
                        new MenuDataItem
                        {
                            Path = "/docker/containers",
                            Name = "容器管理",
                            Key = "docker-containers",
                            Icon = "container",
                        },
                        new MenuDataItem
                        {
                            Path = "/docker/images",
                            Name = "镜像管理",
                            Key = "docker-images",
                            Icon = "hdd",
                        },
                        new MenuDataItem
                        {
                            Path = "/docker/networks",
                            Name = "网络管理",
                            Key = "docker-networks",
                            Icon = "apartment",
                        },
                        new MenuDataItem
                        {
                            Path = "/docker/volumes",
                            Name = "卷管理",
                            Key = "docker-volumes",
                            Icon = "database",
                        },
                        new MenuDataItem
                        {
                            Path = "/docker/prune",
                            Name = "系统清理",
                            Key = "docker-prune",
                            Icon = "delete",
                        }
                    }
                },
                new MenuDataItem
                {
                    Name = "站点管理",
                    Key = "proxy",
                    Icon = "global",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/proxy/http",
                            Name = "HTTP 代理",
                            Key = "proxy-http",
                            Icon = "cloud-server",
                        },
                        new MenuDataItem
                        {
                            Path = "/proxy/tcp",
                            Name = "TCP 代理",
                            Key = "proxy-tcp",
                            Icon = "api",
                        },
                        new MenuDataItem
                        {
                            Path = "/proxy/udp",
                            Name = "UDP 代理",
                            Key = "proxy-udp",
                            Icon = "thunderbolt",
                        },
                        new MenuDataItem
                        {
                            Path = "/proxy/fileserver",
                            Name = "静态文件",
                            Key = "proxy-fileserver",
                            Icon = "file",
                        },
                        new MenuDataItem
                        {
                            Path = "/proxy/discovered",
                            Name = "自动发现",
                            Key = "proxy-discovered",
                            Icon = "radar-chart",
                        }
                    }
                },
                new MenuDataItem
                {
                    Path = "/compose",
                    Name = "Compose 编排",
                    Key = "compose",
                    Icon = "appstore",
                },
                new MenuDataItem
                {
                    Path = "/certificates",
                    Name = "SSL 证书",
                    Key = "certificates",
                    Icon = "safety-certificate",
                },
                new MenuDataItem
                {
                    Name = "Nginx",
                    Key = "nginx",
                    Icon = "code",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/nginx/editor",
                            Name = "原始配置",
                            Key = "nginx-editor",
                            Icon = "file-text",
                        },
                        new MenuDataItem
                        {
                            Path = "/nginx/snapshots",
                            Name = "配置快照",
                            Key = "nginx-snapshots",
                            Icon = "history",
                        },
                        new MenuDataItem
                        {
                            Path = "/nginx/logs",
                            Name = "访问日志",
                            Key = "nginx-logs",
                            Icon = "file-search",
                        }
                    }
                },
                new MenuDataItem
                {
                    Name = "监控中心",
                    Key = "monitoring",
                    Icon = "monitor",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/monitoring/dashboard",
                            Name = "系统指标",
                            Key = "monitoring-dashboard",
                            Icon = "line-chart",
                        },
                        new MenuDataItem
                        {
                            Path = "/monitoring/health",
                            Name = "健康检查",
                            Key = "monitoring-health",
                            Icon = "heart",
                        }
                    }
                },
                new MenuDataItem
                {
                    Name = "系统设置",
                    Key = "settings",
                    Icon = "setting",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/settings/config",
                            Name = "基本配置",
                            Key = "settings-config",
                            Icon = "file-text",
                        },
                        new MenuDataItem
                        {
                            Path = "/settings/account",
                            Name = "账号安全",
                            Key = "settings-account",
                            Icon = "safety",
                        }
                    }
                }
            };
        }

        void Reload()
        {
            TabService.ReloadPage();
        }
    }
}
