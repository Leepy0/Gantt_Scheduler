using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Admin;

// 서버 업데이트 — 확인·받기·적용·되돌리기. 보는 것은 누구나, 바꾸는 것은 관리자만.
// 요청 크기 한도는 핸들러 메서드가 아니라 페이지 클래스에 붙여야 적용된다 (Razor Pages 의 필터 규칙)
[RequestSizeLimit(MaxPackageBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxPackageBytes)]
public class UpdateModel : PageModel
{
    // 배포 파일(zip)은 런타임이 들어 있어 수십 MB 다
    public const long MaxPackageBytes = 300L * 1024 * 1024;

    private readonly UpdateService _updates;
    private readonly CurrentUser _me;

    public UpdateModel(UpdateService updates, CurrentUser me)
    {
        _updates = updates;
        _me = me;
    }

    public bool IsAdmin => _me.IsAdmin;
    public string DataJson { get; private set; } = "{}";

    public record SettingsInput(bool AutoCheck, bool AutoApply, int WindowFrom, int WindowTo, int IdleMinutes);

    public void OnGet() => DataJson = AppJson.Serialize(new { status = _updates.Status(), admin = IsAdmin });

    public IActionResult OnGetStatus() => StatusJson();

    public Task<IActionResult> OnPostCheckAsync() => Run(() => _updates.CheckAsync(HttpContext.RequestAborted));

    public Task<IActionResult> OnPostDownloadAsync() => Run(() => _updates.StartDownload(_me.Name));

    public Task<IActionResult> OnPostDiscardAsync() => Run(() => _updates.Discard());

    public Task<IActionResult> OnPostApplyAsync() => Run(() => _updates.Apply(_me.Name));

    public Task<IActionResult> OnPostRollbackAsync() => Run(() => _updates.Rollback(_me.Name));

    public Task<IActionResult> OnPostSettingsAsync([FromBody] SettingsInput input) =>
        Run(() => _updates.SaveSettings(new UpdateSettings(input.AutoCheck, input.AutoApply, input.WindowFrom, input.WindowTo, input.IdleMinutes)));

    public Task<IActionResult> OnPostUploadAsync(IFormFile? file) => Run(async () =>
    {
        if (file == null || file.Length == 0) throw new RuleException("배포 파일(zip)을 선택해 주세요.", "file");
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("zip 파일만 올릴 수 있습니다.", "릴리스의 GanttScheduler-win-x64.zip 을 그대로 올려 주세요.");
        await using var stream = file.OpenReadStream();
        await _updates.UploadAsync(stream, _me.Name, HttpContext.RequestAborted);
    });

    private Task<IActionResult> Run(Action action) => Run(() => { action(); return Task.CompletedTask; });

    // 실행 → 최신 상태. 실패는 원인과 해결 방법을 같이 돌려준다
    private async Task<IActionResult> Run(Func<Task> action)
    {
        if (!_me.IsAdmin)
            return new JsonResult(new { ok = false, error = "관리자만 업데이트를 다룰 수 있습니다." }) { StatusCode = StatusCodes.Status403Forbidden };
        try
        {
            await action();
            return StatusJson();
        }
        catch (UpdateException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, fix = ex.Fix }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    private JsonResult StatusJson() => new(new { ok = true, status = _updates.Status() });
}
