using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 리드타임 통계: 유형·단계별 실제 소요일
public class LeadTimeModel : PageModel
{
    private readonly InsightService _insights;
    public LeadTimeModel(InsightService insights) => _insights = insights;

    // zero=exclude: 근무일 0 이하 표본(같은 날·순서를 거슬러 몰아 완료한 기록)을 소요일 평균에서 뺀다
    [BindProperty(SupportsGet = true)] public string? Zero { get; set; }
    public bool ExcludeZero => Zero == "exclude";

    // 종료 연도: 그 해에 끝난 완료 과제만 (없으면 전체 — 진행 과제의 완료 단계 포함)
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    public List<int> Years { get; private set; } = new();

    public List<LeadGroup> Groups { get; private set; } = new();

    public async Task OnGetAsync()
    {
        if (Year is int y0 && !Rules.IsPickableYear(y0)) Year = null;
        Groups = await _insights.LeadTimesAsync(ExcludeZero, Year);
        Years = await _insights.ClosedYearsAsync();
        if (Year is int y && !Years.Contains(y)) Years = Years.Append(y).OrderByDescending(x => x).ToList();
    }
}
