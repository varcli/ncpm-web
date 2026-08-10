using System.Security.Cryptography;

namespace Ncpm.Data;

#region Docker Models

public enum DockerHostType
{
    Local,
    Tcp,
    Ssh
}

public class DockerHost
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public DockerHostType Type { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastChecked { get; set; }
    public bool IsConnected { get; set; }
    public int ContainerCount { get; set; }

    // TCP settings
    public int TcpPort { get; set; } = 2375;
    public bool UseTls { get; set; }

    // SSH settings
    public int SshPort { get; set; } = 22;
    public string? SshUser { get; set; }
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }

    public string ConnectionString
    {
        get => Type switch
        {
            DockerHostType.Local => Host,
            DockerHostType.Tcp => UseTls ? $"https://{Host}:{TcpPort}" : $"tcp://{Host}:{TcpPort}",
            DockerHostType.Ssh => $"ssh://{SshUser}@{Host}:{SshPort}",
            _ => Host
        };
        set { } // Allow YAML deserialization to set without storing
    }
}

public class DockerHostStatus
{
    public string HostId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public int ContainerCount { get; set; }
    public int RunningContainers { get; set; }
    public string? DockerVersion { get; set; }
    public string? OsType { get; set; }
    public string? Architecture { get; set; }
    public long TotalMemory { get; set; }
    public int CpuCount { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? Error { get; set; }
}

public class DockerContainer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<PortMapping> Ports { get; set; } = new();
    public string HostId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;

    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => State.Equals("exited", StringComparison.OrdinalIgnoreCase) ||
                             State.Equals("stopped", StringComparison.OrdinalIgnoreCase);
}

public class PortMapping
{
    public string? Ip { get; set; }
    public int PrivatePort { get; set; }
    public int? PublicPort { get; set; }
    public string? Type { get; set; }
}

