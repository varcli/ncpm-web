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
                        }
                    }
                },
                new MenuDataItem
                {
                    Name = "反向代理",
                    Key = "proxy",
                    Icon = "global",
                    Children = new[]
                    {
                        new MenuDataItem
                        {
                            Path = "/proxy/hosts",
                            Name = "代理主机",
                            Key = "proxy-hosts",
                            Icon = "cloud-server",
                        }
                    }
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
