using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NetTester.Services;

namespace NetTester.Pages;

public class StatsModel : PageModel
{
    private readonly NetworkTesterService _networkTesterService;

    public StatsModel(NetworkTesterService networkTesterService)
    {
        _networkTesterService = networkTesterService;
    }

    [BindProperty(SupportsGet = true)]
    public bool AutoRefresh { get; set; }

    public bool IsRunning { get; private set; }
    public bool EnableIpProbe { get; private set; }
    public bool EnableHttpProbe { get; private set; }
    public IReadOnlyList<PingTargetState> PingStats { get; private set; } = Array.Empty<PingTargetState>();
    public IReadOnlyList<HttpTargetState> HttpStats { get; private set; } = Array.Empty<HttpTargetState>();

    public long IpSentTotal { get; private set; }
    public long IpReceivedTotal { get; private set; }
    public long IpFailureTotal { get; private set; }

    public long HttpSentTotal { get; private set; }
    public long HttpReceivedTotal { get; private set; }
    public long HttpFailureTotal { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadStateAsync();
    }

    public async Task<IActionResult> OnPostRefreshAsync(bool autoRefresh)
    {
        AutoRefresh = autoRefresh;
        await LoadStateAsync();
        TempData["StatusMessage"] = "统计已刷新。";
        return Page();
    }

    private async Task LoadStateAsync()
    {
        var state = await _networkTesterService.GetStateAsync();
        IsRunning = state.IsRunning;
        EnableIpProbe = state.EnableIpProbe;
        EnableHttpProbe = state.EnableHttpProbe;
        PingStats = state.PingStats;
        HttpStats = state.HttpStats;

        IpSentTotal = PingStats.Sum(x => x.Total);
        IpReceivedTotal = PingStats.Sum(x => x.Success);
        IpFailureTotal = PingStats.Sum(x => x.Failure);

        HttpSentTotal = HttpStats.Sum(x => x.Total);
        HttpReceivedTotal = HttpStats.Sum(x => x.Success);
        HttpFailureTotal = HttpStats.Sum(x => x.Failure);
    }
}
