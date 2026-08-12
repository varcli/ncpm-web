using System.Text.RegularExpressions;
using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Thrown when a proxy host would produce unsafe or malformed Nginx configuration.
/// </summary>
public class NginxConfigValidationException : Exception
{
    public NginxConfigValidationException(string message) : base(message) { }
}

/// <summary>
/// Validates user-supplied values before they are interpolated into Nginx config.
/// Every field written into a generated .conf file passes through here first: an
/// unescaped <c>;</c>, <c>{</c> or newline would otherwise let a proxy-host form
/// inject arbitrary directives into the data plane.
/// </summary>
public static class NginxConfigValidator
{
    // Hostname, optional leading wildcard, or the catch-all "_".
    private static readonly Regex ServerNamePattern = new(
        @"^(_|\*\.[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?)*|[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?)*)$",
        RegexOptions.Compiled);

    // RFC 7230 token characters.
    private static readonly Regex HeaderNamePattern = new(
        @"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$",
        RegexOptions.Compiled);

    // host[:port] with no scheme, credentials or path.
    private static readonly Regex UpstreamAddressPattern = new(
        @"^(\[[0-9a-fA-F:]+\]|[a-zA-Z0-9]([a-zA-Z0-9\-._]*[a-zA-Z0-9])?)(:\d{1,5})?$",
        RegexOptions.Compiled);

    private static readonly char[] ConfigBreakers = [';', '{', '}', '\n', '\r', '#', '\\', '"', '\''];

