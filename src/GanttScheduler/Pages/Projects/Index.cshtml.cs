using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages.Projects;

// 과제 목록
public class IndexModel : PageModel
{
    private readonly DashboardService _dash;
    private readonly AppDb _db;
    private readonly ExcelExporter _excel;
    private readonly IClock _clock;

    private readonly ProjectService _projects;

    public IndexModel(DashboardService dash, AppDb db, ExcelExporter excel, IClock clock, ProjectService projects)
    {
        _dash = dash;
        _db = db;
        _excel = excel;
        _clock = clock;
        _projects = projects;
    }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? Category { get; set; }
    [BindProperty(SupportsGet = true)] public int? Corp { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }

    public List<ProjectView> Rows { get; private set; } = new();
    public List<Category> Categories { get; private set; } = new();
    public List<Location> Corporations { get; private set; } = new();
    public DateOnly Today { get; private set; }

    // 상태 필터를 지정하지 않으면 진행 과제만 본다 ("all" 은 전체)
    public string StateKey => State ?? "active";

    public static readonly (string Key, string Label)[] States =
    {
        ("active", "진행"), ("delayed", "지연"), ("hold", "보류"), ("done", "완료"), ("dropped", "중단"), ("all", "전체"),
    };

    public async Task OnGetAsync()
    {
        Today = _clock.Today;
        Categories = (await _db.Categories.AsNoTracking().ToListAsync()).OrderBy(c => c.SortOrder).ToList();
        // 위치는 목록 계산(ListAsync)이 읽는 것을 같이 쓴다 (요청 안에서 한 번만 읽기)
        Corporations = (await _projects.LocationsAsync()).All.Where(l => l.Level == 1).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList();
        Rows = await _dash.ListAsync(Filter());
    }

    public async Task<IActionResult> OnGetExcelAsync()
    {
        var rows = await _dash.ListAsync(Filter());
        var label = States.FirstOrDefault(x => x.Key == StateKey).Label ?? "전체";
        var filter = "상태: " + label + (string.IsNullOrWhiteSpace(Q) ? "" : $" · 검색: {Q.Trim()}") + (Mine ? " · 내 과제" : "");
        return File(_excel.ProjectList(rows, _clock.Today, filter), ExcelExporter.ContentType, $"과제목록_{_clock.Today:yyyyMMdd}.xlsx");
    }

    private ProjectFilter Filter() => new(Q, Category, Corp, StateKey, Mine);
}
