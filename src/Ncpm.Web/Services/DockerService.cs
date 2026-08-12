using System.Collections.Concurrent;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Ncpm.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ncpm.Services;

public class DockerService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly ILogger<DockerService> _logger;
    private readonly ConcurrentDictionary<string, DockerClient> _clients = new();
    private readonly ConcurrentDictionary<string, DockerHostStatus> _hostStatuses = new();

    // Previous CPU counters per container, needed to compute CPU% from one-shot samples.
    private readonly ConcurrentDictionary<string, (ulong TotalUsage, ulong SystemUsage)> _previousCpuSamples = new();

    // Hosts currently failing, keyed by host id, holding the last error message.
    private readonly ConcurrentDictionary<string, string> _failingHosts = new();
    private readonly string _hostsFilePath;
    private readonly object _configurationLock = new();
    private List<DockerHost> _hosts = new();
    private string _configuredDefaultHost = string.Empty;
    private int _apiTimeoutSeconds = 30;
    private Timer? _healthCheckTimer;
    private int _checkingHosts;
    private bool _disposed;

    public event Action<string, DockerHostStatus>? OnHostStatusChanged;

    public DockerService(ConfigService configService, ILogger<DockerService> logger)
    {
        _configService = configService;
        _logger = logger;
        _hostsFilePath = Path.Combine(configService.ConfigPath, "docker-hosts.yml");

        var dockerConfig = configService.LoadAppConfig().Docker;
        _configuredDefaultHost = dockerConfig.Host;
        _apiTimeoutSeconds = Math.Max(1, dockerConfig.ApiTimeout);
        
        LoadHosts();
        EnsureDefaultHost();
        InitializeClients();
        StartHealthCheck();
        _configService.OnConfigChanged += ApplyDockerConfiguration;
    }

    private void LoadHosts()
    {
        if (!File.Exists(_hostsFilePath))
        {
            _hosts = new List<DockerHost>();
            return;
        }

        try
        {
            var content = File.ReadAllText(_hostsFilePath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            _hosts = deserializer.Deserialize<List<DockerHost>>(content) ?? new List<DockerHost>();
            var containedLegacyPassword = false;
            foreach (var host in _hosts)
            {
                if (!string.IsNullOrEmpty(host.SshPassword))
                {
                    host.SshPassword = null;
                    containedLegacyPassword = true;
                }

                if (host.Type == DockerHostType.Ssh)
                {
                    host.IsEnabled = false;
                    host.LastError = "SSH Docker transport is not supported; configure HTTPS/mTLS instead";
                }
            }

            if (containedLegacyPassword)
            {
                SaveHosts();
                _logger.LogWarning("Removed unsupported legacy SSH passwords from Docker host configuration");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Docker hosts");
            _hosts = new List<DockerHost>();
        }
    }

    private void SaveHosts()
    {
        try
        {
            var dir = Path.GetDirectoryName(_hostsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var content = serializer.Serialize(_hosts);
            var temp = _hostsFilePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, content);
                RestrictFileToOwner(temp);
                File.Move(temp, _hostsFilePath, overwrite: true);
                RestrictFileToOwner(_hostsFilePath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Docker hosts");
        }
    }

    private void EnsureDefaultHost()
    {
        if (_hosts.Any(h => h.IsDefault))
            return;

        var configuredHost = _configService.LoadAppConfig().Docker.Host;
        var localHost = CreateDefaultHost(configuredHost);

        _hosts.Add(localHost);
        SaveHosts();
    }

    private static DockerHost CreateDefaultHost(string configuredHost)
    {
        var host = new DockerHost
        {
            Id = "local",
            Name = "Local Docker",
            IsEnabled = true,
            IsDefault = true,
            Description = "Local Docker daemon",
            CreatedAt = DateTime.UtcNow
        };

        if (Uri.TryCreate(configuredHost, UriKind.Absolute, out var uri)
            && uri.Scheme is "tcp" or "http" or "https")
        {
            host.Type = DockerHostType.Tcp;
            host.Host = uri.Host;
            host.TcpPort = uri.IsDefaultPort ? (uri.Scheme == "https" ? 2376 : 2375) : uri.Port;
            host.UseTls = uri.Scheme == "https";
        }
        else
        {
            host.Type = DockerHostType.Local;
            host.Host = string.IsNullOrWhiteSpace(configuredHost)
                ? "unix:///var/run/docker.sock"
                : configuredHost;
        }

        return host;
    }

    private void InitializeClients()
    {
        foreach (var host in _hosts.Where(h => h.IsEnabled))
        {
            try
            {
                var client = CreateClient(host);
                _clients[host.Id] = client;
                _logger.LogInformation("Initialized Docker client for {Host}", host.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Docker client for {Host}", host.Name);
                host.LastError = ex.Message;
            }
        }
    }

    private DockerClient CreateClient(DockerHost host)
    {
        if (host.Type == DockerHostType.Ssh)
        {
            throw new NotSupportedException(
                "SSH Docker transport is not supported. Expose a TLS-protected Docker API or use a socket proxy.");
        }

        var uri = new Uri(host.ConnectionString);
        
        Credentials credentials = host.UseTls
            && (!string.IsNullOrWhiteSpace(host.TlsCaCertPath)
                || !string.IsNullOrWhiteSpace(host.TlsClientCertPath)
                || !string.IsNullOrWhiteSpace(host.TlsClientKeyPath))
            ? new DockerTlsCredentials(
                host.TlsCaCertPath,
                host.TlsClientCertPath,
                host.TlsClientKeyPath)
            : new AnonymousCredentials();
        var config = new DockerClientConfiguration(
            uri,
            credentials,
            defaultTimeout: TimeSpan.FromSeconds(_apiTimeoutSeconds));
        return config.CreateClient();
    }

    private void ApplyDockerConfiguration()
    {
        lock (_configurationLock)
        {
            var config = _configService.LoadAppConfig().Docker;
            var hostChanged = !string.Equals(
                config.Host,
                _configuredDefaultHost,
                StringComparison.Ordinal);
            var timeoutChanged = config.ApiTimeout != _apiTimeoutSeconds;

            if (!hostChanged && !timeoutChanged)
                return;

            _apiTimeoutSeconds = Math.Max(1, config.ApiTimeout);
            if (hostChanged)
            {
                _configuredDefaultHost = config.Host;
                var existing = _hosts.FirstOrDefault(h => h.Id == "local");
                if (existing != null)
                {
                    var replacement = CreateDefaultHost(config.Host);
                    replacement.Name = existing.Name;
                    replacement.Description = existing.Description;
                    replacement.IsEnabled = existing.IsEnabled;
                    replacement.IsDefault = existing.IsDefault;
                    replacement.CreatedAt = existing.CreatedAt;
                    _hosts[_hosts.IndexOf(existing)] = replacement;
                    SaveHosts();
                }
            }

            foreach (var item in _clients.ToArray())
            {
                if (_clients.TryRemove(item.Key, out var client))
                    client.Dispose();
            }

            InitializeClients();
            _logger.LogInformation(
                "Applied Docker configuration (host changed: {HostChanged}, timeout: {Timeout}s)",
                hostChanged,
                _apiTimeoutSeconds);
        }
    }

    private void StartHealthCheck()
    {
        // Exceptions must not escape a timer callback, or they take down the process.
        _healthCheckTimer = new Timer(async void (_) =>
        {
            if (Interlocked.CompareExchange(ref _checkingHosts, 1, 0) != 0)
                return;

            try
            {
                await CheckAllHosts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Docker host health sweep failed");
            }
            finally
            {
                Volatile.Write(ref _checkingHosts, 0);
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    private async Task CheckAllHosts()
    {
        foreach (var host in _hosts.Where(h => h.IsEnabled))
        {
            await CheckHostHealth(host);
        }
    }

    private async Task CheckHostHealth(DockerHost host)
    {
        if (!_clients.TryGetValue(host.Id, out var client))
        {
            return;
        }

        try
        {
            var info = await client.System.GetSystemInfoAsync();
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

            var status = new DockerHostStatus
            {
                HostId = host.Id,
                HostName = host.Name,
                IsConnected = true,
                ContainerCount = containers.Count,
                RunningContainers = containers.Count(c => c.State == "running"),
                DockerVersion = info.ServerVersion,
                OsType = info.OSType,
                Architecture = info.Architecture,
                TotalMemory = info.MemTotal,
                CpuCount = (int)info.NCPU,
                CheckedAt = DateTime.UtcNow
            };

            _hostStatuses[host.Id] = status;
            host.IsConnected = true;
            host.LastError = null;
            host.LastChecked = DateTime.UtcNow;
            host.ContainerCount = containers.Count;

            OnHostStatusChanged?.Invoke(host.Id, status);
        }
        catch (Exception ex)
        {
            host.IsConnected = false;
            host.LastError = ex.Message;
            host.LastChecked = DateTime.UtcNow;

            var status = new DockerHostStatus
            {
                HostId = host.Id,
                HostName = host.Name,
                IsConnected = false,
                Error = ex.Message,
                CheckedAt = DateTime.UtcNow
            };

            _hostStatuses[host.Id] = status;
            OnHostStatusChanged?.Invoke(host.Id, status);
        }
    }

    #region Host Management

    public List<DockerHost> GetAllHosts()
    {
        return _hosts.Select(CloneHost).ToList();
    }

    public DockerHost? GetHost(string hostId)
    {
        var host = _hosts.FirstOrDefault(h => h.Id == hostId);
        return host == null ? null : CloneHost(host);
    }

    public DockerHost? GetDefaultHost()
    {
        var host = _hosts.FirstOrDefault(h => h.IsDefault && h.IsEnabled);
        return host == null ? null : CloneHost(host);
    }

    /// <summary>
    /// Returns the shared <see cref="DockerClient"/> for a host, or null when the
    /// host is disabled or never connected. Lets background services reuse the
    /// pooled connection rather than creating their own.
    /// </summary>
    public DockerClient? GetClient(string hostId)
    {
        return _clients.TryGetValue(hostId, out var client) ? client : null;
    }

    public DockerHostStatus? GetHostStatus(string hostId)
    {
        return _hostStatuses.TryGetValue(hostId, out var status) ? status : null;
    }

    public List<DockerHostStatus> GetAllHostStatuses()
    {
        return _hostStatuses.Values.ToList();
    }

    public bool AddHost(DockerHost host)
    {
        if (_hosts.Any(h => h.Id == host.Id))
        {
            return false;
        }

        if (host.IsDefault)
        {
            foreach (var h in _hosts)
            {
                h.IsDefault = false;
            }
        }

        var stored = CloneHost(host);
        stored.CreatedAt = stored.CreatedAt == default ? DateTime.UtcNow : stored.CreatedAt;
        _hosts.Add(stored);

        if (stored.IsEnabled)
        {
            try
            {
                var client = CreateClient(stored);
                _clients[stored.Id] = client;
            }
            catch (Exception ex)
            {
                stored.LastError = ex.Message;
            }
        }

        SaveHosts();

        return true;
    }

    public bool UpdateHost(DockerHost host)
    {
        var existing = _hosts.FirstOrDefault(h => h.Id == host.Id);
        if (existing == null)
        {
            return false;
        }

        if (host.IsDefault)
        {
            foreach (var h in _hosts)
            {
                h.IsDefault = false;
            }
        }

        var index = _hosts.IndexOf(existing);
        var stored = CloneHost(host);
        _hosts[index] = stored;

        // Always rebuild the client: port, TLS, enabled state and future
        // credential fields all affect the transport, not just host/type.
        if (_clients.TryRemove(host.Id, out var oldClient))
        {
            oldClient.Dispose();
        }

        if (stored.IsEnabled)
        {
            try
            {
                var client = CreateClient(stored);
                _clients[stored.Id] = client;
                stored.LastError = null;
            }
            catch (Exception ex)
            {
                stored.LastError = ex.Message;
            }
        }
        else
        {
            _hostStatuses.TryRemove(stored.Id, out _);
        }

        SaveHosts();

        return true;
    }

    public bool DeleteHost(string hostId)
    {
        var host = _hosts.FirstOrDefault(h => h.Id == hostId);
        if (host == null || host.IsDefault)
        {
            return false;
        }

        _hosts.Remove(host);
        SaveHosts();

        if (_clients.TryRemove(hostId, out var client))
        {
            client.Dispose();
        }

        _hostStatuses.TryRemove(hostId, out _);
        return true;
    }

    public async Task<bool> TestConnection(DockerHost host)
    {
        try
        {
            using var client = CreateClient(host);
            var info = await client.System.GetSystemInfoAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed for {Host}", host.Name);
            return false;
        }
    }

    #endregion

    #region Container Operations

    public async Task<List<DockerContainer>> ListContainersAsync(string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId) 
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var allContainers = new List<DockerContainer>();
        var successfulHosts = 0;
        Exception? lastError = null;

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
            {
                lastError = new InvalidOperationException($"Docker host {host.Name} has no active client");
                continue;
            }

            try
            {
                var containers = await client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true }, cancellationToken);

                var hostContainers = containers.Select(c => new DockerContainer
                {
                    Id = c.ID,
                    Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? string.Empty,
                    Image = c.Image,
                    State = c.State,
                    Status = c.Status,
                    Created = c.Created,
                    Labels = c.Labels?.ToDictionary(l => l.Key, l => l.Value) ?? new(),
                    Ports = c.Ports?.Select(p => new PortMapping
                    {
                        Ip = p.IP,
                        PrivatePort = (int)p.PrivatePort,
                        PublicPort = p.PublicPort > 0 ? (int)p.PublicPort : null,
                        Type = p.Type
                    }).ToList() ?? new(),
                    HostId = host.Id,
                    HostName = host.Name
                }).ToList();

                allContainers.AddRange(hostContainers);
                successfulHosts++;
                ClearHostFailure(host);
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogHostFailure(host, ex, "list containers");
            }
        }

        if (hosts.Count > 0 && successfulHosts == 0)
        {
            throw new InvalidOperationException(
                $"Unable to query any Docker host: {lastError?.Message ?? "no active client"}",
                lastError);
        }

        return allContainers;
    }

    /// <summary>
    /// Connects the panel container to a local user-defined network so nginx can
    /// resolve an automatically discovered container by name. Remote daemons
    /// cannot attach this panel container and therefore return false.
    /// </summary>
    public async Task<bool> EnsurePanelConnectedToNetworkAsync(
        string hostId,
        string networkName,
        CancellationToken cancellationToken = default)
    {
        var host = _hosts.FirstOrDefault(h => h.Id == hostId);
        if (host == null || host.Type != DockerHostType.Local
            || !string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase)
            || !_clients.TryGetValue(hostId, out var client))
        {
            return false;
        }

        var panelContainer = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        try
        {
            var inspect = await client.Containers.InspectContainerAsync(panelContainer, cancellationToken);
            if (inspect.NetworkSettings?.Networks?.ContainsKey(networkName) == true)
                return true;

            await client.Networks.ConnectNetworkAsync(
                networkName,
                new NetworkConnectParameters { Container = inspect.ID },
                cancellationToken);
            _logger.LogInformation("Connected panel container to Docker network {Network}", networkName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect panel container to Docker network {Network}", networkName);
            return false;
        }
    }

    public string GetReachableHostAddress(string hostId)
    {
        var host = _hosts.FirstOrDefault(h => h.Id == hostId);
        if (host == null || host.Type == DockerHostType.Local)
            return "host.docker.internal";

        return host.Host;
    }

    private static DockerHost CloneHost(DockerHost host) => new()
    {
        Id = host.Id,
        Name = host.Name,
        Host = host.Host,
        Type = host.Type,
        IsEnabled = host.IsEnabled,
        IsDefault = host.IsDefault,
        Description = host.Description,
        CreatedAt = host.CreatedAt,
        LastError = host.LastError,
        LastChecked = host.LastChecked,
        IsConnected = host.IsConnected,
        ContainerCount = host.ContainerCount,
        TcpPort = host.TcpPort,
        UseTls = host.UseTls,
        TlsCaCertPath = host.TlsCaCertPath,
        TlsClientCertPath = host.TlsClientCertPath,
        TlsClientKeyPath = host.TlsClientKeyPath,
        SshPort = host.SshPort,
        SshUser = host.SshUser,
        SshKeyPath = host.SshKeyPath
    };

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

    /// <summary>
    /// Logs a host failure once with full detail, then stays quiet while the host
    /// remains down. Metrics collection polls every few seconds, so logging each
    /// failure with a stack trace filled the log file for an offline host.
    /// </summary>
    private void LogHostFailure(DockerHost host, Exception ex, string operation)
    {
        if (_failingHosts.TryAdd(host.Id, ex.Message))
        {
            _logger.LogError(ex, "Failed to {Operation} from {Host}", operation, host.Name);
            return;
        }

        // Message changed: report the new failure mode, still without the trace.
        if (_failingHosts.TryGetValue(host.Id, out var previous) && previous != ex.Message)
        {
            _failingHosts[host.Id] = ex.Message;
            _logger.LogWarning("Docker host {Host} still failing to {Operation}: {Error}",
                host.Name, operation, ex.Message);
        }
        else
        {
            _logger.LogDebug("Docker host {Host} still failing to {Operation}", host.Name, operation);
        }
    }

    private void ClearHostFailure(DockerHost host)
    {
        if (_failingHosts.TryRemove(host.Id, out _))
        {
            _logger.LogInformation("Docker host {Host} recovered", host.Name);
        }
    }

    public async Task<DockerContainer?> GetContainerAsync(string containerId, string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                var inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
                return new DockerContainer
                {
                    Id = inspect.ID,
                    Name = inspect.Name?.TrimStart('/') ?? string.Empty,
                    Image = inspect.Config.Image,
                    State = inspect.State.Status,
                    Status = inspect.State.Running ? "running" : "stopped",
                    Created = inspect.Created,
                    Labels = inspect.Config.Labels?.ToDictionary(l => l.Key, l => l.Value) ?? new(),
                    Ports = MapInspectPorts(inspect.NetworkSettings),
                    HostId = host.Id,
                    HostName = host.Name
                };
            }
            catch (DockerContainerNotFoundException)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get container {ContainerId} from {Host}", containerId, host.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the raw inspect response so callers that need network aliases,
    /// endpoint settings or mount details — fields not surfaced on
    /// <see cref="DockerContainer"/> — can read them without re-inspecting.
    /// </summary>
    public async Task<ContainerInspectResponse?> InspectContainerAsync(
        string containerId, string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                return await client.Containers.InspectContainerAsync(containerId, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect container {ContainerId} from {Host}", containerId, host.Name);
            }
        }

        return null;
    }

    public async Task<bool> StartContainerAsync(string containerId, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken);
            _logger.LogInformation("Started container {ContainerId} on {Host}", containerId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start container {ContainerId} on {Host}", containerId, hostId);
            return false;
        }
    }

    public async Task<bool> StopContainerAsync(string containerId, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
            _logger.LogInformation("Stopped container {ContainerId} on {Host}", containerId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop container {ContainerId} on {Host}", containerId, hostId);
            return false;
        }
    }

    public async Task<bool> RestartContainerAsync(string containerId, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Containers.RestartContainerAsync(containerId,
                new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
            _logger.LogInformation("Restarted container {ContainerId} on {Host}", containerId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart container {ContainerId} on {Host}", containerId, hostId);
            return false;
        }
    }

    public async Task<bool> RemoveContainerAsync(string containerId, string hostId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = force }, cancellationToken);
            _logger.LogInformation("Removed container {ContainerId} on {Host}", containerId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove container {ContainerId} on {Host}", containerId, hostId);
            return false;
        }
    }

    public async Task<List<ContainerLogEntry>> GetContainerLogsAsync(string containerId, string hostId, int tail = 100, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return new List<ContainerLogEntry>();

        try
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
            // The multiplexed overload is required: for containers created without a
            // TTY, Docker frames stdout/stderr with an 8-byte header per chunk. The
            // obsolete overload returns those headers inline and corrupts every line.
            using var stream = await client.Containers.GetContainerLogsAsync(containerId,
                tty: inspect.Config.Tty,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = tail.ToString(),
                    Timestamps = true
                }, cancellationToken);

            var entries = new List<ContainerLogEntry>();
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);

            AppendLogLines(entries, stdout, "stdout");
            AppendLogLines(entries, stderr, "stderr");

            return entries.OrderBy(e => e.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get logs for container {ContainerId}", containerId);
            return new List<ContainerLogEntry>();
        }
    }

    private static List<PortMapping> MapInspectPorts(NetworkSettings? settings)
    {
        var result = new List<PortMapping>();
        if (settings?.Ports == null)
            return result;

        foreach (var (containerPort, bindings) in settings.Ports)
        {
            var parts = containerPort.Split('/', 2);
            if (!int.TryParse(parts[0], out var privatePort))
                continue;

            var protocol = parts.Length > 1 ? parts[1] : "tcp";
            if (bindings == null || bindings.Count == 0)
            {
                result.Add(new PortMapping { PrivatePort = privatePort, Type = protocol });
                continue;
            }

            foreach (var binding in bindings)
            {
                result.Add(new PortMapping
                {
                    Ip = binding.HostIP,
                    PrivatePort = privatePort,
                    PublicPort = int.TryParse(binding.HostPort, out var publicPort) ? publicPort : null,
                    Type = protocol
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a demultiplexed log blob into entries, pulling the RFC3339 timestamp
    /// prefix that Docker prepends when Timestamps is set.
    /// </summary>
    private static void AppendLogLines(List<ContainerLogEntry> entries, string? content, string streamName)
    {
        if (string.IsNullOrEmpty(content))
            return;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0 && DateTime.TryParse(
                    trimmed[..spaceIdx],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var ts))
            {
                entries.Add(new ContainerLogEntry
                {
                    Timestamp = ts,
                    Stream = streamName,
                    Message = trimmed[(spaceIdx + 1)..]
                });
            }
            else
            {
                entries.Add(new ContainerLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = streamName,
                    Message = trimmed
                });
            }
        }
    }

    public async Task<ContainerStatsInfo?> GetContainerStatsAsync(string containerId, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return null;

        try
        {
            ContainerStatsResponse? sample = null;
            var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var progress = new Progress<ContainerStatsResponse>(response =>
            {
                sample ??= response;
                received.TrySetResult(true);
            });

            // One-shot read via the typed progress API. The stream-returning overload
            // is obsolete and required hand-parsing the JSON.
            await client.Containers.GetContainerStatsAsync(containerId,
                new ContainerStatsParameters { Stream = false },
                progress,
                cancellationToken);

            if (sample == null)
            {
                // Progress may deliver slightly after the call returns.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await received.Task.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("No stats sample received for container {ContainerId}", containerId);
                    return null;
                }
            }

            if (sample == null)
                return null;

            var stats = BuildStats(containerId, sample);
            _previousCpuSamples[containerId] = (sample.CPUStats.CPUUsage.TotalUsage, sample.CPUStats.SystemUsage);
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get stats for container {ContainerId}", containerId);
            return null;
        }
    }

    /// <summary>
    /// Maps a Docker stats sample onto <see cref="ContainerStatsInfo"/>.
    /// With Stream=false Docker zeroes precpu_stats, so CPU% is derived from the
    /// previous sample this service saw for the same container; the first reading
    /// after startup therefore reports 0.
    /// </summary>
    private ContainerStatsInfo BuildStats(string containerId, ContainerStatsResponse response)
    {
        var stats = new ContainerStatsInfo
        {
            ContainerId = containerId,
            Name = response.Name?.TrimStart('/') ?? string.Empty,
            CollectedAt = DateTime.UtcNow
        };

        var totalUsage = response.CPUStats.CPUUsage.TotalUsage;
        var systemUsage = response.CPUStats.SystemUsage;

        var prevTotal = response.PreCPUStats.CPUUsage.TotalUsage;
        var prevSystem = response.PreCPUStats.SystemUsage;

        if (prevTotal == 0 && prevSystem == 0 &&
            _previousCpuSamples.TryGetValue(containerId, out var cached))
        {
            prevTotal = cached.TotalUsage;
            prevSystem = cached.SystemUsage;
        }

        if (totalUsage >= prevTotal && systemUsage > prevSystem)
        {
            var cpuDelta = (double)(totalUsage - prevTotal);
            var sysDelta = (double)(systemUsage - prevSystem);
            var onlineCpus = response.CPUStats.OnlineCPUs > 0
                ? response.CPUStats.OnlineCPUs
                : (uint)Math.Max(1, response.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);

            stats.CpuPercent = cpuDelta / sysDelta * onlineCpus * 100;
        }

        stats.MemoryUsage = response.MemoryStats.Usage;
        stats.MemoryLimit = response.MemoryStats.Limit;
        stats.MemoryPercent = stats.MemoryLimit > 0
            ? (double)stats.MemoryUsage / stats.MemoryLimit * 100
            : 0;

        if (response.Networks != null)
        {
            foreach (var net in response.Networks.Values)
            {
                stats.NetworkRx += net.RxBytes;
                stats.NetworkTx += net.TxBytes;
            }
        }

        if (response.BlkioStats?.IoServiceBytesRecursive != null)
        {
            foreach (var entry in response.BlkioStats.IoServiceBytesRecursive)
            {
                if (string.Equals(entry.Op, "read", StringComparison.OrdinalIgnoreCase))
                    stats.BlockRead += entry.Value;
                else if (string.Equals(entry.Op, "write", StringComparison.OrdinalIgnoreCase))
                    stats.BlockWrite += entry.Value;
            }
        }

        return stats;
    }

    #endregion

    #region Image Operations

    public async Task<List<DockerImage>> ListImagesAsync(string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var allImages = new List<DockerImage>();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = true }, cancellationToken);

                var hostImages = images.Select(i => new DockerImage
                {
                    Id = i.ID,
                    Repository = i.RepoTags?.FirstOrDefault()?.Split(':').FirstOrDefault() ?? "<none>",
                    Tag = i.RepoTags?.FirstOrDefault()?.Split(':').LastOrDefault() ?? "<none>",
                    Digest = i.RepoDigests?.FirstOrDefault() ?? string.Empty,
                    Created = i.Created,
                    Size = i.Size,
                    Labels = i.Labels?.ToDictionary(l => l.Key, l => l.Value) ?? new(),
                    HostId = host.Id,
                    HostName = host.Name
                }).ToList();

                allImages.AddRange(hostImages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list images from {Host}", host.Name);
            }
        }

        return allImages;
    }

    public async Task<bool> PullImageAsync(string repository, string tag, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repository, Tag = tag },
                null,
                new Progress<JSONMessage>(),
                cancellationToken);
            _logger.LogInformation("Pulled image {Repository}:{Tag} on {Host}", repository, tag, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull image {Repository}:{Tag} on {Host}", repository, tag, hostId);
            return false;
        }
    }

    public async Task<bool> RemoveImageAsync(string imageId, string hostId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Images.DeleteImageAsync(imageId,
                new ImageDeleteParameters { Force = force }, cancellationToken);
            _logger.LogInformation("Removed image {ImageId} on {Host}", imageId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove image {ImageId} on {Host}", imageId, hostId);
            return false;
        }
    }

    #endregion

    #region Network Operations

    public async Task<List<DockerNetwork>> ListNetworksAsync(string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var allNetworks = new List<DockerNetwork>();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                var networks = await client.Networks.ListNetworksAsync(
                    new NetworksListParameters(), cancellationToken);

                foreach (var n in networks)
                {
                    var subnet = n.IPAM?.Config?.FirstOrDefault()?.Subnet;
                    var gateway = n.IPAM?.Config?.FirstOrDefault()?.Gateway;

                    allNetworks.Add(new DockerNetwork
                    {
                        Id = n.ID,
                        Name = n.Name,
                        Driver = n.Driver,
                        Scope = n.Scope,
                        Created = n.Created,
                        Subnet = subnet,
                        Gateway = gateway,
                        Internal = n.Internal,
                        EnableIpv6 = n.EnableIPv6,
                        Labels = n.Labels?.ToDictionary(l => l.Key, l => l.Value) ?? new(),
                        ConnectedContainers = n.Containers?.Values
                            .Select(c => c.Name?.TrimStart('/') ?? c.EndpointID ?? "unknown")
                            .ToList() ?? new(),
                        HostId = host.Id,
                        HostName = host.Name
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list networks from {Host}", host.Name);
            }
        }

        return allNetworks;
    }

    public async Task<bool> DeleteNetworkAsync(string networkId, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Networks.DeleteNetworkAsync(networkId, cancellationToken);
            _logger.LogInformation("Deleted network {NetworkId} on {Host}", networkId, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete network {NetworkId} on {Host}", networkId, hostId);
            return false;
        }
    }

    public async Task<bool> CreateNetworkAsync(string name, string driver, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = name,
                Driver = string.IsNullOrEmpty(driver) ? "bridge" : driver
            }, cancellationToken);
            _logger.LogInformation("Created network {Name} on {Host}", name, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create network {Name} on {Host}", name, hostId);
            return false;
        }
    }

    #endregion

    #region Volume Operations

    public async Task<List<DockerVolume>> ListVolumesAsync(string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var allVolumes = new List<DockerVolume>();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                var response = await client.Volumes.ListAsync(cancellationToken);

                foreach (var v in response.Volumes ?? new List<VolumeResponse>())
                {
                    allVolumes.Add(new DockerVolume
                    {
                        Name = v.Name,
                        Driver = v.Driver,
                        Mountpoint = v.Mountpoint,
                        CreatedAt = ParseVolumeCreatedAt(v.CreatedAt),
                        Labels = v.Labels?.ToDictionary(l => l.Key, l => l.Value) ?? new(),
                        HostId = host.Id,
                        HostName = host.Name
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list volumes from {Host}", host.Name);
            }
        }

        return allVolumes;
    }

    /// <summary>
    /// Volume timestamps arrive as RFC3339 strings rather than as a parsed
    /// <see cref="DateTime"/> like the other Docker.DotNet responses. Returns null
    /// when the value is absent or unparseable, so the volumes page can show "-"
    /// instead of a bogus 0001-01-01.
    /// </summary>
    private static DateTime? ParseVolumeCreatedAt(string? value) =>
        DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    public async Task<bool> DeleteVolumeAsync(string volumeName, string hostId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Volumes.RemoveAsync(volumeName, force, cancellationToken);
            _logger.LogInformation("Removed volume {Name} on {Host}", volumeName, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove volume {Name} on {Host}", volumeName, hostId);
            return false;
        }
    }

    public async Task<bool> CreateVolumeAsync(string name, string driver, string hostId, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            await client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = name,
                Driver = string.IsNullOrEmpty(driver) ? "local" : driver
            }, cancellationToken);
            _logger.LogInformation("Created volume {Name} on {Host}", name, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create volume {Name} on {Host}", name, hostId);
            return false;
        }
    }

    #endregion

    #region Container Exec

    /// <summary>
    /// Runs a one-shot <c>docker exec</c> and returns the captured stdout/stderr.
    /// This is non-interactive: no PTY, no streaming input. A full terminal
    /// emulator would need a JS client and a bidirectional signalr channel, which
    /// is out of scope for a control panel.
    /// </summary>
    public async Task<ContainerExecResult> ExecAsync(
        string containerId,
        string hostId,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return new ContainerExecResult { Success = false, Error = "主机未连接" };

        try
        {
            // Split the command string the way a shell would, so the user can type
            // "ls -la /app" rather than passing an array. The exec API takes each
            // token as a separate argv element.
            var cmdParts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var createParams = new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = cmdParts
            };

            var exec = await client.Exec.ExecCreateContainerAsync(containerId, createParams, cancellationToken);

            using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, cancellationToken);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);

            var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);

            var output = new StringBuilder();
            if (!string.IsNullOrEmpty(stdout))
                output.Append(stdout);
            if (!string.IsNullOrEmpty(stderr))
            {
                if (output.Length > 0)
                    output.AppendLine();
                output.Append(stderr);
            }

            return new ContainerExecResult
            {
                Success = inspect.ExitCode == 0,
                ExitCode = inspect.ExitCode,
                Output = output.ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exec failed in {ContainerId} on {Host}", containerId, hostId);
            return new ContainerExecResult { Success = false, Error = ex.Message };
        }
    }

    #endregion

    #region Create Container

    public async Task<bool> CreateAndStartContainerAsync(
        CreateContainerRequest request, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(request.HostId, out var client))
            return false;

        try
        {
            var createParams = new CreateContainerParameters
            {
                Name = request.Name,
                Image = request.Image,
                Env = request.Env.Select(e => $"{e.Key}={e.Value}").ToList(),
                Labels = request.Labels,
                ExposedPorts = request.Ports
                    .GroupBy(p => $"{p.PrivatePort}/{p.Type?.ToLowerInvariant() ?? "tcp"}")
                    .ToDictionary(g => g.Key, _ => new EmptyStruct()),
                HostConfig = new HostConfig
                {
                    PortBindings = request.Ports
                        .GroupBy(p => $"{p.PrivatePort}/{p.Type?.ToLowerInvariant() ?? "tcp"}")
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(p => new PortBinding
                                {
                                    HostPort = p.PublicPort?.ToString() ?? string.Empty
                                }).ToList()
                                as IList<PortBinding>),
                    Binds = request.Mounts
                        .Select(m => m.ReadOnly
                            ? $"{m.Source}:{m.Destination}:ro"
                            : $"{m.Source}:{m.Destination}")
                        .ToList(),
                    RestartPolicy = new RestartPolicy
                    {
                        Name = request.RestartPolicy switch
                        {
                            "always" => RestartPolicyKind.Always,
                            "unless-stopped" => RestartPolicyKind.UnlessStopped,
                            "on-failure" => RestartPolicyKind.OnFailure,
                            _ => RestartPolicyKind.No
                        }
                    },
                    NetworkMode = request.Network
                }
            };

            if (!string.IsNullOrEmpty(request.Network))
            {
                createParams.NetworkingConfig = new NetworkingConfig
                {
                    EndpointsConfig = new Dictionary<string, EndpointSettings>
                    {
                        [request.Network] = new EndpointSettings()
                    }
                };
            }

            var created = await client.Containers.CreateContainerAsync(createParams, cancellationToken);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
            _logger.LogInformation("Created and started container {Name} on {Host}", request.Name, request.HostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create container {Name} on {Host}", request.Name, request.HostId);
            return false;
        }
    }

    #endregion

    #region Pull Progress

    /// <summary>
    /// Pulls an image, invoking <paramref name="progress"/> for each layer event
    /// so the UI can show per-layer download status. Each <see cref="JSONMessage"/>
    /// carries a layer id, status string and current/total bytes.
    /// </summary>
    public async Task<bool> PullImageWithProgressAsync(
        string repository,
        string tag,
        string hostId,
        IProgress<JSONMessage>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(hostId, out var client))
            return false;

        try
        {
            var progressHandler = progress != null
                ? new Progress<JSONMessage>(progress.Report)
                : new Progress<JSONMessage>();

            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repository, Tag = tag },
                null,
                progressHandler,
                cancellationToken);
            _logger.LogInformation("Pulled image {Repository}:{Tag} on {Host}", repository, tag, hostId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull image {Repository}:{Tag} on {Host}", repository, tag, hostId);
            return false;
        }
    }

    #endregion

    #region System Df & Prune

    public async Task<List<DockerSystemDf>> SystemDfAsync(string? hostId = null, CancellationToken cancellationToken = default)
    {
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var rows = new List<DockerSystemDf>();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                var data = await client.System.GetSystemInfoAsync(cancellationToken);

                // The Docker API does not expose `system df` directly via the typed
                // client; surface host-level summary instead so the prune page has
                // something actionable.
                rows.Add(new DockerSystemDf
                {
                    Category = $"主机: {host.Name}",
                    Total = $"{data.Containers}",
                    Active = $"{data.ContainersRunning}",
                    Reclaimable = "见下方清理预览",
                    TotalBytes = data.Containers
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "system df failed on {Host}", host.Name);
            }
        }

        return rows;
    }

    /// <summary>
    /// Previews what a prune of the given category would remove, without deleting
    /// anything. Lists the objects so the user can confirm before committing.
    /// </summary>
    public async Task<PrunePreview> PrunePreviewAsync(string category, string? hostId = null, CancellationToken cancellationToken = default)
    {
        var preview = new PrunePreview { Category = category };
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
                continue;

            try
            {
                switch (category.ToLowerInvariant())
                {
                    case "container":
                        var stopped = await client.Containers.ListContainersAsync(
                            new ContainersListParameters { All = true, Filters = new Dictionary<string, IDictionary<string, bool>>
                            {
                                ["status"] = new Dictionary<string, bool> { ["exited"] = true, ["dead"] = true }
                            }}, cancellationToken);
                        preview.Items.AddRange(stopped.Select(c => c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12]));
                        break;

                    case "image":
                        var dangling = await client.Images.ListImagesAsync(
                            new ImagesListParameters
                            {
                                Filters = new Dictionary<string, IDictionary<string, bool>>
                                {
                                    ["dangling"] = new Dictionary<string, bool> { ["true"] = true }
                                }
                            }, cancellationToken);
                        preview.Items.AddRange(dangling.Select(i => i.RepoTags?.FirstOrDefault() ?? i.ID[..19]));
                        break;

                    case "volume":
                        var volumes = await client.Volumes.ListAsync(new VolumesListParameters
                        {
                            Filters = new Dictionary<string, IDictionary<string, bool>>
                            {
                                ["dangling"] = new Dictionary<string, bool> { ["true"] = true }
                            }
                        }, cancellationToken);
                        preview.Items.AddRange((volumes.Volumes ?? new List<VolumeResponse>()).Select(v => v.Name));
                        break;

                    case "network":
                        var networks = await client.Networks.ListNetworksAsync(
                            new NetworksListParameters(), cancellationToken);
                        // Only user-defined networks with no connected containers are prunable.
                        preview.Items.AddRange(networks
                            .Where(n => !string.Equals(n.Driver, "bridge", StringComparison.Ordinal)
                                     && !string.Equals(n.Driver, "host", StringComparison.Ordinal)
                                     && !string.Equals(n.Driver, "none", StringComparison.Ordinal)
                                     && (n.Containers == null || n.Containers.Count == 0))
                            .Select(n => n.Name));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prune preview failed for {Category} on {Host}", category, host.Name);
            }
        }

        preview.Count = preview.Items.Count;
        return preview;
    }

    public async Task<PruneResult> PruneAsync(string category, string? hostId = null, CancellationToken cancellationToken = default)
    {
        var result = new PruneResult { Category = category };
        var hosts = string.IsNullOrEmpty(hostId)
            ? _hosts.Where(h => h.IsEnabled).ToList()
            : _hosts.Where(h => h.Id == hostId && h.IsEnabled).ToList();

        var attempted = 0;
        var succeeded = 0;
        var errors = new List<string>();

        foreach (var host in hosts)
        {
            if (!_clients.TryGetValue(host.Id, out var client))
            {
                errors.Add($"{host.Name}: no active client");
                continue;
            }

            attempted++;
            try
            {
                switch (category.ToLowerInvariant())
                {
                    case "container":
                        await client.Containers.PruneContainersAsync(new ContainersPruneParameters(), cancellationToken);
                        break;
                    case "image":
                        await client.Images.PruneImagesAsync(new ImagesPruneParameters
                        {
                            Filters = new Dictionary<string, IDictionary<string, bool>>
                            {
                                ["dangling"] = new Dictionary<string, bool> { ["true"] = true }
                            }
                        }, cancellationToken);
                        break;
                    case "volume":
                        await client.Volumes.PruneAsync(new VolumesPruneParameters(), cancellationToken);
                        break;
                    case "network":
                        // The parameters type is optional and its name differs across
                        // Docker.DotNet versions, so let the default apply.
                        await client.Networks.PruneNetworksAsync(cancellationToken: cancellationToken);
                        break;
                }

                succeeded++;
                _logger.LogInformation("Pruned {Category} on {Host}", category, host.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"{host.Name}: {ex.Message}");
                _logger.LogError(ex, "Prune {Category} failed on {Host}", category, host.Name);
            }
        }

        result.Success = attempted > 0 && succeeded == attempted && errors.Count == 0;
        result.Error = errors.Count == 0 ? null : string.Join("; ", errors);

        return result;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _configService.OnConfigChanged -= ApplyDockerConfiguration;
            _healthCheckTimer?.Dispose();
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _disposed = true;
        }
    }
}

/// <summary>Result of a one-shot docker exec invocation.</summary>
public class ContainerExecResult
{
    public bool Success { get; set; }
    public long ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
}
