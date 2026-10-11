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
    private readonly AnalyticsService? _analytics;

    public IndexModel(DashboardService dash, AppDb db, ExcelExporter excel, IClock clock, ProjectService projects, AnalyticsService? analytics = null)
    {
        _analytics = analytics;
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
    // 대시보드 숫자에서 온 조건: 그 해에 끝난 완료 과제(올해 완료), 미완료 단계 목표일이 n일 안인 진행 과제(n일 내 목표·다가오는 일정)
    [BindProperty(SupportsGet = true)] public int? DoneYear { get; set; }
    [BindProperty(SupportsGet = true)] public int? Due { get; set; }
    // 대시보드 막대에서 온 조건: 진행·보류 과제와 올해 끝난 과제(Dashboard.InBars)
    [BindProperty(SupportsGet = true)] public bool Current { get; set; }
    // 조직: 과제 담당자의 소속(하위 파트 포함)
    [BindProperty(SupportsGet = true)] public int? Org { get; set; }
    // 완료·중단 탭의 종료 연도(Project.ClosedOn). 없으면 올해(대시보드 숫자에서 온 doneYear 가 있으면 그 해), 0 이면 전체 연도
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    // 고를 수 있는 연도: 가장 이른 종료 연도 ~ 올해 (최근 해가 먼저)
    public List<int> Years { get; private set; } = new();
    public List<PickOption> Orgs { get; private set; } = new();
    // 머리글 정렬 (Dashboard.SortKeys). 없으면 상태 → 지연 큰 순 → 목표 종료
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }

    public List<ProjectView> Rows { get; private set; } = new();
    // 과제별 '새 활동' 수(다른 사람이 마지막 방문 뒤에 남긴 것)와 내 관심 과제 — 한 번씩만 읽는다
    public Dictionary<int, int> NewCounts { get; private set; } = new();
    public HashSet<int> Watched { get; private set; } = new();
    public List<Category> Categories { get; private set; } = new();
    public List<Location> Corporations { get; private set; } = new();
    public DateOnly Today { get; private set; }

    // 상태 필터를 지정하지 않으면 진행 과제만 본다 ("all" 은 전체)
    public string StateKey => State ?? "active";

    // 연도 고르기가 있는 탭(끝난 과제를 보는 탭)
    public bool HasYear => StateKey is "done" or "dropped";

    // 이 목록에 적용하는 종료 연도 (null = 전체 연도·연도 조건 없는 탭)
    public int? ClosedYear => !HasYear || Year == 0 ? null : Year ?? DoneYear ?? _clock.Today.Year;

    public static readonly (string Key, string Label)[] States =
    {
        ("active", "진행"), ("delayed", "지연"), ("risk", "위험"), ("stale", "정체"), ("hold", "보류"), ("done", "완료"), ("dropped", "중단"), ("all", "전체"),
    };

    // 머리글 (정렬 키, 글자)
    public static readonly (string Key, string Label)[] Columns =
    {
        ("title", "과제"), ("corp", "위치"), ("owner", "담당"), ("stage", "현재 단계"), ("progress", "진행률"), ("end", "목표 종료"), ("state", "상태"),
    };

    // 머리글이 없는 좁은 화면(모바일)의 정렬 고르기 (키, 내림차순, 글자)
    public static readonly (string? Key, bool Desc, string Label)[] SortChoices =
    {
        (null, false, "기본 순서"), ("delay", true, "지연 큰 순"), ("end", false, "목표 종료 빠른 순"), ("recent", true, "최근 변경 순"),
        ("progress", true, "진행률 높은 순"), ("title", false, "과제명 순"),
    };

    public Dictionary<string, string> ChoiceRoute(string? key, bool desc)
    {
        var r = Route();
        r.Remove("sort");
        r.Remove("desc");
        if (key != null) r["sort"] = key;
        if (desc) r["desc"] = "true";
        return r;
    }

    public string? SortKey => Dashboard.SortKeys.Any(k => k.Key == Sort) ? Sort : null;

    // 머리글 링크: 같은 열을 다시 누르면 방향을 바꾼다. 처음 누르면 그 열의 기본 방향(진행률은 큰 순, 나머지는 작은 순)
    public Dictionary<string, string> SortRoute(string key)
    {
        var r = Route();
        var first = Dashboard.SortKeys.First(k => k.Key == key).DescFirst;
        r["sort"] = key;
        var desc = SortKey == key ? !Desc : first;
        if (desc) r["desc"] = "true"; else r.Remove("desc");
        return r;
    }

    // aria-sort 값 (정렬하지 않은 열은 none)
    public string AriaSort(string key) =>
        (SortKey ?? "state") != key ? "none" : Desc ? "descending" : "ascending";

    // 지금 조건 그대로의 쿼리 (Excel·정렬 링크)
    public Dictionary<string, string> Route()
    {
        var r = new Dictionary<string, string> { ["state"] = StateKey };
        if (!string.IsNullOrWhiteSpace(Q)) r["q"] = Q;
        if (Category != null) r["category"] = Category.Value.ToString();
        if (Corp != null) r["corp"] = Corp.Value.ToString();
        if (Mine) r["mine"] = "true";
        if (DoneYear != null) r["doneYear"] = DoneYear.Value.ToString();
        if (Due != null) r["due"] = Due.Value.ToString();
        if (Current) r["current"] = "true";
        if (Org != null) r["org"] = Org.Value.ToString();
        if (HasYear && Year != null) r["year"] = Year.Value.ToString();
        if (SortKey != null) r["sort"] = SortKey;
        if (Desc) r["desc"] = "true";
        return r;
    }

    public async Task OnGetAsync()
    {
        Today = _clock.Today;
        Categories = (await _db.Categories.AsNoTracking().ToListAsync()).OrderBy(c => c.SortOrder).ToList();
        // 위치는 목록 계산(ListAsync)이 읽는 것을 같이 쓴다 (요청 안에서 한 번만 읽기)
        Corporations = (await _projects.LocationsAsync()).All.Where(l => l.Level == 1).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList();
        Rows = await _dash.ListAsync(Filter());
        if (_analytics != null) Orgs = await _analytics.OrgOptionsAsync();
        NewCounts = await _projects.NewCountsAsync();
        Watched = await _projects.WatchedIdsAsync();
        if (HasYear)
        {
            // 가장 이른 종료일 하나만 읽는다(연도별 묶음 집계는 두 DB 의 번역이 달라질 수 있어 피한다)
            var first = await _db.LiveProjects.AsNoTracking().Where(p => p.ClosedOn != null).OrderBy(p => p.ClosedOn)
                .Select(p => p.ClosedOn).FirstOrDefaultAsync();
            var thisYear = Today.Year;
            var from = Math.Min(first?.Year ?? thisYear, ClosedYear ?? thisYear);
            for (var y = Math.Max(thisYear, ClosedYear ?? thisYear); y >= from; y--) Years.Add(y);
        }
    }

    public async Task<IActionResult> OnGetExcelAsync()
    {
        var rows = await _dash.ListAsync(Filter());
        // 제목의 조건은 화면에서 고른 것 전부 (유형·법인 포함) — 내려받은 파일만 보고도 어떤 목록인지 알 수 있게
        var category = Category == null ? null : (await _db.Categories.AsNoTracking().Where(c => c.Id == Category.Value).Select(c => c.Name).FirstOrDefaultAsync());
        var corp = Corp == null ? null : (await _projects.LocationsAsync()).Get(Corp.Value)?.Name;
        if (Org != null && _analytics != null) Orgs = await _analytics.OrgOptionsAsync();
        return File(_excel.ProjectList(rows, _clock.Today, FilterText(category, corp)), ExcelExporter.ContentType, $"과제목록_{_clock.Today:yyyyMMdd}.xlsx");
    }

    // 조건 설명 (Excel 제목·화면 안내)
    public string FilterText(string? category, string? corp)
    {
        var label = States.FirstOrDefault(x => x.Key == StateKey).Label ?? "전체";
        var parts = new List<string> { "상태: " + label };
        if (category != null) parts.Add("유형: " + category);
        if (corp != null) parts.Add("법인: " + corp);
        if (Org != null && OrgName != null) parts.Add("조직: " + OrgName);
        if (HasYear) parts.Add(ClosedYear is int y ? $"종료 연도: {y}년" : "종료 연도: 전체");
        if (!string.IsNullOrWhiteSpace(Q)) parts.Add("검색: " + Q.Trim());
        if (SpecialText is { } special) parts.Add(special);
        if (Mine) parts.Add("내 과제");
        if (SortKey != null) parts.Add("정렬: " + SortName(SortKey) + (Desc ? " ↓" : " ↑"));
        return string.Join(" · ", parts);
    }

    // 정렬 키의 한국어 이름 (Excel 제목). 머리글이 없는 키(지연·최근 변경 — 좁은 화면의 고르기와 기억한 정렬로만 고른다)도 이름을 둔다
    public static string SortName(string key) =>
        Columns.FirstOrDefault(c => c.Key == key).Label
        ?? key switch { "delay" => "지연 일수", "recent" => "최근 변경", _ => "기본 순서" };

    // 대시보드 숫자에서 온 조건의 설명 (없으면 null)
    public string? SpecialText =>
        DoneYear != null ? $"{DoneYear}년 완료"
        : Due != null ? $"단계 목표일 {Math.Clamp(Due.Value, 0, Dashboard.MaxDueDays)}일 안"
        : Current ? $"진행·보류와 {_clock.Today.Year}년 완료 (대시보드 막대와 같은 범위)"
        : null;

    public string? OrgName => Orgs.FirstOrDefault(o => o.Id == Org)?.Name;

    private ProjectFilter Filter() => new(Q, Category, Corp, StateKey, Mine, DoneYear, Due, SortKey, Desc, Current, Org, ClosedYear);
}
