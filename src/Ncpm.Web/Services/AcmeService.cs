using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Certes;
using Certes.Acme;
using Certes.Pkcs;
using Ncpm.Data;

namespace Ncpm.Services;

public class AcmeService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly NginxService _nginxService;
    private readonly AcmeDnsProviderService _dnsProviderService;
    private readonly NotificationService _notificationService;
    private readonly ILogger<AcmeService> _logger;
    private readonly SemaphoreSlim _renewalGate = new(1, 1);
    private Timer? _renewalTimer;
    private bool _started;
    private bool _disposed;

    public string? LastError { get; private set; }

    public event Action<string, string>? OnCertificateIssued;
    public event Action<string, string>? OnCertificateFailed;
    public event Action<string>? OnRenewalTriggered;

    public AcmeService(
        ConfigService configService,
        NginxService nginxService,
        AcmeDnsProviderService dnsProviderService,
        NotificationService notificationService,
        ILogger<AcmeService> logger)
    {
        _configService = configService;
        _nginxService = nginxService;
        _dnsProviderService = dnsProviderService;
        _notificationService = notificationService;
        _logger = logger;

    }

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AcmeService));
        if (_started)
            return;

        _started = true;
        _renewalTimer = new Timer(async void (_) =>
        {
            if (!await _renewalGate.WaitAsync(0))
                return;

            try
            {
                await CheckAndRenewCertificates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during certificate renewal sweep");
            }
            finally
            {
                _renewalGate.Release();
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));

        _logger.LogInformation("ACME renewal service started");
    }

    public IReadOnlyList<DnsProviderInfo> GetDnsProviders() => AcmeDnsProviderService.Providers;

    public async Task<bool> IssueCertificateAsync(AcmeConfig acmeConfig, CancellationToken cancellationToken = default)
    {
        var challengeFiles = new List<string>();
        string? certFile = null;
        string? keyFile = null;
        string? previousCertPem = null;
        string? previousKeyPem = null;
        List<CertificateConfig>? previousCertificates = null;
        var certificateTransactionPrepared = false;
        var activationCommitted = false;
        try
        {
            LastError = null;
            acmeConfig.Domains = NormalizeDomains(acmeConfig.Domains);
            ValidateOrder(acmeConfig);
            _logger.LogInformation("Starting ACME certificate issuance for {Domains}", string.Join(", ", acmeConfig.Domains));

            var certPath = acmeConfig.CertPath ?? _configService.LoadAppConfig().Nginx.CertPath;
            Directory.CreateDirectory(certPath);
            previousCertificates = _configService.LoadCertificates();
            var existing = FindCertificate(acmeConfig.Domains, previousCertificates);
            var certificateId = existing?.Id ?? Guid.NewGuid().ToString("N")[..12];
            certFile = Path.Combine(certPath, $"{certificateId}.fullchain.pem");
            keyFile = Path.Combine(certPath, $"{certificateId}.key");
            previousCertPem = File.Exists(certFile)
                ? await File.ReadAllTextAsync(certFile, cancellationToken)
                : null;
            previousKeyPem = File.Exists(keyFile)
                ? await File.ReadAllTextAsync(keyFile, cancellationToken)
                : null;
            certificateTransactionPrepared = true;

            string certPem;
            string keyPem;
            if (acmeConfig.Challenge == AcmeChallengeType.Dns01)
            {
                var dnsResult = await _dnsProviderService.IssueAsync(
                    acmeConfig,
                    certFile,
                    keyFile,
                    cancellationToken);
                certPem = dnsResult.FullChainPem;
                keyPem = dnsResult.PrivateKeyPem;
            }
            else
            {
                var dirUri = new Uri(acmeConfig.CaDirUrl);
                var accountKey = await GetOrCreateAccountKey();
                var acme = new AcmeContext(dirUri, accountKey);
                await acme.NewAccount(acmeConfig.Email, true);
                var orderCtx = await acme.NewOrder(acmeConfig.Domains.ToArray());
                var authorizations = await orderCtx.Authorizations();

                foreach (var auth in authorizations)
                {
                    var httpChallenge = await auth.Http();
                    if (httpChallenge == null)
                        throw new InvalidOperationException("HTTP challenge not available");

                    var challengePath = Path.Combine(
                        _configService.DataPath,
                        "certbot",
                        ".well-known",
                        "acme-challenge");
                    Directory.CreateDirectory(challengePath);
                    var challengeFile = Path.Combine(challengePath, httpChallenge.Token);
                    await File.WriteAllTextAsync(challengeFile, httpChallenge.KeyAuthz, cancellationToken);
                    challengeFiles.Add(challengeFile);
                    await httpChallenge.Validate();
                }

                var csrBuilder = new CertificationRequestBuilder(
                    KeyFactory.NewKey(GetCertKeyType(acmeConfig.CertificateKeyType)));
                csrBuilder.AddName($"CN={acmeConfig.Domains.First()}");
                foreach (var domain in acmeConfig.Domains)
                    csrBuilder.SubjectAlternativeNames.Add(domain);

                var csr = csrBuilder.Generate();
                await orderCtx.Finalize(csr);
                certPem = (await orderCtx.Download()).ToPem();
                keyPem = csrBuilder.Key.ToPem();
            }

            // Reject malformed output and mismatched key pairs before they can
            // replace the currently served certificate.
            using (var certificateWithKey = X509Certificate2.CreateFromPem(certPem, keyPem))
            {
                if (!certificateWithKey.HasPrivateKey)
                    throw new InvalidOperationException("The issued certificate does not match its private key");
            }

            await WriteAtomicallyAsync(certFile, certPem, cancellationToken);
            await WriteAtomicallyAsync(keyFile, keyPem, cancellationToken, restrictToOwner: true);

            string? dnsSecretId = null;
            if (acmeConfig.Challenge == AcmeChallengeType.Dns01)
            {
                dnsSecretId = certificateId;
            }

            _logger.LogInformation("Certificate issued successfully for {Domains}", string.Join(", ", acmeConfig.Domains));
            UpdateCertificateConfig(acmeConfig, certificateId, certFile, keyFile, certPem, dnsSecretId);

            // A renewal replaces files in place. Validate the complete active tree
            // and reload so workers start serving the new certificate immediately.
            if (!await _nginxService.ValidateConfigAsync(cancellationToken)
                || !await _nginxService.ReloadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Certificate was saved, but Nginx could not activate it: {_nginxService.LastError}");
            }

            // Persist DNS credentials only after Nginx accepted the new config.
            // If this fails, the catch block restores the old files/config and
            // reloads Nginx, leaving the last known-good deployment active.
            if (dnsSecretId != null)
            {
                await _dnsProviderService.SaveCredentialsAsync(
                    dnsSecretId,
                    acmeConfig.DnsProvider!,
                    acmeConfig.DnsCredentials,
                    cancellationToken);
            }

            activationCommitted = true;
            if (acmeConfig.Challenge != AcmeChallengeType.Dns01
                && !string.IsNullOrWhiteSpace(existing?.DnsCredentialsSecretId))
            {
                try
                {
                    _dnsProviderService.DeleteCredentials(existing.DnsCredentialsSecretId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to remove obsolete DNS credentials for certificate {CertificateId}", certificateId);
                }
            }

            OnCertificateIssued?.Invoke(acmeConfig.Domains.First(), certFile);

            await _notificationService.SendAsync(
                "证书签发成功",
                $"域名 {string.Join(", ", acmeConfig.Domains)} 的证书已签发。",
                "info",
                new Dictionary<string, string> { ["domains"] = string.Join(", ", acmeConfig.Domains) });

            return true;
        }
        catch (Exception ex)
        {
            if (certificateTransactionPrepared && !activationCommitted)
            {
                await RollbackCertificateActivationAsync(
                    certFile!,
                    keyFile!,
                    previousCertPem,
                    previousKeyPem,
                    previousCertificates!);
            }

            LastError = ex.Message;
            var domains = string.Join(", ", acmeConfig.Domains);
            _logger.LogError(ex, "Failed to issue certificate for {Domains}", domains);
            OnCertificateFailed?.Invoke(acmeConfig.Domains.FirstOrDefault() ?? "(none)", ex.Message);

            await _notificationService.SendAsync(
                "证书签发失败",
                $"域名 {domains} 签发失败：{ex.Message}",
                "error",
                new Dictionary<string, string> { ["domains"] = domains, ["error"] = ex.Message });

            return false;
        }
        finally
        {
            foreach (var file in challengeFiles)
            {
                try { File.Delete(file); }
                catch { /* Challenge cleanup is best effort. */ }
            }
        }
    }

    public async Task<bool> RenewCertificateAsync(string domain, CancellationToken cancellationToken = default)
    {
        try
        {
            LastError = null;
            var certificates = _configService.LoadCertificates();
            var cert = certificates.FirstOrDefault(c => c.Domain == domain);
            if (cert == null || cert.Source != CertificateSource.Acme)
            {
                LastError = "Certificate was not found or is not managed by ACME";
                _logger.LogWarning("Cannot renew certificate for {Domain}: not found or not ACME", domain);
                return false;
            }

            OnRenewalTriggered?.Invoke(domain);

            var acmeConfig = new AcmeConfig
            {
                Email = cert.AcmeEmail ?? string.Empty,
                Domains = cert.Domains.Count > 0 ? cert.Domains.ToList() : [domain],
                Challenge = cert.AcmeChallenge ?? AcmeChallengeType.Http01,
                DnsProvider = cert.DnsProvider,
                DnsPropagationSeconds = cert.DnsPropagationSeconds,
                CaDirUrl = cert.AcmeCaDirUrl ?? "https://acme-v02.api.letsencrypt.org/directory",
                CertificateKeyType = cert.CertificateKeyType ?? "EC256",
                CertPath = Path.GetDirectoryName(cert.CertPath),
                KeyPath = Path.GetDirectoryName(cert.KeyPath)
            };

            if (acmeConfig.Challenge == AcmeChallengeType.Dns01)
            {
                if (string.IsNullOrWhiteSpace(cert.DnsProvider))
                    throw new InvalidOperationException("DNS provider metadata is missing; issue the certificate again");
                acmeConfig.DnsCredentials = await _dnsProviderService.LoadCredentialsAsync(
                    cert.DnsCredentialsSecretId ?? cert.Id,
                    cert.DnsProvider,
                    cancellationToken);
            }

            return await IssueCertificateAsync(acmeConfig, cancellationToken);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to renew certificate for {Domain}", domain);
            OnCertificateFailed?.Invoke(domain, ex.Message);
            await _notificationService.SendAsync(
                "证书续期失败",
                $"域名 {domain} 续期失败：{ex.Message}",
                "error",
                new Dictionary<string, string> { ["domain"] = domain, ["error"] = ex.Message });
            return false;
        }
    }

    private async Task<IKey> GetOrCreateAccountKey()
    {
        var keyPath = Path.Combine(_configService.SecretsPath, "acme-account.key");
        var legacyKeyPath = Path.Combine(_configService.ConfigPath, "acme-account.key");

        if (File.Exists(keyPath))
        {
            RestrictFileToOwner(keyPath);
            var keyPem = await File.ReadAllTextAsync(keyPath);
            return KeyFactory.FromPem(keyPem);
        }

        if (File.Exists(legacyKeyPath))
        {
            var legacyKeyPem = await File.ReadAllTextAsync(legacyKeyPath);
            await WriteAtomicallyAsync(keyPath, legacyKeyPem, restrictToOwner: true);
            File.Delete(legacyKeyPath);
            return KeyFactory.FromPem(legacyKeyPem);
        }

        var newKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        await WriteAtomicallyAsync(keyPath, newKey.ToPem(), restrictToOwner: true);
        return newKey;
    }

    private KeyAlgorithm GetCertKeyType(string? keyType) => keyType?.ToUpperInvariant() switch
    {
        "EC256" => KeyAlgorithm.ES256,
        "EC384" => KeyAlgorithm.ES384,
        "EC512" => KeyAlgorithm.ES512,
        "RS256" => KeyAlgorithm.RS256,
        _ => KeyAlgorithm.ES256
    };

    private void UpdateCertificateConfig(
        AcmeConfig acmeConfig,
        string certificateId,
        string certFile,
        string keyFile,
        string certPem,
        string? dnsSecretId)
    {
        var certificates = _configService.LoadCertificates();
        var existing = FindCertificate(acmeConfig.Domains, certificates);

        using var parsed = X509Certificate2.CreateFromPem(certPem);

        var certConfig = new CertificateConfig
        {
            Id = certificateId,
            Domain = acmeConfig.Domains.First(),
            Domains = acmeConfig.Domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Issuer = parsed.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            NotBefore = parsed.NotBefore.ToUniversalTime(),
            NotAfter = parsed.NotAfter.ToUniversalTime(),
            CertPath = certFile,
            KeyPath = keyFile,
            Source = CertificateSource.Acme,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            AcmeEmail = acmeConfig.Email,
            AcmeCaDirUrl = acmeConfig.CaDirUrl,
            AcmeChallenge = acmeConfig.Challenge,
            CertificateKeyType = acmeConfig.CertificateKeyType,
            DnsProvider = acmeConfig.DnsProvider,
            DnsCredentialsSecretId = dnsSecretId,
            DnsPropagationSeconds = acmeConfig.DnsPropagationSeconds
        };

        if (existing != null)
        {
            var index = certificates.IndexOf(existing);
            certificates[index] = certConfig;
        }
        else
        {
            certificates.Add(certConfig);
        }

        _configService.SaveCertificates(certificates);
    }

    public void DeleteCertificateSecrets(CertificateConfig certificate)
    {
        if (!string.IsNullOrWhiteSpace(certificate.DnsCredentialsSecretId))
            _dnsProviderService.DeleteCredentials(certificate.DnsCredentialsSecretId);
    }

    private CertificateConfig? FindCertificate(IReadOnlyCollection<string> domains) =>
        FindCertificate(domains, _configService.LoadCertificates());

    private static CertificateConfig? FindCertificate(
        IReadOnlyCollection<string> domains,
        IEnumerable<CertificateConfig> certificates)
    {
        var requested = domains.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return certificates.FirstOrDefault(c =>
            requested.Contains(c.Domain)
            || c.Domains.Any(requested.Contains));
    }

    private static void ValidateOrder(AcmeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Email)
            || !System.Net.Mail.MailAddress.TryCreate(config.Email, out _))
            throw new InvalidOperationException("ACME account email is required");
        if (config.Domains.Count == 0 || config.Domains.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("At least one valid domain is required");
        if (config.Challenge == AcmeChallengeType.Http01
            && config.Domains.Any(domain => domain.StartsWith("*.", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Wildcard certificates require DNS-01; HTTP-01 cannot validate wildcards");
        }
        if (config.Challenge == AcmeChallengeType.Dns01 && string.IsNullOrWhiteSpace(config.DnsProvider))
            throw new InvalidOperationException("A DNS provider is required for DNS-01");
        if (config.DnsPropagationSeconds is < 0 or > 3600)
            throw new InvalidOperationException("DNS propagation wait must be between 0 and 3600 seconds");
    }

    private static List<string> NormalizeDomains(IEnumerable<string> domains)
    {
        var idn = new System.Globalization.IdnMapping();
        var normalized = new List<string>();
        foreach (var rawDomain in domains)
        {
            var domain = rawDomain.Trim().TrimEnd('.');
            var wildcard = domain.StartsWith("*.", StringComparison.Ordinal);
            var host = wildcard ? domain[2..] : domain;
            if (host.Contains('*'))
                throw new InvalidOperationException($"Invalid wildcard domain '{rawDomain}'");

            string asciiHost;
            try
            {
                asciiHost = idn.GetAscii(host).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"Invalid domain '{rawDomain}'");
            }

            if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
                throw new InvalidOperationException($"Invalid DNS domain '{rawDomain}'");

            normalized.Add(wildcard ? $"*.{asciiHost}" : asciiHost);
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain.StartsWith("*.", StringComparison.Ordinal))
            .ThenBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default,
        bool restrictToOwner = false)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            if (restrictToOwner)
                RestrictFileToOwner(temp);
            File.Move(temp, path, overwrite: true);
            if (restrictToOwner)
                RestrictFileToOwner(path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private async Task RollbackCertificateActivationAsync(
        string certFile,
        string keyFile,
        string? previousCertPem,
        string? previousKeyPem,
        List<CertificateConfig> previousCertificates)
    {
        try
        {
            if (previousCertPem == null)
                File.Delete(certFile);
            else
                await WriteAtomicallyAsync(certFile, previousCertPem, CancellationToken.None);

            if (previousKeyPem == null)
                File.Delete(keyFile);
            else
                await WriteAtomicallyAsync(
                    keyFile,
                    previousKeyPem,
                    CancellationToken.None,
                    restrictToOwner: true);

            _configService.SaveCertificates(previousCertificates);
            var valid = await _nginxService.ValidateConfigAsync(CancellationToken.None);
            if (valid)
                await _nginxService.ReloadAsync(CancellationToken.None);

            _logger.LogWarning("Rolled back failed certificate activation to the last known-good state");
        }
        catch (Exception rollbackException)
        {
            _logger.LogCritical(
                rollbackException,
                "Certificate activation failed and automatic rollback was unsuccessful");
        }
    }

    private static void RestrictFileToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some bind-mounted filesystems do not expose Unix permission changes.
        }
    }

    private async Task CheckAndRenewCertificates()
    {
        try
        {
            var certificates = _configService.LoadCertificates();
            foreach (var cert in certificates.Where(c => c.Source == CertificateSource.Acme && c.IsExpiringSoon))
            {
                _logger.LogInformation("Certificate for {Domain} expiring in {Days} days, renewing...",
                    cert.Domain, cert.DaysUntilExpiry);
                await RenewCertificateAsync(cert.Domain);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check/renew certificates");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _renewalTimer?.Dispose();
            _disposed = true;
        }
    }
}
