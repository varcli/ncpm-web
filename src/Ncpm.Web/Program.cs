using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using AntDesign.ProLayout;
using Blazored.LocalStorage;
using Ncpm.Services;
using Ncpm.Data;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Globalization;
using System.Net;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);
var bootstrapConfig = LoadBootstrapConfig(builder.Configuration);

// Panel:Host / Panel:Port (and the Panel__Port env var) now select the listen
// address. ASPNETCORE_URLS still wins when it is set explicitly, which is how the
// container image configures itself.
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"])
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var panelHost = bootstrapConfig?.Panel.Host
        ?? builder.Configuration.GetValue<string>("Panel:Host")
        ?? "0.0.0.0";
    var panelPort = bootstrapConfig?.Panel.Port
        ?? builder.Configuration.GetValue<int?>("Panel:Port")
        ?? 8098;
    builder.WebHost.UseUrls($"http://{panelHost}:{panelPort}");
}

// config.yml is authoritative after first run. Build the logger from it before
// DI starts, while retaining appsettings/default values for a brand-new install.
var loggingConfig = bootstrapConfig?.Logging ?? new LoggingConfig();
var initialLogLevel = Enum.TryParse<LogEventLevel>(loggingConfig.Level, true, out var parsedLevel)
    ? parsedLevel
    : LogEventLevel.Information;
var levelSwitch = new LoggingLevelSwitch(initialLogLevel);
var loggerBuilder = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext();

if (loggingConfig.EnableConsole)
    loggerBuilder.WriteTo.Console();

if (loggingConfig.EnableFile)
{
    var logDirectory = string.IsNullOrWhiteSpace(loggingConfig.Path) ? "data/logs" : loggingConfig.Path;
    Directory.CreateDirectory(logDirectory);
    loggerBuilder.WriteTo.File(
        Path.Combine(logDirectory, "ncpm-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: Math.Max(1, loggingConfig.RetainDays));
}

Log.Logger = loggerBuilder.CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
var secretsPath = builder.Configuration.GetValue<string>("Config:SecretsPath")
    ?? Path.Combine(
        Path.GetDirectoryName((builder.Configuration.GetValue<string>("Config:Path") ?? "data/config").TrimEnd('/', '\\'))
            ?? "data",
        "secrets");
Directory.CreateDirectory(secretsPath);
if (!OperatingSystem.IsWindows())
{
    try
    {
        File.SetUnixFileMode(
            secretsPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    catch
    {
        // Some bind-mounted filesystems do not expose Unix permission changes.
    }
}
builder.Services.AddDataProtection()
    .SetApplicationName("Ncpm")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(secretsPath, "data-protection")));

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddAntDesign();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(sp.GetService<NavigationManager>()!.BaseUri)
});
// Singleton services must not take the scoped HttpClient above; they use the factory.
builder.Services.AddHttpClient();
builder.Services.Configure<ProSettings>(builder.Configuration.GetSection("ProSettings"));
builder.Services.AddInteractiveStringLocalizer();
builder.Services.AddLocalization(options =>
{
    options.ResourcesPath = "Resources";
});

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { new CultureInfo("zh-CN"), new CultureInfo("en-US") };
    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("zh-CN");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

var trustedProxyConfig = bootstrapConfig?.Panel
    ?? builder.Configuration.GetSection("Panel").Get<PanelConfig>()
    ?? new PanelConfig();
if (trustedProxyConfig.TrustedProxies.Count > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = trustedProxyConfig.ForwardedHeaderLimit;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in trustedProxyConfig.TrustedProxies.Select(value => value.Trim()))
        {
            if (entry.Contains('/'))
            {
                var network = System.Net.IPNetwork.Parse(entry);
                options.KnownIPNetworks.Add(network);
                if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                        network.BaseAddress.MapToIPv6(),
                        network.PrefixLength + 96));
                }
            }
            else
            {
                var proxy = IPAddress.Parse(entry);
                options.KnownProxies.Add(proxy);
                if (proxy.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    options.KnownProxies.Add(proxy.MapToIPv6());
            }
        }
    });
}

// Add application services
builder.Services.AddSingleton<Ncpm.Services.ConfigService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<Ncpm.Services.NginxSnapshotService>();
builder.Services.AddSingleton<NginxService>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AcmeService>();
builder.Services.AddSingleton<AcmeDnsProviderService>();
builder.Services.AddSingleton<AclService>();
builder.Services.AddSingleton<Ncpm.Services.NotificationService>();
builder.Services.AddSingleton<Ncpm.Services.ComposeService>();
builder.Services.AddSingleton<Ncpm.Services.LabelDiscoveryService>();
builder.Services.AddSingleton<Ncpm.Services.AccessLogAnalyzer>();

// Add authentication
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();

// Health checks (used by the container HEALTHCHECK probe)
builder.Services.AddHealthChecks()
    .AddCheck<PanelHealthCheck>("panel");

var app = builder.Build();

if (trustedProxyConfig.TrustedProxies.Count > 0)
    app.UseForwardedHeaders();

var panelBasePath = Ncpm.Services.ConfigService.NormalizeBasePath(
    bootstrapConfig?.Panel.BasePath
    ?? builder.Configuration.GetValue<string>("Panel:BasePath")
    ?? "/");
if (panelBasePath != "/")
    app.UsePathBase(panelBasePath);

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRequestLocalization();

// ACL middleware
app.Use(async (context, next) =>
{
    var aclService = context.RequestServices.GetRequiredService<AclService>();
    var clientIp = context.Connection.RemoteIpAddress;
    if (clientIp != null && !aclService.IsAllowed(clientIp))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Access denied");
        return;
    }
    await next();
});

// Live, per-client token-bucket rate limiting from config.yml.
app.UseMiddleware<DynamicRateLimitMiddleware>();

app.UseRouting();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready");

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Start background services
var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
healthCheckService.Start();

var monitorService = app.Services.GetRequiredService<MonitorService>();
monitorService.Start();

var labelDiscoveryService = app.Services.GetRequiredService<Ncpm.Services.LabelDiscoveryService>();
labelDiscoveryService.Start();

// Materialize authentication state during startup so the bootstrap administrator
// and initial password file exist before an operator opens the first browser.
_ = app.Services.GetRequiredService<AuthService>();

var acmeService = app.Services.GetRequiredService<AcmeService>();
acmeService.Start();

// Log level changes are safe to apply live. Sink/path/retention changes are
// picked up on restart, as reported by the settings page.
var configService = app.Services.GetRequiredService<Ncpm.Services.ConfigService>();
configService.OnConfigChanged += () =>
{
    var level = configService.LoadAppConfig().Logging.Level;
    if (Enum.TryParse<LogEventLevel>(level, true, out var parsed))
        levelSwitch.MinimumLevel = parsed;
};

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Panel terminated unexpectedly");
    throw;
}
finally
{
    // Flush buffered log events before the process exits.
    Log.CloseAndFlush();
}

static AppConfig? LoadBootstrapConfig(IConfiguration configuration)
{
    var configPath = configuration.GetValue<string>("Config:Path") ?? "data/config";
    var file = Path.Combine(configPath, "config.yml");
    if (!File.Exists(file))
        return null;

    try
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var config = deserializer.Deserialize<AppConfig>(File.ReadAllText(file));
        if (config != null)
            Ncpm.Services.ConfigService.ValidateAppConfig(config);
        return config;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unable to read bootstrap config {file}: {ex.Message}");
        return null;
    }
}
