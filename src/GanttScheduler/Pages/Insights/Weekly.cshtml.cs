using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 주간 보고: 한 주(월~일)의 실적·일정 변경·진행 내용과 다음 주 계획을 과제별로 모은다
public class WeeklyModel : PageModel
{
    private readonly InsightService _insights;
    private readonly ExcelExporter _excel;
    private readonly PdfExporter _pdf;

    public WeeklyModel(InsightService insights, ExcelExporter excel, PdfExporter pdf)
    {
        _insights = insights;
        _excel = excel;
        _pdf = pdf;
    }

    [BindProperty(SupportsGet = true)] public DateOnly? Day { get; set; }
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    public WeeklyReport Report { get; private set; } = null!;

    public async Task OnGetAsync() => Report = await _insights.WeeklyAsync(Day, Mine);

    public async Task<IActionResult> OnGetExcelAsync()
    {
        var report = await _insights.WeeklyAsync(Day, Mine);
        return File(_excel.Weekly(report), ExcelExporter.ContentType, $"주간보고_{report.From:yyyyMMdd}.xlsx");
    }

    public async Task<IActionResult> OnGetPdfAsync()
    {
        var report = await _insights.WeeklyAsync(Day, Mine);
        try
        {
            return File(_pdf.Weekly(report), PdfExporter.ContentType, $"주간보고_{report.From:yyyyMMdd}.pdf");
        }
        catch (RuleException ex)
        {
            return new ContentResult { StatusCode = StatusCodes.Status500InternalServerError, Content = ex.Message, ContentType = "text/plain; charset=utf-8" };
        }
    }
}
