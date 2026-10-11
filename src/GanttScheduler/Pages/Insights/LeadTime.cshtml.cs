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

    public List<LeadGroup> Groups { get; private set; } = new();

    public async Task OnGetAsync() => Groups = await _insights.LeadTimesAsync(ExcludeZero);
}
