using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ncpm.Services;

/// <summary>
/// Readiness probe for the control plane. Nginx is required; a temporarily
/// unavailable Docker daemon reports Degraded while keeping the panel alive.
/// </summary>
public class PanelHealthCheck : IHealthCheck
{
    private readonly ConfigService _configService;
    private readonly NginxService _nginxService;
    private readonly DockerService _dockerService;

    public PanelHealthCheck(
        ConfigService configService,
        NginxService nginxService,
        DockerService dockerService)
    {
        _configService = configService;
        _nginxService = nginxService;
        _dockerService = dockerService;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configService.LoadAppConfig();
            var nginx = await _nginxService.GetStubStatusAsync(cancellationToken);
            var dockerHosts = _dockerService.GetAllHosts().Where(host => host.IsEnabled).ToList();
            var dockerStatuses = _dockerService.GetAllHostStatuses();
            var connectedDockerHosts = dockerStatuses.Count(status => status.IsConnected);
            var data = new Dictionary<string, object>
            {
                ["configPath"] = _configService.ConfigPath,
                ["nginxActivePath"] = config.Nginx.ActivePath,
                ["nginxReachable"] = nginx != null,
                ["enabledDockerHosts"] = dockerHosts.Count,
                ["connectedDockerHosts"] = connectedDockerHosts
            };

            if (nginx == null)
                return HealthCheckResult.Unhealthy("Nginx control-plane endpoint is unreachable", data: data);
            if (dockerHosts.Count > 0 && connectedDockerHosts == 0)
                return HealthCheckResult.Degraded("Panel and Nginx are ready, but Docker is unavailable", data: data);

            return HealthCheckResult.Healthy("Panel, Nginx and Docker are ready", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Readiness check failed", ex);
        }
    }
}
