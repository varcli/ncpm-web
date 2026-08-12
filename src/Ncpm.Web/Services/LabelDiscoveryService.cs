using Docker.DotNet.Models;
using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Discovers proxy hosts from <c>ncpm.*</c> container labels and persists them
/// to a directory separate from manual hosts, so the two never overwrite each
/// other. A running container with <c>ncpm.enable=true</c> becomes a proxy host
/// whose upstream points at the container on a shared network, falling back to
/// the host's published port when no shared network exists.
/// </summary>
public class LabelDiscoveryService : IDisposable
{
    private const string LabelPrefix = "ncpm.";

    /// <summary>
    /// Label suffixes, appended to <see cref="LabelPrefix"/>. Every one of them
    /// can also be scoped to a single route as <c>ncpm.&lt;alias&gt;.&lt;suffix&gt;</c>.
    /// </summary>
    private static class Labels
    {
        public const string Enable = "enable";
        public const string Exclude = "exclude";
        public const string Aliases = "aliases";
        public const string Host = "host";
        public const string Port = "port";
        public const string Scheme = "scheme";
        public const string Network = "network";
        public const string Tls = "tls";
        public const string Websocket = "websocket";
        public const string PreserveHost = "preserve_host";
        public const string HealthDisable = "healthcheck.disable";
        public const string HealthPath = "healthcheck.path";
        public const string HealthInterval = "healthcheck.interval";
        public const string HealthTimeout = "healthcheck.timeout";
    }

    /// <summary>Networks Docker creates itself; none of them give a container a DNS name.</summary>
    private static readonly HashSet<string> DefaultNetworks =
        new(StringComparer.Ordinal) { "bridge", "host", "none" };

    /// <summary>
    /// Ports that almost always mean a datastore. Discovery is opt-in, so these
    /// are not blocked — but proxying a database is rarely what was intended, and
    /// silently doing it would be worse than saying so.
    /// </summary>
    private static readonly HashSet<int> DatabasePorts =
        [5432, 3306, 6379, 11211, 27017, 1433, 5672, 9200];

    /// <summary>The single unnamed route a container declares when it has no aliases.</summary>
    private static readonly IReadOnlyList<string?> SingleRoute = new string?[] { null };

    private readonly DockerService _dockerService;
    private readonly NginxService _nginxService;
    private readonly ConfigService _configService;
    private readonly ILogger<LabelDiscoveryService> _logger;
    private readonly string _discoveredPath;
    private readonly object _sync = new();
    private Timer? _sweepTimer;
    private Timer? _eventReconnectTimer;
    private bool _disposed;

    // Names of manual host domains, to flag discovered hosts that collide.
    private readonly HashSet<string> _manualDomains = new(StringComparer.OrdinalIgnoreCase);

    // Result of the most recent sweep. The on-disk YAML holds only the generated
    // config, so provenance (container, alias, conflict) lives here.
    private volatile List<DiscoveredProxyHost> _lastSweep = new();

    public bool Enabled { get; set; } = true;

    public event Action? OnDiscoveryChanged;

    public LabelDiscoveryService(
        DockerService dockerService,
        NginxService nginxService,
        ConfigService configService,
        ILogger<LabelDiscoveryService> logger)
    {
        _dockerService = dockerService;
        _nginxService = nginxService;
        _configService = configService;
        _logger = logger;
        _discoveredPath = Path.Combine(_configService.ConfigPath, "discovered-hosts");
        Directory.CreateDirectory(_discoveredPath);
    }

