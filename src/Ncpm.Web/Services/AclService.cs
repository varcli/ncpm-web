using System.Collections.Concurrent;
using System.Net;
using Ncpm.Data;

namespace Ncpm.Services;

public class AclService
{
    private readonly ConfigService _configService;
    private readonly ILogger<AclService> _logger;
    private readonly ConcurrentDictionary<string, bool> _resultCache = new();

    public AclService(ConfigService configService, ILogger<AclService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public bool IsAllowed(IPAddress clientIp)
    {
        var config = _configService.LoadAppConfig().Acl;

        // Loopback always allowed
        if (IPAddress.IsLoopback(clientIp))
            return true;

        // Private IPs
        if (config.AllowLocal && IsPrivateIp(clientIp))
            return true;

        // Check cache
        var cacheKey = $"{clientIp}:{config.DefaultPolicy}:{config.Allow.Count}:{config.Deny.Count}";
        if (_resultCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Check deny rules first
        foreach (var rule in config.Deny)
        {
            if (MatchesRule(clientIp, rule))
            {
                _resultCache[cacheKey] = false;
                LogAccess(clientIp, false, "denied by rule");
                return false;
            }
        }

        // Check allow rules
        foreach (var rule in config.Allow)
        {
            if (MatchesRule(clientIp, rule))
            {
                _resultCache[cacheKey] = true;
                LogAccess(clientIp, true, "allowed by rule");
                return true;
            }
        }

        // Default policy
        var allowed = config.DefaultPolicy.Equals("allow", StringComparison.OrdinalIgnoreCase);
        _resultCache[cacheKey] = allowed;
        LogAccess(clientIp, allowed, $"default policy: {config.DefaultPolicy}");
        return allowed;
    }

    public void ClearCache()
    {
        _resultCache.Clear();
    }

    private bool MatchesRule(IPAddress clientIp, AclRule rule)
    {
        return rule.Type switch
        {
            AclMatchType.Ip => IPAddress.TryParse(rule.Value, out var ruleIp) && clientIp.Equals(ruleIp),
            AclMatchType.Cidr => MatchesCidr(clientIp, rule.Value),
            _ => false
        };
    }

    private static bool MatchesCidr(IPAddress ip, string cidr)
    {
        try
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return false;

            var network = IPAddress.Parse(parts[0]);
            var prefixLength = int.Parse(parts[1]);

            var ipBytes = ip.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();

            if (ipBytes.Length != networkBytes.Length) return false;

            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (ipBytes[i] != networkBytes[i]) return false;
            }

            if (remainingBits > 0 && fullBytes < ipBytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((ipBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }

    private void LogAccess(IPAddress ip, bool allowed, string reason)
    {
        if (_configService.LoadAppConfig().Acl.EnableLogging)
        {
            _logger.LogInformation("ACL: {IP} {Result} ({Reason})", ip, allowed ? "allowed" : "denied", reason);
        }
    }
}
