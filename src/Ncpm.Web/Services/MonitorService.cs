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
    private readonly ConcurrentDictionary<string, ContainerMetrics> _containerMetrics = new();
    private Timer? _metricsTimer;
    private int _collecting;
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
        if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
            return;

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

                var running = containers.Where(c => c.IsRunning).ToList();
                var activeKeys = running.Select(c => $"{c.HostId}:{c.Id}").ToHashSet(StringComparer.Ordinal);
                foreach (var stale in _containerMetrics.Keys.Where(k => !activeKeys.Contains(k)))
                    _containerMetrics.TryRemove(stale, out _);

                await Parallel.ForEachAsync(
                    running,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    async (container, cancellationToken) =>
                    {
                        var stats = await _dockerService.GetContainerStatsAsync(
                            container.Id,
                            container.HostId,
                            cancellationToken);
                        if (stats == null)
                            return;

                        _containerMetrics[$"{container.HostId}:{container.Id}"] = new ContainerMetrics
                        {
                            ContainerId = container.Id,
                            Name = string.IsNullOrWhiteSpace(stats.Name) ? container.Name : stats.Name,
                            HostId = container.HostId,
                            HostName = container.HostName,
                            CpuUsage = stats.CpuPercent,
                            MemoryUsage = ToInt64(stats.MemoryUsage),
                            MemoryLimit = ToInt64(stats.MemoryLimit),
                            CollectedAt = stats.CollectedAt
                        };
                    });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Container metrics are temporarily unavailable");
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
        finally
        {
            Volatile.Write(ref _collecting, 0);
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
        return _containerMetrics.Values
            .OrderBy(x => x.HostName, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static long ToInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    public void Dispose()
    {
        if (!_disposed)
        {
            _metricsTimer?.Dispose();
            _disposed = true;
        }
    }
}
