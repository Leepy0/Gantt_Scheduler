using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Projects;

// 과제 상세 — 화면은 JSON 으로 그리고, 변경은 핸들러가 처리한 뒤 최신 과제를 돌려준다
public class DetailModel : PageModel
{
    private readonly ProjectService _projects;
    private readonly ExcelExporter _excel;
    private readonly CurrentUser _me;
    private readonly IClock _clock;

    public DetailModel(ProjectService projects, ExcelExporter excel, CurrentUser me, IClock clock)
    {
        _projects = projects;
        _excel = excel;
        _me = me;
        _clock = clock;
    }

    public record IdInput(int Id);
    public record MoveInput(int Id, int Dir);
    public record StateInput(int ProjectId, string? State, string? Reason);

    public ProjectView Project { get; private set; } = null!;
    public string DataJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        if (view == null) return NotFound();
        Project = view;
        DataJson = AppJson.Serialize(new
        {
            project = view,
            today = _clock.Today,
            holidays = await _projects.HolidaysAsync(),
            users = await _projects.UserNamesAsync(),
            me = new { name = _me.Name, isAdmin = _me.IsAdmin },
        });
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        return view == null ? NotFound() : new JsonResult(new { ok = true, project = view });
    }

    public async Task<IActionResult> OnGetExcelAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        if (view == null) return NotFound();
        return File(_excel.ProjectSchedule(view), ExcelExporter.ContentType, $"{SafeFileName(view.Title)}_{_clock.Today:yyyyMMdd}.xlsx");
    }

    public Task<IActionResult> OnPostSaveStageAsync([FromBody] StageInput input) =>
        Run(async () => { await _projects.SaveStageAsync(input); return input.ProjectId; });

    public Task<IActionResult> OnPostMoveStageAsync([FromBody] MoveInput input) =>
        Run(() => _projects.MoveStageAsync(input.Id, input.Dir));

    public Task<IActionResult> OnPostDeleteStageAsync([FromBody] IdInput input) =>
        Run(() => _projects.DeleteStageAsync(input.Id));

    public Task<IActionResult> OnPostSaveWorkAsync([FromBody] WorkInput input) =>
        Run(() => _projects.SaveWorkAsync(input));

    public Task<IActionResult> OnPostDeleteWorkAsync([FromBody] IdInput input) =>
        Run(() => _projects.DeleteWorkAsync(input.Id));

    public Task<IActionResult> OnPostCommentAsync([FromBody] CommentInput input) =>
        Run(async () => { await _projects.CommentAsync(input); return input.ProjectId; });

    public Task<IActionResult> OnPostStateAsync([FromBody] StateInput input) =>
        Run(async () =>
        {
            if (!Enum.TryParse<ProjectState>(input.State, ignoreCase: true, out var state))
                throw new RuleException("알 수 없는 상태입니다.");
            await _projects.SetStateAsync(input.ProjectId, state, input.Reason);
            return input.ProjectId;
        });

    public async Task<IActionResult> OnPostDeleteAsync([FromBody] IdInput input)
    {
        try
        {
            await _projects.DeleteAsync(input.Id);
            return new JsonResult(new { ok = true, redirect = Url.Page("/Projects/Index") });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    // 변경 실행 → 최신 과제 JSON. 규칙 위반은 400 과 메시지로 돌려준다
    private async Task<IActionResult> Run(Func<Task<int>> action)
    {
        try
        {
            var projectId = await action();
            return new JsonResult(new { ok = true, project = await _projects.ViewAsync(projectId) });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    private static JsonResult Fail(RuleException ex) =>
        new(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 60 ? name[..60] : name;
    }
}
