using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ncpm.Services;

/// <summary>
/// Per-client token-bucket limiter backed by the live YAML configuration.
/// Changes made in Settings take effect on the next request.
/// </summary>
public sealed class DynamicRateLimitMiddleware
{
    private const int CleanupThreshold = 10_000;
    private static readonly long IdleBucketTicks = Stopwatch.Frequency * 600;

    private readonly RequestDelegate _next;
    private readonly ConfigService _configService;
    private readonly ConcurrentDictionary<string, ClientBucket> _buckets = new();
    private long _requestCounter;

    public DynamicRateLimitMiddleware(RequestDelegate next, ConfigService configService)
    {
        _next = next;
        _configService = configService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var config = _configService.LoadAppConfig().RateLimit;
        if (!config.Enabled || context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = Stopwatch.GetTimestamp();
        var bucket = _buckets.GetOrAdd(key, _ => new ClientBucket(now));
        var allowed = bucket.TryConsume(
            now,
            Math.Max(1, config.Average),
            Math.Max(0, config.Burst),
            Math.Max(1, config.PeriodSeconds));

        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Max(
                1,
                (int)Math.Ceiling((double)config.PeriodSeconds / config.Average)).ToString();
            await context.Response.WriteAsync("Too many requests");
            return;
        }

        if (Interlocked.Increment(ref _requestCounter) % 1024 == 0
            && _buckets.Count > CleanupThreshold)
        {
            CleanupIdleBuckets(now);
        }

        await _next(context);
    }

    private void CleanupIdleBuckets(long now)
    {
        foreach (var item in _buckets)
        {
            if (now - item.Value.LastSeen > IdleBucketTicks)
                _buckets.TryRemove(item.Key, out _);
        }
    }

    private sealed class ClientBucket
    {
        private readonly object _gate = new();
        private double _tokens = double.MaxValue;
        private long _lastRefill;

        public ClientBucket(long now)
        {
            _lastRefill = now;
            LastSeen = now;
        }

        public long LastSeen { get; private set; }

        public bool TryConsume(long now, int average, int burst, int periodSeconds)
        {
            lock (_gate)
            {
                var capacity = Math.Max(1, average + burst);
                if (_tokens == double.MaxValue)
                    _tokens = capacity;

                var elapsedSeconds = (double)(now - _lastRefill) / Stopwatch.Frequency;
                _tokens = Math.Min(capacity, _tokens + elapsedSeconds * average / periodSeconds);
                _lastRefill = now;
                LastSeen = now;

                if (_tokens < 1)
                    return false;

                _tokens -= 1;
                return true;
            }
        }
    }
}
