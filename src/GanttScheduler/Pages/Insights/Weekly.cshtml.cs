using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 기간 보고(주간 보고): 주·월·직접 지정 기간의 실적·일정 변경·진행 내용·밀린 항목·미달과 다음 기간 계획을 과제별로 모은다.
// 범위는 전체/내 과제와 유형·법인·조직(하위 파트 포함), '변동 있는 과제만'
public class WeeklyModel : PageModel
{
    private readonly InsightService _insights;
    private readonly ExcelExporter _excel;
    private readonly PdfExporter _pdf;
    private readonly IClock _clock;
    private readonly AnalyticsService? _analytics;

    public WeeklyModel(InsightService insights, ExcelExporter excel, PdfExporter pdf, IClock clock, AnalyticsService? analytics = null)
    {
        _insights = insights;
        _excel = excel;
        _pdf = pdf;
        _clock = clock;
        _analytics = analytics;
    }

    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Day { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    [BindProperty(SupportsGet = true)] public int? Category { get; set; }
    [BindProperty(SupportsGet = true)] public int? Corp { get; set; }
    [BindProperty(SupportsGet = true)] public int? Org { get; set; }
    [BindProperty(SupportsGet = true)] public bool Changed { get; set; }

    public WeeklyReport Report { get; private set; } = null!;
    public List<PickOption> Categories { get; private set; } = new();
    public List<PickOption> Corporations { get; private set; } = new();
    public List<PickOption> Orgs { get; private set; } = new();

    public async Task OnGetAsync()
    {
        await LoadOptionsAsync();
        Report = await BuildAsync();
    }

    private async Task LoadOptionsAsync()
    {
        if (_analytics == null) return;
        Categories = await _analytics.CategoryOptionsAsync();
        Corporations = await _analytics.CorporationOptionsAsync();
        Orgs = await _analytics.OrgOptionsAsync();
    }

    private async Task<WeeklyReport> BuildAsync()
    {
        var (from, to, kind, notice) = Reporting.Period(Period, Day, From, To, _clock.Today);
        var report = await _insights.ReportAsync(from, to, new ProjectFilter(null, Category, Corp, null, Mine, OrgUnitId: Org), kind, Changed, notice);
        return report with { Scope = ScopeText() };
    }

    // 범위 설명 (Excel·PDF 제목 줄과 화면)
    public string ScopeText()
    {
        var parts = new List<string> { Mine ? "내 과제" : "전체" };
        if (Category != null && Categories.FirstOrDefault(c => c.Id == Category) is { } c) parts.Add("유형: " + c.Name);
        if (Corp != null && Corporations.FirstOrDefault(c => c.Id == Corp) is { } l) parts.Add("법인: " + l.Name);
        if (Org != null && Orgs.FirstOrDefault(o => o.Id == Org) is { } o) parts.Add("조직: " + o.Name);
        if (Changed) parts.Add("변동 있는 과제만");
        return string.Join(" · ", parts);
    }

    // 지금 조건 그대로의 쿼리 (이전·다음 기간, 내려받기). 기간은 바꿔 넣는다
    public Dictionary<string, string> Route(string? kind = null, DateOnly? day = null, DateOnly? from = null, DateOnly? to = null, bool? mine = null)
    {
        var r = new Dictionary<string, string>();
        var k = kind ?? Report.Kind;
        if (k != "week") r["period"] = k;
        if (k == "custom")
        {
            r["from"] = (from ?? Report.From).ToString("yyyy-MM-dd");
            r["to"] = (to ?? Report.To).ToString("yyyy-MM-dd");
        }
        else r["day"] = (day ?? Report.From).ToString("yyyy-MM-dd");
        if (mine ?? Mine) r["mine"] = "true";
        if (Category != null) r["category"] = Category.Value.ToString();
        if (Corp != null) r["corp"] = Corp.Value.ToString();
        if (Org != null) r["org"] = Org.Value.ToString();
        if (Changed) r["changed"] = "true";
        return r;
    }

    // 이전·다음 기간: 주는 7일, 달은 한 달, 직접 지정은 같은 길이만큼
    public Dictionary<string, string> Shift(int dir)
    {
        var r = Report;
        return r.Kind switch
        {
            "month" => Route(day: r.From.AddMonths(dir)),
            "custom" => Route(from: r.From.AddDays(dir * (r.To.DayNumber - r.From.DayNumber + 1)), to: r.To.AddDays(dir * (r.To.DayNumber - r.From.DayNumber + 1))),
            _ => Route(day: r.From.AddDays(7 * dir)),
        };
    }

    public string FileStem => $"{Report.Title.Replace(" ", "")}_{Report.From:yyyyMMdd}" + (Report.Kind == "week" ? "" : $"-{Report.To:yyyyMMdd}");

    public async Task<IActionResult> OnGetExcelAsync()
    {
        await LoadOptionsAsync();
        Report = await BuildAsync();
        return File(_excel.Weekly(Report), ExcelExporter.ContentType, FileStem + ".xlsx");
    }

    public async Task<IActionResult> OnGetPdfAsync()
    {
        await LoadOptionsAsync();
        Report = await BuildAsync();
        try
        {
            return File(_pdf.Weekly(Report), PdfExporter.ContentType, FileStem + ".pdf");
        }
        catch (RuleException ex)
        {
            return new ContentResult { StatusCode = StatusCodes.Status500InternalServerError, Content = ex.Message, ContentType = "text/plain; charset=utf-8" };
        }
    }
}
