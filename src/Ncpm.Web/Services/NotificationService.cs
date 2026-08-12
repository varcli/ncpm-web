using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ncpm.Data;

namespace Ncpm.Services;

public class NotificationService : IDisposable
{
    private const int MaxRetryQueueLength = 200;
    private const int MaxDeliveryAttempts = 4;
    private const int MaxRecentMessages = 100;

    private readonly ConfigService _configService;
    private readonly ILogger<NotificationService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentQueue<PendingDelivery> _retryQueue = new();
    private readonly ConcurrentQueue<NotifyMessage> _recentMessages = new();
    private readonly SemaphoreSlim _retryGate = new(1, 1);
    private Timer? _retryTimer;
    private bool _disposed;

    // Takes IHttpClientFactory rather than HttpClient: this service is a singleton
    // and the registered HttpClient is scoped, so direct injection would fail to
    // resolve at runtime.
    public NotificationService(
        ConfigService configService,
        ILogger<NotificationService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _configService = configService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _retryTimer = new Timer(ProcessRetries, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(NotificationService));
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    public Task SendAsync(string title, string body, string level = "info", Dictionary<string, string>? fields = null)
    {
        return SendAsync(new NotifyMessage
        {
            Level = level,
            Title = title,
            Body = body,
            Fields = fields ?? new(),
            Timestamp = DateTime.UtcNow
        });
    }

    public async Task SendAsync(NotifyMessage message)
    {
        // Keep a small in-memory activity feed even when outbound notifications
        // are disabled. The dashboard uses this to explain recent health changes
        // without requiring a webhook provider.
        _recentMessages.Enqueue(CloneForActivityFeed(message));
        while (_recentMessages.Count > MaxRecentMessages)
        {
            _recentMessages.TryDequeue(out _);
        }

        var config = _configService.LoadAppConfig().Notification;
        if (!config.Enabled || !config.Providers.Any(p => p.IsEnabled))
            return;

        var tasks = config.Providers
            .Where(p => p.IsEnabled)
            .Select(p => SendToProviderAsync(p, CloneForDelivery(message)));

        await Task.WhenAll(tasks);
    }

    public List<NotifyMessage> GetRecentMessages(int count = 20)
    {
        return _recentMessages
            .TakeLast(Math.Clamp(count, 1, MaxRecentMessages))
            .Reverse()
            .Select(CloneForActivityFeed)
            .ToList();
    }

    private static NotifyMessage CloneForActivityFeed(NotifyMessage message)
    {
        return new NotifyMessage
        {
            Level = message.Level,
            Title = message.Title,
            Body = message.Body,
            Timestamp = message.Timestamp,
            Fields = new Dictionary<string, string>(message.Fields),
            Attempts = message.Attempts
        };
    }

    private static NotifyMessage CloneForDelivery(NotifyMessage message)
    {
        return new NotifyMessage
        {
            Level = message.Level,
            Title = message.Title,
            Body = message.Body,
            Timestamp = message.Timestamp,
            Fields = new Dictionary<string, string>(message.Fields),
            Attempts = message.Attempts
        };
    }

    public async Task SendToProviderAsync(NotifyProvider provider, NotifyMessage message)
    {
        message.Attempts++;
        try
        {
            var (url, content, headers) = provider.Type switch
            {
                NotifyProviderType.Gotify => FormatGotify(provider, message),
                NotifyProviderType.Ntfy => FormatNtfy(provider, message),
                NotifyProviderType.Webhook => FormatWebhook(provider, message),
                _ => throw new NotSupportedException($"Provider type {provider.Type} not supported")
            };

            using var request = new HttpRequestMessage(new HttpMethod(provider.Method ?? "POST"), url);
            request.Content = content;

            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var client = CreateClient();
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _logger.LogDebug("Notification sent to {Provider} ({Type}): {Title}",
                provider.Name, provider.Type, message.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification to {Provider}", provider.Name);

            if (message.Attempts >= MaxDeliveryAttempts)
            {
                _logger.LogWarning("Dropping notification '{Title}' after {Attempts} attempts",
                    message.Title, message.Attempts);
                return;
            }

            // Bound the queue: an unreachable provider would otherwise grow it without limit.
            if (_retryQueue.Count < MaxRetryQueueLength)
            {
                _retryQueue.Enqueue(new PendingDelivery(provider.Name, CloneForDelivery(message)));
            }
        }
    }

    private (string url, HttpContent content, Dictionary<string, string> headers) FormatGotify(NotifyProvider provider, NotifyMessage message)
    {
        var priority = message.Level switch
        {
            "error" => 8,
            "warning" => 5,
            "info" => 3,
            _ => 1
        };

        var payload = new Dictionary<string, object>
        {
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = priority,
            ["extras"] = new Dictionary<string, object>
            {
                ["client::display"] = new Dictionary<string, string> { ["contentType"] = "text/markdown" }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var url = $"{provider.Url.TrimEnd('/')}/message";
        var headers = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(provider.Token))
            headers["X-Gotify-Key"] = provider.Token;

        return (url, content, headers);
    }

    private (string url, HttpContent content, Dictionary<string, string> headers) FormatNtfy(NotifyProvider provider, NotifyMessage message)
    {
        var priority = message.Level switch
        {
            "error" => "urgent",
            "warning" => "high",
            "info" => "default",
            _ => "low"
        };

        var content = new StringContent(message.Body, Encoding.UTF8, "text/plain");
        var url = $"{provider.Url.TrimEnd('/')}/{provider.Topic ?? "ncpm"}";
        var headers = new Dictionary<string, string>
        {
            ["Title"] = message.Title,
            ["Priority"] = priority,
            ["Tags"] = message.Level == "error" ? "warning" : "white_check_mark"
        };

        if (!string.IsNullOrEmpty(provider.Token))
            headers["Authorization"] = $"Bearer {provider.Token}";

        return (url, content, headers);
    }

    private (string url, HttpContent content, Dictionary<string, string> headers) FormatWebhook(NotifyProvider provider, NotifyMessage message)
    {
        var payload = new
        {
            title = message.Title,
            message = message.Body,
            level = message.Level,
            timestamp = message.Timestamp.ToString("o"),
            fields = message.Fields
        };

        var mimeType = provider.MimeType ?? "application/json";
        HttpContent content;

        if (mimeType == "application/json")
        {
            content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        else if (mimeType == "application/x-www-form-urlencoded")
        {
            var formData = new List<KeyValuePair<string, string>>
            {
                new("title", message.Title),
                new("message", message.Body),
                new("level", message.Level)
            };
            content = new FormUrlEncodedContent(formData);
        }
        else
        {
            content = new StringContent($"{message.Title}\n{message.Body}", Encoding.UTF8, "text/plain");
        }

        return (provider.Url, content, new Dictionary<string, string>());
    }

    private async void ProcessRetries(object? state)
    {
        if (!await _retryGate.WaitAsync(0))
            return;

        try
        {
            var config = _configService.LoadAppConfig().Notification;
            if (!config.Enabled)
                return;

            var processed = 0;
            while (processed < 10 && _retryQueue.TryDequeue(out var pending))
            {
                var provider = config.Providers.FirstOrDefault(item =>
                    item.IsEnabled
                    && item.Name.Equals(pending.ProviderName, StringComparison.Ordinal));
                if (provider == null)
                {
                    _logger.LogWarning(
                        "Dropping notification retry because provider {Provider} is missing or disabled",
                        pending.ProviderName);
                }
                else
                {
                    await SendToProviderAsync(provider, pending.Message);
                }

                processed++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification retry sweep failed");
        }
        finally
        {
            _retryGate.Release();
        }
    }

    private sealed record PendingDelivery(string ProviderName, NotifyMessage Message);

    public void Dispose()
    {
        if (!_disposed)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
            _disposed = true;
        }
    }
}
