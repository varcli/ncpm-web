using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Runs DNS-01 orders through acme.sh's maintained DNS provider adapters. API
/// credentials are passed through the child-process environment (never command
/// arguments) and encrypted at rest below ConfigService.SecretsPath for renewal.
/// </summary>
public sealed class AcmeDnsProviderService
{
    private readonly ConfigService _configService;
    private readonly IDataProtector _credentialProtector;
    private readonly ILogger<AcmeDnsProviderService> _logger;

    public static readonly IReadOnlyList<DnsProviderInfo> Providers =
    [
        Provider("cloudflare", "Cloudflare API Token", "dns_cf",
            Field("api_token", "API Token", "CF_Token")),
        Provider("alidns", "阿里云 DNS", "dns_ali",
            Field("access_key", "AccessKey ID", "Ali_Key"),
            Field("access_secret", "AccessKey Secret", "Ali_Secret")),
        Provider("dnspod", "DNSPod", "dns_dp",
            Field("api_id", "API ID", "DP_Id", secret: false),
            Field("api_token", "API Token", "DP_Key")),
        Provider("route53", "AWS Route 53", "dns_aws",
            Field("access_key", "Access Key ID", "AWS_ACCESS_KEY_ID"),
            Field("secret_key", "Secret Access Key", "AWS_SECRET_ACCESS_KEY"),
            Field("session_token", "Session Token（可选）", "AWS_SESSION_TOKEN", required: false)),
        Provider("azure", "Azure DNS", "dns_azure",
            Field("tenant_id", "Tenant ID", "AZUREDNS_TENANTID", secret: false),
            Field("client_id", "Client ID", "AZUREDNS_APPID", secret: false),
            Field("client_secret", "Client Secret", "AZUREDNS_CLIENTSECRET"),
            Field("subscription_id", "Subscription ID", "AZUREDNS_SUBSCRIPTIONID", secret: false)),
        Provider("digitalocean", "DigitalOcean", "dns_dgon",
            Field("api_token", "API Token", "DO_API_KEY")),
        Provider("godaddy", "GoDaddy", "dns_gd",
            Field("api_key", "API Key", "GD_Key"),
            Field("api_secret", "API Secret", "GD_Secret")),
        Provider("namecheap", "Namecheap", "dns_namecheap",
            Field("api_user", "API Username", "NAMECHEAP_USERNAME", secret: false),
            Field("api_key", "API Key", "NAMECHEAP_API_KEY"),
            Field("source_ip", "API 白名单公网 IP", "NAMECHEAP_SOURCEIP", secret: false)),
        Provider("porkbun", "Porkbun", "dns_porkbun",
            Field("api_key", "API Key", "PORKBUN_API_KEY"),
            Field("secret_key", "Secret API Key", "PORKBUN_SECRET_API_KEY")),
        Provider("linode", "Linode", "dns_linode_v4",
            Field("api_token", "API Token", "LINODE_V4_API_KEY")),
        Provider("vultr", "Vultr", "dns_vultr",
            Field("api_key", "API Key", "VULTR_API_KEY"))
    ];

    public AcmeDnsProviderService(
        ConfigService configService,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AcmeDnsProviderService> logger)
    {
        _configService = configService;
        _credentialProtector = dataProtectionProvider.CreateProtector("Ncpm.AcmeDnsCredentials.v1");
        _logger = logger;
    }

    public DnsProviderInfo GetProvider(string? name) =>
        Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unsupported DNS provider '{name}'");

    public async Task<AcmeDnsResult> IssueAsync(
        AcmeConfig config,
        string installedFullChainPath,
        string installedKeyPath,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(config.DnsProvider);
        ValidateCredentials(provider, config.DnsCredentials);

        var toolConfig = _configService.LoadAppConfig().Acme;
        if (string.IsNullOrWhiteSpace(toolConfig.ExecutablePath))
            throw new InvalidOperationException("ACME DNS executable path is not configured");
        if (!File.Exists(toolConfig.ExecutablePath))
            throw new FileNotFoundException(
                $"acme.sh was not found at '{toolConfig.ExecutablePath}'. DNS-01 requires the deployment image or a configured Acme.ExecutablePath.",
                toolConfig.ExecutablePath);

        var homePath = string.IsNullOrWhiteSpace(toolConfig.HomePath)
            ? Path.Combine(_configService.SecretsPath, "acme-sh")
            : toolConfig.HomePath;
        homePath = Path.GetFullPath(homePath);
        var certHome = Path.Combine(homePath, "certificates");
        var tempRoot = Path.GetFullPath(Path.Combine(_configService.SecretsPath, "acme-dns-tmp"));
        var tempDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homePath);
        Directory.CreateDirectory(certHome);
        Directory.CreateDirectory(tempDir);
        RestrictDirectory(homePath);
        RestrictDirectory(tempDir);

