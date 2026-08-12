using Ncpm.Data;
using Ncpm.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using Xunit;

namespace Ncpm.Web.Tests;

public class CoreValidationTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoSpecialCharacter123")]
    public void PasswordPolicy_RejectsWeakPasswords(string password)
    {
        Assert.Throws<ArgumentException>(() => AuthService.ValidatePassword(password));
    }

    [Fact]
    public void PasswordPolicy_AcceptsStrongPassword()
    {
        AuthService.ValidatePassword("Strong-Password-123");
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("panel", "/panel")]
    [InlineData("/panel/", "/panel")]
    public void BasePath_IsNormalized(string? input, string expected)
    {
        Assert.Equal(expected, ConfigService.NormalizeBasePath(input));
    }

    [Fact]
    public void AppConfig_RejectsInvalidTrustedProxy()
    {
        var config = new AppConfig();
        config.Panel.TrustedProxies.Add("not-an-address");

        Assert.Throws<InvalidOperationException>(() => ConfigService.ValidateAppConfig(config));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("*.example.com")]
    [InlineData("_")]
    public void NginxServerName_AcceptsSafeValues(string value)
    {
        NginxConfigValidator.ValidateServerName(value);
    }

    [Theory]
    [InlineData("example.com; include /tmp/evil.conf")]
    [InlineData("example.com\nreturn 200")]
    [InlineData("https://example.com")]
    public void NginxServerName_RejectsDirectiveInjection(string value)
    {
        Assert.Throws<NginxConfigValidationException>(() =>
            NginxConfigValidator.ValidateServerName(value));
    }

    [Theory]
    [InlineData("wordpress", true)]
    [InlineData("my-stack_2", true)]
    [InlineData("../escape", false)]
    [InlineData("bad name", false)]
    public void ComposeProjectName_IsConstrained(string value, bool expected)
    {
        Assert.Equal(expected, ComposeService.IsValidProjectName(value));
    }

    [Fact]
    public void BootstrapLogin_PersistsOnlyTokenDigest_AndRequiresPasswordChange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ncpm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalPassword = Environment.GetEnvironmentVariable("NCPM_ADMIN_PASSWORD");
        var originalPasswordFile = Environment.GetEnvironmentVariable("NCPM_ADMIN_PASSWORD_FILE");
        try
        {
            Environment.SetEnvironmentVariable("NCPM_ADMIN_PASSWORD", null);
            Environment.SetEnvironmentVariable("NCPM_ADMIN_PASSWORD_FILE", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Config:Path"] = Path.Combine(root, "config"),
                    ["Config:SecretsPath"] = Path.Combine(root, "secrets")
                })
                .Build();
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));
            using var configService = new ConfigService(
                configuration,
                dataProtection,
                NullLogger<ConfigService>.Instance);
            using var authService = new AuthService(
                configService,
                NullLogger<AuthService>.Instance);

            var bootstrapFile = Path.Combine(root, "secrets", "initial-admin-password");
            var bootstrap = File.ReadAllLines(bootstrapFile)
                .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
                .ToDictionary(parts => parts[0], parts => parts[1]);
            var result = authService.Login(new LoginRequest
            {
                Username = bootstrap["username"],
                Password = bootstrap["password"]
            });

            Assert.True(result.Success);
            Assert.True(result.User!.MustChangePassword);
            Assert.NotNull(result.Token);
            var persistedSessions = File.ReadAllText(Path.Combine(root, "secrets", "sessions.yml"));
            Assert.DoesNotContain(result.Token!, persistedSessions, StringComparison.Ordinal);
            Assert.Contains("sha256:", persistedSessions, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCPM_ADMIN_PASSWORD", originalPassword);
            Environment.SetEnvironmentVariable("NCPM_ADMIN_PASSWORD_FILE", originalPasswordFile);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppConfig_EncryptsProviderSecretsAtRest_AndRestoresThem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ncpm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "config");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Config:Path"] = configPath,
                    ["Config:SecretsPath"] = Path.Combine(root, "secrets")
                })
                .Build();
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));

            using (var service = new ConfigService(
                       configuration,
                       dataProtection,
                       NullLogger<ConfigService>.Instance))
            {
                var config = new AppConfig
                {
                    Notification = new NotificationConfig
                    {
                        Enabled = true,
                        Providers =
                        [
                            new NotifyProvider
                            {
                                Name = "test",
                                Type = NotifyProviderType.Webhook,
                                Url = "https://example.invalid/hook",
                                Token = "plain-secret-token"
                            }
                        ]
                    }
                };
                service.SaveAppConfig(config);
            }

            var persisted = File.ReadAllText(Path.Combine(configPath, "config.yml"));
            Assert.DoesNotContain("plain-secret-token", persisted, StringComparison.Ordinal);
            Assert.Contains("enc:v1:", persisted, StringComparison.Ordinal);

            using var reloadedService = new ConfigService(
                configuration,
                dataProtection,
                NullLogger<ConfigService>.Instance);
            Assert.Equal(
                "plain-secret-token",
                reloadedService.LoadAppConfig().Notification.Providers.Single().Token);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrackedYamlFiles_HaveValidSyntax()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var files = Directory.GetFiles(fixtures, "*.yml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var deserializer = new DeserializerBuilder().Build();

        foreach (var file in files)
        {
            var document = deserializer.Deserialize<object>(File.ReadAllText(file));
            Assert.NotNull(document);
        }
    }
}
