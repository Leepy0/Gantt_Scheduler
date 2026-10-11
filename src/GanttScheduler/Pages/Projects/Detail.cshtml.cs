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

    // Version: 화면이 본 버전 (단계·작업 삭제는 그 항목, 체크 항목은 그 단계의 버전). 없으면 확인하지 않는다(예전 화면 호환)
    public record IdInput(int Id, int? Version = null);
    // OtherId·OtherVersion: 화면이 본 맞바꿀 이웃 단계와 그 버전
    public record MoveInput(int Id, int Dir, int? Version = null, int? OtherId = null, int? OtherVersion = null);
    public record StateInput(int ProjectId, string? State, string? Reason, int? Version);
    public record ToggleInput(int Id, bool Done, int? Version = null);
    // 기준선 확정·다시 확정 (다시 확정은 사유 필수, 담당자·관리자만)
    public record BaselineInput(int Id, string? Reason = null, int? Version = null);
    public record DeleteInput(int Id, string? Reason = null, int? Version = null);
    public record PreviewInput(int ProjectId, List<StagePlanRow>? Rows);
    public record WatchInput(int ProjectId, bool On);

    public ProjectView Project { get; private set; } = null!;
    public int UploadMaxMB => _files.MaxMB;
    public string DataJson { get; private set; } = "{}";
    public bool Watched { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        // 상세를 열면 마지막으로 본 시각이 바뀐다 — 그 전 시각(since) 뒤의 남의 활동을 '새 활동'으로 표시한다
        var opened = await _projects.OpenAsync(id);
        if (opened is not var (view, since, watched)) return NotFound();
        Project = view;
        Watched = watched;
        // 담당자 자동완성(이름)과 알릴 사람 고르기(Id·이름)는 같은 목록이다 — 한 번만 읽는다
        var people = await _projects.PeopleAsync();
        DataJson = AppJson.Serialize(new
        {
            project = view,
            today = _clock.Today,
            holidays = await _projects.HolidaysAsync(),
            users = people.Select(u => u.Name),
            people,
            me = new { id = _me.Id, name = _me.Name, isAdmin = _me.IsAdmin },
            seen = new { since, count = view.Activities?.Count(a => a.New) ?? 0 },
            kinds = Reporting.Kinds.Select(k => new { key = k.Key, label = k.Label }),
            // 입력칸 maxlength — 서버 검사·DB 열 길이와 같은 값(Limits)을 화면에 숫자로 따로 적지 않는다
            limits = new
            {
                stageName = Limits.StageName, checkText = Limits.CheckText, workTitle = Limits.WorkTitle, workNote = Limits.WorkNote,
                userName = Limits.UserName, reason = Limits.Reason, text = Limits.ActivityText, projectTitle = Limits.ProjectTitle,
            },
            upload = new { maxBytes = _files.MaxBytes, maxMB = _files.MaxMB, extensions = AttachmentService.Allowed.Keys.OrderBy(x => x) },
        });
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync(int id)
    {
        var view = await _projects.ViewAsync(id, compare: Compare);
        return view == null ? Missing() : new JsonResult(new { ok = true, project = view, today = _clock.Today });
    }

    // 활동 나눠 받기: 활동 탭의 '더 보기'(beforeId = 화면의 마지막 활동)와 작업 시트의 진행 내용(workId).
    // 응답 { ok, activities, more } — more 면 마지막 항목의 id 로 다음 쪽을 묻는다
    // 활동 탭의 조건(kind·stageId·q)이 있으면 그 조건으로 나눠 받는다. since: 화면을 연 때의 '새 활동' 기준(표시용)
    public async Task<IActionResult> OnGetActivitiesAsync(int id, int? beforeId, int? workId, int take = ProjectService.ActivityPageSize,
                                                          string? kind = null, int? stageId = null, string? q = null, DateTime? since = null)
    {
        var filter = kind == null && stageId == null && string.IsNullOrWhiteSpace(q) ? null : new ActivityFilter(kind, stageId, q);
        var page = await _projects.ActivitiesAsync(id, beforeId, workId, take, filter, since?.ToUniversalTime());
        return page == null ? Missing() : new JsonResult(new { ok = true, activities = page.Items, more = page.More });
    }

    // 과제가 마지막으로 바뀐 시각 하나만 돌려준다 — 열린 화면이 1분마다 물어 '다른 사용자가 바꿨습니다' 띠를 띄운다
    public async Task<IActionResult> OnGetStampAsync(int id)
    {
        var stamp = await _projects.StampAsync(id);
        return stamp == null ? Missing() : new JsonResult(new { ok = true, updatedAt = stamp });
    }

    // 화면이 고른 기준선 비교 차수(?compare=n). 상세 화면은 모든 요청 주소에 붙여 보내므로, 저장 뒤 돌려주는 과제도 같은 기준으로 센다
    private int? Compare => int.TryParse(Request.Query["compare"], out var n) ? n : null;

    // 화면이 JSON 으로 묻는 과제가 없을 때 (삭제됨) — 본문 없는 404 대신 이유를 알린다
    private static JsonResult Missing() =>
        new(new { ok = false, missing = true, error = "과제를 찾을 수 없습니다. 다른 사용자가 삭제했을 수 있습니다." }) { StatusCode = StatusCodes.Status404NotFound };

    public async Task<IActionResult> OnGetExcelAsync(int id)
    {
        // Excel 의 활동 이력 시트는 전체 활동을 싣는다 (화면은 최근 일부만)
        var view = await _projects.ViewAsync(id, activityLimit: null);
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

    // ── 일정 변경 미리보기·재계획·과제 간 연결 ──

    // 저장 전 미리보기(저장·활동 없음). kind:
    //  work   — 작업 목표 일정 from~to (id 없으면 stageId 단계의 새 작업, stageId 가 있으면 그 단계로 옮김, links 는 'id:유형:간격' 을 쉼표로 — '-' 는 연결 없음, 없으면 지금 그대로).
    //           작업 시트는 실적도 보낸다: progress 가 오면 actualStart·actualEnd(없으면 비운 것)·clientToday 를 저장과 같이 본다
    //  stage  — 단계(id) 목표일 date (cascade: 이후 미완료 단계 연쇄 이동, works: 이후 단계의 미착수 작업 포함)
    //  replan — 과제(id)를 오늘 기준 예측대로 재계획하면 옮겨질 작업
    //  day    — 날짜(date) 하나의 휴일 안내 (새 단계 목표일)
    public async Task<IActionResult> OnGetPreviewAsync(string? kind, int? id, DateOnly? from, DateOnly? to, int? stageId, string? links,
                                                       DateOnly? date, bool cascade, bool works,
                                                       DateOnly? actualStart = null, DateOnly? actualEnd = null, int? progress = null, DateOnly? clientToday = null)
    {
        try
        {
            var actuals = progress is int pr ? new PreviewActuals(actualStart, actualEnd, pr, clientToday) : null;
            var preview = kind switch
            {
                "work" when from != null && to != null => await _projects.PreviewWorkPlanAsync(id, from.Value, to.Value, stageId, ParseLinks(links), actuals),
                "work" => throw new RuleException("목표 시작일과 종료일을 입력해 주세요.", "planStart"),
                "stage" when id != null => await _projects.PreviewStageTargetAsync(id.Value, date, cascade, works),
                "replan" when id != null => await _projects.ReplanPreviewAsync(id.Value),
                "day" => await _projects.DayPreviewAsync(date, "목표일"),
                _ => throw new RuleException("알 수 없는 미리보기입니다."),
            };
            return new JsonResult(new { ok = true, preview });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    // 'id:유형:간격,…' → 선행 연결 목록 ('-' 는 빈 목록, 비어 있으면 null = 지금 그대로)
    private static List<LinkInput>? ParseLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<LinkInput>();
        if (text.Trim() == "-") return list;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (!int.TryParse(bits[0], out var pred)) throw new RuleException("선행 작업을 다시 선택해 주세요.", "predecessors");
            var lag = bits.Length > 2 && int.TryParse(bits[2], out var l) ? l : 0;
            list.Add(new LinkInput(pred, lag, bits.Length > 1 ? bits[1] : null));
        }
        return list;
    }

    // 오늘 기준 재계획: 예측과 다른 미착수 작업의 목표 일정을 예측대로 옮긴다
    public Task<IActionResult> OnPostReplanAsync([FromBody] ReplanInput input) =>
        Run(input.ProjectId, async () => { await _projects.ReplanAsync(input); return input.ProjectId; });

    // 과제 간 단계 연결: 상대 과제 검색, 연결, 해제
    public async Task<IActionResult> OnGetLinkTargetsAsync(int id, string? q) =>
        new JsonResult(new { ok = true, projects = await _projects.LinkTargetsAsync(id, q) });

    public Task<IActionResult> OnPostAddStageLinkAsync([FromBody] StageLinkInput input) =>
        Run(null, () => _projects.AddStageLinkAsync(input));

    public Task<IActionResult> OnPostRemoveStageLinkAsync([FromBody] StageLinkRemove input) =>
        Run(input.ProjectId, () => _projects.RemoveStageLinkAsync(input));

    public Task<IActionResult> OnPostMoveStageAsync([FromBody] MoveInput input) =>
        Run(null, () => _projects.MoveStageAsync(input.Id, input.Dir, input.Version, input.OtherId, input.OtherVersion));

    public Task<IActionResult> OnPostDeleteStageAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteStageAsync(input.Id, input.Version));

    public Task<IActionResult> OnPostSaveWorkAsync([FromBody] WorkInput input) =>
        Run(null, () => _projects.SaveWorkAsync(input));

    // 작업 여러 개 추가: 붙여넣은 글을 해석·검증한 표(저장하지 않는다) → 확인한 줄을 한 번에 저장
    public async Task<IActionResult> OnPostPreviewWorksAsync([FromBody] PreviewWorksInput input)
    {
        try
        {
            return new JsonResult(new { ok = true, preview = await _projects.PreviewWorksAsync(input) });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    public Task<IActionResult> OnPostAddWorksAsync([FromBody] AddWorksInput input) =>
        Run(null, () => _projects.AddWorksAsync(input));

    public Task<IActionResult> OnPostDeleteWorkAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteWorkAsync(input.Id, input.Version));

    public Task<IActionResult> OnPostCommentAsync([FromBody] CommentInput input) =>
        Run(input.ProjectId, async () => { await _projects.CommentAsync(input); return input.ProjectId; });

    // 댓글 고치기·지우기 (작성자만, Comments:EditHours 안에 — 서비스가 검사한다)
    public Task<IActionResult> OnPostEditCommentAsync([FromBody] CommentEditInput input) =>
        Run(null, () => _projects.EditCommentAsync(input));

    public Task<IActionResult> OnPostDeleteCommentAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteCommentAsync(input.Id));

    // 관심 과제 별 (상세·목록·내 할 일 공용). 개인 설정이라 과제를 다시 보내지 않는다
    public async Task<IActionResult> OnPostWatchAsync([FromBody] WatchInput input)
    {
        try
        {
            return new JsonResult(new { ok = true, watched = await _projects.WatchAsync(input.ProjectId, input.On) });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    public Task<IActionResult> OnPostStateAsync([FromBody] StateInput input) =>
        Run(input.ProjectId, async () =>
        {
            if (!Enum.TryParse<ProjectState>(input.State, ignoreCase: true, out var state))
                throw new RuleException("알 수 없는 상태입니다.");
            await _projects.SetStateAsync(input.ProjectId, state, input.Reason, input.Version);
            return input.ProjectId;
        });

    public Task<IActionResult> OnPostBaselineAsync([FromBody] BaselineInput input) =>
        Run(input.Id, async () => { await _projects.SetBaselineAsync(input.Id, input.Reason, input.Version); return input.Id; });

    public Task<IActionResult> OnPostSaveCheckAsync([FromBody] CheckInput input) =>
        Run(null, () => _projects.SaveCheckAsync(input));

    public Task<IActionResult> OnPostToggleCheckAsync([FromBody] ToggleInput input) =>
        Run(null, () => _projects.ToggleCheckAsync(input.Id, input.Done, input.Version));

    public Task<IActionResult> OnPostDeleteCheckAsync([FromBody] IdInput input) =>
        Run(null, () => _projects.DeleteCheckAsync(input.Id, input.Version));

    public Task<IActionResult> OnPostDeleteFileAsync([FromBody] IdInput input) =>
        Run(null, () => _files.DeleteAsync(input.Id));

    // 복제 시트의 위치 연쇄 선택용 목록 (상세를 열 때마다 싣지 않고 복제할 때만 받는다)
    public async Task<IActionResult> OnGetLocationsAsync() =>
        new JsonResult(new
        {
            ok = true,
            locations = (await _projects.LocationsAsync()).All.OrderBy(l => l.Level).ThenBy(l => l.SortOrder).ThenBy(l => l.Id)
                .Select(l => new { id = l.Id, parentId = l.ParentId, level = l.Level, name = l.Name, active = l.IsActive }),
        });

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

    // 과제 삭제 = 휴지통으로 보내기(첨부 파일도 그대로 둔다 — 영구 삭제는 관리 > 휴지통). 사유는 선택
    public async Task<IActionResult> OnPostDeleteAsync([FromBody] DeleteInput input)
    {
        try
        {
            await _projects.DeleteAsync(input.Id, input.Reason, input.Version);
            return new JsonResult(new { ok = true, redirect = Url.Page("/Projects/Index") });
        }
        catch (ConflictException ex)
        {
            return ConflictJson(ex.ProjectId, ex.Message);
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
            return new JsonResult(new { ok = true, project = await _projects.ViewAsync(id, compare: Compare), today = _clock.Today });
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
