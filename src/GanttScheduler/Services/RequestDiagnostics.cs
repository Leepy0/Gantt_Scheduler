using System.Diagnostics;

namespace GanttScheduler.Services;

// 오류 번호: 사용자가 본 오류와 서버 로그를 잇는 값. HttpContext.TraceIdentifier 를 그대로 쓴다 —
// 호스팅이 요청마다 여는 로그 범위의 RequestId 와 같아서 파일 로그(FileLog)의 '(요청 …)' 에 같은 번호가 남는다
public static class ErrorNumber
{
    public const string Header = "X-Error-Id";

    public static string Message(string id) =>
        $"서버에서 오류가 발생했습니다. 잠시 후 다시 시도하고, 계속되면 관리자에게 오류 번호를 알려 주세요. (오류 번호 {id})";

    // 화면이 JSON 으로 묻는 요청 (시트·폴링). 이런 요청에는 오류 화면(HTML) 대신 JSON 을 돌려준다
    public static bool WantsJson(HttpRequest request) => request.Headers.Accept.ToString().Contains("application/json");
}

// Server-Timing 머리글 (W3C): 'total;dur=12.3'
public static class ServerTiming
{
    public const string Header = "Server-Timing";

    public static string Total(TimeSpan elapsed) =>
        "total;dur=" + elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

// 요청 시간 재기와 오류 번호 머리글 — 파이프라인 맨 앞에 둔다.
// 1초(Logs:SlowRequestMs)를 넘는 요청은 경고로 남긴다: 성능 개선 순서를 가상 데이터가 아니라 실제 운영 기록으로 정하기 위해서다.
// 5xx 응답에는 오류 번호 머리글을 붙여, 본문이 JSON 이 아니어도(개발 환경의 오류 화면 등) 화면이 번호를 보일 수 있게 한다.
// 모든 응답에 Server-Timing(total) 머리글을 붙인다
public sealed class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _log;
    private readonly int _slowMs;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> log, IConfiguration cfg)
    {
        _next = next;
        _log = log;
        _slowMs = Math.Max(0, cfg.GetValue("Logs:SlowRequestMs", 1000));
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var started = Stopwatch.GetTimestamp();
        ctx.Response.OnStarting(() =>
        {
            if (ctx.Response.StatusCode >= 500) ctx.Response.Headers[ErrorNumber.Header] = ctx.TraceIdentifier;
            // 서버에서 걸린 시간(응답을 보내기 시작할 때까지). 브라우저 개발자 도구의 Timing 에 그대로 보여, 사용자 PC 에서
            // '느리다'는 화면이 서버 탓인지 네트워크 탓인지 바로 가를 수 있다
            ctx.Response.Headers[ServerTiming.Header] = ServerTiming.Total(Stopwatch.GetElapsedTime(started));
            return Task.CompletedTask;
        });
        try
        {
            await _next(ctx);
        }
        finally
        {
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (_slowMs > 0 && ms >= _slowMs)
            {
                // 일부러 오래 걸리는 관리 작업(배포 파일 올리기·새 버전 확인·백업)은 느린 요청으로 세지 않는다
                var level = LongByDesign(ctx.Request) ? LogLevel.Information : LogLevel.Warning;
                _log.Log(level, "느린 요청 {Elapsed}ms: {Method} {Path}{Query} → {Status}",
                    ms, ctx.Request.Method, ctx.Request.Path.Value, ctx.Request.QueryString.Value, ctx.Response.StatusCode);
            }
        }
    }

    private static bool LongByDesign(HttpRequest request) =>
        request.Path.StartsWithSegments("/Admin")
        && request.Query["handler"].ToString() is "Upload" or "Check" or "Backup" or "Diagnostics";
}

// 화면이 JSON 으로 묻는 요청의 예기치 않은 예외: 오류 화면(HTML) 대신 {ok:false, error, errorId} 500 을 돌려준다.
// app.js 는 error 를 그대로 보여 주므로 사용자가 오류 번호를 볼 수 있다. 예외 처리기(UseExceptionHandler) 뒤에 둔다 —
// 앞에 두면 예외 처리기가 먼저 오류 화면으로 바꿔 버린다
public sealed class JsonErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JsonErrorMiddleware> _log;

    public JsonErrorMiddleware(RequestDelegate next, ILogger<JsonErrorMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex) when (ErrorNumber.WantsJson(ctx.Request) && !ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
        {
            var id = ctx.TraceIdentifier;
            // 요청 자체가 잘못된 경우(본문 크기 한도 413 등)는 서버 오류가 아니라 그 상태 코드로 알린다 — 화면이 413 을 따로 안내한다
            var status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            if (status >= 500) _log.LogError(ex, "처리하지 못한 오류 (오류 번호 {ErrorId}): {Method} {Path}", id, ctx.Request.Method, ctx.Request.Path.Value);
            else _log.LogWarning("잘못된 요청 {Status} (오류 번호 {ErrorId}): {Method} {Path} — {Message}", status, id, ctx.Request.Method, ctx.Request.Path.Value, ex.Message);
            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.Headers.CacheControl = "no-store";
            var error = status >= 500 ? ErrorNumber.Message(id)
                : status == StatusCodes.Status413PayloadTooLarge ? $"보낸 내용이 서버의 요청 크기 한도를 넘었습니다. (오류 번호 {id})"
                : $"요청을 처리하지 못했습니다({status}). 화면을 새로 고친 뒤 다시 시도해 주세요. (오류 번호 {id})";
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error, errorId = id });
        }
    }
}
