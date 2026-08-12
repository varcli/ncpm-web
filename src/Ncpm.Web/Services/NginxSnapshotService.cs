using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Snapshots the active nginx config directories before a publish so a bad
/// change can be rolled back wholesale, not just to the single file that
/// PublishConfigAsync restores. Each snapshot is a timestamped directory tree
/// under data/backups with a snapshot.yml holding its metadata.
/// </summary>
public class NginxSnapshotService
{
    private readonly ConfigService _configService;
    private readonly ILogger<NginxSnapshotService> _logger;

    public NginxSnapshotService(
        ConfigService configService,
        ILogger<NginxSnapshotService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    private string BackupsRoot => Path.Combine("data", "backups");

    /// <summary>
    /// Zips the active and stream config directories into a timestamped snapshot.
    /// Returns the snapshot metadata, or null if there was nothing to back up.
    /// </summary>
    public async Task<NginxSnapshot?> CreateAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        var config = _configService.LoadAppConfig().Nginx;
        var active = config.ActivePath;
        var stream = config.StreamPath;

        if (!Directory.Exists(active) && !Directory.Exists(stream))
            return null;

        Directory.CreateDirectory(BackupsRoot);

        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var snapshotDir = Path.Combine(BackupsRoot, id);
        while (Directory.Exists(snapshotDir))
        {
            id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..29];
            snapshotDir = Path.Combine(BackupsRoot, id);
        }
        Directory.CreateDirectory(snapshotDir);

