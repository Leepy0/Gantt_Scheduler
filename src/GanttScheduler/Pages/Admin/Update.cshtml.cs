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

    public Task<IActionResult> OnPostDismissFailureAsync() => Run(() => _updates.DismissFailure());

    public Task<IActionResult> OnPostSettingsAsync([FromBody] SettingsInput input) =>
        Run(() => _updates.SaveSettings(new UpdateSettings(input.AutoCheck, input.AutoApply, input.WindowFrom, input.WindowTo, input.IdleMinutes)));

    // 배포 파일(zip)과 함께 배포 정보(GanttScheduler.json)·서명(GanttScheduler.json.sig)을 올릴 수 있다.
    // 한 입력란에서 여러 파일을 골라도, 입력란 이름이 달라도 되게 파일 이름(확장자)으로 가른다
    public Task<IActionResult> OnPostUploadAsync() => Run(async () =>
    {
        var files = Request.HasFormContentType ? Request.Form.Files : null;
        if (files == null || files.Count == 0 || files.All(f => f.Length == 0)) throw new RuleException("배포 파일(zip)을 선택해 주세요.", "file");
        IFormFile? zip = null, manifest = null, signature = null;
        foreach (var f in files.Where(f => f.Length > 0))
        {
            var name = f.FileName;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip ??= f;
            else if (name.EndsWith(UpdateKey.SignatureSuffix, StringComparison.OrdinalIgnoreCase)) signature ??= f;
            else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) manifest ??= f;
            else throw new UpdateException($"올릴 수 없는 파일입니다: {name}", "릴리스의 zip, GanttScheduler.json, GanttScheduler.json.sig 만 올려 주세요.");
        }
        if (zip == null) throw new UpdateException("zip 파일이 없습니다.", "릴리스의 GanttScheduler-win-x64.zip 을 그대로 올려 주세요.");
        await using var stream = zip.OpenReadStream();
        await _updates.UploadAsync(stream, _me.Name, HttpContext.RequestAborted, await BytesAsync(manifest), await BytesAsync(signature));
    });

    // 배포 정보·서명은 몇 KB 라 메모리로 읽는다 (너무 크면 엉뚱한 파일이다)
    private static async Task<byte[]?> BytesAsync(IFormFile? file)
    {
        if (file == null) return null;
        if (file.Length > 1024 * 1024) throw new UpdateException($"{file.FileName} 이 너무 큽니다.", "릴리스의 GanttScheduler.json·GanttScheduler.json.sig 를 그대로 올려 주세요.");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return ms.ToArray();
    }

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
