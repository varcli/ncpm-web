using System.Diagnostics;
using System.Text;
using Ncpm.Data;

namespace Ncpm.Services;

public class NginxService
{
    private readonly ConfigService _configService;
    private readonly ILogger<NginxService> _logger;

    public NginxService(ConfigService configService, ILogger<NginxService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    // Read per call: capturing this in the constructor pinned the path for the
    // process lifetime, so edits in Settings never took effect.
    private string NginxExecutable => _configService.LoadAppConfig().Nginx.ExecutablePath;

    /// <summary>Last validation or reload error, surfaced to the UI.</summary>
    public string? LastError { get; private set; }

    public async Task<bool> GenerateConfigAsync(ProxyHostConfig host, CancellationToken cancellationToken = default)
    {
        try
        {
            // Reject unsafe input before it reaches a generated file.
            NginxConfigValidator.ValidateHost(host);

            var config = _configService.LoadAppConfig();
            var generatedPath = config.Nginx.GeneratedPath;
            var configContent = GenerateNginxConfig(host);
            var fileName = $"{host.Id}.conf";
            var filePath = Path.Combine(generatedPath, fileName);

            Directory.CreateDirectory(generatedPath);
            await File.WriteAllTextAsync(filePath, configContent, cancellationToken);

            _logger.LogInformation("Generated Nginx config for {Host}", host.Id);
            LastError = null;
            return true;
        }
        catch (NginxConfigValidationException ex)
        {
            LastError = ex.Message;
            _logger.LogWarning("Rejected invalid config for {Host}: {Reason}", host.Id, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to generate Nginx config for {Host}", host.Id);
            return false;
        }
    }

    public async Task<bool> ValidateConfigAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, output) = await RunNginxAsync("-t", cancellationToken);

        if (exitCode != 0)
        {
            LastError = string.IsNullOrWhiteSpace(output) ? "nginx -t failed" : output;
            _logger.LogError("Nginx config validation failed: {Output}", output);
            return false;
        }

        _logger.LogInformation("Nginx config validation passed");
        return true;
    }

