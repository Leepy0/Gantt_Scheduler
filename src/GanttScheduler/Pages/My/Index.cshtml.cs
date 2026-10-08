using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.My;

// 내 할 일: 내가 담당인 미완료 작업과, 내가 담당인 과제의 진행 단계
public class IndexModel : PageModel
{
    private readonly InsightService _insights;
    private readonly CurrentUser _me;

    public IndexModel(InsightService insights, CurrentUser me)
    {
        _insights = insights;
        _me = me;
    }

    public MyView Data { get; private set; } = null!;
    public string Name => _me.Name;

    public async Task OnGetAsync() => Data = await _insights.MyAsync();
}
