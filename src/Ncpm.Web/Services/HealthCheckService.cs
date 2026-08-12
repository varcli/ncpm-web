using System.Collections.Concurrent;
using System.Diagnostics;
using Ncpm.Data;

namespace Ncpm.Services;

public class HealthCheckService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly NotificationService _notificationService;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly ConcurrentDictionary<string, ProxyHostHealth> _healthStatus = new();
    private readonly ConcurrentDictionary<string, HealthCheckConfig> _healthConfigs = new();

    // Display name per host id, so the health page can show the domain rather
    // than the opaque id.
    private readonly ConcurrentDictionary<string, string> _hostNames = new();

    // When each host was last probed, so per-host intervals are honoured instead
    // of probing everything on the sweep timer's cadence.
    private readonly ConcurrentDictionary<string, DateTime> _lastProbe = new();

    private readonly HttpClient _httpClient;
    private Timer? _checkTimer;
    private int _checking;
    private bool _disposed;

    // Set whenever configuration changes; the next sweep rebuilds the probe set.
    // Starts at 1 so the first sweep loads it. Reloading directly from the config
    // watcher would re-read every file several times per save.
    private int _reloadRequested = 1;

    public event Action<string, HealthCheckResult>? OnHealthCheckCompleted;
    public event Action<string, HealthStatus>? OnHealthStatusChanged;

    public HealthCheckService(
        ConfigService configService,
        NotificationService notificationService,
        ILogger<HealthCheckService> logger)
    {
        _configService = configService;
        _notificationService = notificationService;
        _logger = logger;
        _httpClient = new HttpClient();

        // Alert on transitions rather than on every failed probe.
        OnHealthStatusChanged += NotifyStatusChange;
    }

    private void NotifyStatusChange(string hostId, HealthStatus status)
    {
        var (level, title) = status switch
        {
            HealthStatus.Healthy => ("info", "上游已恢复"),
            HealthStatus.Unhealthy => ("warning", "上游不可用"),
            HealthStatus.Error => ("error", "健康检查错误"),
            _ => ("info", "上游状态变化")
        };

        var detail = _healthStatus.TryGetValue(hostId, out var health) ? health.Detail : null;

        _ = _notificationService.SendAsync(
            title,
            $"代理主机 {hostId} 状态变为 {status}。{detail}".TrimEnd(),
            level,
            new Dictionary<string, string> { ["hostId"] = hostId, ["status"] = status.ToString() });
    }

    public void Start()
    {
        _configService.OnConfigChanged += RequestReload;
        _checkTimer = new Timer(PerformHealthChecks, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _logger.LogInformation("Health check service started");
    }

    public void Stop()
    {
        _configService.OnConfigChanged -= RequestReload;
        _checkTimer?.Dispose();
        _checkTimer = null;
        _logger.LogInformation("Health check service stopped");
    }

    private void RequestReload() => Interlocked.Exchange(ref _reloadRequested, 1);

    /// <summary>
    /// Rebuilds the probe set from the current proxy hosts, manual and discovered.
    /// This runs on every config change: it used to run once at startup, so a host
    /// added afterwards was never probed.
    /// </summary>
    private void LoadHealthConfigs()
    {
        var hosts = _configService.LoadProxyHosts()
            .Concat(_configService.LoadDiscoveredProxyHosts())
            .ToList();

        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            var probe = BuildProbe(host);
            if (probe == null)
                continue;

            _healthConfigs[host.Id] = probe;
            _hostNames[host.Id] = host.Hosts.FirstOrDefault() ?? host.Id;
            live.Add(host.Id);
        }

        // Forget hosts that no longer exist, or the health page lists them forever.
        foreach (var stale in _healthConfigs.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _healthConfigs.TryRemove(stale, out _);
            _healthStatus.TryRemove(stale, out _);
            _hostNames.TryRemove(stale, out _);
            _lastProbe.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// A host is probed when it is enabled and has an HTTP upstream. Absent
    /// configuration means "probe the root path" — a proxy host pointing at a dead
    /// upstream is exactly what this is for. TCP/UDP and file-server hosts have
    /// nothing to probe over HTTP and are skipped.
    /// </summary>
    private static HealthCheckConfig? BuildProbe(ProxyHostConfig host)
    {
        if (!host.Enabled)
            return null;

        if (host.Scheme is not (ProxyScheme.Http or ProxyScheme.Https))
            return null;

        var upstream = host.Upstreams.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(upstream))
            return null;

        var configured = host.HealthCheck;
        if (configured is { Enabled: false })
            return null;

        return new HealthCheckConfig
        {
            Enabled = true,
            Url = CombineUrl(upstream, configured?.Path),
            Method = configured?.Method ?? "GET",
            IntervalSeconds = Math.Max(5, configured?.IntervalSeconds ?? 30),
            TimeoutSeconds = Math.Max(1, configured?.TimeoutSeconds ?? 5),
            ExpectedStatusCode = configured?.ExpectedStatusCode,
            ExpectedBody = configured?.ExpectedBody,
            Headers = configured?.Headers ?? new()
        };
    }

    private static string CombineUrl(string upstream, string? path)
    {
        var baseUrl = upstream.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return baseUrl + "/";

        return path.StartsWith('/') ? baseUrl + path : $"{baseUrl}/{path}";
    }

    public ProxyHostHealth? GetHealthStatus(string hostId)
    {
        return _healthStatus.TryGetValue(hostId, out var health) ? health : null;
    }

    public List<ProxyHostHealth> GetAllHealthStatus()
    {
        return _healthStatus.Values.ToList();
    }

    private async void PerformHealthChecks(object? state)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            return;

        // async void on a timer callback: an escaping exception would crash the
        // process, so everything is contained here.
        try
        {
            if (Interlocked.Exchange(ref _reloadRequested, 0) == 1)
            {
                LoadHealthConfigs();
            }

            // The timer ticks faster than most intervals, so only probe hosts that
            // are actually due; otherwise every host would be hit every 10s no
            // matter what its interval says.
            var now = DateTime.UtcNow;
            var due = _healthConfigs
                .Where(kvp => !_lastProbe.TryGetValue(kvp.Key, out var last)
                              || (now - last).TotalSeconds >= kvp.Value.IntervalSeconds)
                .ToList();

            foreach (var kvp in due)
            {
                _lastProbe[kvp.Key] = now;
            }

            await Task.WhenAll(due.Select(kvp => CheckHostHealth(kvp.Key, kvp.Value)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check sweep failed");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private async Task CheckHostHealth(string hostId, HealthCheckConfig config)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.Url))
            return;

        var stopwatch = Stopwatch.StartNew();
        var result = new HealthCheckResult
        {
            HostId = hostId,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(config.Method), config.Url);
            
            foreach (var header in config.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            stopwatch.Stop();
            result.StatusCode = (int)response.StatusCode;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;

            if (config.ExpectedStatusCode.HasValue)
            {
                result.Status = result.StatusCode == config.ExpectedStatusCode.Value
                    ? HealthStatus.Healthy
                    : HealthStatus.Unhealthy;
            }
            else
            {
                result.Status = response.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Unhealthy;
            }

            if (config.ExpectedBody != null)
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!body.Contains(config.ExpectedBody))
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Detail = "Expected body content not found";
                }
            }
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            result.Status = HealthStatus.Unhealthy;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
            result.Error = "Health check timed out";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Status = HealthStatus.Error;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
            result.Error = ex.Message;
        }

        UpdateHealthStatus(hostId, result);
        OnHealthCheckCompleted?.Invoke(hostId, result);
    }

    private void UpdateHealthStatus(string hostId, HealthCheckResult result)
    {
        var hostName = _hostNames.GetValueOrDefault(hostId, hostId);

        _healthStatus.AddOrUpdate(hostId,
            new ProxyHostHealth
            {
                HostId = hostId,
                HostName = hostName,
                Status = result.Status,
                LastChecked = result.CheckedAt,
                LatencyMs = result.LatencyMs,
                Detail = result.Error ?? result.Detail,
                History = new List<HealthCheckResult> { result }
            },
            (key, existing) =>
            {
                var previousStatus = existing.Status;
                existing.HostName = hostName;
                existing.Status = result.Status;
                existing.LastChecked = result.CheckedAt;
                existing.LatencyMs = result.LatencyMs;
                existing.Detail = result.Error ?? result.Detail;
                existing.History.Add(result);
                
                // Keep only last 100 results
                if (existing.History.Count > 100)
                {
                    existing.History = existing.History.TakeLast(100).ToList();
                }

                if (previousStatus != result.Status)
                {
                    OnHealthStatusChanged?.Invoke(hostId, result.Status);
                }

                return existing;
            });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _configService.OnConfigChanged -= RequestReload;
            OnHealthStatusChanged -= NotifyStatusChange;
            _checkTimer?.Dispose();
            _checkTimer = null;
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
