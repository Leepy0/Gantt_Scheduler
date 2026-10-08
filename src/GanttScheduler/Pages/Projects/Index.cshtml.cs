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

    public IndexModel(DashboardService dash, AppDb db, ExcelExporter excel, IClock clock)
    {
        _dash = dash;
        _db = db;
        _excel = excel;
        _clock = clock;
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
        Corporations = (await _db.Locations.AsNoTracking().ToListAsync()).Where(l => l.Level == 1).OrderBy(l => l.SortOrder).ToList();
        Rows = await _dash.ListAsync(Filter());
    }

    public async Task<IActionResult> OnGetExcelAsync()
    {
        var rows = await _dash.ListAsync(Filter());
        return File(_excel.ProjectList(rows), ExcelExporter.ContentType, $"과제목록_{_clock.Today:yyyyMMdd}.xlsx");
    }

    private ProjectFilter Filter() => new(Q, Category, Corp, StateKey, Mine);
}