        var snapshot = new NginxSnapshot
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            Reason = reason
        };

        try
        {
            if (Directory.Exists(active))
            {
                var dest = Path.Combine(snapshotDir, "active");
                CopyDirectory(active, dest);
                snapshot.Files.AddRange(Directory.GetFiles(dest, "*.conf", SearchOption.AllDirectories)
                    .Select(f => new NginxSnapshotFile
                    {
                        RelativePath = Path.GetRelativePath(snapshotDir, f).Replace('\\', '/'),
                        Size = new FileInfo(f).Length
                    }));
            }

            if (Directory.Exists(stream))
            {
                var dest = Path.Combine(snapshotDir, "stream");
                CopyDirectory(stream, dest);
                snapshot.Files.AddRange(Directory.GetFiles(dest, "*.conf", SearchOption.AllDirectories)
                    .Select(f => new NginxSnapshotFile
                    {
                        RelativePath = Path.GetRelativePath(snapshotDir, f).Replace('\\', '/'),
                        Size = new FileInfo(f).Length
                    }));
            }

            // Persist metadata alongside the files so the list view does not need to
            // walk the directory tree on every render.
            var metaPath = Path.Combine(snapshotDir, "snapshot.yml");
            await File.WriteAllTextAsync(metaPath,
                $"id: {snapshot.Id}\ncreatedAt: {snapshot.CreatedAt:O}\nreason: {reason ?? ""}\n",
                cancellationToken);

            _logger.LogInformation("Created nginx snapshot {Id} with {Count} files", id, snapshot.Files.Count);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create nginx snapshot {Id}", id);
            return null;
        }
    }

    public List<NginxSnapshot> List()
    {
        var result = new List<NginxSnapshot>();
        if (!Directory.Exists(BackupsRoot))
            return result;

        foreach (var dir in Directory.GetDirectories(BackupsRoot).OrderByDescending(d => d))
        {
            var id = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, "snapshot.yml");
            string? reason = null;
            // Fall back to the directory timestamp when metadata is missing or
            // unparseable, so a hand-copied snapshot dir still lists correctly.
            var createdAt = Directory.GetCreationTimeUtc(dir);

            if (File.Exists(metaPath))
            {
                foreach (var line in File.ReadAllLines(metaPath))
                {
                    if (line.StartsWith("reason: "))
                    {
                        var value = line["reason: ".Length..].Trim();
                        reason = string.IsNullOrEmpty(value) ? null : value;
                    }
                    else if (line.StartsWith("createdAt: ") && DateTime.TryParse(
                                 line["createdAt: ".Length..],
                                 null,
                                 System.Globalization.DateTimeStyles.RoundtripKind,
                                 out var parsed))
                    {
                        createdAt = parsed;
                    }
                }
            }

            result.Add(new NginxSnapshot
            {
                Id = id,
                CreatedAt = createdAt,
                Reason = reason,
                Files = Directory.GetFiles(dir, "*.conf", SearchOption.AllDirectories)
                    .Select(f => new NginxSnapshotFile
                    {
                        RelativePath = Path.GetRelativePath(dir, f).Replace('\\', '/'),
                        Size = new FileInfo(f).Length
                    }).ToList()
            });
        }

        return result;
    }

    /// <summary>
    /// Restores a snapshot by copying its files back over the active and stream
    /// directories. The caller is expected to run nginx -t and reload afterwards;
    /// this method only touches files so a failed reload can still be recovered.
    /// </summary>
    public async Task<bool> RestoreAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var config = _configService.LoadAppConfig().Nginx;
        var snapshotDir = Path.Combine(BackupsRoot, snapshotId);
        if (!Directory.Exists(snapshotDir))
            return false;

        try
        {
            var activeSrc = Path.Combine(snapshotDir, "active");
            Directory.CreateDirectory(config.ActivePath);
            ClearDirectory(config.ActivePath);
            if (Directory.Exists(activeSrc))
            {
                CopyDirectory(activeSrc, config.ActivePath);
            }

            var streamSrc = Path.Combine(snapshotDir, "stream");
            Directory.CreateDirectory(config.StreamPath);
            ClearDirectory(config.StreamPath);
            if (Directory.Exists(streamSrc))
            {
                CopyDirectory(streamSrc, config.StreamPath);
            }

            _logger.LogInformation("Restored nginx snapshot {Id}", snapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore nginx snapshot {Id}", snapshotId);
            return false;
        }
    }

    /// <summary>
    /// Restores, validates, and reloads a snapshot as one transaction. A safety
    /// snapshot of the current tree is restored automatically on any failure.
    /// </summary>
    public async Task<bool> RestoreAndReloadAsync(
        string snapshotId,
        NginxService nginxService,
        CancellationToken cancellationToken = default)
    {
        var safety = await CreateAsync($"回滚 {snapshotId} 前自动备份", cancellationToken);
        if (!await RestoreAsync(snapshotId, cancellationToken))
            return false;

        if (await nginxService.ValidateConfigAsync(cancellationToken)
            && await nginxService.ReloadAsync(cancellationToken))
        {
            return true;
        }

        _logger.LogWarning("Snapshot {Id} failed validation/reload; restoring safety snapshot", snapshotId);
        if (safety != null && await RestoreAsync(safety.Id, CancellationToken.None))
        {
            await nginxService.ValidateConfigAsync(CancellationToken.None);
            await nginxService.ReloadAsync(CancellationToken.None);
        }
        else if (safety == null)
        {
            var config = _configService.LoadAppConfig().Nginx;
            Directory.CreateDirectory(config.ActivePath);
            Directory.CreateDirectory(config.StreamPath);
            ClearDirectory(config.ActivePath);
            ClearDirectory(config.StreamPath);
            await nginxService.ValidateConfigAsync(CancellationToken.None);
            await nginxService.ReloadAsync(CancellationToken.None);
        }

        return false;
    }

    public bool Delete(string snapshotId)
    {
        var snapshotDir = Path.Combine(BackupsRoot, snapshotId);
        if (!Directory.Exists(snapshotDir))
            return false;

        try
        {
            Directory.Delete(snapshotDir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete nginx snapshot {Id}", snapshotId);
            return false;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void ClearDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        foreach (var f in dir.GetFiles()) f.Delete();
        foreach (var d in dir.GetDirectories()) d.Delete(recursive: true);
    }
}
