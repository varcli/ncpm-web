using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Orchestrates Docker Compose stacks by driving the <c>docker compose</c> CLI.
/// Reimplementing compose semantics over the raw Docker API would mean rebuilding
/// dependency ordering, healthcheck gating, profiles and network/volume creation,
/// so the CLI is invoked instead and behaviour matches native compose exactly.
/// </summary>
public class ComposeService
{
    private readonly ConfigService _configService;
    private readonly DockerService _dockerService;
    private readonly ILogger<ComposeService> _logger;

    /// <summary>
    /// Project names must be safe to use as a directory name and as a compose
    /// project identifier. Compose itself only accepts lowercase alphanumerics,
    /// dash and underscore.
    /// </summary>
    private static readonly Regex ValidProjectName = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public ComposeService(ConfigService configService, DockerService dockerService, ILogger<ComposeService> logger)
    {
        _configService = configService;
        _dockerService = dockerService;
        _logger = logger;
    }

    private ComposeConfig Config => _configService.LoadAppConfig().Compose;

    /// <summary>Last command failure, surfaced to the UI.</summary>
    public string? LastError { get; private set; }

    #region Paths & validation

    /// <summary>
    /// Rejects names that could escape the stacks directory or confuse compose.
    /// Every public entry point that takes a project name goes through this.
    /// </summary>
    public static bool IsValidProjectName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && ValidProjectName.IsMatch(name);

    private string StacksRoot => Config.StacksPath;

    private string ProjectDir(string name) => Path.Combine(StacksRoot, name);

    private string ComposeFilePath(string name) => Path.Combine(ProjectDir(name), "docker-compose.yml");

    /// <summary>
    /// Maps an in-container project directory onto its host-side equivalent so the
    /// daemon resolves relative bind mounts against a path that actually exists on
    /// the host. Returns the input unchanged when no prefix is configured.
    /// </summary>
    private string HostProjectDir(string name)
    {
        var local = Path.GetFullPath(ProjectDir(name));
        var prefix = Config.HostPathPrefix;

        if (string.IsNullOrWhiteSpace(prefix))
            return local;

        var stacksRoot = Path.GetFullPath(StacksRoot);
        if (!local.StartsWith(stacksRoot, StringComparison.Ordinal))
            return local;

        var relative = Path.GetRelativePath(stacksRoot, local);
        return Path.Combine(prefix.TrimEnd('/', '\\'), relative).Replace('\\', '/');
    }

    /// <summary>True when a host path prefix is needed but not configured.</summary>
    public bool HasBindMountCaveat => string.IsNullOrWhiteSpace(Config.HostPathPrefix);

    #endregion

    #region Discovery

