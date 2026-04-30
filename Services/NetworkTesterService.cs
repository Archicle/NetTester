using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;

namespace NetTester.Services;

public enum TargetType
{
    Ip1,
    Ip2,
    External
}

public sealed class NetworkTesterService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<NetworkTesterService> _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private string _ip1 = "192.168.1.1";
    private string _ip2 = "192.168.1.254";
    private string _externalUrl = "https://www.baidu.com";

    private volatile bool _isRunning;

    private long _ip1TotalCount;
    private long _ip1SuccessCount;
    private long _ip1FailureCount;

    private long _ip2TotalCount;
    private long _ip2SuccessCount;
    private long _ip2FailureCount;

    private long _externalTotalCount;
    private long _externalSuccessCount;
    private long _externalFailureCount;

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
    }

    public async Task<TesterState> GetStateAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            return new TesterState
            {
                Ip1 = _ip1,
                Ip2 = _ip2,
                ExternalUrl = _externalUrl,
                IsRunning = _isRunning,
                Ip1Total = Interlocked.Read(ref _ip1TotalCount),
                Ip1Success = Interlocked.Read(ref _ip1SuccessCount),
                Ip1Failure = Interlocked.Read(ref _ip1FailureCount),
                Ip2Total = Interlocked.Read(ref _ip2TotalCount),
                Ip2Success = Interlocked.Read(ref _ip2SuccessCount),
                Ip2Failure = Interlocked.Read(ref _ip2FailureCount),
                ExternalTotal = Interlocked.Read(ref _externalTotalCount),
                ExternalSuccess = Interlocked.Read(ref _externalSuccessCount),
                ExternalFailure = Interlocked.Read(ref _externalFailureCount)
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SaveConfigAsync(string ip1, string ip2, string externalUrl)
    {
        await _stateLock.WaitAsync();
        try
        {
            _ip1 = ip1.Trim();
            _ip2 = ip2.Trim();
            _externalUrl = externalUrl.Trim();
            var lines = new[]
            {
                $"Ip1={_ip1}",
                $"Ip2={_ip2}",
                $"ExternalUrl={_externalUrl}"
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

    public async Task<string> ReadLogTextAsync()
    {
        if (!File.Exists(LogFilePath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(LogFilePath);
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

            await CheckPingAsync(_ip1, TargetType.Ip1, token);
            await CheckPingAsync(_ip2, TargetType.Ip2, token);
            await TryWritePeriodicStatsAsync();
        }
    }

    private async Task RunExternalLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        while (await timer.WaitForNextTickAsync(token))
        {
            if (!_isRunning)
            {
                continue;
            }

            await CheckExternalAsync(_externalUrl, token);
        }
    }

    private async Task CheckPingAsync(string ip, TargetType type, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        IncrementSent(type);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1000);
            if (reply.Status == IPStatus.Success)
            {
                IncrementResult(type, true);
                return;
            }

            IncrementResult(type, false);
            await WriteLogAsync($"[{type}] Ping {ip} 失败，状态: {reply.Status}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IncrementResult(type, false);
            await WriteLogAsync($"[{type}] Ping {ip} 异常: {ex.Message}");
        }
    }

    private async Task CheckExternalAsync(string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        IncrementSent(TargetType.External);
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
                IncrementResult(TargetType.External, false);
                await WriteLogAsync($"外网检测失败，URL: {url}，HTTP 状态码: {(int)response.StatusCode}");
                return;
            }

            IncrementResult(TargetType.External, true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            IncrementResult(TargetType.External, false);
            await WriteLogAsync($"外网检测异常，URL: {url}，错误: 外网检测超时（>{timeoutMs}ms）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IncrementResult(TargetType.External, false);
            await WriteLogAsync($"外网检测异常，URL: {url}，错误: {ex.Message}");
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

    private void IncrementSent(TargetType target)
    {
        switch (target)
        {
            case TargetType.Ip1:
                Interlocked.Increment(ref _ip1TotalCount);
                break;
            case TargetType.Ip2:
                Interlocked.Increment(ref _ip2TotalCount);
                break;
            default:
                Interlocked.Increment(ref _externalTotalCount);
                break;
        }
    }

    private void IncrementResult(TargetType target, bool success)
    {
        switch (target)
        {
            case TargetType.Ip1:
                if (success)
                {
                    Interlocked.Increment(ref _ip1SuccessCount);
                }
                else
                {
                    Interlocked.Increment(ref _ip1FailureCount);
                }
                break;
            case TargetType.Ip2:
                if (success)
                {
                    Interlocked.Increment(ref _ip2SuccessCount);
                }
                else
                {
                    Interlocked.Increment(ref _ip2FailureCount);
                }
                break;
            default:
                if (success)
                {
                    Interlocked.Increment(ref _externalSuccessCount);
                }
                else
                {
                    Interlocked.Increment(ref _externalFailureCount);
                }
                break;
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

        _ip1 = ReadConfig(config, "Ip1", _ip1);
        _ip2 = ReadConfig(config, "Ip2", _ip2);
        _externalUrl = ReadConfig(config, "ExternalUrl", _externalUrl);
    }

    private static string ReadConfig(IReadOnlyDictionary<string, string> config, string key, string defaultValue)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value;
    }

    private async Task WriteLogAsync(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        await File.AppendAllTextAsync(LogFilePath, line + Environment.NewLine);
        _logger.LogWarning(message);
    }

    private void WriteStatsSnapshot()
    {
        var ip1Total = Interlocked.Read(ref _ip1TotalCount);
        var ip1Success = Interlocked.Read(ref _ip1SuccessCount);
        var ip1Failure = Interlocked.Read(ref _ip1FailureCount);
        var ip2Total = Interlocked.Read(ref _ip2TotalCount);
        var ip2Success = Interlocked.Read(ref _ip2SuccessCount);
        var ip2Failure = Interlocked.Read(ref _ip2FailureCount);
        var externalTotal = Interlocked.Read(ref _externalTotalCount);
        var externalSuccess = Interlocked.Read(ref _externalSuccessCount);
        var externalFailure = Interlocked.Read(ref _externalFailureCount);
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] IP1(总:{ip1Total}, 成功:{ip1Success}, 失败:{ip1Failure}); IP2(总:{ip2Total}, 成功:{ip2Success}, 失败:{ip2Failure}); 外网(总:{externalTotal}, 成功:{externalSuccess}, 失败:{externalFailure})";
        File.AppendAllText(StatsFilePath, line + Environment.NewLine);
    }
}

public sealed class TesterState
{
    public string Ip1 { get; init; } = string.Empty;
    public string Ip2 { get; init; } = string.Empty;
    public string ExternalUrl { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public long Ip1Total { get; init; }
    public long Ip1Success { get; init; }
    public long Ip1Failure { get; init; }
    public long Ip2Total { get; init; }
    public long Ip2Success { get; init; }
    public long Ip2Failure { get; init; }
    public long ExternalTotal { get; init; }
    public long ExternalSuccess { get; init; }
    public long ExternalFailure { get; init; }
}
