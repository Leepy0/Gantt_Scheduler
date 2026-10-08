using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 리드타임 통계: 유형·단계별 실제 소요일
public class LeadTimeModel : PageModel
{
    private readonly InsightService _insights;
    public LeadTimeModel(InsightService insights) => _insights = insights;

    public List<LeadGroup> Groups { get; private set; } = new();

    public async Task OnGetAsync() => Groups = await _insights.LeadTimesAsync();
}
