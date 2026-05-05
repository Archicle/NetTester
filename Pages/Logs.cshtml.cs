using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NetTester.Services;

namespace NetTester.Pages;

public class LogsModel : PageModel
{
    private readonly NetworkTesterService _networkTesterService;
    private const int LogPageSize = 200;
    private const int StatsMaxLines = 300;

    public LogsModel(NetworkTesterService networkTesterService)
    {
        _networkTesterService = networkTesterService;
    }

    public string LogText { get; private set; } = string.Empty;
    public string StatsText { get; private set; } = string.Empty;
    public int LogPage { get; private set; }
    public bool HasMoreLogs { get; private set; }

    public async Task OnGetAsync(int page = 0)
    {
        await LoadAsync(page);
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await _networkTesterService.ClearLogAndStatsAsync();
        TempData["StatusMessage"] = "日志数据已清空。";
        await LoadAsync(0);
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync(int page = 0)
    {
        await LoadAsync(page);
        TempData["StatusMessage"] = "内容已刷新。";
        return Page();
    }

    public async Task<IActionResult> OnPostLoadMoreAsync(int page = 0)
    {
        await LoadAsync(page + 1);
        return Page();
    }

    private async Task LoadAsync(int page)
    {
        var logPage = await _networkTesterService.ReadLogPageAsync(page, LogPageSize);
        LogText = logPage.Text;
        HasMoreLogs = logPage.HasMore;
        LogPage = logPage.Page;

        StatsText = await _networkTesterService.ReadStatsTailTextAsync(StatsMaxLines);
    }
}
