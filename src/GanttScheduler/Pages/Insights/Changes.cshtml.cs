using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 일정 변경 분석: 목표일·작업 일정 변경을 결정 단위로 — 유형·법인별 월간 결정 수, 재계획 상위 과제, 누적 밀림, 원인(사유 분류) 파레토
public class ChangesModel : PageModel
{
    private readonly AnalyticsService _analytics;
    public ChangesModel(AnalyticsService analytics) => _analytics = analytics;

    [BindProperty(SupportsGet = true)] public int? Months { get; set; }
    [BindProperty(SupportsGet = true)] public int? Category { get; set; }
    [BindProperty(SupportsGet = true)] public int? Corp { get; set; }
    [BindProperty(SupportsGet = true)] public int? Org { get; set; }

    public ChangesView Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _analytics.ChangesAsync(Months, new AnalyticsFilter(Category, Corp, Org));
}
