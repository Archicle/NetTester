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
        [Display(Name = "HTTP 列表")]
        public string HttpTargetsText { get; set; } = string.Empty;

        [BindProperty]
        public bool EnableIpProbe { get; set; } = true;

        [BindProperty]
        public bool EnableHttpProbe { get; set; } = true;

        public bool IsRunning { get; private set; }

        public async Task OnGetAsync()
        {
            await LoadStateAsync();
        }

        public async Task<IActionResult> OnPostSaveConfigAsync()
        {
            await _networkTesterService.SaveConfigAsync(ParseTargets(IpTargetsText), ParseTargets(HttpTargetsText), EnableIpProbe, EnableHttpProbe);
            await LoadStateAsync();
            TempData["StatusMessage"] = "配置已保存。";
            return Page();
        }

        public async Task<IActionResult> OnPostStartAsync()
        {
            await _networkTesterService.SaveConfigAsync(ParseTargets(IpTargetsText), ParseTargets(HttpTargetsText), EnableIpProbe, EnableHttpProbe);
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

        private async Task LoadStateAsync()
        {
            var state = await _networkTesterService.GetStateAsync();
            IpTargetsText = string.Join(Environment.NewLine, state.IpTargets);
            HttpTargetsText = string.Join(Environment.NewLine, state.HttpTargets);
            EnableIpProbe = state.EnableIpProbe;
            EnableHttpProbe = state.EnableHttpProbe;
            IsRunning = state.IsRunning;
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
