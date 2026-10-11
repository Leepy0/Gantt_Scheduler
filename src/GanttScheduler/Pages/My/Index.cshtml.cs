using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages.My;

// 내 할 일: 새 소식(내 작업·과제·관심 과제에 남이 남긴 활동), 내가 담당인 미완료 작업, 내가 담당인 과제의 진행 단계, 관심 과제.
// 작업 행에서 진행률·실적·진행 내용을 바로 고친다(OnPostProgress — my.js)
public class IndexModel : PageModel
{
    private readonly InsightService _insights;
    private readonly ProjectService _projects;
    private readonly CurrentUser _me;
    private readonly IClock _clock;

    public IndexModel(InsightService insights, ProjectService projects, CurrentUser me, IClock clock)
    {
        _insights = insights;
        _projects = projects;
        _me = me;
        _clock = clock;
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

    // 빠른 갱신: 저장한 뒤 그 행을 서버가 다시 나눠(지연·이번 주·이후) 돌려준다. 내 할 일에서 빠졌으면(완료 등) work 는 null.
    // 규칙 위반은 400, 다른 사람이 먼저 고쳤으면 409 와 최신 행(상세의 Run 과 같은 약속 — conflict:true)
    public async Task<IActionResult> OnPostProgressAsync([FromBody] WorkProgressInput input)
    {
        try
        {
            var projectId = await _projects.UpdateWorkProgressAsync(input);
            return new JsonResult(new { ok = true, work = await _insights.MyWorkAsync(projectId, input.WorkId), today = _clock.Today });
        }
        catch (ConflictException ex)
        {
            return new JsonResult(new { ok = false, conflict = true, error = ex.Message, work = await _insights.MyWorkAsync(ex.ProjectId, input.WorkId) })
            { StatusCode = StatusCodes.Status409Conflict };
        }
        catch (DbUpdateConcurrencyException)
        {
            return new JsonResult(new { ok = false, conflict = true, error = new ConflictException(0).Message, work = (MyWork?)null })
            { StatusCode = StatusCodes.Status409Conflict };
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
