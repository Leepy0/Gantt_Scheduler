using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.My;

// 알림 목록: 내 작업·내 과제·관심 과제·멘션 알림. 누르면 읽음으로 바꾸고 과제 상세의 그 활동(#a{id})으로 간다
public class NotificationsModel : PageModel
{
    private readonly NotificationService _notes;

    public NotificationsModel(NotificationService notes) => _notes = notes;

    public record IdInput(int Id);

    [BindProperty(SupportsGet = true)] public bool Unread { get; set; }
    [BindProperty(SupportsGet = true)] public int? Before { get; set; }

    public NotificationPage Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _notes.ListAsync(Before, Unread);

    // 항목을 누름: 읽음으로 바꾸고 그 활동으로. 링크만으로 동작해(스크립트 없이도) 읽음 처리가 빠지지 않는다
    public async Task<IActionResult> OnGetGoAsync(int id)
    {
        var n = await _notes.ReadAsync(id);
        if (n == null) return RedirectToPage();
        var url = Url.Page("/Projects/Detail", new { id = n.ProjectId }) + (n.ActivityId is int a ? "#a" + a : "#activity");
        return Redirect(url!);
    }

    public async Task<IActionResult> OnPostReadAsync([FromBody] IdInput input)
    {
        await _notes.ReadAsync(input.Id);
        return new JsonResult(new { ok = true, unread = await _notes.UnreadAsync() });
    }

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        var done = await _notes.ReadAllAsync();
        return new JsonResult(new { ok = true, read = done, unread = 0 });
    }
}