    /// <summary>
    /// Lists every compose project visible to the panel, merging two sources:
    /// stacks whose compose file the panel owns on disk, and stacks discovered from
    /// the compose labels Docker stamps on running containers. A managed stack that
    /// is also running appears once, with its live services attached.
    /// </summary>
    public async Task<List<ComposeProject>> ListProjectsAsync(
        string? hostId = null,
        CancellationToken cancellationToken = default)
    {
        var projects = new Dictionary<string, ComposeProject>(StringComparer.Ordinal);

        // Managed stacks first so IsManaged and the on-disk paths win.
        foreach (var project in LoadManagedProjects(hostId))
        {
            projects[project.Name] = project;
        }

        var containers = await _dockerService.ListContainersAsync(hostId, cancellationToken);

        foreach (var group in containers
                     .Where(c => c.Labels.ContainsKey(ComposeLabels.Project))
                     .GroupBy(c => c.Labels[ComposeLabels.Project]))
        {
            var name = group.Key;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var first = group.First();

            if (!projects.TryGetValue(name, out var project))
            {
                project = new ComposeProject
                {
                    Name = name,
                    IsManaged = false,
                    WorkingDir = first.Labels.GetValueOrDefault(ComposeLabels.WorkingDir) ?? string.Empty,
                    ConfigFile = first.Labels.GetValueOrDefault(ComposeLabels.ConfigFiles),
                    HostId = first.HostId,
                    HostName = first.HostName
                };
                projects[name] = project;
            }

            project.Services = group
                .Select(c => new ComposeServiceInfo
                {
                    Name = c.Labels.GetValueOrDefault(ComposeLabels.Service) ?? c.Name,
                    ContainerId = c.Id,
                    ContainerName = c.Name,
                    Image = c.Image,
                    State = c.State,
                    Status = c.Status,
                    Ports = c.Ports
                })
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

            project.Status = project.RunningCount == 0
                ? ComposeProjectStatus.Down
                : project.RunningCount == project.ServiceCount
                    ? ComposeProjectStatus.Running
                    : ComposeProjectStatus.Partial;
        }

        return projects.Values
            .OrderByDescending(p => p.IsManaged)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Scans the stacks directory for panel-owned compose files.</summary>
    private List<ComposeProject> LoadManagedProjects(string? hostId)
    {
        var result = new List<ComposeProject>();

        if (!Directory.Exists(StacksRoot))
            return result;

        var defaultHost = _dockerService.GetDefaultHost();

        foreach (var dir in Directory.GetDirectories(StacksRoot))
        {
            var name = Path.GetFileName(dir);
            if (!IsValidProjectName(name))
                continue;

            var file = ResolveComposeFile(dir);
            if (file == null)
                continue;

            var ownerHostId = ReadHostBinding(dir) ?? defaultHost?.Id ?? string.Empty;

            // Host filter applies to managed stacks through their recorded binding.
            if (!string.IsNullOrEmpty(hostId) && ownerHostId != hostId)
                continue;

            var host = _dockerService.GetHost(ownerHostId);

            result.Add(new ComposeProject
            {
                Name = name,
                IsManaged = true,
                WorkingDir = dir,
                ConfigFile = file,
                HostId = ownerHostId,
                HostName = host?.Name ?? ownerHostId,
                Status = ComposeProjectStatus.Down,
                UpdatedAt = File.GetLastWriteTimeUtc(file)
            });
        }

        return result;
    }

    /// <summary>Picks the compose file in a project directory, preferring the canonical name.</summary>
    private static string? ResolveComposeFile(string dir)
    {
        string[] candidates =
        [
            Path.Combine(dir, "docker-compose.yml"),
            Path.Combine(dir, "docker-compose.yaml"),
            Path.Combine(dir, "compose.yml"),
            Path.Combine(dir, "compose.yaml")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads the Docker host a managed stack is bound to. Stored as a plain file so
    /// the binding survives restarts without adding another YAML schema.
    /// </summary>
    private static string? ReadHostBinding(string dir)
    {
        var file = Path.Combine(dir, ".ncpm-host");
        if (!File.Exists(file))
            return null;

        try
        {
            var value = File.ReadAllText(file).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ComposeProject?> GetProjectAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProjectName(name))
            return null;

        var projects = await ListProjectsAsync(null, cancellationToken);
        return projects.FirstOrDefault(p => p.Name == name);
    }

    #endregion

    #region Stack file CRUD

    /// <summary>Reads a managed stack's compose file, or null when not panel-owned.</summary>
    public async Task<string?> ReadStackFileAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidProjectName(name))
            return null;

        var file = ResolveComposeFile(ProjectDir(name));
        if (file == null)
            return null;

        try
        {
            return await File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to read compose file for {Project}", name);
            return null;
        }
    }

    /// <summary>
    /// Validates YAML with <c>docker compose config -q</c> and only then writes it.
    /// The candidate is validated from a temporary file so a rejected edit never
    /// replaces a working stack definition.
    /// </summary>
    public async Task<ComposeCommandResult> SaveStackFileAsync(
        string name,
        string content,
        string? hostId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProjectName(name))
        {
            return Fail("项目名只能包含小写字母、数字、下划线和短横线，且以字母或数字开头");
        }

        var dir = ProjectDir(name);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return Fail($"无法创建项目目录: {ex.Message}");
        }

        // Validate the candidate in a scratch directory. Compose resolves relative
        // paths against the file's directory, so the scratch copy lives alongside
        // the real one to keep that resolution identical.
        var tempFile = Path.Combine(dir, $".candidate-{Guid.NewGuid():N}.yml");

