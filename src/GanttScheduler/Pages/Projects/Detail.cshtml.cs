using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages.Projects;

// 과제 상세 — 화면은 JSON 으로 그리고, 변경은 핸들러가 처리한 뒤 최신 과제를 돌려준다
public class DetailModel : PageModel
{
    private readonly ProjectService _projects;
    private readonly AttachmentService _files;
    private readonly ExcelExporter _excel;
    private readonly PdfExporter _pdf;
    private readonly CurrentUser _me;
    private readonly IClock _clock;

    public DetailModel(ProjectService projects, AttachmentService files, ExcelExporter excel, PdfExporter pdf, CurrentUser me, IClock clock)
    {
        _projects = projects;
        _files = files;
        _excel = excel;
        _pdf = pdf;
        _me = me;
        _clock = clock;
    }

    public record IdInput(int Id);
    public record MoveInput(int Id, int Dir);
    public record StateInput(int ProjectId, string? State, string? Reason, int? Version);
    public record ToggleInput(int Id, bool Done);
    public record PreviewInput(int ProjectId, List<StagePlanRow>? Rows);

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
            upload = new { maxBytes = AttachmentService.MaxBytes, extensions = AttachmentService.Allowed.Keys.OrderBy(x => x) },
        });
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        return view == null ? Missing() : new JsonResult(new { ok = true, project = view, today = _clock.Today });
    }

    // 과제가 마지막으로 바뀐 시각 하나만 돌려준다 — 열린 화면이 1분마다 물어 '다른 사용자가 바꿨습니다' 띠를 띄운다
    public async Task<IActionResult> OnGetStampAsync(int id)
    {
        var stamp = await _projects.StampAsync(id);
        return stamp == null ? Missing() : new JsonResult(new { ok = true, updatedAt = stamp });
    }

    // 화면이 JSON 으로 묻는 과제가 없을 때 (삭제됨) — 본문 없는 404 대신 이유를 알린다
    private static JsonResult Missing() =>
        new(new { ok = false, missing = true, error = "과제를 찾을 수 없습니다. 다른 사용자가 삭제했을 수 있습니다." }) { StatusCode = StatusCodes.Status404NotFound };

    public async Task<IActionResult> OnGetExcelAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        if (view == null) return NotFound();
        return File(_excel.ProjectSchedule(view, _clock.Today), ExcelExporter.ContentType, $"{SafeFileName(view.Title)}_{_clock.Today:yyyyMMdd}.xlsx");
    }

    public async Task<IActionResult> OnGetPdfAsync(int id)
    {
        var view = await _projects.ViewAsync(id);
        if (view == null) return NotFound();
        try
        {
            return File(_pdf.ProjectSchedule(view, _clock.Today), PdfExporter.ContentType, $"{SafeFileName(view.Title)}_{_clock.Today:yyyyMMdd}.pdf");
        }
        catch (RuleException ex)
        {
            // 글꼴을 못 찾은 경우 등 — 내려받기 링크이므로 글자로 알려 준다
            return new ContentResult { StatusCode = StatusCodes.Status500InternalServerError, Content = ex.Message, ContentType = "text/plain; charset=utf-8" };
        }
    }

    public Task<IActionResult> OnPostSaveStageAsync([FromBody] StageInput input) =>
        Run(input.ProjectId, async () => { await _projects.SaveStageAsync(input); return input.ProjectId; });

    // 단계 일정 표: 여러 단계의 목표일·완료일을 한 번에 저장한다
    public Task<IActionResult> OnPostSaveScheduleAsync([FromBody] StageScheduleInput input) =>
        Run(input.ProjectId, async () => { await _projects.SaveStageScheduleAsync(input); return input.ProjectId; });

    // 단계 일정 표에서 날짜를 고치는 동안 근무일 간격·추천 입고일을 다시 계산한다(저장하지 않는다)
    public async Task<IActionResult> OnPostSchedulePreviewAsync([FromBody] PreviewInput input)
    {
        try
        {
            return new JsonResult(new { ok = true, rows = await _projects.SchedulePreviewAsync(input.ProjectId, input.Rows) });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    public Task<IActionResult> OnPostMoveStageAsync([FromBody] MoveInput input) =>
        Run(null, () => _projects.MoveStageAsync(input.Id, input.Dir));

    public Task<IActionResult> OnPostDeleteStageAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteStageAsync(input.Id));

    public Task<IActionResult> OnPostSaveWorkAsync([FromBody] WorkInput input) =>
        Run(null, () => _projects.SaveWorkAsync(input));

    public Task<IActionResult> OnPostDeleteWorkAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteWorkAsync(input.Id));

    public Task<IActionResult> OnPostCommentAsync([FromBody] CommentInput input) =>
        Run(input.ProjectId, async () => { await _projects.CommentAsync(input); return input.ProjectId; });

    public Task<IActionResult> OnPostStateAsync([FromBody] StateInput input) =>
        Run(input.ProjectId, async () =>
        {
            if (!Enum.TryParse<ProjectState>(input.State, ignoreCase: true, out var state))
                throw new RuleException("알 수 없는 상태입니다.");
            await _projects.SetStateAsync(input.ProjectId, state, input.Reason, input.Version);
            return input.ProjectId;
        });

    public Task<IActionResult> OnPostBaselineAsync([FromBody] IdInput input) =>
        Run(input.Id, async () => { await _projects.SetBaselineAsync(input.Id); return input.Id; });

    public Task<IActionResult> OnPostSaveCheckAsync([FromBody] CheckInput input) =>
        Run(null, () => _projects.SaveCheckAsync(input));

    public Task<IActionResult> OnPostToggleCheckAsync([FromBody] ToggleInput input) =>
        Run(null, () => _projects.ToggleCheckAsync(input.Id, input.Done));

    public Task<IActionResult> OnPostDeleteCheckAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteCheckAsync(input.Id));

    public Task<IActionResult> OnPostDeleteFileAsync([FromBody] IdInput input) =>
        Run(null, () => _files.DeleteAsync(input.Id));

    public async Task<IActionResult> OnPostCloneAsync([FromBody] CloneInput input)
    {
        try
        {
            var id = await _projects.CloneAsync(input);
            return new JsonResult(new { ok = true, redirect = Url.Page("/Projects/Detail", new { id }) });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromBody] IdInput input)
    {
        try
        {
            await _projects.DeleteAsync(input.Id);
            _files.RemoveProjectFiles(input.Id);
            return new JsonResult(new { ok = true, redirect = Url.Page("/Projects/Index") });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    // 변경 실행 → 최신 과제 JSON. 규칙 위반은 400, 다른 사람이 먼저 고쳤으면 409 와 최신 과제를 돌려준다
    private async Task<IActionResult> Run(int? projectId, Func<Task<int>> action)
    {
        try
        {
            var id = await action();
            // today: 화면을 오래 열어 두면 날짜가 바뀐다. 응답마다 한국 날짜를 실어 화면의 '오늘'을 맞춘다
            return new JsonResult(new { ok = true, project = await _projects.ViewAsync(id), today = _clock.Today });
        }
        catch (ConflictException ex)
        {
            return ConflictJson(ex.ProjectId, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 읽고 저장하는 사이에 다른 저장이 끼어든 경우
            return ConflictJson(projectId, new ConflictException(projectId ?? 0).Message);
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    // 화면이 최신 과제를 다시 읽도록 알린다 (실패한 변경이 남은 컨텍스트로는 과제를 다시 만들지 않는다)
    private static JsonResult ConflictJson(int? projectId, string message) =>
        new(new { ok = false, conflict = true, error = message, projectId }) { StatusCode = StatusCodes.Status409Conflict };

    private static JsonResult Fail(RuleException ex) =>
        new(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 60 ? name[..60] : name;
    }
}