        var domains = NormalizeDomains(config.Domains);
        var mainDomain = domains[0];
        var environment = BuildEnvironment(provider, config.DnsCredentials);
        var redactions = environment.Values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        var common = new List<string>
        {
            "--home", homePath,
            "--config-home", homePath,
            "--cert-home", certHome
        };

        try
        {
            var issueArgs = new List<string>
            {
                "--issue",
                "--dns", provider.AcmeDnsApi,
                "--server", config.CaDirUrl,
                "--accountemail", config.Email,
                "--keylength", ToAcmeKeyLength(config.CertificateKeyType),
                "--force"
            };
            issueArgs.AddRange(common);
            if (config.DnsPropagationSeconds > 0)
            {
                issueArgs.Add("--dnssleep");
                issueArgs.Add(config.DnsPropagationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var domain in domains)
            {
                issueArgs.Add("-d");
                issueArgs.Add(domain);
            }

            await RunAsync(toolConfig, issueArgs, environment, redactions, cancellationToken);

            var keyFile = Path.Combine(tempDir, "certificate.key");
            var fullChainFile = Path.Combine(tempDir, "fullchain.pem");
            var installArgs = new List<string> { "--install-cert", "-d", mainDomain };
            installArgs.AddRange(common);
            if (IsEcc(config.CertificateKeyType))
                installArgs.Add("--ecc");
            installArgs.Add("--key-file");
            installArgs.Add(keyFile);
            installArgs.Add("--fullchain-file");
            installArgs.Add(fullChainFile);

            await RunAsync(toolConfig, installArgs, environment, redactions, cancellationToken);

            if (!File.Exists(keyFile) || !File.Exists(fullChainFile))
                throw new InvalidOperationException("acme.sh completed without producing the certificate and private key");

            // Persist stable install targets in acme.sh's domain metadata. On the
            // next renewal acme.sh may run its stored install action as part of
            // issuance, so these paths must not point at the temporary directory.
            Directory.CreateDirectory(Path.GetDirectoryName(installedFullChainPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(installedKeyPath)!);
            var stableInstallArgs = new List<string> { "--install-cert", "-d", mainDomain };
            stableInstallArgs.AddRange(common);
            if (IsEcc(config.CertificateKeyType))
                stableInstallArgs.Add("--ecc");
            stableInstallArgs.Add("--key-file");
            stableInstallArgs.Add(installedKeyPath);
            stableInstallArgs.Add("--fullchain-file");
            stableInstallArgs.Add(installedFullChainPath);
            await RunAsync(toolConfig, stableInstallArgs, environment, redactions, cancellationToken);
            RestrictFile(installedKeyPath);

            return new AcmeDnsResult(
                await File.ReadAllTextAsync(fullChainFile, cancellationToken),
                await File.ReadAllTextAsync(keyFile, cancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(tempRoot, tempDir);
        }
    }

    public async Task SaveCredentialsAsync(
        string certificateId,
        string provider,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken = default)
    {
        var directory = CredentialsDirectory();
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        var secret = new DnsCredentialSecret
        {
            Provider = provider,
            Credentials = GetProvider(provider).Fields
                .Where(field => credentials.TryGetValue(field.Name, out var value)
                    && !string.IsNullOrWhiteSpace(value))
                .ToDictionary(
                    field => field.Name,
                    field => credentials[field.Name],
                    StringComparer.Ordinal)
        };
        var target = CredentialPath(certificateId);
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var protectedPayload = _credentialProtector.Protect(JsonSerializer.Serialize(secret));
            await File.WriteAllTextAsync(temp, protectedPayload, cancellationToken);
            RestrictFile(temp);
            File.Move(temp, target, overwrite: true);
            RestrictFile(target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<Dictionary<string, string>> LoadCredentialsAsync(
        string certificateId,
        string expectedProvider,
        CancellationToken cancellationToken = default)
    {
        var path = CredentialPath(certificateId);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "DNS API credentials are missing. Open the certificate and issue it again to restore unattended renewal.");

        var payload = await File.ReadAllTextAsync(path, cancellationToken);
        var legacyPlaintext = payload.TrimStart().StartsWith('{');
        var json = legacyPlaintext ? payload : _credentialProtector.Unprotect(payload);
        var secret = JsonSerializer.Deserialize<DnsCredentialSecret>(json)
            ?? throw new InvalidOperationException("DNS credential secret is invalid");
        if (!string.Equals(secret.Provider, expectedProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DNS credential provider does not match the certificate metadata");

        if (legacyPlaintext)
        {
            await SaveCredentialsAsync(
                certificateId,
                secret.Provider,
                secret.Credentials,
                cancellationToken);
            _logger.LogInformation("Migrated legacy plaintext DNS credentials for certificate {CertificateId}", certificateId);
        }

        return secret.Credentials;
    }

    public void DeleteCredentials(string certificateId)
    {
        var path = CredentialPath(certificateId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task RunAsync(
        AcmeToolConfig config,
        IReadOnlyCollection<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyCollection<string> redactions,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment)
            startInfo.Environment[variable.Key] = variable.Value;

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(config.CommandTimeoutSeconds, 60, 3600)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start acme.sh");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var output = Redact($"{stderr}\n{stdout}".Trim(), redactions);
                if (output.Length > 4000)
                    output = output[^4000..];
                throw new InvalidOperationException(
                    $"acme.sh exited with code {process.ExitCode}: {output}");
            }

            _logger.LogInformation("acme.sh DNS operation completed successfully");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"acme.sh exceeded the {config.CommandTimeoutSeconds}-second timeout");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(
        DnsProviderInfo provider,
        IReadOnlyDictionary<string, string> credentials)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in provider.Fields)
        {
            if (credentials.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                environment[field.EnvironmentVariable] = value.Trim();
        }
        return environment;
    }

    private static void ValidateCredentials(
        DnsProviderInfo provider,
        IReadOnlyDictionary<string, string> credentials)
    {
        var missing = provider.Fields
            .Where(field => field.Required
                && (!credentials.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value)))
            .Select(field => field.DisplayName)
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"DNS provider {provider.DisplayName} requires: {string.Join(", ", missing)}");
    }

    private string CredentialsDirectory() =>
        Path.GetFullPath(Path.Combine(_configService.SecretsPath, "acme-dns"));

    private string CredentialPath(string certificateId) =>
        Path.Combine(CredentialsDirectory(), $"{SafeSecretId(certificateId)}.json");

    private static string SafeSecretId(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return value;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))[..24]
            .ToLowerInvariant();
    }

    private static List<string> NormalizeDomains(IEnumerable<string> domains)
    {
        var result = domains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Sort((left, right) =>
            left.StartsWith("*.", StringComparison.Ordinal).CompareTo(
                right.StartsWith("*.", StringComparison.Ordinal)));
        return result;
    }

    private static bool IsEcc(string? keyType) =>
        !string.Equals(keyType, "RS256", StringComparison.OrdinalIgnoreCase);

    private static string ToAcmeKeyLength(string? keyType) => keyType?.ToUpperInvariant() switch
    {
        "EC384" => "ec-384",
        "RS256" => "2048",
        _ => "ec-256"
    };

    private static string Redact(string value, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.OrderByDescending(secret => secret.Length))
        {
            if (secret.Length >= 4)
                value = value.Replace(secret, "***", StringComparison.Ordinal);
        }
        return value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort after cancellation or process startup failure.
        }
    }

    private static void DeleteTemporaryDirectory(string root, string directory)
    {
        try
        {
            var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedDirectory = Path.GetFullPath(directory);
            if (resolvedDirectory.StartsWith(resolvedRoot, StringComparison.Ordinal) && Directory.Exists(resolvedDirectory))
                Directory.Delete(resolvedDirectory, recursive: true);
        }
        catch
        {
            // The parent directory is mode 0700. Cleanup remains best effort so
            // an already-issued certificate is not reported as failed solely due
            // to a mounted filesystem refusing directory deletion.
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Some mounted filesystems do not expose Unix mode changes.
        }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some mounted filesystems do not expose Unix mode changes.
        }
    }

    private static DnsProviderInfo Provider(
        string name,
        string displayName,
        string acmeDnsApi,
        params DnsCredentialField[] fields) => new()
    {
        Name = name,
        DisplayName = displayName,
        AcmeDnsApi = acmeDnsApi,
        Fields = fields.ToList()
    };

    private static DnsCredentialField Field(
        string name,
        string displayName,
        string environmentVariable,
        bool required = true,
        bool secret = true,
        string? placeholder = null) => new()
    {
        Name = name,
        DisplayName = displayName,
        EnvironmentVariable = environmentVariable,
        Required = required,
        Secret = secret,
        Placeholder = placeholder
    };

    private sealed class DnsCredentialSecret
    {
        public string Provider { get; set; } = string.Empty;
        public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record AcmeDnsResult(string FullChainPem, string PrivateKeyPem);
