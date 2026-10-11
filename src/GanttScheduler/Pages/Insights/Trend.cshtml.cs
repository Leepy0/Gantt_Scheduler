using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 추세: 일별 스냅샷으로 본 진행·지연·위험 과제 수와 평균 진행률의 흐름, 법인 × 기기 계획 수량
public class TrendModel : PageModel
{
    private readonly AnalyticsService _analytics;
    public TrendModel(AnalyticsService analytics) => _analytics = analytics;

    [BindProperty(SupportsGet = true)] public string? Range { get; set; }
    [BindProperty(SupportsGet = true)] public int? Category { get; set; }
    [BindProperty(SupportsGet = true)] public int? Corp { get; set; }
    [BindProperty(SupportsGet = true)] public int? Org { get; set; }

    public TrendView Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _analytics.TrendAsync(Range, new AnalyticsFilter(Category, Corp, Org));
}