    public void Start()
    {
        // Sweep on startup so labels are reflected before the first page load,
        // then on a 60s cadence as a backstop for the event stream.
        _sweepTimer = new Timer(async void (_) =>
        {
            try { await SweepAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Discovery sweep failed"); }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));

        _eventReconnectTimer = new Timer(async void (_) =>
        {
            try { await EnsureEventStreamAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Discovery event stream watchdog failed"); }
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    #region Sweep

    /// <summary>
    /// Reconciles discovered hosts with current container labels. Containers are
    /// the source of truth: a label gone means its host file is deleted, a label
    /// changed means the host is regenerated.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;

        lock (_sync)
        {
            _manualDomains.Clear();
            foreach (var host in _configService.LoadProxyHosts())
            {
                foreach (var domain in host.Hosts)
                    _manualDomains.Add(domain);
            }
        }

        var containers = await _dockerService.ListContainersAsync(null, cancellationToken);
        var desired = new Dictionary<string, DiscoveredProxyHost>(StringComparer.Ordinal);

        foreach (var container in containers.Where(c => c.IsRunning))
        {
            var labels = container.Labels;
            if (labels == null || labels.Count == 0)
                continue;

            // Exclude wins over enable, so one container can be opted out of a
            // stack whose labels are managed elsewhere.
            if (IsTrue(labels.GetValueOrDefault(Label(Labels.Exclude))))
                continue;

            if (!IsTrue(labels.GetValueOrDefault(Label(Labels.Enable))))
                continue;

            foreach (var alias in ResolveAliases(labels))
            {
                var discovered = await BuildHostFromLabels(container, alias);
                if (discovered != null)
                    desired[discovered.HostId] = discovered;
            }
        }

        await ReconcileFilesAsync(desired, cancellationToken);

        _lastSweep = desired.Values
            .OrderBy(d => d.HostId, StringComparer.Ordinal)
            .ToList();

        OnDiscoveryChanged?.Invoke();
    }

    /// <summary>
    /// The routes one container declares. <c>ncpm.aliases=web,api</c> declares two,
    /// each configured under <c>ncpm.&lt;alias&gt;.*</c> and inheriting anything set
    /// on the unprefixed labels. Without that label a container declares one route.
    /// </summary>
    private static IReadOnlyList<string?> ResolveAliases(IReadOnlyDictionary<string, string> labels)
    {
        var raw = labels.GetValueOrDefault(Label(Labels.Aliases));
        if (string.IsNullOrWhiteSpace(raw))
            return SingleRoute;

        var aliases = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(a => (string?)a)
            .ToList();

        return aliases.Count == 0 ? SingleRoute : aliases;
    }

    /// <summary>
    /// Maps <c>ncpm.*</c> labels onto a <see cref="DiscoveredProxyHost"/> for one
    /// route. Returns null when the route is missing a domain or a port, since
    /// neither can be guessed.
    /// </summary>
    private async Task<DiscoveredProxyHost?> BuildHostFromLabels(DockerContainer container, string? alias)
    {
        var labels = container.Labels!;

        var host = Read(labels, alias, Labels.Host);
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var port = Read(labels, alias, Labels.Port);
        if (string.IsNullOrWhiteSpace(port))
            return null;

        var scheme = ParseScheme(Read(labels, alias, Labels.Scheme));
        var upstreamUrl = await ResolveUpstreamUrl(container, alias, port, scheme);

        var shortId = container.Id[..Math.Min(12, container.Id.Length)];
        var hostId = alias == null ? $"auto-{shortId}" : $"auto-{shortId}-{SanitizeAlias(alias)}";

        var config = new ProxyHostConfig
        {
            Id = hostId,
            Scheme = scheme,
            Hosts = host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Upstreams = new() { new UpstreamConfig { Url = upstreamUrl } },
            Tls = new TlsHostConfig(),
            Http = new HttpHostConfig
            {
                Websocket = IsTrue(Read(labels, alias, Labels.Websocket)),
                PreserveHost = IsTrue(Read(labels, alias, Labels.PreserveHost))
            },
            LoadBalance = new LoadBalanceConfig(),
            Logging = new LoggingHostConfig { AccessLog = true, ErrorLog = true },
            UpdatedAt = DateTime.UtcNow
        };

        if (Enum.TryParse<TlsMode>(Read(labels, alias, Labels.Tls), ignoreCase: true, out var tlsMode)
            && tlsMode != TlsMode.Off)
        {
            config.Tls.Mode = tlsMode;
        }

        // Stream schemes need a listen port; the validator rejects them without one.
        if (scheme is ProxyScheme.Tcp or ProxyScheme.Udp)
        {
            if (int.TryParse(port, out var listenPort))
                config.Http.ListenPort = listenPort;
        }
        else
        {
            config.HealthCheck = BuildHealthCheck(labels, alias);
        }

        var discovered = new DiscoveredProxyHost
        {
            HostId = hostId,
            Alias = alias,
            ContainerId = container.Id,
            ContainerName = container.Name,
            ComposeProject = labels.GetValueOrDefault(ComposeLabels.Project),
            Config = config
        };

        lock (_sync)
        {
            foreach (var domain in config.Hosts)
            {
                if (_manualDomains.Contains(domain))
                {
                    discovered.HasConflict = true;
                    discovered.ConflictWith = domain;
                    break;
                }
            }
        }

        return discovered;
    }

    /// <summary>
    /// Probing is on by default for HTTP routes — a discovered host that silently
    /// points at a dead container is the failure this is meant to surface.
    /// <c>ncpm.healthcheck.disable=true</c> turns it off.
    /// </summary>
    private static HealthCheckConfig? BuildHealthCheck(IReadOnlyDictionary<string, string> labels, string? alias)
    {
        if (IsTrue(Read(labels, alias, Labels.HealthDisable)))
            return null;

        return new HealthCheckConfig
        {
            Enabled = true,
            Path = Read(labels, alias, Labels.HealthPath) ?? "/",
            IntervalSeconds = ParseSeconds(Read(labels, alias, Labels.HealthInterval), 30),
            TimeoutSeconds = ParseSeconds(Read(labels, alias, Labels.HealthTimeout), 5)
        };
    }

    #endregion

    #region Upstream resolution

    /// <summary>
    /// Picks the address nginx should proxy to. A user-defined Docker network is
    /// what makes a container resolvable by name, so one of those is chosen first —
    /// pinned by <c>ncpm.network</c> when set, otherwise the first non-default one
    /// the container is attached to. Only when no such network exists does this
    /// fall back to the container's published port on the host.
    /// </summary>
    private async Task<string> ResolveUpstreamUrl(
        DockerContainer container,
        string? alias,
        string port,
        ProxyScheme scheme)
    {
        WarnIfDatabasePort(container, port);

        var inspect = await _dockerService.InspectContainerAsync(container.Id, container.HostId);
        var networks = inspect?.NetworkSettings?.Networks;
        var requested = Read(container.Labels!, alias, Labels.Network);

        if (networks is { Count: > 0 })
        {
            var selected = SelectNetwork(networks, requested, container);

            if (selected != null)
            {
                // Prefer the container name: Docker's embedded DNS resolves it on
                // any user-defined network, and unlike the IP it survives a restart.
                var target = !string.IsNullOrEmpty(container.Name)
                    ? container.Name
                    : selected.Value.Endpoint.IPAddress;

                if (!string.IsNullOrEmpty(target))
                    return Format(scheme, target, port);
            }
            else if (!string.IsNullOrWhiteSpace(requested))
            {
                _logger.LogWarning(
                    "Container {Container} pins network {Network} but is not attached to it; using its published port instead",
                    container.Name, requested);
            }
        }

        // Published port on the host. Reachable from nginx only if the port is
        // published and the host address resolves from inside the panel container.
        var published = container.Ports
            .FirstOrDefault(p => p.PrivatePort.ToString() == port && p.PublicPort.HasValue);

        if (published?.PublicPort is int publicPort)
            return Format(scheme, "host.docker.internal", publicPort.ToString());

        // Last resort: the bare container name. nginx fails loudly if it cannot
        // resolve it, which beats silently proxying nowhere.
        return Format(scheme, container.Name, port);
    }

    /// <summary>
    /// Resolves which network to proxy over. Compose renames networks to
    /// <c>{project}_{name}</c>, so a pinned name is also tried in that form —
    /// otherwise every compose stack would have to spell out the mangled name.
    /// </summary>
    private static (string Name, EndpointSettings Endpoint)? SelectNetwork(
        IDictionary<string, EndpointSettings> networks,
        string? requested,
        DockerContainer container)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (networks.TryGetValue(requested, out var pinned))
                return (requested, pinned);

            var project = container.Labels?.GetValueOrDefault(ComposeLabels.Project);
            if (!string.IsNullOrEmpty(project))
            {
                var composeName = $"{project}_{requested}";
                if (networks.TryGetValue(composeName, out var composed))
                    return (composeName, composed);
            }

            return null;
        }

        foreach (var (name, endpoint) in networks)
        {
            if (!DefaultNetworks.Contains(name))
                return (name, endpoint);
        }

        return null;
    }

    private static string Format(ProxyScheme scheme, string target, string port) => scheme switch
    {
        ProxyScheme.Https => $"https://{target}:{port}",
        ProxyScheme.Tcp or ProxyScheme.Udp => $"{target}:{port}",
        _ => $"http://{target}:{port}"
    };

    private void WarnIfDatabasePort(DockerContainer container, string port)
    {
        if (int.TryParse(port, out var value) && DatabasePorts.Contains(value))
        {
            _logger.LogWarning(
                "Discovered host for {Container} targets port {Port}, a well-known database port. " +
                "Remove its ncpm.* labels if exposing it was not intended.",
                container.Name, value);
        }
    }

    #endregion

    #region Label helpers

    private static string Label(string suffix) => LabelPrefix + suffix;

    /// <summary>
    /// Reads a label for one route: the alias-scoped key wins, and the unprefixed
    /// key is the default every alias inherits.
    /// </summary>
    private static string? Read(IReadOnlyDictionary<string, string> labels, string? alias, string suffix)
    {
        if (alias != null
            && labels.TryGetValue($"{LabelPrefix}{alias}.{suffix}", out var scoped)
            && !string.IsNullOrWhiteSpace(scoped))
        {
            return scoped;
        }

        return labels.TryGetValue(Label(suffix), out var shared) && !string.IsNullOrWhiteSpace(shared)
            ? shared
            : null;
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static ProxyScheme ParseScheme(string? value) =>
        Enum.TryParse<ProxyScheme>(value, ignoreCase: true, out var parsed) ? parsed : ProxyScheme.Http;

    /// <summary>Accepts a bare second count or an nginx-style <c>30s</c> / <c>5m</c> literal.</summary>
    private static int ParseSeconds(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var text = value.Trim();
        var multiplier = 1;

        if (text.EndsWith('s'))
        {
            text = text[..^1];
        }
        else if (text.EndsWith('m'))
        {
            text = text[..^1];
            multiplier = 60;
        }
        else if (text.EndsWith('h'))
        {
            text = text[..^1];
            multiplier = 3600;
        }

        return int.TryParse(text, out var seconds) && seconds > 0 ? seconds * multiplier : fallback;
    }

    /// <summary>
    /// An alias ends up in the host id, which nginx uses as an upstream block name,
    /// so anything outside the identifier charset is folded to a dash.
    /// </summary>
    private static string SanitizeAlias(string alias)
    {
        var chars = alias
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();

        return new string(chars);
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Writes the desired set of discovered hosts to disk, deleting files for
    /// routes no longer declared. After writing, each new or changed host is
    /// published to nginx so the config takes effect.
    /// </summary>
    private async Task ReconcileFilesAsync(
        Dictionary<string, DiscoveredProxyHost> desired,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(_discoveredPath))
        {
            foreach (var file in Directory.GetFiles(_discoveredPath, "*.yml"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (desired.ContainsKey(id))
                    continue;

                // Container gone, label removed, or alias dropped — pull its config.
                try
                {
                    var content = await File.ReadAllTextAsync(file, cancellationToken);
                    var host = _configService.DeserializeProxyHost(content);
                    if (host != null)
                        _nginxService.DeleteConfig(host.Id);
                    File.Delete(file);
                    _logger.LogInformation("Removed discovered host {Id} (no longer declared)", id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up discovered host {Id}", id);
                }
            }
        }

        foreach (var discovered in desired.Values)
        {
            var filePath = Path.Combine(_discoveredPath, $"{discovered.HostId}.yml");
            var content = _configService.SerializeProxyHost(discovered.Config);

            var previous = File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, cancellationToken)
                : null;

            if (previous == content)
                continue; // Unchanged, skip the publish round-trip.

            await File.WriteAllTextAsync(filePath, content, cancellationToken);

            // Skip publishing conflicted hosts — a manual host owns the domain.
            if (discovered.HasConflict)
            {
                _logger.LogWarning(
                    "Discovered host {Id} conflicts with manual host for {Domain}, skipping nginx publish",
                    discovered.HostId, discovered.ConflictWith);
                continue;
            }

            // PublishConfigAsync validates, stages, and rolls back on failure,
            // so a bad discovered config cannot take nginx down.
            var ok = await _nginxService.PublishConfigAsync(discovered.Config, cancellationToken);
            if (!ok)
            {
                _logger.LogWarning(
                    "Nginx rejected discovered host {Id}: {Error}",
                    discovered.HostId, _nginxService.LastError);
            }
        }
    }

    /// <summary>
    /// Returns the hosts from the most recent sweep, which carry the container
    /// they came from and any domain conflict. Before the first sweep completes,
    /// the on-disk configs are returned instead, without that provenance.
    /// </summary>
    public List<DiscoveredProxyHost> ListDiscovered()
    {
        var snapshot = _lastSweep;
        return snapshot.Count > 0 ? snapshot.ToList() : LoadFromDisk();
    }

    private List<DiscoveredProxyHost> LoadFromDisk()
    {
        var result = new List<DiscoveredProxyHost>();
        if (!Directory.Exists(_discoveredPath))
            return result;

        foreach (var file in Directory.GetFiles(_discoveredPath, "*.yml"))
        {
            try
            {
                var config = _configService.DeserializeProxyHost(File.ReadAllText(file));
                if (config == null)
                    continue;

                result.Add(new DiscoveredProxyHost { HostId = config.Id, Config = config });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read discovered host {File}", file);
            }
        }

        return result;
    }

    /// <summary>
    /// Promotes a discovered host to a manual one: copies its config into the
    /// manual directory, removes the discovered file, and re-publishes under the
    /// manual id so nginx picks it up without the auto- prefix.
    /// </summary>
    public async Task<bool> PromoteAsync(string hostId, CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.Combine(_discoveredPath, $"{hostId}.yml");
        if (!File.Exists(sourcePath))
            return false;

        var config = _configService.DeserializeProxyHost(await File.ReadAllTextAsync(sourcePath, cancellationToken));
        if (config == null)
            return false;

        // Give the manual host a stable, non-auto id and save it.
        config.Id = hostId.StartsWith("auto-", StringComparison.Ordinal)
            ? hostId["auto-".Length..]
            : hostId;

        _configService.SaveProxyHost(config);
        _nginxService.DeleteConfig(hostId); // remove the auto- config from nginx
        await _nginxService.PublishConfigAsync(config, cancellationToken);

        File.Delete(sourcePath);

        // Drop it from the cached sweep so the UI stops listing it as discovered
        // before the next sweep runs.
        _lastSweep = _lastSweep.Where(d => d.HostId != hostId).ToList();

        _logger.LogInformation("Promoted discovered host {OldId} to manual host {NewId}", hostId, config.Id);
        return true;
    }

    #endregion

    /// <summary>
    /// Originally tried to subscribe to the Docker event stream for sub-second
    /// label updates, but the <c>DockerSystemEventsStream</c> API differs across
    /// Docker.DotNet versions and the typed client's <c>MonitorEventsAsync</c>
    /// surface is brittle. The 60s sweep timer (see <see cref="Start"/>) already
    /// reconciles state on a fixed cadence, which is responsive enough for a
    /// control panel, so the event stream is intentionally left as a no-op
    /// rather than depending on an unstable API.
    /// </summary>
    private Task EnsureEventStreamAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _sweepTimer?.Dispose();
            _eventReconnectTimer?.Dispose();
            _disposed = true;
        }
    }
}
