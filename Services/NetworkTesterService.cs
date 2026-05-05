using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;

namespace NetTester.Services;

public sealed class NetworkTesterService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<NetworkTesterService> _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private List<string> _ipTargets = ["192.168.1.1", "192.168.1.254"];
    private readonly ConcurrentDictionary<string, PingCounter> _pingCounters = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _httpTargets = ["https://www.baidu.com"];
    private readonly ConcurrentDictionary<string, HttpCounter> _httpCounters = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableIpProbe = true;
    private bool _enableHttpProbe = true;

    private volatile bool _isRunning;

    private DateTime _lastStatsWriteUtc = DateTime.UtcNow;

    private string ConfigFilePath => Path.Combine(_environment.ContentRootPath, "network-config.ini");
    private string LogFilePath => Path.Combine(_environment.ContentRootPath, "network-log.txt");
    private string StatsFilePath => Path.Combine(_environment.ContentRootPath, "network-stats.txt");

    public NetworkTesterService(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment, ILogger<NetworkTesterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
        LoadConfig();
        SyncPingCounters(_ipTargets);
        SyncHttpCounters(_httpTargets);
    }

    public async Task<TesterState> GetStateAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            var pingStats = _ipTargets.Select(ip =>
            {
                var counter = _pingCounters.GetOrAdd(ip, _ => new PingCounter());
                return counter.ToState(ip);
            }).ToList();

            return new TesterState
            {
                IpTargets = _ipTargets.ToList(),
                PingStats = pingStats,
                HttpTargets = _httpTargets.ToList(),
                HttpStats = _httpTargets.Select(url => _httpCounters.GetOrAdd(url, _ => new HttpCounter()).ToState(url)).ToList(),
                EnableIpProbe = _enableIpProbe,
                EnableHttpProbe = _enableHttpProbe,
                IsRunning = _isRunning,
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SaveConfigAsync(IReadOnlyList<string> ipTargets, IReadOnlyList<string> httpTargets, bool enableIpProbe, bool enableHttpProbe)
    {
        await _stateLock.WaitAsync();
        try
        {
            _ipTargets = NormalizeTargets(ipTargets);
            SyncPingCounters(_ipTargets);
            _httpTargets = NormalizeTargets(httpTargets);
            SyncHttpCounters(_httpTargets);
            _enableIpProbe = enableIpProbe;
            _enableHttpProbe = enableHttpProbe;
            var lines = new[]
            {
                $"IpTargets={string.Join(',', _ipTargets)}",
                $"HttpTargets={string.Join(',', _httpTargets)}",
                $"EnableIpProbe={_enableIpProbe}",
                $"EnableHttpProbe={_enableHttpProbe}"
            };
            await File.WriteAllLinesAsync(ConfigFilePath, lines);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void StartTesting() => _isRunning = true;

    public void StopTesting() => _isRunning = false;

    public async Task<LogPageResult> ReadLogPageAsync(int page, int pageSize)
    {
        if (page < 0)
        {
            page = 0;
        }

        if (pageSize <= 0)
        {
            pageSize = 200;
        }

        if (!File.Exists(LogFilePath))
        {
            return new LogPageResult
            {
                Text = string.Empty,
                HasMore = false,
                Page = page
            };
        }

        var targetCount = checked((page + 1) * pageSize);
        var queue = new Queue<string>(targetCount);
        var totalLines = 0;

        await foreach (var line in File.ReadLinesAsync(LogFilePath))
        {
            totalLines++;
            queue.Enqueue(line);
            if (queue.Count > targetCount)
            {
                queue.Dequeue();
            }
        }

        var tailLines = queue.ToArray();
        var endExclusive = Math.Max(0, tailLines.Length - page * pageSize);
        var start = Math.Max(0, endExclusive - pageSize);
        var selected = tailLines[start..endExclusive];

        return new LogPageResult
        {
            Text = string.Join(Environment.NewLine, selected),
            HasMore = totalLines > (page + 1) * pageSize,
            Page = page
        };
    }

    public async Task<string> ReadStatsTailTextAsync(int maxLines)
    {
        if (maxLines <= 0)
        {
            maxLines = 300;
        }

        return await ReadTailTextAsync(StatsFilePath, maxLines);
    }

    public async Task<string> ReadStatsTextAsync()
    {
        if (!File.Exists(StatsFilePath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(StatsFilePath);
    }

    public async Task ClearLogAndStatsAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(LogFilePath, string.Empty);
            await File.WriteAllTextAsync(StatsFilePath, string.Empty);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var externalTimer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        var pingLoop = RunPingLoopAsync(pingTimer, stoppingToken);
        var externalLoop = RunExternalLoopAsync(externalTimer, stoppingToken);

        try
        {
            await Task.WhenAll(pingLoop, externalLoop);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            WriteStatsSnapshot();
        }
    }

    private async Task RunPingLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        while (await timer.WaitForNextTickAsync(token))
        {
            if (!_isRunning)
            {
                await TryWritePeriodicStatsAsync();
                continue;
            }

            if (_enableIpProbe)
            {
                var targets = _ipTargets;
                if (targets.Count > 0)
                {
                    var pingTasks = targets.Select(ip => CheckPingAsync(ip, token));
                    await Task.WhenAll(pingTasks);
                }
            }

            await TryWritePeriodicStatsAsync();
        }
    }

    private async Task RunExternalLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        while (await timer.WaitForNextTickAsync(token))
        {
            if (!_isRunning || !_enableHttpProbe)
            {
                continue;
            }

            var targets = _httpTargets;
            if (targets.Count == 0)
            {
                continue;
            }

            var checkTasks = targets.Select(url => CheckExternalAsync(url, token));
            await Task.WhenAll(checkTasks);
        }
    }

    private async Task CheckPingAsync(string ip, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        IncrementSent(ip);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1000);
            if (reply.Status == IPStatus.Success)
            {
                IncrementResult(ip, true);
                return;
            }

            IncrementResult(ip, false);
            await WriteLogAsync($"[IP:{ip}] Ping 失败，状态: {reply.Status}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IncrementResult(ip, false);
            await WriteLogAsync($"[IP:{ip}] Ping 异常: {ex.Message}");
        }
    }

    private async Task CheckExternalAsync(string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        IncrementHttpSent(url);
        const int timeoutMs = 2800;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeoutMs);

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if ((int)response.StatusCode >= 400)
            {
                IncrementHttpResult(url, false);
                await WriteLogAsync($"[HTTP:{url}] 外网检测失败，HTTP 状态码: {(int)response.StatusCode}");
                return;
            }

            IncrementHttpResult(url, true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            IncrementHttpResult(url, false);
            await WriteLogAsync($"[HTTP:{url}] 外网检测异常，错误: 外网检测超时（>{timeoutMs}ms）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IncrementHttpResult(url, false);
            await WriteLogAsync($"[HTTP:{url}] 外网检测异常，错误: {ex.Message}");
        }
    }

    private async Task TryWritePeriodicStatsAsync()
    {
        if (DateTime.UtcNow - _lastStatsWriteUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastStatsWriteUtc = DateTime.UtcNow;
        await Task.Run(WriteStatsSnapshot);
    }

    private void IncrementSent(string ip)
    {
        var counter = _pingCounters.GetOrAdd(ip, _ => new PingCounter());
        Interlocked.Increment(ref counter.TotalCount);
    }

    private void IncrementResult(string ip, bool success)
    {
        var counter = _pingCounters.GetOrAdd(ip, _ => new PingCounter());
        if (success)
        {
            Interlocked.Increment(ref counter.SuccessCount);
            return;
        }

        Interlocked.Increment(ref counter.FailureCount);
    }

    private void IncrementHttpSent(string url)
    {
        var counter = _httpCounters.GetOrAdd(url, _ => new HttpCounter());
        Interlocked.Increment(ref counter.TotalCount);
    }

    private void IncrementHttpResult(string url, bool success)
    {
        var counter = _httpCounters.GetOrAdd(url, _ => new HttpCounter());
        if (success)
        {
            Interlocked.Increment(ref counter.SuccessCount);
            return;
        }

        Interlocked.Increment(ref counter.FailureCount);
    }

    private static List<string> NormalizeTargets(IEnumerable<string> targets)
    {
        return targets
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private void SyncPingCounters(IReadOnlyCollection<string> targets)
    {
        foreach (var ip in targets)
        {
            _pingCounters.GetOrAdd(ip, _ => new PingCounter());
        }

        var activeSet = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _pingCounters.Keys)
        {
            if (!activeSet.Contains(key))
            {
                _pingCounters.TryRemove(key, out _);
            }
        }
    }

    private void SyncHttpCounters(IReadOnlyCollection<string> targets)
    {
        foreach (var url in targets)
        {
            _httpCounters.GetOrAdd(url, _ => new HttpCounter());
        }

        var activeSet = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _httpCounters.Keys)
        {
            if (!activeSet.Contains(key))
            {
                _httpCounters.TryRemove(key, out _);
            }
        }
    }

    private void LoadConfig()
    {
        if (!File.Exists(ConfigFilePath))
        {
            return;
        }

        var config = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(ConfigFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('='))
            {
                continue;
            }

            var index = line.IndexOf('=');
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                config[key] = value;
            }
        }

        var ipTargetsConfig = ReadConfig(config, "IpTargets", string.Empty);
        if (!string.IsNullOrWhiteSpace(ipTargetsConfig))
        {
            _ipTargets = NormalizeTargets(ParseTargets(ipTargetsConfig));
        }
        else
        {
            var fallbackIp1 = ReadConfig(config, "Ip1", string.Empty);
            var fallbackIp2 = ReadConfig(config, "Ip2", string.Empty);
            var fallbackTargets = NormalizeTargets([fallbackIp1, fallbackIp2]);
            if (fallbackTargets.Count > 0)
            {
                _ipTargets = fallbackTargets;
            }
        }

        var httpTargetsConfig = ReadConfig(config, "HttpTargets", string.Empty);
        if (!string.IsNullOrWhiteSpace(httpTargetsConfig))
        {
            _httpTargets = NormalizeTargets(ParseTargets(httpTargetsConfig));
        }
        else
        {
            var legacyExternalUrl = ReadConfig(config, "ExternalUrl", string.Empty);
            var fallbackHttpTargets = NormalizeTargets([legacyExternalUrl]);
            if (fallbackHttpTargets.Count > 0)
            {
                _httpTargets = fallbackHttpTargets;
            }
        }

        _enableIpProbe = ReadBoolConfig(config, "EnableIpProbe", true);
        _enableHttpProbe = ReadBoolConfig(config, "EnableHttpProbe", true);
    }

    private static IEnumerable<string> ParseTargets(string value)
    {
        return value.Split([',', ';', '|', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ReadConfig(IReadOnlyDictionary<string, string> config, string key, string defaultValue)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value;
    }

    private static bool ReadBoolConfig(IReadOnlyDictionary<string, string> config, string key, bool defaultValue)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private async Task WriteLogAsync(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        await File.AppendAllTextAsync(LogFilePath, line + Environment.NewLine);
        _logger.LogWarning(message);
    }

    private void WriteStatsSnapshot()
    {
        var pingParts = _ipTargets.Select(ip =>
        {
            var counter = _pingCounters.GetOrAdd(ip, _ => new PingCounter());
            var total = Interlocked.Read(ref counter.TotalCount);
            var success = Interlocked.Read(ref counter.SuccessCount);
            var failure = Interlocked.Read(ref counter.FailureCount);
            return $"{ip}(总:{total}, 成功:{success}, 失败:{failure})";
        }).ToList();

        var httpParts = _httpTargets.Select(url =>
        {
            var counter = _httpCounters.GetOrAdd(url, _ => new HttpCounter());
            var total = Interlocked.Read(ref counter.TotalCount);
            var success = Interlocked.Read(ref counter.SuccessCount);
            var failure = Interlocked.Read(ref counter.FailureCount);
            return $"{url}(总:{total}, 成功:{success}, 失败:{failure})";
        }).ToList();

        var pingSummary = pingParts.Count > 0 ? string.Join("; ", pingParts) : "无IP目标";
        var httpSummary = httpParts.Count > 0 ? string.Join("; ", httpParts) : "无HTTP目标";
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IP探测[{(_enableIpProbe ? "启用" : "禁用")}]: {pingSummary}; HTTP探测[{(_enableHttpProbe ? "启用" : "禁用")}]: {httpSummary}";
        File.AppendAllText(StatsFilePath, line + Environment.NewLine);
    }

    private static async Task<string> ReadTailTextAsync(string filePath, int maxLines)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        var queue = new Queue<string>(maxLines);
        await foreach (var line in File.ReadLinesAsync(filePath))
        {
            queue.Enqueue(line);
            if (queue.Count > maxLines)
            {
                queue.Dequeue();
            }
        }

        return string.Join(Environment.NewLine, queue);
    }

    private sealed class PingCounter
    {
        public long TotalCount;
        public long SuccessCount;
        public long FailureCount;

        public PingTargetState ToState(string ip)
        {
            return new PingTargetState
            {
                Ip = ip,
                Total = Interlocked.Read(ref TotalCount),
                Success = Interlocked.Read(ref SuccessCount),
                Failure = Interlocked.Read(ref FailureCount)
            };
        }
    }

    private sealed class HttpCounter
    {
        public long TotalCount;
        public long SuccessCount;
        public long FailureCount;

        public HttpTargetState ToState(string url)
        {
            return new HttpTargetState
            {
                Url = url,
                Total = Interlocked.Read(ref TotalCount),
                Success = Interlocked.Read(ref SuccessCount),
                Failure = Interlocked.Read(ref FailureCount)
            };
        }
    }
}

public sealed class TesterState
{
    public IReadOnlyList<string> IpTargets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PingTargetState> PingStats { get; init; } = Array.Empty<PingTargetState>();
    public IReadOnlyList<string> HttpTargets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<HttpTargetState> HttpStats { get; init; } = Array.Empty<HttpTargetState>();
    public bool EnableIpProbe { get; init; }
    public bool EnableHttpProbe { get; init; }
    public bool IsRunning { get; init; }
}

public sealed class PingTargetState
{
    public string Ip { get; init; } = string.Empty;
    public long Total { get; init; }
    public long Success { get; init; }
    public long Failure { get; init; }
}

public sealed class HttpTargetState
{
    public string Url { get; init; } = string.Empty;
    public long Total { get; init; }
    public long Success { get; init; }
    public long Failure { get; init; }
}

public sealed class LogPageResult
{
    public string Text { get; init; } = string.Empty;
    public bool HasMore { get; init; }
    public int Page { get; init; }
}
