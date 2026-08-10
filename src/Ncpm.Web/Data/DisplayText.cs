using System.Text.RegularExpressions;

namespace Ncpm.Data;

/// <summary>
/// Chinese display text for values that reach the UI in English: enum names and
/// the free-form state/status strings the Docker API returns.
/// </summary>
public static class DisplayText
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // "Up 2 hours", "Up 5 seconds (healthy)", "Up 3 days (health: starting)"
    private static readonly Regex UpPattern =
        new(@"^Up\s+(?<dur>.+?)(?:\s*\((?<health>[^)]*)\))?$", Opts);

    // "Exited (0) 3 minutes ago", "Restarting (1) 2 seconds ago"
    private static readonly Regex ExitPattern =
        new(@"^(?<verb>Exited|Restarting)\s*\((?<code>-?\d+)\)(?:\s+(?<dur>.+?))?(?:\s+ago)?$", Opts);

    private static readonly Regex DurationPattern =
        new(@"^(?<n>\d+)\s+(?<unit>second|minute|hour|day|week|month|year)s?$", Opts);

    /// <summary>Docker's container <c>State</c> field: running, exited, paused, ...</summary>
    public static string ContainerState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "created" => "已创建",
        "running" => "运行中",
        "paused" => "已暂停",
        "restarting" => "重启中",
        "removing" => "删除中",
        "exited" => "已退出",
        "stopped" => "已停止",
        "dead" => "已失效",
        null or "" => "未知",
        _ => state!
    };

    /// <summary>
    /// Docker's human-readable <c>Status</c> field. Falls back to the original
    /// string when the shape is not one Docker is known to produce.
    /// </summary>
    public static string ContainerStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "-";

        var value = status.Trim();

        var up = UpPattern.Match(value);
        if (up.Success)
        {
            var text = $"已运行{Join(Duration(up.Groups["dur"].Value))}";
            return up.Groups["health"].Success
                ? $"{text}（{HealthSuffix(up.Groups["health"].Value)}）"
                : text;
        }

        var exit = ExitPattern.Match(value);
        if (exit.Success)
        {
            var verb = exit.Groups["verb"].Value.Equals("Exited", StringComparison.OrdinalIgnoreCase)
                ? "已退出"
                : "重启中";
            var text = $"{verb}（代码 {exit.Groups["code"].Value}）";
            var duration = exit.Groups["dur"];
            return duration.Success ? $"{text}{Duration(duration.Value)}前" : text;
        }

        return value.ToLowerInvariant() switch
        {
            "created" => "已创建",
            "paused" => "已暂停",
            "dead" => "已失效",
            "removal in progress" => "删除中",
            _ => value
        };
    }

    /// <summary>
    /// Prefixes a space only when the fragment starts with a latin character or
    /// digit. Chinese needs no separator against Chinese, so "已运行约 1 分钟"
    /// reads correctly while "已运行 2 小时" keeps the space before the numeral.
    /// </summary>
    private static string Join(string fragment) =>
        fragment.Length > 0 && char.IsAscii(fragment[0]) ? $" {fragment}" : fragment;

    /// <summary>Translates the duration fragments produced by Docker's go-units humaniser.</summary>
    private static string Duration(string value)
    {
        var duration = value.Trim();

        if (duration.Equals("Less than a second", StringComparison.OrdinalIgnoreCase))
            return "不到 1 秒";
        if (duration.Equals("About a minute", StringComparison.OrdinalIgnoreCase))
            return "约 1 分钟";
        if (duration.Equals("About an hour", StringComparison.OrdinalIgnoreCase))
            return "约 1 小时";

        var match = DurationPattern.Match(duration);
        if (!match.Success)
            return duration;

        var unit = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "second" => "秒",
            "minute" => "分钟",
            "hour" => "小时",
            "day" => "天",
            "week" => "周",
            "month" => "个月",
            "year" => "年",
            _ => match.Groups["unit"].Value
        };
        return $"{match.Groups["n"].Value} {unit}";
    }

    /// <summary>The parenthesised health hint Docker appends to a running container's status.</summary>
    private static string HealthSuffix(string value) => value.Trim().ToLowerInvariant() switch
    {
        "healthy" => "健康",
        "unhealthy" => "异常",
        "health: starting" => "健康检查中",
        "paused" => "已暂停",
        _ => value.Trim()
    };

    public static string Health(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "健康",
        HealthStatus.Unhealthy => "异常",
        HealthStatus.Starting => "启动中",
        HealthStatus.Error => "检查失败",
        _ => status.ToString()
    };

    public static string CertificateSource(CertificateSource source) => source switch
    {
        Data.CertificateSource.Manual => "手动上传",
        Data.CertificateSource.Acme => "自动申请",
        Data.CertificateSource.Imported => "导入",
        _ => source.ToString()
    };

    public static string ProxyScheme(ProxyScheme scheme) => scheme switch
    {
        Data.ProxyScheme.Http => "HTTP",
        Data.ProxyScheme.Https => "HTTPS",
        Data.ProxyScheme.Tcp => "TCP",
        Data.ProxyScheme.Udp => "UDP",
        Data.ProxyScheme.FileServer => "静态文件",
        _ => scheme.ToString()
    };

    public static string TlsMode(TlsMode mode) => mode switch
    {
        Data.TlsMode.Off => "关闭",
        Data.TlsMode.Manual => "手动证书",
        Data.TlsMode.Acme => "ACME 自动",
        _ => mode.ToString()
    };

    public static string LoadBalanceMode(LoadBalanceMode mode) => mode switch
    {
        Data.LoadBalanceMode.RoundRobin => "轮询",
        Data.LoadBalanceMode.LeastConn => "最少连接",
        Data.LoadBalanceMode.IpHash => "IP 哈希",
        _ => mode.ToString()
    };

    public static string DockerHostType(DockerHostType type) => type switch
    {
        Data.DockerHostType.Local => "本地 Socket",
        Data.DockerHostType.Tcp => "TCP",
        Data.DockerHostType.Ssh => "SSH",
        _ => type.ToString()
    };

    public static string UserRole(UserRole role) => role switch
    {
        Data.UserRole.Admin => "管理员",
        Data.UserRole.User => "普通用户",
        _ => role.ToString()
    };
}
