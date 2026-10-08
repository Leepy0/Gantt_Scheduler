using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Admin;

// 기준 정보 관리 — 탭별 데이터는 JSON 으로 주고받는다
public class IndexModel : PageModel
{
    private readonly MasterService _masters;
    private readonly CurrentUser _me;

    public IndexModel(MasterService masters, CurrentUser me)
    {
        _masters = masters;
        _me = me;
    }

    public bool IsAdmin => _me.IsAdmin;

    public void OnGet() { }

    public Task<IActionResult> OnGetDataAsync(string kind) => Run(kind, () => Task.CompletedTask);

    public Task<IActionResult> OnPostSaveAsync([FromBody] MasterSave input) => Run(input.Kind, () => _masters.SaveAsync(input));

    public Task<IActionResult> OnPostToggleAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.ToggleAsync(input));

    public Task<IActionResult> OnPostMoveAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.MoveAsync(input));

    // 변경 실행 → 해당 종류의 최신 목록
    private async Task<IActionResult> Run(string kind, Func<Task> action)
    {
        try
        {
            await action();
            return new JsonResult(new { ok = true, nodes = await _masters.ListAsync(kind) });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
