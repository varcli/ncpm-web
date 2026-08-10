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
    private readonly HttpClient _httpClient;
    private Timer? _checkTimer;
    private bool _disposed;

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
        LoadHealthConfigs();
        _checkTimer = new Timer(PerformHealthChecks, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _logger.LogInformation("Health check service started");
    }

    public void Stop()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
        _logger.LogInformation("Health check service stopped");
    }

    private void LoadHealthConfigs()
    {
        var hosts = _configService.LoadProxyHosts();
        foreach (var host in hosts)
        {
            if (host.Http.ConnectTimeout != null)
            {
                _healthConfigs[host.Id] = new HealthCheckConfig
                {
                    Enabled = host.Enabled,
                    Url = host.Upstreams.FirstOrDefault()?.Url ?? string.Empty,
                    IntervalSeconds = 30,
                    TimeoutSeconds = 5
                };
            }
        }
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
        // async void on a timer callback: an escaping exception would crash the
        // process, so everything is contained here.
        try
        {
            var tasks = _healthConfigs.Select(kvp => CheckHostHealth(kvp.Key, kvp.Value));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check sweep failed");
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
        _healthStatus.AddOrUpdate(hostId,
            new ProxyHostHealth
            {
                HostId = hostId,
                Status = result.Status,
                LastChecked = result.CheckedAt,
                LatencyMs = result.LatencyMs,
                Detail = result.Error ?? result.Detail,
                History = new List<HealthCheckResult> { result }
            },
            (key, existing) =>
            {
                var previousStatus = existing.Status;
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
            OnHealthStatusChanged -= NotifyStatusChange;
            _checkTimer?.Dispose();
            _checkTimer = null;
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