public class DockerImage
{
    public string Id { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public long Size { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public string HostId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;

    public string SizeFormatted
    {
        get
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            int order = 0;
            double size = Size;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}

#endregion

#region Proxy Models

public enum TlsMode
{
    Off,
    Manual,
    Acme
}

public class ProxyHostConfig
{
    public string Id { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "1.0";
    public bool Enabled { get; set; } = true;
    public ProxyScheme Scheme { get; set; } = ProxyScheme.Http;
    public List<string> Hosts { get; set; } = new();
    public TlsHostConfig Tls { get; set; } = new();
    public HttpHostConfig Http { get; set; } = new();
    public List<UpstreamConfig> Upstreams { get; set; } = new();
    public LoadBalanceConfig LoadBalance { get; set; } = new();
    public List<RouteRule> Rules { get; set; } = new();
    public LoggingHostConfig Logging { get; set; } = new();
    public MtlsConfig? Mtls { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public enum ProxyScheme
{
    Http,
    Https,
    Tcp,
    Udp,
    FileServer
}

public class TlsHostConfig
{
    public TlsMode Mode { get; set; }
    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
}

public class HttpHostConfig
{
    public bool RedirectToHttps { get; set; }
    public bool Websocket { get; set; }
    public bool PreserveHost { get; set; }
    public bool Buffering { get; set; }
    public string? BufferSize { get; set; }
    public string? ConnectTimeout { get; set; }
    public string? ResponseTimeout { get; set; }
    public string? SendTimeout { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    // File Server / SPA mode
    public string? Root { get; set; }
    public bool Spa { get; set; }
    public string? Index { get; set; } = "index.html";
    // Stream proxy
    public int? ListenPort { get; set; }
}

public class UpstreamConfig
{
    public string Url { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
    public bool IsBackup { get; set; }
}

public class LoadBalanceConfig
{
    public LoadBalanceMode Mode { get; set; } = LoadBalanceMode.RoundRobin;
    public bool Sticky { get; set; }
    public int StickyMaxAgeSeconds { get; set; } = 3600;
}

public enum LoadBalanceMode
{
    RoundRobin,
    LeastConn,
    IpHash
}

public class RouteRule
{
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();
}

public class RuleCondition
{
    public RuleMatchType Type { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Negate { get; set; }
}

public enum RuleMatchType
{
    Header,
    Query,
    Cookie,
    Method,
    Host,
    Path,
    RemoteIp
}

public class RuleAction
{
    public RuleActionType Type { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }
    public int? StatusCode { get; set; }
}

public enum RuleActionType
{
    Rewrite,
    Redirect,
    Error,
    SetHeader,
    RemoveHeader
}

public class LoggingHostConfig
{
    public bool AccessLog { get; set; } = true;
    public bool ErrorLog { get; set; } = true;
    public string? AccessLogPath { get; set; }
    public string? ErrorLogPath { get; set; }
}

#endregion

#region Certificate Models

public enum CertificateSource
{
    Manual,
    Acme,
    Imported
}

public class CertificateConfig
{
    public string Id { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
    public CertificateSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsExpired => DateTime.UtcNow > NotAfter;
    public bool IsExpiringSoon => !IsExpired && (NotAfter - DateTime.UtcNow).TotalDays < 30;
    public int DaysUntilExpiry => Math.Max(0, (int)(NotAfter - DateTime.UtcNow).TotalDays);
}

#endregion

#region App Configuration Models

public class AppConfig
{
    public PanelConfig Panel { get; set; } = new();
    public DockerConfig Docker { get; set; } = new();
    public NginxConfig Nginx { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public AclConfig Acl { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
    public NotificationConfig Notification { get; set; } = new();
    public OidcConfig? Oidc { get; set; }
}

public class OidcConfig
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string? RedirectUri { get; set; }
    public List<string> Scopes { get; set; } = new() { "openid", "profile", "email" };
    public List<string> AllowedUsers { get; set; } = new();
    public List<string> AllowedGroups { get; set; } = new();
}

public class PanelConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8098;
    public string BasePath { get; set; } = "/";
}

public class DockerConfig
{
    public string Host { get; set; } = "unix:///var/run/docker.sock";
    public int ApiTimeout { get; set; } = 30;
}

public class NginxConfig
{
    public string ConfigPath { get; set; } = "/etc/nginx";
    public string GeneratedPath { get; set; } = "data/nginx/generated";
    public string ActivePath { get; set; } = "data/nginx/active";

    /// <summary>
    /// Active directory for TCP/UDP hosts. Included from a top-level <c>stream</c>
    /// block, not from <c>http</c>, which would make the config invalid.
    /// </summary>
    public string StreamPath { get; set; } = "data/nginx/stream";
    public string CertPath { get; set; } = "data/certs";
    public string ExecutablePath { get; set; } = "nginx";
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string Path { get; set; } = "data/logs";
    public bool EnableConsole { get; set; } = true;
    public bool EnableFile { get; set; } = true;
    public int RetainDays { get; set; } = 30;
}

public class SecurityConfig
{
    public bool RequireAuth { get; set; } = true;
    public int SessionTimeout { get; set; } = 3600;
    public int MaxLoginAttempts { get; set; } = 5;
    public int LockoutDuration { get; set; } = 300;
}

#endregion

#region Health Check Models

public enum HealthStatus
{
    Healthy,
    Unhealthy,
    Starting,
    Error
}

public class HealthCheckConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int IntervalSeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 5;
    public int? ExpectedStatusCode { get; set; }
    public string? ExpectedBody { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

public class HealthCheckResult
{
    public string HostId { get; set; } = string.Empty;
    public HealthStatus Status { get; set; }
    public int StatusCode { get; set; }
    public long LatencyMs { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? Error { get; set; }
    public string? Detail { get; set; }
}

public class ProxyHostHealth
{
    public string HostId { get; set; } = string.Empty;
    public string? HostName { get; set; }
    public HealthStatus Status { get; set; }
    public DateTime? LastChecked { get; set; }
    public long LatencyMs { get; set; }
    public string? Detail { get; set; }
    public List<HealthCheckResult> History { get; set; } = new();
}

#endregion

#region Monitoring Models

public class SystemMetrics
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int ContainerCount { get; set; }
    public int RunningContainers { get; set; }
    public int ProxyHostCount { get; set; }
    public int HealthyHosts { get; set; }
    public int UnhealthyHosts { get; set; }
    public long MemoryUsed { get; set; }
}

public class ContainerMetrics
{
    public string ContainerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double CpuUsage { get; set; }
    public long MemoryUsage { get; set; }
    public long MemoryLimit { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}

public class RequestMetrics
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int TotalRequests { get; set; }
    public int ActiveConnections { get; set; }
    public double RequestsPerSecond { get; set; }
    public long AverageResponseTimeMs { get; set; }
}

#endregion

#region Auth Models

public enum UserRole
{
    Admin,
    User
}

public class User
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedOutUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsLockedOut => LockedOutUntil.HasValue && LockedOutUntil.Value > DateTime.UtcNow;
}

public class AuthToken
{
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public User? User { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class PasswordHelper
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, saltBytes, Iterations, HashAlgorithmName.SHA256, HashSize);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(saltBytes));
    }

    public static bool VerifyPassword(string password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
            return false;

        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);

            var computed = Rfc2898DeriveBytes.Pbkdf2(
                password, saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);

            // Fixed-time compare: string equality short-circuits on first difference.
            return CryptographicOperations.FixedTimeEquals(computed, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

#endregion

#region Container Log & Stats Models

public class ContainerLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Stream { get; set; } = "stdout"; // stdout or stderr
    public string Message { get; set; } = string.Empty;
}

public class ContainerStatsInfo
{
    public string ContainerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public ulong MemoryUsage { get; set; }
    public ulong MemoryLimit { get; set; }
    public double MemoryPercent { get; set; }
    public ulong NetworkRx { get; set; }
    public ulong NetworkTx { get; set; }
    public ulong BlockRead { get; set; }
    public ulong BlockWrite { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    public string MemoryUsageFormatted => FormatBytes(MemoryUsage);
    public string MemoryLimitFormatted => FormatBytes(MemoryLimit);
    public string NetworkRxFormatted => FormatBytes(NetworkRx);
    public string NetworkTxFormatted => FormatBytes(NetworkTx);

    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:0.##} {sizes[order]}";
    }
}

#endregion

#region ACME Certificate Models

public class AcmeConfig
{
    public string Email { get; set; } = string.Empty;
    public List<string> Domains { get; set; } = new();
    public AcmeChallengeType Challenge { get; set; } = AcmeChallengeType.Http01;
    public string? DnsProvider { get; set; }
    public Dictionary<string, string> DnsCredentials { get; set; } = new();
    public string CaDirUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";
    public string CertificateKeyType { get; set; } = "EC256";
    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
}

public enum AcmeChallengeType
{
    Http01,
    Dns01
}

public class DnsProviderInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> RequiredFields { get; set; } = new();
}

public class AcmeChallenge
{
    public string Token { get; set; } = string.Empty;
    public string KeyAuth { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

#endregion

#region ACL Models

public class AclConfig
{
    public string DefaultPolicy { get; set; } = "allow"; // allow or deny
    public bool AllowLocal { get; set; } = true;
    public List<AclRule> Allow { get; set; } = new();
    public List<AclRule> Deny { get; set; } = new();
    public bool EnableLogging { get; set; }
}

public class AclRule
{
    public AclMatchType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public enum AclMatchType
{
    Ip,
    Cidr
}

#endregion

#region Rate Limiting Models

public class RateLimitConfig
{
    public bool Enabled { get; set; }
    public int Average { get; set; } = 10; // requests per period
    public int Burst { get; set; } = 20;
    public int PeriodSeconds { get; set; } = 1;
}

#endregion

#region Notification Models

public class NotificationConfig
{
    public bool Enabled { get; set; }
    public List<NotifyProvider> Providers { get; set; } = new();
}

public class NotifyProvider
{
    public string Name { get; set; } = string.Empty;
    public NotifyProviderType Type { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? Topic { get; set; }
    public string? Method { get; set; } = "POST";
    public string? MimeType { get; set; } = "application/json";
    public bool IsEnabled { get; set; } = true;
}

public enum NotifyProviderType
{
    Webhook,
    Gotify,
    Ntfy
}

public class NotifyMessage
{
    public string Level { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>Delivery attempts so far; used to stop retrying a dead provider.</summary>
    public int Attempts { get; set; }
}

#endregion

#region mTLS Models

public class MtlsConfig
{
    public string? ProfileName { get; set; }
    public string CaCertPath { get; set; } = string.Empty;
    public bool RequireClientCert { get; set; }
}

public class MtlsProfile
{
    public string Name { get; set; } = string.Empty;
    public string CaCertPath { get; set; } = string.Empty;
    public bool RequireClientCert { get; set; } = true;
    public string? Description { get; set; }
}

#endregion
