using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

// 대시보드
public class IndexModel : PageModel
{
    private readonly DashboardService _dash;
    private readonly ProjectService _projects;
    private readonly IClock _clock;

    public IndexModel(DashboardService dash, ProjectService projects, IClock clock)
    {
        _dash = dash;
        _projects = projects;
        _clock = clock;
    }

    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    public DashboardView Data { get; private set; } = null!;
    // 과제별 '새 활동' 수(다른 사람이 마지막 방문 뒤에 남긴 것) — 지연 과제·다가오는 일정 줄에 붙인다
    public Dictionary<int, int> NewCounts { get; private set; } = new();
    public string DataJson { get; private set; } = "{}";
    public string AsOf { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Data = await _dash.BuildAsync(Mine);
        NewCounts = await _projects.NewCountsAsync();
        AsOf = KoreaClock.ToKst(_clock.UtcNow).ToString("MM-dd HH:mm");
        DataJson = AppJson.Serialize(new
        {
            today = Data.Today,
            holidays = await _projects.HolidaysAsync(),
            timeline = Data.Timeline,
        });
    }
}
