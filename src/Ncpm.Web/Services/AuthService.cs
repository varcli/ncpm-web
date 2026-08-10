using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ncpm.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ncpm.Services;

public class AuthService : IDisposable
{
    private static readonly TimeSpan TokenCleanupInterval = TimeSpan.FromMinutes(5);

    private readonly ConfigService _configService;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, AuthToken> _activeTokens = new();
    private readonly string _usersFilePath;
    private readonly string _sessionsFilePath;
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

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadUsers();
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

            var content = _serializer.Serialize(_users);
            File.WriteAllText(_usersFilePath, content);
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
            foreach (var token in tokens.Where(t => !t.IsExpired && !string.IsNullOrEmpty(t.Token)))
            {
                _activeTokens[token.Token] = token;
                restored++;
            }

            if (restored > 0)
            {
                _logger.LogInformation("Restored {Count} active session(s)", restored);
            }

            // Drop anything that expired while the panel was down.
            if (restored != tokens.Count)
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
            File.WriteAllText(_sessionsFilePath, _serializer.Serialize(tokens));
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

        _logger.LogInformation("Creating default admin user");
        var (hash, salt) = PasswordHelper.HashPassword("admin123");

        var admin = new User
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt,
            DisplayName = "Administrator",
            Email = "admin@localhost",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _users.Add(admin);
        SaveUsers();
        _logger.LogWarning("Default admin user created with password 'admin123'. Please change it immediately!");
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
        var token = GenerateToken(user);
        _activeTokens[token.Token] = token;
        SaveSessions();

        _logger.LogInformation("User {Username} logged in successfully", request.Username);

        return new LoginResult
        {
            Success = true,
            Token = token.Token,
            User = user
        };
    }

    public void Logout(string token)
    {
        if (_activeTokens.TryRemove(token, out var removed))
        {
            SaveSessions();
            _logger.LogInformation("User {Username} logged out", removed.Username);
        }
    }

    public AuthToken? ValidateToken(string token)
    {
        if (!_activeTokens.TryGetValue(token, out var authToken))
            return null;

        if (authToken.IsExpired)
        {
            _activeTokens.TryRemove(token, out _);
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
        lock (_usersLock)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
                return false;

            var (hash, salt) = PasswordHelper.HashPassword(newPassword);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            SaveUsers();

            // A password change must not leave older sessions usable.
            RevokeSessionsForUser(userId);
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

    private AuthToken GenerateToken(User user)
    {
        var tokenBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);

        var config = _configService.LoadAppConfig();
        
        return new AuthToken
        {
            Token = Convert.ToBase64String(tokenBytes),
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role,
            ExpiresAt = DateTime.UtcNow.AddSeconds(config.Security.SessionTimeout)
        };
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        _disposed = true;
    }
}
