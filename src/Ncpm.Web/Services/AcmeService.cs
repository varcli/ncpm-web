using System.Security.Cryptography;
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
    private readonly NotificationService _notificationService;
    private readonly ILogger<AcmeService> _logger;
    private Timer? _renewalTimer;
    private bool _disposed;

    public event Action<string, string>? OnCertificateIssued;
    public event Action<string, string>? OnCertificateFailed;
    public event Action<string>? OnRenewalTriggered;

    public static readonly List<DnsProviderInfo> SupportedDnsProviders = new()
    {
        new() { Name = "cloudflare", DisplayName = "Cloudflare", RequiredFields = ["api_token"] },
        new() { Name = "route53", DisplayName = "AWS Route53", RequiredFields = ["access_key", "secret_key", "region"] },
        new() { Name = "azure", DisplayName = "Azure DNS", RequiredFields = ["tenant_id", "client_id", "client_secret", "subscription_id"] },
        new() { Name = "google", DisplayName = "Google Cloud DNS", RequiredFields = ["project_id", "service_account_json"] },
        new() { Name = "digitalocean", DisplayName = "DigitalOcean", RequiredFields = ["api_token"] },
        new() { Name = "godaddy", DisplayName = "GoDaddy", RequiredFields = ["api_key", "api_secret"] },
        new() { Name = "namecheap", DisplayName = "Namecheap", RequiredFields = ["api_user", "api_key"] },
        new() { Name = "hetzner", DisplayName = "Hetzner", RequiredFields = ["api_token"] },
        new() { Name = "porkbun", DisplayName = "Porkbun", RequiredFields = ["api_key", "secret_key"] },
        new() { Name = "linode", DisplayName = "Linode", RequiredFields = ["api_token"] },
        new() { Name = "vultr", DisplayName = "Vultr", RequiredFields = ["api_key"] },
    };

    public AcmeService(
        ConfigService configService,
        NginxService nginxService,
        NotificationService notificationService,
        ILogger<AcmeService> logger)
    {
        _configService = configService;
        _nginxService = nginxService;
        _notificationService = notificationService;
        _logger = logger;

        // Timer callbacks must not surface exceptions: an unhandled one on a
        // thread-pool thread would take the process down.
        _renewalTimer = new Timer(async void (_) =>
        {
            try
            {
                await CheckAndRenewCertificates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during certificate renewal sweep");
            }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(12));
    }

    public List<DnsProviderInfo> GetDnsProviders() => SupportedDnsProviders;

    public async Task<bool> IssueCertificateAsync(AcmeConfig acmeConfig, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting ACME certificate issuance for {Domains}", string.Join(", ", acmeConfig.Domains));

            var dirUri = new Uri(acmeConfig.CaDirUrl);
            var accountKey = await GetOrCreateAccountKey();
            var acme = new AcmeContext(dirUri, accountKey);

            // Create account
            var account = await acme.NewAccount(acmeConfig.Email, true);

            // Create order via acme context
            var orderCtx = await acme.NewOrder(acmeConfig.Domains.ToArray());

            // Get authorizations
            var authorizations = await orderCtx.Authorizations();

            foreach (var auth in authorizations)
            {
                if (acmeConfig.Challenge == AcmeChallengeType.Http01)
                {
                    var httpChallenge = await auth.Http();
                    if (httpChallenge == null)
                        throw new InvalidOperationException("HTTP challenge not available");

                    var token = httpChallenge.Token;
                    var keyAuth = httpChallenge.KeyAuthz;

                    // Write challenge file
                    var challengePath = Path.Combine("data", "certbot", ".well-known", "acme-challenge");
                    Directory.CreateDirectory(challengePath);
                    await File.WriteAllTextAsync(Path.Combine(challengePath, token), keyAuth, cancellationToken);

                    // Validate
                    await httpChallenge.Validate();
                }
                else if (acmeConfig.Challenge == AcmeChallengeType.Dns01)
                {
                    var dnsChallenge = await auth.Dns();
                    if (dnsChallenge == null)
                        throw new InvalidOperationException("DNS challenge not available");

                    // DNS-01 record: _acme-challenge.{domain} = base64url(SHA256(keyAuthz))
                    var domain = acmeConfig.Domains.First();
                    var dnsRecord = $"_acme-challenge.{domain}";
                    using var sha256 = SHA256.Create();
                    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(dnsChallenge.KeyAuthz));
                    var dnsValue = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');

                    _logger.LogInformation("DNS challenge: add TXT record {Record} = {Value}", dnsRecord, dnsValue);

                    throw new InvalidOperationException(
                        $"DNS-01 challenge requires TXT record: {dnsRecord} = {dnsValue}\n" +
                        "Please add this record to your DNS provider, then retry.\n" +
                        $"Provider: {acmeConfig.DnsProvider ?? "manual"}");
                }
            }

            // Generate CSR
            var csrBuilder = new CertificationRequestBuilder();
            csrBuilder.AddName($"CN={acmeConfig.Domains.First()}");
            foreach (var domain in acmeConfig.Domains)
            {
                csrBuilder.SubjectAlternativeNames.Add(domain);
            }

            var csr = csrBuilder.Generate();

            // Finalize order
            await orderCtx.Finalize(csr);

            // Download certificate
            var certChain = await orderCtx.Download();
            var certPem = certChain.ToPem();

            // Save certificate
            var certPath = acmeConfig.CertPath ?? Path.Combine("data", "config", "certificates");
            Directory.CreateDirectory(certPath);

            var domainName = acmeConfig.Domains.First().Replace("*", "wildcard");
            var certFile = Path.Combine(certPath, $"{domainName}.pem");
            var keyFile = Path.Combine(certPath, $"{domainName}.key");

            await File.WriteAllTextAsync(certFile, certPem, cancellationToken);
            await File.WriteAllTextAsync(keyFile, csrBuilder.Key.ToPem(), cancellationToken);

            _logger.LogInformation("Certificate issued successfully for {Domains}", string.Join(", ", acmeConfig.Domains));

            await UpdateCertificateConfig(acmeConfig, certFile, keyFile);

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
            var domains = string.Join(", ", acmeConfig.Domains);
            _logger.LogError(ex, "Failed to issue certificate for {Domains}", domains);
            OnCertificateFailed?.Invoke(acmeConfig.Domains.First(), ex.Message);

            await _notificationService.SendAsync(
                "证书签发失败",
                $"域名 {domains} 签发失败：{ex.Message}",
                "error",
                new Dictionary<string, string> { ["domains"] = domains, ["error"] = ex.Message });

            return false;
        }
    }

    public async Task<bool> RenewCertificateAsync(string domain, CancellationToken cancellationToken = default)
    {
        try
        {
            var certificates = _configService.LoadCertificates();
            var cert = certificates.FirstOrDefault(c => c.Domain == domain);
            if (cert == null || cert.Source != CertificateSource.Acme)
            {
                _logger.LogWarning("Cannot renew certificate for {Domain}: not found or not ACME", domain);
                return false;
            }

            OnRenewalTriggered?.Invoke(domain);

            var acmeConfig = new AcmeConfig
            {
                Domains = [domain],
                CertPath = Path.GetDirectoryName(cert.CertPath),
                KeyPath = Path.GetDirectoryName(cert.KeyPath)
            };

            return await IssueCertificateAsync(acmeConfig, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew certificate for {Domain}", domain);
            return false;
        }
    }

    private async Task<IKey> GetOrCreateAccountKey()
    {
        var keyPath = Path.Combine("data", "config", "acme-account.key");

        if (File.Exists(keyPath))
        {
            var keyPem = await File.ReadAllTextAsync(keyPath);
            return KeyFactory.FromPem(keyPem);
        }

        var newKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        await File.WriteAllTextAsync(keyPath, newKey.ToPem());
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

    private async Task UpdateCertificateConfig(AcmeConfig acmeConfig, string certFile, string keyFile)
    {
        var certificates = _configService.LoadCertificates();
        var existing = certificates.FirstOrDefault(c => c.Domain == acmeConfig.Domains.First());

        var certConfig = new CertificateConfig
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Domain = acmeConfig.Domains.First(),
            Issuer = "Let's Encrypt",
            NotBefore = DateTime.UtcNow,
            NotAfter = DateTime.UtcNow.AddDays(90),
            CertPath = certFile,
            KeyPath = keyFile,
            Source = CertificateSource.Acme,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow
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
