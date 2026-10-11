using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.My;

// 내 할 일: 새 소식(내 작업·과제·관심 과제에 남이 남긴 활동), 내가 담당인 미완료 작업, 내가 담당인 과제의 진행 단계, 관심 과제
public class IndexModel : PageModel
{
    private readonly InsightService _insights;
    private readonly ProjectService _projects;
    private readonly CurrentUser _me;

    public IndexModel(InsightService insights, ProjectService projects, CurrentUser me)
    {
        _insights = insights;
        _projects = projects;
        _me = me;
    }

    public MyView Data { get; private set; } = null!;
    public List<NewsItem> News { get; private set; } = new();
    public List<WatchedProject> Watched { get; private set; } = new();
    public string Name => _me.Name;

    public async Task OnGetAsync()
    {
        Data = await _insights.MyAsync();
        News = await _projects.NewsAsync();
        Watched = await _projects.WatchedProjectsAsync();
    }
}
