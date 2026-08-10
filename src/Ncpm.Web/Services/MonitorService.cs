using System.Collections.Concurrent;
using System.Diagnostics;
using Ncpm.Data;

namespace Ncpm.Services;

public class MonitorService : IDisposable
{
    private readonly DockerService _dockerService;
    private readonly HealthCheckService _healthCheckService;
    private readonly ILogger<MonitorService> _logger;
    private readonly ConcurrentQueue<SystemMetrics> _systemMetrics = new();
    private readonly ConcurrentQueue<RequestMetrics> _requestMetrics = new();
    private Timer? _metricsTimer;
    private bool _disposed;

    public MonitorService(
        DockerService dockerService,
        HealthCheckService healthCheckService,
        ILogger<MonitorService> logger)
    {
        _dockerService = dockerService;
        _healthCheckService = healthCheckService;
        _logger = logger;
    }

    public void Start()
    {
        _metricsTimer = new Timer(CollectMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _logger.LogInformation("Monitor service started");
    }

    public void Stop()
    {
        _metricsTimer?.Dispose();
        _metricsTimer = null;
        _logger.LogInformation("Monitor service stopped");
    }

    private async void CollectMetrics(object? state)
    {
        // async void timer callback: everything stays inside the try.
        try
        {
            var metrics = new SystemMetrics();

            // Collect system metrics. GetCurrentProcess returns a disposable handle;
            // this runs every few seconds, so it has to be released.
            using (var process = Process.GetCurrentProcess())
            {
                metrics.MemoryUsed = process.WorkingSet64;
            }

            // Collect container metrics
            try
            {
                var containers = await _dockerService.ListContainersAsync();
                metrics.ContainerCount = containers.Count;
                metrics.RunningContainers = containers.Count(c => c.IsRunning);
            }
            catch
            {
                // Docker might not be available
            }

            // Collect health status
            var healthStatuses = _healthCheckService.GetAllHealthStatus();
            metrics.ProxyHostCount = healthStatuses.Count;
            metrics.HealthyHosts = healthStatuses.Count(h => h.Status == HealthStatus.Healthy);
            metrics.UnhealthyHosts = healthStatuses.Count(h => h.Status == HealthStatus.Unhealthy);

            _systemMetrics.Enqueue(metrics);

            // Keep only last 1000 metrics
            while (_systemMetrics.Count > 1000)
            {
                _systemMetrics.TryDequeue(out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect metrics");
        }
    }

    public SystemMetrics GetCurrentMetrics()
    {
        return _systemMetrics.LastOrDefault() ?? new SystemMetrics();
    }

    public List<SystemMetrics> GetMetricsHistory(int count = 100)
    {
        return _systemMetrics.TakeLast(count).ToList();
    }

    public List<ContainerMetrics> GetContainerMetrics()
    {
        // This would typically come from Docker stats API
        // For now, return empty list
        return new List<ContainerMetrics>();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _metricsTimer?.Dispose();
            _disposed = true;
        }
    }
}
