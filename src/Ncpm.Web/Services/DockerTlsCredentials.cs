using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;

namespace Ncpm.Services;

/// <summary>
/// Docker.DotNet transport credentials for an HTTPS daemon. Supports a custom
/// CA and the client certificate pair required by Docker's verify mode.
/// </summary>
internal sealed class DockerTlsCredentials : Credentials
{
    private readonly X509Certificate2? _caCertificate;
    private readonly X509Certificate2? _clientCertificate;

    public DockerTlsCredentials(
        string? caCertificatePath,
        string? clientCertificatePath,
        string? clientKeyPath)
    {
        if (!string.IsNullOrWhiteSpace(caCertificatePath))
        {
            EnsureFileExists(caCertificatePath, "Docker TLS CA certificate");
            _caCertificate = X509CertificateLoader.LoadCertificateFromFile(caCertificatePath);
        }

        var hasClientCertificate = !string.IsNullOrWhiteSpace(clientCertificatePath);
        var hasClientKey = !string.IsNullOrWhiteSpace(clientKeyPath);
        if (hasClientCertificate != hasClientKey)
            throw new InvalidOperationException("Docker TLS client certificate and key must be configured together");

        if (hasClientCertificate)
        {
            EnsureFileExists(clientCertificatePath!, "Docker TLS client certificate");
            EnsureFileExists(clientKeyPath!, "Docker TLS client private key");
            _clientCertificate = X509Certificate2.CreateFromPemFile(clientCertificatePath!, clientKeyPath!);
        }
    }

    public override bool IsTlsCredentials() => true;

    public override HttpMessageHandler GetHandler(HttpMessageHandler innerHandler)
    {
        // Docker.DotNet's built-in transport has no public TLS client-certificate
        // hook. TCP/HTTPS hosts can use the platform handler directly.
        innerHandler.Dispose();
        var handler = new SocketsHttpHandler();
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        if (_clientCertificate != null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { _clientCertificate };
        if (_caCertificate != null)
            handler.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;

        return handler;
    }

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate == null || _caCertificate == null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;

        var serverCertificate = certificate as X509Certificate2;
        var ownsServerCertificate = serverCertificate == null;
        serverCertificate ??= X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(_caCertificate);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return customChain.Build(serverCertificate);
        }
        finally
        {
            if (ownsServerCertificate)
                serverCertificate.Dispose();
        }
    }

    private static void EnsureFileExists(string path, string description)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{description} was not found at '{path}'", path);
    }
}
