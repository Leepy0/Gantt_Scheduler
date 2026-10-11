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

    // 종류별·단계별 이름 길이 상한 (입력칸 maxlength) — 서버 검사·DB 열 길이와 같은 값(MasterService.NameLimit → Limits)
    public string LimitsJson => AppJson.Serialize(MasterService.Kinds.ToDictionary(k => k,
        k => Enumerable.Range(1, k switch { "location" => MasterService.LocationDepth, "category" => 3, "equipment" or "org" => 2, _ => 1 })
            .Select(level => MasterService.NameLimit(k, level)).ToArray()));

    public Task<IActionResult> OnGetDataAsync(string kind) => Run(kind, () => Task.CompletedTask);

    public Task<IActionResult> OnPostSaveAsync([FromBody] MasterSave input) => Run(input.Kind, () => _masters.SaveAsync(input));

    public Task<IActionResult> OnPostToggleAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.ToggleAsync(input));

    public Task<IActionResult> OnPostMoveAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.MoveAsync(input));

    public Task<IActionResult> OnPostAddIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.AddAddressAsync(input));

    // 공휴일 여러 줄 붙여넣기 (한 줄에 '날짜 이름')
    public record HolidayPaste(string? Text);
    public Task<IActionResult> OnPostPasteHolidaysAsync([FromBody] HolidayPaste input) => Run("holiday", () => _masters.PasteHolidaysAsync(input.Text));

    public Task<IActionResult> OnPostRemoveIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.RemoveAddressAsync(input));

    public record MailInput(string? Email);

    // 관리 > 사용자의 '시험 메일 보내기': 메일 서버 설정(Notify:Smtp)이 맞는지 그 주소로 한 통 보내 본다(관리자만)
    public async Task<IActionResult> OnPostTestMailAsync([FromBody] MailInput input)
    {
        try
        {
            if (!_me.IsAdmin) throw new RuleException("관리자만 시험 메일을 보낼 수 있습니다.");
            var digest = HttpContext.RequestServices.GetRequiredService<DigestService>();
            await digest.SendTestAsync(input.Email, _me.Name, HttpContext.RequestAborted);
            return new JsonResult(new { ok = true });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // 변경 실행 → 해당 종류의 최신 목록
    private async Task<IActionResult> Run(string kind, Func<Task> action)
    {
        try
        {
            await action();
            // 법인·내년 공휴일을 등록하면 상단 칩('첫 설정 필요'·'내년 공휴일 없음')이 몇 분 기다리지 않고 바로 사라지게 한다
            if (kind is "holiday" or "location") HttpContext.RequestServices.GetService<OpsAlerts>()?.Invalidate();
            // notice: 변경에 따른 안내(공휴일이 걸린 진행 과제·선행 위반 등) — 화면이 알림으로 보인다
            return new JsonResult(new { ok = true, nodes = await _masters.ListAsync(kind), notice = _masters.Notice });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
