using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ncpm.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ncpm.Services;

public class AuthService : IDisposable
{
    private static readonly TimeSpan TokenCleanupInterval = TimeSpan.FromMinutes(5);
    public const int MinimumPasswordLength = 12;

    private readonly ConfigService _configService;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, AuthToken> _activeTokens = new();
    private readonly string _usersFilePath;
    private readonly string _sessionsFilePath;
    private readonly string _initialPasswordFilePath;
    private readonly object _usersLock = new();
    private List<User> _users = new();
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private Timer? _cleanupTimer;
    private bool _disposed;

    public AuthService(ConfigService configService, ILogger<AuthService> logger)
    {
        _configService = configService;
        _logger = logger;
        _usersFilePath = Path.Combine(configService.ConfigPath, "users.yml");
        _sessionsFilePath = Path.Combine(configService.SecretsPath, "sessions.yml");
        _initialPasswordFilePath = Path.Combine(configService.SecretsPath, "initial-admin-password");

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadUsers();
        MarkLegacyDefaultPasswordForChange();
        EnsureDefaultAdmin();
        LoadSessions();

        // CleanupExpiredTokens had no caller, so expired entries accumulated for
        // the process lifetime. Drive it on a timer instead.
        _cleanupTimer = new Timer(_ => CleanupExpiredTokens(), null,
            TokenCleanupInterval, TokenCleanupInterval);
    }

    private void LoadUsers()
    {
        if (!File.Exists(_usersFilePath))
        {
            _users = new List<User>();
            return;
        }

        try
        {
            var content = File.ReadAllText(_usersFilePath);
            _users = _deserializer.Deserialize<List<User>>(content) ?? new List<User>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load users");
            _users = new List<User>();
        }
    }