    /// <summary>Validates a whole proxy host, throwing on the first problem found.</summary>
    public static void ValidateHost(ProxyHostConfig host)
    {
        if (string.IsNullOrWhiteSpace(host.Id))
            throw new NginxConfigValidationException("Proxy host id is required");

        ValidateIdentifier(host.Id, "proxy host id");

        if (host.Hosts.Count == 0)
            throw new NginxConfigValidationException("At least one server name is required");

        foreach (var serverName in host.Hosts)
        {
            ValidateServerName(serverName);
        }

        foreach (var header in host.Http.CustomHeaders)
        {
            ValidateHeaderName(header.Key);
        }

        foreach (var upstream in host.Upstreams)
        {
            ValidateUpstreamUrl(upstream.Url, host.Scheme);

            if (upstream.Weight < 1 || upstream.Weight > 1000)
                throw new NginxConfigValidationException(
                    $"Upstream weight must be between 1 and 1000, got {upstream.Weight}");
        }

        if (host.Http.ListenPort is { } port && (port < 1 || port > 65535))
            throw new NginxConfigValidationException($"Listen port out of range: {port}");

        if (host.Scheme is ProxyScheme.Tcp or ProxyScheme.Udp)
        {
            // Never guess a port for a stream host: a wrong guess silently binds
            // something the user did not ask for.
            if (host.Http.ListenPort is null)
                throw new NginxConfigValidationException(
                    "TCP/UDP proxy hosts require an explicit listen port");

            if (host.Upstreams.Count == 0)
                throw new NginxConfigValidationException(
                    "TCP/UDP proxy hosts require at least one upstream");
        }

        ValidateOptionalPath(host.Http.Root, "root");
        ValidateOptionalPath(host.Logging.AccessLogPath, "access log path");
        ValidateOptionalPath(host.Logging.ErrorLogPath, "error log path");
        ValidateOptionalPath(host.Mtls?.CaCertPath, "mTLS CA certificate path");
        ValidateOptionalPath(host.Tls.CertPath, "TLS certificate path");
        ValidateOptionalPath(host.Tls.KeyPath, "TLS private key path");

        if (host.Tls.Mode == TlsMode.Manual
            && (string.IsNullOrWhiteSpace(host.Tls.CertPath) || string.IsNullOrWhiteSpace(host.Tls.KeyPath)))
        {
            throw new NginxConfigValidationException(
                "Manual TLS requires both a certificate path and a private key path");
        }

        var upstreamSchemes = host.Upstreams
            .Select(upstream => Uri.TryCreate(upstream.Url, UriKind.Absolute, out var uri) ? uri.Scheme : string.Empty)
            .Where(scheme => !string.IsNullOrEmpty(scheme))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (upstreamSchemes.Count > 1)
            throw new NginxConfigValidationException("All upstreams must use the same URL scheme");

        if (!string.IsNullOrEmpty(host.Http.Index))
            ValidateNoBreakers(host.Http.Index, "index");

        if (!string.IsNullOrEmpty(host.Http.BufferSize))
            ValidateSizeOrTime(host.Http.BufferSize, "buffer size");

        ValidateOptionalTime(host.Http.ConnectTimeout, "connect timeout");
        ValidateOptionalTime(host.Http.ResponseTimeout, "response timeout");
        ValidateOptionalTime(host.Http.SendTimeout, "send timeout");

        foreach (var rule in host.Rules)
        {
            ValidateRule(rule);
        }

        if (host.Rules.Count(rule => rule.IsDefault) > 1)
            throw new NginxConfigValidationException("Only one default route rule is allowed");

        var duplicatePath = host.Rules
            .Where(rule => !rule.IsDefault)
            .Select(rule => rule.Conditions.Single(condition => condition.Type == RuleMatchType.Path).Value)
            .GroupBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicatePath is not null)
            throw new NginxConfigValidationException($"Duplicate route path '{duplicatePath}'");
    }

    /// <summary>Ids become file names and Nginx upstream identifiers.</summary>
    public static void ValidateIdentifier(string value, string field)
    {
        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9\-_]{0,63}$"))
            throw new NginxConfigValidationException(
                $"Invalid {field} '{value}': use letters, digits, '-' and '_' only (max 64 chars)");
    }

    public static void ValidateServerName(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new NginxConfigValidationException("Server name cannot be empty");

        if (serverName.Length > 253)
            throw new NginxConfigValidationException($"Server name too long: '{serverName}'");

        if (!ServerNamePattern.IsMatch(serverName))
            throw new NginxConfigValidationException($"Invalid server name '{serverName}'");
    }

    public static void ValidateHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new NginxConfigValidationException("Header name cannot be empty");

        if (!HeaderNamePattern.IsMatch(name))
            throw new NginxConfigValidationException($"Invalid header name '{name}'");
    }

    /// <summary>
    /// Escapes a value destined for a double-quoted Nginx string. Nginx variables
    /// such as <c>$host</c> stay intact; quotes and backslashes are escaped and
    /// control characters are stripped.
    /// </summary>
    public static string EscapeQuotedValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\0')
                continue;

            if (c is '"' or '\\')
                sb.Append('\\');

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static void ValidateUpstreamUrl(string url, ProxyScheme scheme)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new NginxConfigValidationException("Upstream address cannot be empty");

        ValidateNoBreakers(url, "upstream address");

        if (scheme is ProxyScheme.Tcp or ProxyScheme.Udp)
        {
            var address = StripScheme(url, "tcp://", "udp://");
            if (!UpstreamAddressPattern.IsMatch(address))
                throw new NginxConfigValidationException($"Invalid stream upstream address '{url}'");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new NginxConfigValidationException(
                $"Invalid upstream URL '{url}': an absolute http:// or https:// URL is required");

        if (uri.Scheme is not ("http" or "https"))
            throw new NginxConfigValidationException(
                $"Unsupported upstream scheme '{uri.Scheme}': only http and https are allowed");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new NginxConfigValidationException("Upstream URL must not contain credentials");
    }

    /// <summary>Validates the address form used inside an upstream block.</summary>
    public static string ToUpstreamAddress(string url)
    {
        var address = StripScheme(url, "http://", "https://", "tcp://", "udp://").TrimEnd('/');

        if (!UpstreamAddressPattern.IsMatch(address))
            throw new NginxConfigValidationException($"Invalid upstream address '{url}'");

        return address;
    }

    public static void ValidateRule(RouteRule rule)
    {
        if (!string.IsNullOrEmpty(rule.Name))
            ValidateNoBreakers(rule.Name, "rule name");

        var pathConditionCount = rule.Conditions.Count(condition => condition.Type == RuleMatchType.Path);
        if (rule.IsDefault && rule.Conditions.Count != 0)
            throw new NginxConfigValidationException("The default route rule cannot contain match conditions");
        if (!rule.IsDefault && (rule.Conditions.Count != 1 || pathConditionCount != 1))
            throw new NginxConfigValidationException("A non-default route rule requires exactly one path condition");

        foreach (var condition in rule.Conditions)
        {
            if (condition.Type != RuleMatchType.Path)
                throw new NginxConfigValidationException(
                    $"Route condition {condition.Type} is not supported yet; use a path condition");
            if (condition.Negate)
                throw new NginxConfigValidationException("Negated path conditions are not supported");

            ValidateLocationPath(condition.Value);
        }

        foreach (var action in rule.Actions)
        {
            ValidateAction(action);
        }
    }

    private static void ValidateAction(RuleAction action)
    {
        switch (action.Type)
        {
            case RuleActionType.Rewrite:
                if (string.IsNullOrEmpty(action.Key) || string.IsNullOrEmpty(action.Value))
                    throw new NginxConfigValidationException("Rewrite requires both a pattern and a replacement");
                ValidateNoBreakers(action.Key, "rewrite pattern");
                ValidateNoBreakers(action.Value, "rewrite replacement");
                break;

            case RuleActionType.Redirect:
                if (string.IsNullOrEmpty(action.Value))
                    throw new NginxConfigValidationException("Redirect requires a target");
                ValidateNoBreakers(action.Value, "redirect target");
                ValidateStatusCode(action.StatusCode);
                break;

            case RuleActionType.Error:
                ValidateStatusCode(action.StatusCode);
                break;

            case RuleActionType.SetHeader:
            case RuleActionType.RemoveHeader:
                if (string.IsNullOrEmpty(action.Key))
                    throw new NginxConfigValidationException("Header action requires a header name");
                ValidateHeaderName(action.Key);
                break;
        }
    }

    private static void ValidateStatusCode(int? statusCode)
    {
        if (statusCode is { } code && (code < 100 || code > 599))
            throw new NginxConfigValidationException($"Invalid HTTP status code: {code}");
    }

    public static void ValidateLocationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new NginxConfigValidationException("Location path cannot be empty");

        ValidateNoBreakers(path, "location path");

        if (path.Any(char.IsWhiteSpace))
            throw new NginxConfigValidationException($"Location path must not contain whitespace: '{path}'");
    }

    private static void ValidateOptionalPath(string? path, string field)
    {
        if (string.IsNullOrEmpty(path))
            return;

        ValidateNoBreakers(path, field);

        if (path.Any(char.IsWhiteSpace))
            throw new NginxConfigValidationException($"{field} must not contain whitespace: '{path}'");
    }

    private static void ValidateOptionalTime(string? value, string field)
    {
        if (string.IsNullOrEmpty(value))
            return;

        ValidateSizeOrTime(value, field);
    }

    /// <summary>Accepts Nginx size/time literals such as <c>16k</c>, <c>60s</c>, <c>5m</c>.</summary>
    private static void ValidateSizeOrTime(string value, string field)
    {
        if (!Regex.IsMatch(value, @"^\d+(ms|[smhdwMy]|[kKmMgG])?$"))
            throw new NginxConfigValidationException($"Invalid {field} '{value}'");
    }

    private static void ValidateNoBreakers(string value, string field)
    {
        var index = value.IndexOfAny(ConfigBreakers);
        if (index >= 0)
            throw new NginxConfigValidationException(
                $"{field} contains a character that is not allowed in Nginx configuration: '{value[index]}'");
    }

    private static string StripScheme(string url, params string[] schemes)
    {
        foreach (var scheme in schemes)
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return url[scheme.Length..];
        }

        return url;
    }
}
