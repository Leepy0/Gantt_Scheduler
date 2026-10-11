using System.Text;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Admin;

// 관리 > 서버 상태 — 점검 결과(OpsChecks), 첫 설정 점검표, 첨부 대조·고아 파일 정리, 최근 경고·오류.
// 경로·계정·DB 서버 이름이 보이므로 관리자만 연다(연결 문자열은 서버·DB 이름만 보이고 계정·비밀번호는 보이지 않는다)
public class StatusModel : PageModel
{
    private readonly OpsChecks _ops;
    private readonly CurrentUser _me;
    private readonly FileLog _log;
    private readonly OpsAlerts _alerts;
    private readonly IClock _clock;

    public StatusModel(OpsChecks ops, CurrentUser me, FileLog log, OpsAlerts alerts, IClock clock)
    {
        _ops = ops;
        _me = me;
        _log = log;
        _alerts = alerts;
        _clock = clock;
    }

    public string DataJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_me.IsAdmin) return StatusCode(StatusCodes.Status403Forbidden);
        // 관리 화면에서 고친 것(공휴일 등록 등)이 상단 칩에 바로 반영되게 한다
        _alerts.Invalidate();
        DataJson = AppJson.Serialize(new { report = await _ops.ReportAsync(HttpContext.RequestAborted) });
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync()
    {
        if (!_me.IsAdmin) return Denied();
        _alerts.Invalidate();
        return new JsonResult(new { ok = true, report = await _ops.ReportAsync(HttpContext.RequestAborted) });
    }

    public async Task<IActionResult> OnPostCleanOrphansAsync()
    {
        if (!_me.IsAdmin) return Denied();
        var (files, bytes) = await _ops.CleanOrphansAsync(_me.Name, HttpContext.RequestAborted);
        return new JsonResult(new { ok = true, removed = files, freed = OpsChecks.Size(bytes), report = await _ops.ReportAsync(HttpContext.RequestAborted) });
    }

    // 오늘(한국 날짜)의 로그 파일 — 원격 데스크톱 없이 관리자가 전달할 수 있게. 연결 문자열의 계정·비밀번호는 가린다
    public IActionResult OnGetLog()
    {
        if (!_me.IsAdmin) return Denied();
        _log.Flush(TimeSpan.FromSeconds(2));
        var day = _clock.Today;
        var path = _log.PathOf(day);
        var text = "(오늘 기록된 로그가 없습니다)";
        if (System.IO.File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 아주 큰 파일은 끝부분만 (보통 하루 몇백 KB)
            if (fs.Length > 20 * 1024 * 1024) fs.Seek(-20 * 1024 * 1024, SeekOrigin.End);
            using var r = new StreamReader(fs, Encoding.UTF8);
            text = Redact.Secrets(r.ReadToEnd());
        }
        return File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", $"서버로그_{day:yyyy-MM-dd}.log");
    }

    private static JsonResult Denied() =>
        new(new { ok = false, error = "관리자만 서버 상태를 볼 수 있습니다." }) { StatusCode = StatusCodes.Status403Forbidden };
}
