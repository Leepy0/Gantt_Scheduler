using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

public enum WelcomeMode { Register, Switch, Confirm }

// 이 기기(IP)의 사용자 정하기: 처음 접속한 IP 의 이름 등록, 사용자 바꾸기(?switch=1), 오래 쓰지 않은 IP 의 재확인
public class WelcomeModel : PageModel
{
    private readonly UserService _users;
    private readonly MasterService _masters;
    private readonly CurrentUser _me;
    private readonly AccessOptions _access;

    public WelcomeModel(UserService users, MasterService masters, CurrentUser me, AccessOptions access)
    {
        _users = users;
        _masters = masters;
        _me = me;
        _access = access;
    }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public int? OrgUnitId { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    // 이미 아는 기기에서 다른 이름으로 다시 연결하겠다는 표시. 없으면 아는 기기의 등록 요청은 받지 않는다
    [BindProperty(SupportsGet = true, Name = "switch")] public string? SwitchFlag { get; set; }
    public string? Error { get; private set; }
    public string Ip => _me.Ip;
    public string CurrentName => _me.Name;
    public int ReconfirmDays => _access.ReconfirmDays;
    public string OrgJson { get; private set; } = "{}";
    public WelcomeMode Mode { get; private set; }

    private bool IsSwitch => SwitchFlag is "1" or "true";

    public async Task<IActionResult> OnGetAsync()
    {
        if (_me.NeedsConfirm && !IsSwitch) Mode = WelcomeMode.Confirm;
        else if (_me.IsKnown && !IsSwitch) return LocalRedirect(SafeReturn());
        else Mode = _me.IsKnown ? WelcomeMode.Switch : WelcomeMode.Register;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // 아는 기기의 등록은 '사용자 바꾸기'로 들어온 경우만 받는다 (열어 둔 옛 등록 화면이 연결을 바꾸지 않게)
        if (_me.IsKnown && !IsSwitch) return LocalRedirect(SafeReturn());
        Mode = _me.IsKnown ? WelcomeMode.Switch : WelcomeMode.Register;
        try
        {
            await _users.RegisterAsync(_me.Ip, Name, OrgUnitId, _me.IsLocal);
            return LocalRedirect(SafeReturn());
        }
        catch (RuleException ex)
        {
            Error = ex.Message;
            await LoadAsync();
            return Page();
        }
    }

    // 'OOO 님이 맞습니까?' → 맞습니다
    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (_me.NeedsConfirm) await _users.ConfirmAsync(_me.Ip);
        return LocalRedirect(SafeReturn());
    }

    private async Task LoadAsync() =>
        OrgJson = AppJson.Serialize(new { orgs = (await _masters.ListAsync("org")).Where(o => o.Active), selected = OrgUnitId });

    private string SafeReturn() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Content("~/");
}