    private void SaveUsers()
    {
        try
        {
            var dir = Path.GetDirectoryName(_usersFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            WriteFileAtomically(_usersFilePath, _serializer.Serialize(_users), restrictToOwner: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save users");
        }
    }

    /// <summary>
    /// Restores sessions issued before the last restart. Without this, every panel
    /// restart silently logs out every browser holding a still-valid token.
    /// </summary>
    private void LoadSessions()
    {
        if (!File.Exists(_sessionsFilePath))
            return;

        try
        {
            var content = File.ReadAllText(_sessionsFilePath);
            var tokens = _deserializer.Deserialize<List<AuthToken>>(content) ?? new List<AuthToken>();

            var restored = 0;
            var migrated = false;
            foreach (var token in tokens.Where(t => !t.IsExpired && !string.IsNullOrEmpty(t.Token)))
            {
                if (!IsTokenHash(token.Token))
                {
                    token.Token = HashToken(token.Token);
                    migrated = true;
                }

                _activeTokens[token.Token] = token;
                restored++;
            }

            if (restored > 0)
            {
                _logger.LogInformation("Restored {Count} active session(s)", restored);
            }

            // Drop anything that expired while the panel was down.
            if (restored != tokens.Count || migrated)
            {
                SaveSessions();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sessions");
        }
    }

    private void SaveSessions()
    {
        try
        {
            var dir = Path.GetDirectoryName(_sessionsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tokens = _activeTokens.Values.Where(t => !t.IsExpired).ToList();
            WriteFileAtomically(_sessionsFilePath, _serializer.Serialize(tokens), restrictToOwner: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sessions");
        }
    }

    private void EnsureDefaultAdmin()
    {
        if (_users.Any())
            return;

        var username = Environment.GetEnvironmentVariable("NCPM_ADMIN_USERNAME")?.Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "admin";

        var configuredPassword = ReadBootstrapPassword();
        var generatedPassword = string.IsNullOrWhiteSpace(configuredPassword);
        var password = generatedPassword ? GenerateBootstrapPassword() : configuredPassword!;
        ValidatePassword(password);

        _logger.LogInformation("Creating initial administrator {Username}", username);
        var (hash, salt) = PasswordHelper.HashPassword(password);

        var admin = new User
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Username = username,
            PasswordHash = hash,
            PasswordSalt = salt,
            DisplayName = "Administrator",
            Email = "admin@localhost",
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow
        };

        _users.Add(admin);
        SaveUsers();

        if (generatedPassword)
        {
            WriteFileAtomically(
                _initialPasswordFilePath,
                $"username: {username}{Environment.NewLine}password: {password}{Environment.NewLine}",
                restrictToOwner: true);
            _logger.LogCritical(
                "Initial administrator password was generated. Read it from {Path}, sign in, then change it immediately.",
                _initialPasswordFilePath);
        }
        else
        {
            _logger.LogWarning(
                "Initial administrator {Username} was created from NCPM_ADMIN_PASSWORD and must change the password after sign-in.",
                username);
        }
    }

    private void MarkLegacyDefaultPasswordForChange()
    {
        var changed = false;
        foreach (var user in _users.Where(user =>
                     user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)
                     && PasswordHelper.VerifyPassword("admin123", user.PasswordHash, user.PasswordSalt)))
        {
            user.MustChangePassword = true;
            changed = true;
        }

        if (changed)
        {
            SaveUsers();
            _logger.LogCritical(
                "Legacy default administrator password detected. The account must change its password before normal use.");
        }
    }

    public LoginResult Login(LoginRequest request)
    {
        lock (_usersLock)
        {
            return LoginCore(request);
        }
    }

    private LoginResult LoginCore(LoginRequest request)
    {
        var user = _users.FirstOrDefault(u =>
            u.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase));

        if (user == null)
        {
            _logger.LogWarning("Login failed: User {Username} not found", request.Username);
            return new LoginResult { Success = false, ErrorMessage = "Invalid username or password" };
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login failed: User {Username} is disabled", request.Username);
            return new LoginResult { Success = false, ErrorMessage = "Account is disabled" };
        }

        if (user.IsLockedOut)
        {
            _logger.LogWarning("Login failed: User {Username} is locked out until {LockedOut}", 
                request.Username, user.LockedOutUntil);
            return new LoginResult { Success = false, ErrorMessage = "Account is temporarily locked" };
        }

        if (!PasswordHelper.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            user.FailedLoginAttempts++;
            
            var config = _configService.LoadAppConfig();
            if (user.FailedLoginAttempts >= config.Security.MaxLoginAttempts)
            {
                user.LockedOutUntil = DateTime.UtcNow.AddSeconds(config.Security.LockoutDuration);
                _logger.LogWarning("User {Username} locked out after {Attempts} failed attempts", 
                    request.Username, user.FailedLoginAttempts);
            }
            
            SaveUsers();
            return new LoginResult { Success = false, ErrorMessage = "Invalid username or password" };
        }

        // Success - reset failed attempts
        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        SaveUsers();

        // Generate token
        var (rawToken, session) = GenerateToken(user);
        _activeTokens[session.Token] = session;
        SaveSessions();

        _logger.LogInformation("User {Username} logged in successfully", request.Username);

        return new LoginResult
        {
            Success = true,
            Token = rawToken,
            User = user
        };
    }

    public void Logout(string token)
    {
        if (_activeTokens.TryRemove(HashToken(token), out var removed))
        {
            SaveSessions();
            _logger.LogInformation("User {Username} logged out", removed.Username);
        }
    }

    public AuthToken? ValidateToken(string token)
    {
        var tokenHash = HashToken(token);
        if (!_activeTokens.TryGetValue(tokenHash, out var authToken))
            return null;

        if (authToken.IsExpired)
        {
            _activeTokens.TryRemove(tokenHash, out _);
            return null;
        }

        return authToken;
    }

    public User? GetUser(string userId)
    {
        lock (_usersLock)
        {
            return _users.FirstOrDefault(u => u.Id == userId);
        }
    }

    public User? GetUserByUsername(string username)
    {
        lock (_usersLock)
        {
            return _users.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }
    }

    public List<User> GetAllUsers()
    {
        lock (_usersLock)
        {
            return _users.ToList();
        }
    }

    public bool CreateUser(User user, string password)
    {
        ValidatePassword(password);
        lock (_usersLock)
        {
            if (_users.Any(u => u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var (hash, salt) = PasswordHelper.HashPassword(password);
            user.Id = Guid.NewGuid().ToString("N")[..8];
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.CreatedAt = DateTime.UtcNow;

            _users.Add(user);
            SaveUsers();
            return true;
        }
    }

    public bool UpdateUser(User user)
    {
        lock (_usersLock)
        {
            var existing = _users.FirstOrDefault(u => u.Id == user.Id);
            if (existing == null)
                return false;

            existing.DisplayName = user.DisplayName;
            existing.Email = user.Email;
            existing.Role = user.Role;
            existing.IsActive = user.IsActive;
            SaveUsers();
            return true;
        }
    }

    public bool ChangePassword(string userId, string newPassword)
    {
        ValidatePassword(newPassword);
        lock (_usersLock)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
                return false;

            var (hash, salt) = PasswordHelper.HashPassword(newPassword);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.MustChangePassword = false;
            SaveUsers();

            // A password change must not leave older sessions usable.
            RevokeSessionsForUser(userId);
            TryDeleteInitialPasswordFile();
            return true;
        }
    }

    public bool ChangeOwnPassword(string userId, string currentPassword, string newPassword, out string? error)
    {
        error = null;
        try
        {
            ValidatePassword(newPassword);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        lock (_usersLock)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId && u.IsActive);
            if (user == null)
            {
                error = "用户不存在或已停用";
                return false;
            }

            if (!PasswordHelper.VerifyPassword(currentPassword, user.PasswordHash, user.PasswordSalt))
            {
                error = "当前密码不正确";
                return false;
            }

            if (PasswordHelper.VerifyPassword(newPassword, user.PasswordHash, user.PasswordSalt))
            {
                error = "新密码不能与当前密码相同";
                return false;
            }

            var (hash, salt) = PasswordHelper.HashPassword(newPassword);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.MustChangePassword = false;
            SaveUsers();
            RevokeSessionsForUser(userId);
            TryDeleteInitialPasswordFile();
            _logger.LogInformation("User {Username} changed their password", user.Username);
            return true;
        }
    }

    public bool DeleteUser(string userId)
    {
        lock (_usersLock)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user == null || user.Role == UserRole.Admin)
                return false;

            _users.Remove(user);
            SaveUsers();
            RevokeSessionsForUser(userId);
            return true;
        }
    }

