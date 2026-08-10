using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using AntDesign.ProLayout;
using Blazored.LocalStorage;
using Ncpm.Services;
using Serilog;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Panel:Host / Panel:Port (and the Panel__Port env var) now select the listen
// address. ASPNETCORE_URLS still wins when it is set explicitly, which is how the
// container image configures itself.
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"])
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var panelHost = builder.Configuration.GetValue<string>("Panel:Host") ?? "0.0.0.0";
    var panelPort = builder.Configuration.GetValue<int?>("Panel:Port") ?? 8098;
    builder.WebHost.UseUrls($"http://{panelHost}:{panelPort}");
}

// Configure Serilog. Sinks come from the Serilog section of appsettings only:
// adding Console/File here as well duplicated every log line.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
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

// Add rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
});

// Add application services
builder.Services.AddSingleton<Ncpm.Services.ConfigService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<NginxService>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AcmeService>();
builder.Services.AddSingleton<AclService>();
builder.Services.AddSingleton<Ncpm.Services.NotificationService>();

// Add authentication
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();

// Health checks (used by the container HEALTHCHECK probe)
builder.Services.AddHealthChecks()
    .AddCheck<PanelHealthCheck>("panel");

var app = builder.Build();

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

// Rate limiting
app.UseRateLimiter();

app.UseRouting();

app.MapHealthChecks("/health");

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Start background services
var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
healthCheckService.Start();

var monitorService = app.Services.GetRequiredService<MonitorService>();
monitorService.Start();

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
