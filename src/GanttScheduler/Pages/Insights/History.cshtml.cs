using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 전체 변경 이력: 과제를 가로질러 누가 언제 무엇을 바꿨는지 찾는다
public class HistoryModel : PageModel
{
    private readonly InsightService _insights;
    private readonly ExcelExporter? _excel;
    public HistoryModel(InsightService insights, ExcelExporter? excel = null)
    {
        _insights = insights;
        _excel = excel;
    }

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    // 'user'·'page' 는 PageModel 이 이미 쓰는 이름이라 쿼리 이름만 맞춘다 ('page' 는 라우트 값이기도 해서 'p' 를 쓴다)
    [BindProperty(SupportsGet = true, Name = "user")] public string? ByUser { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNo { get; set; } = 1;
    // 과제 하나의 이력만 (상세 활동 탭의 '이 과제 전체 이력')
    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }

    public HistoryPage Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _insights.HistoryAsync(Filter());

    private HistoryFilter Filter() => new(From, To, ByUser, Kind, Q, PageNo, ProjectId);

    // 지금 조건 그대로 내려받기 (모든 쪽 — 상한 InsightService.HistoryExportMax 행)
    public async Task<IActionResult> OnGetExcelAsync()
    {
        var data = await _insights.HistoryExportAsync(Filter());
        var parts = new List<string> { $"기간 {data.From:yyyy-MM-dd} ~ {data.To:yyyy-MM-dd}" };
        if (data.ProjectId != null) parts.Add("과제: " + (data.Project ?? "삭제된 과제"));
        if (!string.IsNullOrWhiteSpace(ByUser)) parts.Add("사용자: " + ByUser.Trim());
        if (!string.IsNullOrWhiteSpace(Kind)) parts.Add("구분: " + (Reporting.Kinds.FirstOrDefault(k => k.Key == Kind).Label ?? Kind));
        if (!string.IsNullOrWhiteSpace(Q)) parts.Add("검색: " + Q.Trim());
        var excel = _excel ?? HttpContext.RequestServices.GetRequiredService<ExcelExporter>();
        return File(excel.History(data.Rows, string.Join(" · ", parts), data.Total), ExcelExporter.ContentType, $"변경이력_{data.From:yyyyMMdd}-{data.To:yyyyMMdd}.xlsx");
    }
}
