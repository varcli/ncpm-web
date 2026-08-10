using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ncpm.Services;

/// <summary>
/// Liveness probe for the panel itself. Reports healthy when the control plane
/// can still read its own configuration; proxy-host health is reported separately
/// by <see cref="HealthCheckService"/>.
/// </summary>
public class PanelHealthCheck : IHealthCheck
{
    private readonly ConfigService _configService;

    public PanelHealthCheck(ConfigService configService)
    {
        _configService = configService;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configService.LoadAppConfig();
            var data = new Dictionary<string, object>
            {
                ["configPath"] = _configService.ConfigPath,
                ["nginxActivePath"] = config.Nginx.ActivePath
            };

            return Task.FromResult(HealthCheckResult.Healthy("Panel is running", data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Configuration is not readable", ex));
        }
    }
}
