using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 담당자·조직별 부하: 사람 × 주의 '배정 근무일 / 가용 근무일'(담당 1.0, 부담당 0.5 — Rules.Load)
public class LoadModel : PageModel
{
    private readonly AnalyticsService _analytics;
    public LoadModel(AnalyticsService analytics) => _analytics = analytics;

    [BindProperty(SupportsGet = true)] public int? Weeks { get; set; }
    [BindProperty(SupportsGet = true)] public int? Org { get; set; }

    public LoadView Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _analytics.LoadAsync(Weeks, Org);
}
