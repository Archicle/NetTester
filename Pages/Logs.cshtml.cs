using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NetTester.Services;

namespace NetTester.Pages;

public class LogsModel : PageModel
{
    private readonly NetworkTesterService _networkTesterService;

    public LogsModel(NetworkTesterService networkTesterService)
    {
        _networkTesterService = networkTesterService;
    }

    public string LogText { get; private set; } = string.Empty;
    public string StatsText { get; private set; } = string.Empty;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await _networkTesterService.ClearLogAndStatsAsync();
        TempData["StatusMessage"] = "日志与统计数据已清空。";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        await LoadAsync();
        TempData["StatusMessage"] = "内容已刷新。";
        return Page();
    }

    private async Task LoadAsync()
    {
        LogText = await _networkTesterService.ReadLogTextAsync();
        StatsText = await _networkTesterService.ReadStatsTextAsync();
    }
}