    public async Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, output) = await RunNginxAsync("-s reload", cancellationToken);

        if (exitCode != 0)
        {
            LastError = string.IsNullOrWhiteSpace(output) ? "nginx reload failed" : output;
            _logger.LogError("Nginx reload failed: {Output}", output);
            return false;
        }

        _logger.LogInformation("Nginx reloaded successfully");
        return true;
    }

    /// <summary>Runs the nginx binary and returns its exit code plus stderr text.</summary>
    private async Task<(int ExitCode, string Output)> RunNginxAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = NginxExecutable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read both pipes before waiting: a full stderr buffer would otherwise deadlock.
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            var stdout = await stdoutTask;

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (process.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to run nginx {Arguments}", arguments);
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Publishes a single proxy host: generate candidate, stage it into the active
    /// directory, validate the resulting tree with <c>nginx -t</c>, then reload.
    /// The previous file is restored if validation or reload fails, so a bad host
    /// can never take the data plane down.
    /// </summary>
    public async Task<bool> PublishConfigAsync(ProxyHostConfig host, CancellationToken cancellationToken = default)
    {
        var config = _configService.LoadAppConfig();

        if (!await GenerateConfigAsync(host, cancellationToken))
            return false;

        var activeDir = ActiveDirectoryFor(host, config.Nginx);
        var sourceFile = Path.Combine(config.Nginx.GeneratedPath, $"{host.Id}.conf");
        var targetFile = Path.Combine(activeDir, $"{host.Id}.conf");

        Directory.CreateDirectory(activeDir);

        // Keep the currently active file so a failed validation can be undone.
        // nginx -t inspects what is on disk, so the candidate has to be staged first.
        string? previousContent = File.Exists(targetFile)
            ? await File.ReadAllTextAsync(targetFile, cancellationToken)
            : null;

        try
        {
            File.Copy(sourceFile, targetFile, true);

            if (!await ValidateConfigAsync(cancellationToken))
            {
                await RestoreAsync(targetFile, previousContent, cancellationToken);
                _logger.LogError("Publish aborted for {Host}: nginx -t rejected the new config", host.Id);
                return false;
            }

            if (!await ReloadAsync(cancellationToken))
            {
                await RestoreAsync(targetFile, previousContent, cancellationToken);
                await ReloadAsync(cancellationToken);
                _logger.LogError("Publish rolled back for {Host}: reload failed", host.Id);
                return false;
            }

            _logger.LogInformation("Published Nginx config for {Host}", host.Id);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to publish Nginx config for {Host}", host.Id);
            await RestoreAsync(targetFile, previousContent, cancellationToken);
            return false;
        }
    }

    private static async Task RestoreAsync(string targetFile, string? previousContent, CancellationToken cancellationToken)
    {
        if (previousContent == null)
        {
            if (File.Exists(targetFile))
                File.Delete(targetFile);
        }
        else
        {
            await File.WriteAllTextAsync(targetFile, previousContent, cancellationToken);
        }
    }

    public bool DeleteConfig(string hostId)
    {
        try
        {
            var config = _configService.LoadAppConfig();

            // The host may have been either an http or a stream host, so clear both.
            string[] candidates =
            [
                Path.Combine(config.Nginx.ActivePath, $"{hostId}.conf"),
                Path.Combine(config.Nginx.StreamPath, $"{hostId}.conf"),
                Path.Combine(config.Nginx.GeneratedPath, $"{hostId}.conf")
            ];

            foreach (var file in candidates.Where(File.Exists))
            {
                File.Delete(file);
            }

            _logger.LogInformation("Deleted Nginx config for {Host}", hostId);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to delete Nginx config for {Host}", hostId);
            return false;
        }
    }

    private string GenerateNginxConfig(ProxyHostConfig host)
    {
        var sb = new StringBuilder();
        var config = _configService.LoadAppConfig();

        sb.AppendLine($"# Auto-generated by Ncpm for {host.Id}");
        sb.AppendLine($"# Schema: {host.SchemaVersion}");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // TCP/UDP stream proxy - use nginx stream block
        if (host.Scheme == ProxyScheme.Tcp || host.Scheme == ProxyScheme.Udp)
        {
            return GenerateStreamConfig(host);
        }

        // HTTP-to-HTTPS redirect block
        if (host.Tls.Mode != Data.TlsMode.Off && host.Http.RedirectToHttps)
        {
            sb.AppendLine("server {");
            sb.AppendLine("    listen 80;");
            sb.AppendLine($"    server_name {string.Join(" ", host.Hosts)};");
            sb.AppendLine();
            sb.AppendLine("    location /.well-known/acme-challenge/ {");
            sb.AppendLine("        root /var/www/certbot;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    location / {");
            sb.AppendLine("        return 301 https://$host$request_uri;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Upstream block (for load balancing)
        if (host.Upstreams.Count > 1)
        {
            sb.AppendLine($"upstream {host.Id}_backend {{");

            // Load balance mode
            switch (host.LoadBalance.Mode)
            {
                case LoadBalanceMode.LeastConn:
                    sb.AppendLine("    least_conn;");
                    break;
                case LoadBalanceMode.IpHash:
                    sb.AppendLine("    ip_hash;");
                    break;
                // RoundRobin is default, no directive needed
            }

            foreach (var upstream in host.Upstreams)
            {
                var address = NginxConfigValidator.ToUpstreamAddress(upstream.Url);
                var backup = upstream.IsBackup ? " backup" : "";
                var weight = upstream.Weight > 1 ? $" weight={upstream.Weight}" : "";
                sb.AppendLine($"    server {address}{weight}{backup};");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Main server block
        sb.AppendLine("server {");

        // Listen directive
        if (host.Tls.Mode != Data.TlsMode.Off)
        {
            sb.AppendLine("    listen 443 ssl;");
            sb.AppendLine("    listen [::]:443 ssl;");
            sb.AppendLine("    http2 on;");
        }
        else
        {
            var port = host.Http.ListenPort ?? 80;
            sb.AppendLine($"    listen {port};");
            sb.AppendLine($"    listen [::]:{port};");
        }

        sb.AppendLine($"    server_name {string.Join(" ", host.Hosts)};");
        sb.AppendLine();

        // SSL configuration
        if (host.Tls.Mode != Data.TlsMode.Off)
        {
            sb.AppendLine($"    ssl_certificate {Path.Combine(config.Nginx.CertPath, $"{host.Id}.crt")};");
            sb.AppendLine($"    ssl_certificate_key {Path.Combine(config.Nginx.CertPath, $"{host.Id}.key")};");
            sb.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
            sb.AppendLine("    ssl_ciphers HIGH:!aNULL:!MD5;");
            sb.AppendLine("    ssl_prefer_server_ciphers on;");

            // mTLS
            if (host.Mtls != null)
            {
                sb.AppendLine($"    ssl_client_certificate {host.Mtls.CaCertPath};");
                sb.AppendLine(host.Mtls.RequireClientCert
                    ? "    ssl_verify_client on;"
                    : "    ssl_verify_client optional;");
            }

            sb.AppendLine();
        }

        // Custom headers
        if (host.Http.CustomHeaders.Any())
        {
            foreach (var header in host.Http.CustomHeaders)
            {
                var value = NginxConfigValidator.EscapeQuotedValue(header.Value);
                sb.AppendLine($"    proxy_set_header {header.Key} \"{value}\";");
            }
            sb.AppendLine();
        }

        // Route rules - generate location blocks with conditions
        if (host.Rules.Any())
        {
            foreach (var rule in host.Rules.Where(r => !r.IsDefault))
            {
                GenerateRuleLocationBlock(sb, host, rule);
            }
        }

        // File Server / SPA mode
        if (host.Scheme == ProxyScheme.FileServer)
        {
            var root = host.Http.Root ?? "/var/www/html";
            sb.AppendLine("    location / {");
            sb.AppendLine($"        root {root};");

            if (host.Http.Spa)
            {
                sb.AppendLine("        try_files $uri $uri/ /index.html;");
            }
            else
            {
                sb.AppendLine($"        try_files $uri $uri/ =404;");
                sb.AppendLine($"        index {host.Http.Index ?? "index.html"};");
            }

            sb.AppendLine("    }");
        }
        // Default proxy location (or default rule fallback)
        else if (host.Upstreams.Any())
        {
            var defaultRule = host.Rules.FirstOrDefault(r => r.IsDefault);
            sb.AppendLine("    location / {");

            // Apply default rule actions first
            if (defaultRule != null)
            {
                foreach (var action in defaultRule.Actions)
                {
                    GenerateRuleAction(sb, action, "        ");
                }
            }

            // Proxy pass - use upstream block if multiple, direct if single
            if (host.Upstreams.Count > 1)
            {
                sb.AppendLine($"        proxy_pass http://{host.Id}_backend;");
            }
            else
            {
                sb.AppendLine($"        proxy_pass {host.Upstreams[0].Url};");
            }

            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");

            // WebSocket
            if (host.Http.Websocket)
            {
                sb.AppendLine("        proxy_http_version 1.1;");
                sb.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                sb.AppendLine("        proxy_set_header Connection \"upgrade\";");
            }

            // Timeouts
            if (!string.IsNullOrEmpty(host.Http.ConnectTimeout))
                sb.AppendLine($"        proxy_connect_timeout {host.Http.ConnectTimeout};");
            if (!string.IsNullOrEmpty(host.Http.ResponseTimeout))
                sb.AppendLine($"        proxy_read_timeout {host.Http.ResponseTimeout};");
            if (!string.IsNullOrEmpty(host.Http.SendTimeout))
                sb.AppendLine($"        proxy_send_timeout {host.Http.SendTimeout};");

            // Buffering
            if (host.Http.Buffering)
            {
                sb.AppendLine("        proxy_buffering on;");
                if (!string.IsNullOrEmpty(host.Http.BufferSize))
                    sb.AppendLine($"        proxy_buffer_size {host.Http.BufferSize};");
            }

            sb.AppendLine("    }");
        }

        // Logging
        sb.AppendLine();
        if (host.Logging.AccessLog)
        {
            var logPath = host.Logging.AccessLogPath ?? $"logs/{host.Id}-access.log";
            sb.AppendLine($"    access_log {logPath};");
        }
        if (host.Logging.ErrorLog)
        {
            var logPath = host.Logging.ErrorLogPath ?? $"logs/{host.Id}-error.log";
            sb.AppendLine($"    error_log {logPath};");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Emits the contents of a stream host without the enclosing <c>stream { }</c>
    /// block. The wrapper lives in nginx.conf, which includes this file from the
    /// top level; emitting our own wrapper here would nest stream inside http.
    /// </summary>
    private string GenerateStreamConfig(ProxyHostConfig host)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Stream proxy for {host.Id}");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"upstream {host.Id}_stream_backend {{");

        foreach (var upstream in host.Upstreams)
        {
            var addr = NginxConfigValidator.ToUpstreamAddress(upstream.Url);
            var weight = upstream.Weight > 1 ? $" weight={upstream.Weight}" : "";
            sb.AppendLine($"    server {addr}{weight};");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("server {");

        // Guaranteed present: NginxConfigValidator rejects stream hosts without one.
        var listenPort = host.Http.ListenPort!.Value;
        var proto = host.Scheme == ProxyScheme.Udp ? " udp" : "";
        sb.AppendLine($"    listen {listenPort}{proto};");
        sb.AppendLine($"    proxy_pass {host.Id}_stream_backend;");

        if (host.Logging.ErrorLog)
        {
            var logPath = host.Logging.ErrorLogPath ?? $"logs/{host.Id}-error.log";
            sb.AppendLine($"    error_log {logPath};");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>True when a host belongs in the stream include directory.</summary>
    private static bool IsStreamHost(ProxyHostConfig host) =>
        host.Scheme is ProxyScheme.Tcp or ProxyScheme.Udp;

    private static string ActiveDirectoryFor(ProxyHostConfig host, NginxConfig config) =>
        IsStreamHost(host) ? config.StreamPath : config.ActivePath;

    private void GenerateRuleLocationBlock(StringBuilder sb, ProxyHostConfig host, RouteRule rule)
    {
        // Build location condition from rule
        var path = rule.Conditions.FirstOrDefault(c => c.Type == RuleMatchType.Path);
        var locationPath = path?.Value ?? "/";

        sb.AppendLine($"    # Rule: {rule.Name}");
        sb.AppendLine($"    location {locationPath} {{");

        // Generate rule actions (rewrite, redirect, error, headers)
        foreach (var action in rule.Actions)
        {
            GenerateRuleAction(sb, action, "        ");
        }

        // If no terminate action, proxy to upstream
        if (!rule.Actions.Any(a => a.Type == RuleActionType.Redirect || a.Type == RuleActionType.Error))
        {
            if (host.Upstreams.Count > 1)
                sb.AppendLine($"        proxy_pass http://{host.Id}_backend;");
            else if (host.Upstreams.Any())
                sb.AppendLine($"        proxy_pass {host.Upstreams[0].Url};");

            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");

            if (host.Http.Websocket)
            {
                sb.AppendLine("        proxy_http_version 1.1;");
                sb.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                sb.AppendLine("        proxy_set_header Connection \"upgrade\";");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private void GenerateRuleAction(StringBuilder sb, RuleAction action, string indent)
    {
        switch (action.Type)
        {
            case RuleActionType.Rewrite:
                if (!string.IsNullOrEmpty(action.Key) && !string.IsNullOrEmpty(action.Value))
                    sb.AppendLine($"{indent}rewrite {action.Key} {action.Value} break;");
                break;
            case RuleActionType.Redirect:
                var code = action.StatusCode ?? 302;
                var target = NginxConfigValidator.EscapeQuotedValue(action.Value);
                sb.AppendLine($"{indent}return {code} \"{target}\";");
                break;
            case RuleActionType.Error:
                var errCode = action.StatusCode ?? 502;
                sb.AppendLine($"{indent}return {errCode};");
                break;
            case RuleActionType.SetHeader:
                if (!string.IsNullOrEmpty(action.Key))
                {
                    var headerValue = NginxConfigValidator.EscapeQuotedValue(action.Value);
                    sb.AppendLine($"{indent}proxy_set_header {action.Key} \"{headerValue}\";");
                }
                break;
            case RuleActionType.RemoveHeader:
                if (!string.IsNullOrEmpty(action.Key))
                    sb.AppendLine($"{indent}proxy_set_header {action.Key} \"\";");
                break;
        }
    }
}