        try
        {
            await File.WriteAllTextAsync(tempFile, content, cancellationToken);

            var validation = await RunComposeAsync(
                hostId,
                dir,
                ["-f", tempFile, "-p", name, "config", "-q"],
                cancellationToken);

            if (!validation.Success)
            {
                LastError = validation.Output;
                _logger.LogWarning("Rejected compose file for {Project}: {Error}", name, validation.Output);
                return validation;
            }

            var target = ResolveComposeFile(dir) ?? ComposeFilePath(name);
            await File.WriteAllTextAsync(target, content, cancellationToken);

            if (!string.IsNullOrEmpty(hostId))
            {
                await File.WriteAllTextAsync(Path.Combine(dir, ".ncpm-host"), hostId, cancellationToken);
            }

            _logger.LogInformation("Saved compose file for {Project}", name);
            LastError = null;
            return new ComposeCommandResult { Success = true, Output = "配置校验通过并已保存" };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to save compose file for {Project}", name);
            return Fail(ex.Message);
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    /// <summary>
    /// Removes a managed stack from disk. Containers are torn down first so the
    /// stack cannot be left orphaned with no compose file to manage it.
    /// </summary>
    public async Task<ComposeCommandResult> DeleteStackAsync(
        string name,
        bool removeVolumes = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProjectName(name))
            return Fail("非法的项目名");

        var dir = ProjectDir(name);
        if (!Directory.Exists(dir))
            return Fail("项目不存在或不由面板托管");

        var down = await DownAsync(name, removeVolumes, cancellationToken: cancellationToken);
        if (!down.Success)
            return down;

        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted managed stack {Project}", name);
            return new ComposeCommandResult { Success = true, Output = "项目已删除" };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to delete stack {Project}", name);
            return Fail(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a stray candidate file is harmless, it is filtered out
            // of discovery by the compose-file name check.
        }
    }

    #endregion

    #region Operations

    public Task<ComposeCommandResult> UpAsync(
        string project,
        string? service = null,
        bool pullFirst = false,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "up", "-d", "--remove-orphans" };
        if (pullFirst)
        {
            args.Add("--pull");
            args.Add("always");
        }
        if (!string.IsNullOrEmpty(service))
            args.Add(service);

