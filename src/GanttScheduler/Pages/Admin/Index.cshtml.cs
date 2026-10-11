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
    private readonly UserService _users;

    public IndexModel(MasterService masters, CurrentUser me, UserService users)
    {
        _masters = masters;
        _me = me;
        _users = users;
    }

    public bool IsAdmin => _me.IsAdmin;
    public string Ip => _me.Ip;
    // 관리자가 아무도 없다 — 서버에서 접속하거나 admin 명령을 써야 한다고 안내한다
    public bool NoAdmin { get; private set; }

    // 서버 PC 에서 접속할 주소. 첫 관리자는 루프백에서 온 등록만 되므로 서버 이름·IP 가 아니라 localhost 로 안내한다(포트는 지금 접속한 그대로)
    public string LocalUrl
    {
        get
        {
            var port = Request.Host.Port;
            var defaultPort = Request.IsHttps ? 443 : 80;
            return $"{Request.Scheme}://localhost{(port == null || port == defaultPort ? "" : ":" + port)}/";
        }
    }

    public async Task OnGetAsync() => NoAdmin = !await _users.HasAdminAsync();

    public Task<IActionResult> OnGetDataAsync(string kind) => Run(kind, () => Task.CompletedTask);

    public Task<IActionResult> OnPostSaveAsync([FromBody] MasterSave input) => Run(input.Kind, () => _masters.SaveAsync(input));

    public Task<IActionResult> OnPostToggleAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.ToggleAsync(input));

    public Task<IActionResult> OnPostMoveAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.MoveAsync(input));

    public Task<IActionResult> OnPostAddIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.AddAddressAsync(input));

    public Task<IActionResult> OnPostRemoveIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.RemoveAddressAsync(input));

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
