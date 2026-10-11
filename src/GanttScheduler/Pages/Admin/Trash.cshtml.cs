using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages.Admin;

// 관리 > 휴지통 — 지운 과제의 목록(지운 사람·시각·사유), 복원, 바로 영구 삭제, 최근 영구 삭제 기록.
// 지운 과제의 내용과 영구 삭제는 되돌릴 수 없는 일이라 관리자만 연다
public class TrashModel : PageModel
{
    private readonly TrashService _trash;
    private readonly CurrentUser _me;

    public TrashModel(TrashService trash, CurrentUser me)
    {
        _trash = trash;
        _me = me;
    }

    // Version: 화면이 본 과제 Version — 그 사이 복원·수정 뒤 다시 지워졌으면 409 로 새로고침을 안내한다
    public record IdInput(int Id, int? Version);

    public List<TrashRow> Rows { get; private set; } = new();
    public List<PurgeLogView> Log { get; private set; } = new();
    public int KeepDays => _trash.Options.KeepDays;
    public bool AutoPurge => _trash.Options.AutoPurge;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_me.IsAdmin) return StatusCode(StatusCodes.Status403Forbidden);
        Rows = await _trash.ListAsync();
        Log = await _trash.LogAsync();
        return Page();
    }

    public Task<IActionResult> OnPostRestoreAsync([FromBody] IdInput input) => Run(() => _trash.RestoreAsync(input.Id, input.Version));

    public Task<IActionResult> OnPostPurgeAsync([FromBody] IdInput input) => Run(() => _trash.PurgeAsync(input.Id, version: input.Version));

    private async Task<IActionResult> Run(Func<Task> action)
    {
        if (!_me.IsAdmin)
            return new JsonResult(new { ok = false, error = "관리자만 휴지통을 다룰 수 있습니다." }) { StatusCode = StatusCodes.Status403Forbidden };
        try
        {
            await action();
            return new JsonResult(new { ok = true });
        }
        catch (ConflictException)
        {
            return new JsonResult(new { ok = false, conflict = true, error = "그 사이 이 과제가 복원되었거나 바뀌었습니다. 목록을 새로고침해 주세요." }) { StatusCode = StatusCodes.Status409Conflict };
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        catch (DbUpdateConcurrencyException)
        {
            return new JsonResult(new { ok = false, conflict = true, error = "다른 사용자가 먼저 바꿨습니다. 화면을 새로고침해 주세요." }) { StatusCode = StatusCodes.Status409Conflict };
        }
    }
}
