using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

// 처음 접속한 IP 의 이름 등록
public class WelcomeModel : PageModel
{
    private readonly UserService _users;
    private readonly CurrentUser _me;

    public WelcomeModel(UserService users, CurrentUser me)
    {
        _users = users;
        _me = me;
    }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Dept { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }
    public string Ip => _me.Ip;

    public IActionResult OnGet() => _me.IsKnown ? LocalRedirect(SafeReturn()) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await _users.RegisterAsync(_me.Ip, Name, Dept);
            return LocalRedirect(SafeReturn());
        }
        catch (RuleException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    private string SafeReturn() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Content("~/");
}
