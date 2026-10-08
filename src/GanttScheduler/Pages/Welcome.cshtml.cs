using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

// 처음 접속한 IP 의 이름 등록
public class WelcomeModel : PageModel
{
    private readonly UserService _users;
    private readonly MasterService _masters;
    private readonly CurrentUser _me;

    public WelcomeModel(UserService users, MasterService masters, CurrentUser me)
    {
        _users = users;
        _masters = masters;
        _me = me;
    }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public int? OrgUnitId { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }
    public string Ip => _me.Ip;
    public string OrgJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (_me.IsKnown) return LocalRedirect(SafeReturn());
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await _users.RegisterAsync(_me.Ip, Name, OrgUnitId);
            return LocalRedirect(SafeReturn());
        }
        catch (RuleException ex)
        {
            Error = ex.Message;
            await LoadAsync();
            return Page();
        }
    }

    private async Task LoadAsync() =>
        OrgJson = AppJson.Serialize(new { orgs = (await _masters.ListAsync("org")).Where(o => o.Active), selected = OrgUnitId });

    private string SafeReturn() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Content("~/");
}
