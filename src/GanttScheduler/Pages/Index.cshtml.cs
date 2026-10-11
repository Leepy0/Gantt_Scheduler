using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages;

// 대시보드
public class IndexModel : PageModel
{
    private readonly DashboardService _dash;
    private readonly ProjectService _projects;
    private readonly IClock _clock;
    private readonly AppDb _db;

    public IndexModel(DashboardService dash, ProjectService projects, IClock clock, AppDb db)
    {
        _dash = dash;
        _projects = projects;
        _clock = clock;
        _db = db;
    }

    // 조건은 URL 쿼리로 둔다(링크로 공유·새로고침해도 같은 화면). 마지막 조건은 화면(dashboard.js)이 기억해 다음 방문 때 되살린다
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    [BindProperty(SupportsGet = true)] public int? Category { get; set; }
    [BindProperty(SupportsGet = true)] public int? Corp { get; set; }
    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    public List<Category> Categories { get; private set; } = new();
    public List<Location> Corporations { get; private set; } = new();
    public DashboardQuery Query => new(Mine, Category, Corp, Scope, Sort);

    // 이 화면의 다른 조건은 그대로 두고 일부만 바꾼 쿼리 (범위·정렬 단추, KPI·막대 링크가 쓴다)
    public Dictionary<string, string> Route(string? scope = null, string? sort = null)
    {
        var r = new Dictionary<string, string>();
        if (Mine) r["mine"] = "true";
        if (Category != null) r["category"] = Category.Value.ToString();
        if (Corp != null) r["corp"] = Corp.Value.ToString();
        // 범위는 늘 적는다 — 쿼리 없는 '/'는 다른 화면에서 온 것으로 보고 기억한 조건을 되살리기 때문에(dashboard.js),
        // 이 화면 안의 링크는 기본 조건이라도 쿼리를 가져야 한다
        r["scope"] = scope ?? Query.ScopeKey;
        var so = sort ?? Query.SortKey;
        if (so != "delay") r["sort"] = so;
        return r;
    }
    public DashboardView Data { get; private set; } = null!;
    // 과제별 '새 활동' 수(다른 사람이 마지막 방문 뒤에 남긴 것) — 지연 과제·다가오는 일정 줄에 붙인다
    public Dictionary<int, int> NewCounts { get; private set; } = new();
    public string DataJson { get; private set; } = "{}";
    public string AsOf { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Data = await _dash.BuildAsync(Query);
        Categories = (await _db.Categories.AsNoTracking().ToListAsync()).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
        Corporations = (await _projects.LocationsAsync()).All.Where(l => l.Level == 1).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList();
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
