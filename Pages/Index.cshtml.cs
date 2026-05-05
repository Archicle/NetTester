using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NetTester.Services;
using System.ComponentModel.DataAnnotations;

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
        [Display(Name = "IP 列表")]
        public string IpTargetsText { get; set; } = string.Empty;

        [BindProperty]
        public string ExternalUrl { get; set; } = string.Empty;

        public bool IsRunning { get; private set; }
        public IReadOnlyList<PingTargetState> PingStats { get; private set; } = Array.Empty<PingTargetState>();
        public long ExternalTotal { get; private set; }
        public long ExternalSuccess { get; private set; }
        public long ExternalFailure { get; private set; }

        public async Task OnGetAsync()
        {
            await LoadStateAsync();
        }

        public async Task<IActionResult> OnPostSaveConfigAsync()
        {
            await _networkTesterService.SaveConfigAsync(ParseTargets(IpTargetsText), ExternalUrl);
            await LoadStateAsync();
            TempData["StatusMessage"] = "配置已保存。";
            return Page();
        }

        public async Task<IActionResult> OnPostStartAsync()
        {
            await _networkTesterService.SaveConfigAsync(ParseTargets(IpTargetsText), ExternalUrl);
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
            IpTargetsText = string.Join(Environment.NewLine, state.IpTargets);
            ExternalUrl = state.ExternalUrl;
            IsRunning = state.IsRunning;
            PingStats = state.PingStats;
            ExternalTotal = state.ExternalTotal;
            ExternalSuccess = state.ExternalSuccess;
            ExternalFailure = state.ExternalFailure;
        }

        private static IReadOnlyList<string> ParseTargets(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            return text
                .Split(['\r', '\n', ',', ';', '|', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
