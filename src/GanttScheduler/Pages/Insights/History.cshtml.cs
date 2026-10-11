using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Insights;

// 전체 변경 이력: 과제를 가로질러 누가 언제 무엇을 바꿨는지 찾는다
public class HistoryModel : PageModel
{
    private readonly InsightService _insights;
    public HistoryModel(InsightService insights) => _insights = insights;

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

    public async Task OnGetAsync() => Data = await _insights.HistoryAsync(new HistoryFilter(From, To, ByUser, Kind, Q, PageNo, ProjectId));
}