    /// <summary>Invalidates every active session belonging to a user.</summary>
    public void RevokeSessionsForUser(string userId)
    {
        var revoked = _activeTokens
            .Where(kvp => kvp.Value.UserId == userId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var token in revoked)
        {
            _activeTokens.TryRemove(token, out _);
        }

        if (revoked.Count > 0)
        {
            SaveSessions();
            _logger.LogInformation("Revoked {Count} session(s) for user {UserId}", revoked.Count, userId);
        }
    }

    private (string RawToken, AuthToken Session) GenerateToken(User user)
    {
        var tokenBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);
        var rawToken = Convert.ToBase64String(tokenBytes);

        var config = _configService.LoadAppConfig();

        return (rawToken, new AuthToken
        {
            // Persist only a one-way digest. The raw bearer token is returned to
            // the browser once and is never written to sessions.yml.
            Token = HashToken(rawToken),
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role,
            ExpiresAt = DateTime.UtcNow.AddSeconds(config.Security.SessionTimeout)
        });
    }

    private static bool IsTokenHash(string token) =>
        token.StartsWith("sha256:", StringComparison.Ordinal)
        && token.Length == 71;

    private static string HashToken(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    public void CleanupExpiredTokens()
    {
        var expiredTokens = _activeTokens
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        if (expiredTokens.Count == 0)
            return;

        foreach (var token in expiredTokens)
        {
            _activeTokens.TryRemove(token, out _);
        }

        SaveSessions();
        _logger.LogDebug("Cleaned up {Count} expired token(s)", expiredTokens.Count);
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
            throw new ArgumentException($"密码至少需要 {MinimumPasswordLength} 个字符");
        if (!password.Any(char.IsUpper)
            || !password.Any(char.IsLower)
            || !password.Any(char.IsDigit)
            || !password.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new ArgumentException("密码必须同时包含大写字母、小写字母、数字和特殊字符");
        }
    }

    private static string GenerateBootstrapPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_=+";
        const string all = upper + lower + digits + symbols;

        var characters = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (characters.Count < 24)
            characters.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        for (var i = characters.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string(characters.ToArray());
    }

    private static string? ReadBootstrapPassword()
    {
        var passwordFile = Environment.GetEnvironmentVariable("NCPM_ADMIN_PASSWORD_FILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(passwordFile))
        {
            if (!File.Exists(passwordFile))
                throw new FileNotFoundException("NCPM_ADMIN_PASSWORD_FILE does not exist", passwordFile);
            return File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        }

        return Environment.GetEnvironmentVariable("NCPM_ADMIN_PASSWORD");
    }

    private static void WriteFileAtomically(string path, string content, bool restrictToOwner)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
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
            // Bind-mounted filesystems may not expose Unix permission changes.
        }
    }

    private void TryDeleteInitialPasswordFile()
    {
        try
        {
            if (File.Exists(_initialPasswordFilePath))
                File.Delete(_initialPasswordFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to remove the initial password file {Path}", _initialPasswordFilePath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        _disposed = true;
    }
}
