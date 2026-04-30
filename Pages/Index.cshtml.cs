using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NetTester.Services;

namespace NetTester.Pages
{
    public class IndexModel : PageModel
    {
        private readonly NetworkTesterService _networkTesterService;

        public IndexModel(NetworkTesterService networkTesterService)
        {
            _networkTesterService = networkTesterService;
        }

        [BindProperty]
        public string Ip1 { get; set; } = string.Empty;

        [BindProperty]
        public string Ip2 { get; set; } = string.Empty;

        [BindProperty]
        public string ExternalUrl { get; set; } = string.Empty;

        public bool IsRunning { get; private set; }
        public long Ip1Total { get; private set; }
        public long Ip1Success { get; private set; }
        public long Ip1Failure { get; private set; }
        public long Ip2Total { get; private set; }
        public long Ip2Success { get; private set; }
        public long Ip2Failure { get; private set; }
        public long ExternalTotal { get; private set; }
        public long ExternalSuccess { get; private set; }
        public long ExternalFailure { get; private set; }

        public async Task OnGetAsync()
        {
            await LoadStateAsync();
        }

        public async Task<IActionResult> OnPostSaveConfigAsync()
        {
            await _networkTesterService.SaveConfigAsync(Ip1, Ip2, ExternalUrl);
            await LoadStateAsync();
            TempData["StatusMessage"] = "配置已保存。";
            return Page();
        }

        public async Task<IActionResult> OnPostStartAsync()
        {
            await _networkTesterService.SaveConfigAsync(Ip1, Ip2, ExternalUrl);
            _networkTesterService.StartTesting();
            await LoadStateAsync();
            TempData["StatusMessage"] = "检测已启动。";
            return Page();
        }

        public async Task<IActionResult> OnPostStopAsync()
        {
            _networkTesterService.StopTesting();
            await LoadStateAsync();
            TempData["StatusMessage"] = "检测已停止。";
            return Page();
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            await LoadStateAsync();
            TempData["StatusMessage"] = "统计已刷新。";
            return Page();
        }

        private async Task LoadStateAsync()
        {
            var state = await _networkTesterService.GetStateAsync();
            Ip1 = state.Ip1;
            Ip2 = state.Ip2;
            ExternalUrl = state.ExternalUrl;
            IsRunning = state.IsRunning;
            Ip1Total = state.Ip1Total;
            Ip1Success = state.Ip1Success;
            Ip1Failure = state.Ip1Failure;
            Ip2Total = state.Ip2Total;
            Ip2Success = state.Ip2Success;
            Ip2Failure = state.Ip2Failure;
            ExternalTotal = state.ExternalTotal;
            ExternalSuccess = state.ExternalSuccess;
            ExternalFailure = state.ExternalFailure;
        }
    }
}