        return RunProjectCommandAsync(project, args, cancellationToken);
    }

    public Task<ComposeCommandResult> DownAsync(
        string project,
        bool removeVolumes = false,
        bool removeImages = false,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "down", "--remove-orphans" };
        if (removeVolumes)
            args.Add("-v");
        if (removeImages)
        {
            args.Add("--rmi");
            args.Add("local");
        }

        return RunProjectCommandAsync(project, args, cancellationToken);
    }

    public Task<ComposeCommandResult> RestartAsync(
        string project,
        string? service = null,
        CancellationToken cancellationToken = default)
        => RunProjectCommandAsync(project, Compose("restart", service), cancellationToken);

    public Task<ComposeCommandResult> StartAsync(
        string project,
        string? service = null,
        CancellationToken cancellationToken = default)
        => RunProjectCommandAsync(project, Compose("start", service), cancellationToken);

    public Task<ComposeCommandResult> StopAsync(
        string project,
        string? service = null,
        CancellationToken cancellationToken = default)
        => RunProjectCommandAsync(project, Compose("stop", service), cancellationToken);

    public Task<ComposeCommandResult> PullAsync(
        string project,
        string? service = null,
        CancellationToken cancellationToken = default)
        => RunProjectCommandAsync(project, Compose("pull", service), cancellationToken);

    /// <summary>Fetches the merged, fully-resolved compose configuration.</summary>
    public Task<ComposeCommandResult> ConfigAsync(
        string project,
        CancellationToken cancellationToken = default)
        => RunProjectCommandAsync(project, ["config"], cancellationToken);

    public Task<ComposeCommandResult> LogsAsync(
        string project,
        string? service = null,
        int tail = 200,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "logs", "--no-color", "--tail", tail.ToString() };
        if (!string.IsNullOrEmpty(service))
            args.Add(service);

        return RunProjectCommandAsync(project, args, cancellationToken);
    }

    private static List<string> Compose(string verb, string? service)
    {
        var args = new List<string> { verb };
        if (!string.IsNullOrEmpty(service))
            args.Add(service);
        return args;
    }

    /// <summary>
    /// Resolves a project to its compose file and host, then runs the command.
    /// Works for both managed stacks and externally-created ones, as long as the
    /// latter's working directory is reachable from the panel.
    /// </summary>
    private async Task<ComposeCommandResult> RunProjectCommandAsync(
        string project,
        List<string> verbArgs,
        CancellationToken cancellationToken)
    {
        if (!IsValidProjectName(project))
            return Fail("非法的项目名");

        var managedDir = ProjectDir(project);
        var managedFile = ResolveComposeFile(managedDir);

        if (managedFile != null)
        {
            var hostId = ReadHostBinding(managedDir) ?? _dockerService.GetDefaultHost()?.Id;

            var args = new List<string>
            {
                "-f", managedFile,
                "-p", project,
                "--project-directory", HostProjectDir(project)
            };
            args.AddRange(verbArgs);

            return await RunComposeAsync(hostId, managedDir, args, cancellationToken);
        }

        // Externally-managed stack: address it by project name. Compose can act on a
        // running project without its file for lifecycle verbs, which covers the
        // common case of adopting a stack the panel did not create.
        var discovered = await GetProjectAsync(project, cancellationToken);
        if (discovered == null)
            return Fail("项目不存在");

        var externalArgs = new List<string> { "-p", project };

        if (!string.IsNullOrWhiteSpace(discovered.ConfigFile) && File.Exists(discovered.ConfigFile))
        {
            externalArgs.Insert(0, discovered.ConfigFile);
            externalArgs.Insert(0, "-f");
        }

        externalArgs.AddRange(verbArgs);

        var workDir = Directory.Exists(discovered.WorkingDir) ? discovered.WorkingDir : null;
        return await RunComposeAsync(discovered.HostId, workDir, externalArgs, cancellationToken);
    }

    #endregion

    #region Command execution

    /// <summary>
    /// Runs <c>docker compose</c> with the given arguments. Arguments are passed as
    /// a list rather than concatenated into a command line, so values that contain
    /// spaces or shell metacharacters cannot alter the invocation.
    /// </summary>
    private async Task<ComposeCommandResult> RunComposeAsync(
        string? hostId,
        string? workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var config = Config;

        var startInfo = new ProcessStartInfo
        {
            FileName = config.DockerExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("compose");
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        // Target a specific daemon by pointing the CLI at it. The default host uses
        // the ambient socket, so DOCKER_HOST is only set when it differs.
        if (!string.IsNullOrEmpty(hostId))
        {
            var host = _dockerService.GetHost(hostId);
            if (host != null && host.Type != DockerHostType.Local)
            {
                startInfo.Environment["DOCKER_HOST"] = host.ConnectionString;
            }
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Drain both pipes before waiting, or a full stderr buffer deadlocks.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, config.CommandTimeout)));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return Fail($"命令超时（{config.CommandTimeout} 秒）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
                output.Append(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (output.Length > 0)
                    output.AppendLine();
                output.Append(stderr.TrimEnd());
            }

            var result = new ComposeCommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output.ToString()
            };

            if (!result.Success)
            {
                LastError = result.Output;
                _logger.LogWarning("docker compose exited {Code}: {Output}", result.ExitCode, result.Output);
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to run docker compose");
            return Fail(ex.Message);
        }
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
            // The process may have exited between the check and the kill.
        }
    }

    /// <summary>Verifies the docker CLI and compose plugin are actually present.</summary>
    public async Task<ComposeCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
        => await RunComposeAsync(null, null, ["version"], cancellationToken);

    private ComposeCommandResult Fail(string message)
    {
        LastError = message;
        return new ComposeCommandResult { Success = false, ExitCode = -1, Output = message };
    }

    #endregion
}
