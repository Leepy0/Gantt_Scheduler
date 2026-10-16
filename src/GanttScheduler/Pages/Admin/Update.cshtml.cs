using System.Data.Common;
using System.Text.Json;
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
    private readonly BackupService? _backup;
    private readonly FileLog? _log;
    private readonly IHostApplicationLifetime? _lifetime;
    // 관리 변경 이력 — 설정 변경·적용·되돌리기·올리기·지금 백업을 남긴다(업데이트 기록은 state.json 에도 있지만 서버를 옮기면 사라진다)
    private readonly MasterLog? _changes;
    private readonly JsonDatabaseService? _jsonDatabase;

    // 백업·로그는 선택 인자다 (업데이트 시험이 페이지를 직접 만들 때 없어도 되게)
    public UpdateModel(UpdateService updates, CurrentUser me, BackupService? backup = null, FileLog? log = null, IHostApplicationLifetime? lifetime = null,
        MasterLog? changes = null, JsonDatabaseService? jsonDatabase = null)
    {
        _updates = updates;
        _me = me;
        _backup = backup;
        _log = log;
        _lifetime = lifetime;
        _changes = changes;
        _jsonDatabase = jsonDatabase;
    }

    public bool IsAdmin => _me.IsAdmin;
    public string DataJson { get; private set; } = "{}";

    public record SettingsInput(bool AutoCheck, bool AutoApply, int WindowFrom, int WindowTo, int IdleMinutes);

    public void OnGet() => DataJson = AppJson.Serialize(new { status = _updates.Status(), admin = IsAdmin, backup = BackupStatus() });

    // 백업 경로·목록은 서버 경로가 보이므로 관리자에게만 준다
    private BackupStatus? BackupStatus() => IsAdmin ? _backup?.Status() : null;

    public IActionResult OnGetStatus() => StatusJson();

    public Task<IActionResult> OnPostCheckAsync() => Run(() => _updates.CheckAsync(HttpContext.RequestAborted));

    public Task<IActionResult> OnPostDownloadAsync() => Run(() => _updates.StartDownload(_me.Name));

    public Task<IActionResult> OnPostDiscardAsync() => Run(() => _updates.Discard());

    public Task<IActionResult> OnPostApplyAsync() => Run(async () =>
    {
        var target = _updates.Status().Staged?.Version;
        _updates.Apply(_me.Name);
        await RecordAsync("update", $"업데이트 적용: {_updates.Status().Version} → {target}", _updates.Status().Version, target);
    });

    public Task<IActionResult> OnPostRollbackAsync() => Run(async () =>
    {
        var target = _updates.Status().Previous?.Version;
        _updates.Rollback(_me.Name);
        await RecordAsync("update", $"이전 버전으로 되돌리기: {_updates.Status().Version} → {target}", _updates.Status().Version, target);
    });

    public Task<IActionResult> OnPostDismissFailureAsync() => Run(() => _updates.DismissFailure());

    // 지금 백업 — 끝날 때까지 기다린다(보통 몇 초~몇 분). 실패하면 이유를 돌려주고 목록에도 실패 기록이 남는다.
    // 요청 취소 토큰(RequestAborted)에 묶지 않는다: 기다리다 다른 화면으로 가거나 탭을 닫아도 백업은 서버에서 끝까지 돈다
    // (다시 열면 화면이 '백업 중'을 보고 끝날 때까지 상태를 묻는다). 서버가 내려갈 때만 멈추고, 멈춘 것도 목록에 남는다
    public Task<IActionResult> OnPostBackupAsync() => Run(async () =>
    {
        if (_backup == null) throw new UpdateException("백업을 쓸 수 없는 서버입니다.", "서버를 다시 시작해 주세요.");
        var entry = await _backup.RunAsync(BackupKinds.Manual, _me.Name, _lifetime?.ApplicationStopping ?? CancellationToken.None);
        if (!entry.Ok)
            throw new UpdateException("백업하지 못했습니다: " + entry.Error,
                "백업 경로(Backup:Path, Backup:FilesPath)와 쓰기 권한·디스크 여유를 확인하세요. 아래 목록에 실패 기록이 남습니다.");
        await RecordAsync("backup", "지금 백업 실행");
    });

    // SQLite 테이블별 JSON 백업 내려받기. JSON 파일은 DB 전체 복구·이관에 쓰며 관리자만 접근한다.
    public async Task<IActionResult> OnGetJsonExportAsync()
    {
        if (!_me.IsAdmin) return Denied();
        try
        {
            if (_jsonDatabase == null) throw new RuleException("JSON 백업 기능을 사용할 수 없습니다.");
            var bytes = await _jsonDatabase.ExportAsync(HttpContext.RequestAborted);
            return File(bytes, "application/zip", $"gantt-json-{KoreaClock.ToKst(DateTime.UtcNow):yyyyMMdd-HHmmss}.zip");
        }
        catch (RuleException ex)
        {
            // 링크(<a>)로 받는 응답이라 JSON 이 아니라 읽을 수 있는 글로 돌려준다
            return new ContentResult { Content = ex.Message, ContentType = "text/plain; charset=utf-8", StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // 테이블별 JSON 백업 복원은 현재 SQLite 스키마와 정확히 맞을 때만 허용한다.
    public Task<IActionResult> OnPostJsonRestoreAsync() => Run(async () =>
    {
        if (_jsonDatabase == null) throw new RuleException("JSON 백업 기능을 사용할 수 없습니다.");
        var files = Request.HasFormContentType ? Request.Form.Files : null;
        var file = files?.FirstOrDefault(f => f.Length > 0 && f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (file == null) throw new RuleException("테이블별 JSON 백업 ZIP 파일을 선택해 주세요.");
        if (file.Length > 300L * 1024 * 1024) throw new RuleException("백업 파일은 300MB 이하여야 합니다.");
        await using var stream = file.OpenReadStream();
        try
        {
            await _jsonDatabase.RestoreAsync(stream, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException or DbException or IOException)
        {
            throw new RuleException("JSON 백업을 복원하지 못했습니다. 파일 형식·DB 구조·관계가 맞는지 확인하세요.");
        }
        await RecordAsync("backup", $"테이블별 JSON 백업 복원: {Path.GetFileName(file.FileName)}");
    });

    // 진단 정보: 교체 기록(apply.log) 끝부분, 시작 오류(startup-error.log), 상태 요약, 최근 업데이트·백업 경고
    public IActionResult OnGetDiagnostics()
    {
        if (!_me.IsAdmin) return Denied();
        return new JsonResult(new { ok = true, diagnostics = _updates.Diagnostics(_log?.Recent()) });
    }

    // 같은 내용을 텍스트 파일 하나로 (관리자가 메일·메신저로 전달하기 쉽게). 연결 문자열의 계정·비밀번호는 가린다
    public IActionResult OnGetDiagnosticsFile()
    {
        if (!_me.IsAdmin) return Denied();
        var text = _updates.DiagnosticsText(_log?.Recent());
        return File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8",
            $"업데이트진단_{KoreaClock.ToKst(DateTime.UtcNow):yyyyMMdd-HHmm}.txt");
    }

    private static JsonResult Denied() =>
        new(new { ok = false, error = "관리자만 업데이트를 다룰 수 있습니다." }) { StatusCode = StatusCodes.Status403Forbidden };

    public Task<IActionResult> OnPostSettingsAsync([FromBody] SettingsInput input) => Run(async () =>
    {
        var before = _updates.Status().Settings;
        var after = new UpdateSettings(input.AutoCheck, input.AutoApply, input.WindowFrom, input.WindowTo, input.IdleMinutes);
        _updates.SaveSettings(after);
        if (before != after)
            await RecordAsync("update", "업데이트 설정 변경: " + string.Join(", ", SettingsDiff(before, after).Select(d => d.Label)),
                string.Join("; ", SettingsDiff(before, after).Select(d => $"{d.Label}: {d.Old}")),
                string.Join("; ", SettingsDiff(before, after).Select(d => $"{d.Label}: {d.New}")));
    });

    private static IEnumerable<(string Label, string Old, string New)> SettingsDiff(UpdateSettings a, UpdateSettings b)
    {
        static string OnOff(bool v) => v ? "켬" : "끔";
        if (a.AutoCheck != b.AutoCheck) yield return ("자동 확인", OnOff(a.AutoCheck), OnOff(b.AutoCheck));
        if (a.AutoApply != b.AutoApply) yield return ("자동 적용", OnOff(a.AutoApply), OnOff(b.AutoApply));
        if (a.WindowFrom != b.WindowFrom || a.WindowTo != b.WindowTo)
            yield return ("적용 시간", $"{a.WindowFrom}~{a.WindowTo}시", $"{b.WindowFrom}~{b.WindowTo}시");
        if (a.IdleMinutes != b.IdleMinutes) yield return ("대기 시간", $"{a.IdleMinutes}분", $"{b.IdleMinutes}분");
    }

    private async Task RecordAsync(string kind, string text, string? oldValue = null, string? newValue = null)
    {
        if (_changes == null) return;
        _changes.Add(kind, (int?)null, text, oldValue, newValue);
        await _changes.SaveAsync();
    }

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
        await RecordAsync("update", $"배포 파일 올리기: {zip.FileName}", null, _updates.Status().Staged?.Version);
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
        if (!_me.IsAdmin) return Denied();
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

    private JsonResult StatusJson() => new(new { ok = true, status = _updates.Status(), backup = BackupStatus() });
}
