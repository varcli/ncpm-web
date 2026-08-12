using System.Text.RegularExpressions;
using Ncpm.Data;

namespace Ncpm.Services;

/// <summary>
/// Parses nginx access logs in the default combined format and aggregates them
/// into the counts the access-log page renders. The combined format is:
/// <c>$remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent" $request_time</c>
/// </summary>
public class AccessLogAnalyzer
{
    // Example: 192.168.1.1 - - [11/Aug/2026:12:00:00 +0000] "GET / HTTP/1.1" 200 612 "-" "curl/8.0" 0.001
    private static readonly Regex LogPattern = new(
        @"^(?<ip>\S+)\s+\S+\s+\S+\s+\[(?<time>[^\]]+)\]\s+""(?<method>\S+)\s+(?<path>\S+)\s+(?<proto>[^""]+)""\s+(?<status>\d+)\s+(?<bytes>\d+)\s+""(?<referer>[^""]*)""\s+""(?<ua>[^""]*)""(?:\s+(?<rt>[\d.]+))?",
        RegexOptions.Compiled);

    private readonly ILogger<AccessLogAnalyzer> _logger;

    public AccessLogAnalyzer(ILogger<AccessLogAnalyzer> logger)
    {
        _logger = logger;
    }

    public AccessLogAnalysis Analyze(string logPath, int maxLines = 10000)
    {
        var analysis = new AccessLogAnalysis();

        if (!File.Exists(logPath))
            return analysis;

        var byStatus = new Dictionary<string, int>();
        var byPath = new Dictionary<string, int>();
        var byIp = new Dictionary<string, int>();
        var byUa = new Dictionary<string, int>();
        var byReferrer = new Dictionary<string, int>();
        var byMethod = new Dictionary<string, int>();
        var rtSum = 0.0;
        var rtCount = 0;
        var errorCount = 0;

        try
        {
            // Read the tail of the file so analysis stays responsive on large logs.
            var lines = ReadTailLines(logPath, maxLines).ToList();

            foreach (var line in lines)
            {
                var match = LogPattern.Match(line);
                if (!match.Success)
                    continue;

                analysis.TotalRequests++;

                var status = match.Groups["status"].Value;
                var path = match.Groups["path"].Value;
                var ip = match.Groups["ip"].Value;
                var ua = match.Groups["ua"].Value;
                var referer = match.Groups["referer"].Value;
                var method = match.Groups["method"].Value;

                byStatus[status] = byStatus.GetValueOrDefault(status) + 1;
                byPath[TruncatePath(path)] = byPath.GetValueOrDefault(TruncatePath(path)) + 1;
                byIp[ip] = byIp.GetValueOrDefault(ip) + 1;
                byUa[Truncate(ua, 60)] = byUa.GetValueOrDefault(Truncate(ua, 60)) + 1;
                byReferrer[Truncate(referer, 80)] = byReferrer.GetValueOrDefault(Truncate(referer, 80)) + 1;
                byMethod[method] = byMethod.GetValueOrDefault(method) + 1;

                if (int.TryParse(status, out var code) && code >= 400)
                    errorCount++;

                if (double.TryParse(match.Groups["rt"].Value, out var rt))
                {
                    rtSum += rt;
                    rtCount++;
                }
            }

            analysis.ByStatusCode = byStatus
                .OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            analysis.TopPaths = TopN(byPath, 10);
            analysis.TopIps = TopN(byIp, 10);
            analysis.TopUserAgents = TopN(byUa, 10);
            analysis.TopReferrers = TopN(byReferrer, 10);
            analysis.ByMethod = TopN(byMethod, 10);
            analysis.ErrorCount = errorCount;
            analysis.AverageResponseTimeMs = rtCount > 0 ? rtSum / rtCount * 1000 : 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze access log {Path}", logPath);
        }

        return analysis;
    }

    /// <summary>
    /// Reads the last <paramref name="maxLines"/> lines of a file. nginx access logs
    /// can grow into the hundreds of megabytes, so the whole file is never read;
    /// a small ring buffer keeps only the most recent lines in memory.
    /// </summary>
    private static IEnumerable<string> ReadTailLines(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);

        var ring = new Queue<string>(maxLines);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            ring.Enqueue(line);
            if (ring.Count > maxLines)
                ring.Dequeue();
        }

        return ring;
    }

    private static List<NameCount> TopN(Dictionary<string, int> counts, int n) =>
        counts.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => new NameCount { Name = kv.Key, Count = kv.Value })
            .ToList();

    /// <summary>
    /// Strips query strings and numeric path segments so /api/users/123/edit
    /// collapses to /api/users/{id}/edit. Without this, every distinct id
    /// becomes its own entry and the Top-N is meaningless.
    /// </summary>
    private static string TruncatePath(string path)
    {
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (int.TryParse(segments[i], out _))
                segments[i] = "{id}";
            else if (segments[i].Length == 32 && IsHex(segments[i]))
                segments[i] = "{hash}";
        }

        return "/" + string.Join('/', segments);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
