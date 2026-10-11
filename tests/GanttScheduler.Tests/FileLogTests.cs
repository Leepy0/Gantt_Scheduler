using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 파일 로그(일자별·보존 기간·최근 경고 링 버퍼·요청 번호), 오류 번호(JSON 500), 느린 요청 경고
public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gantt-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }

    [Fact]
    public void 한국_날짜의_파일에_쓰고_요청_번호를_함께_남긴다()
    {
        // 2026-10-10 16:30 UTC = 한국 10-11 01:30 — 파일 이름은 한국 날짜
        using var log = new FileLog(_dir, 30, () => new DateTime(2026, 10, 10, 16, 30, 0, DateTimeKind.Utc));
        using (var factory = LoggerFactory.Create(b => b.AddProvider(log)))
        {
            var logger = factory.CreateLogger("GanttScheduler.Services.시험");
            logger.LogInformation("시작");
            // 호스팅이 요청마다 여는 범위와 같은 모양 (RequestId = HttpContext.TraceIdentifier)
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "0HABC:00000002", ["RequestPath"] = "/Projects" }))
                logger.LogError(new InvalidOperationException("터졌다"), "처리하지 못한 오류");
        }
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        var path = Path.Combine(_dir, "2026-10-11.log");
        Assert.Equal(path, log.PathOf(new DateOnly(2026, 10, 11)));
        var text = File.ReadAllText(path);
        Assert.Contains("2026-10-11 01:30:00.000 [INFO] GanttScheduler.Services.시험: 시작", text);
        Assert.Contains("[FAIL] GanttScheduler.Services.시험: 처리하지 못한 오류 (요청 0HABC:00000002 /Projects)", text);
        Assert.Contains("System.InvalidOperationException: 터졌다", text);

        // 경고 이상만 최근 목록에 (요청 번호 포함)
        var recent = Assert.Single(log.Recent());
        Assert.Equal("error", recent.Level);
        Assert.Equal("0HABC:00000002", recent.RequestId);
        Assert.Contains("InvalidOperationException: 터졌다", recent.Message);
    }

    [Fact]
    public void 최근_경고는_200건만_새것부터_둔다()
    {
        using var log = new FileLog(_dir, 30);
        using (var factory = LoggerFactory.Create(b => b.AddProvider(log)))
        {
            var logger = factory.CreateLogger("시험");
            for (var i = 1; i <= 250; i++) logger.LogWarning("경고 {N}", i);
            logger.LogInformation("정보는 목록에 넣지 않는다");
        }
        var recent = log.Recent();
        Assert.Equal(FileLog.RecentMax, recent.Count);
        Assert.Equal("경고 250", recent[0].Message);
        Assert.Equal("경고 51", recent[^1].Message);
    }

    [Fact]
    public void 보존_기간이_지난_로그_파일은_지운다()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "2026-09-10.log", "2026-09-11.log", "2026-10-10.log", "notes.txt", "backup-2026-01-01.log" })
            File.WriteAllText(Path.Combine(_dir, name), "x");
        using var log = new FileLog(_dir, 30);

        Assert.Equal(1, log.Prune(new DateOnly(2026, 10, 11)));   // 30일 전(09-11)까지는 남긴다

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "2026-09-11.log", "2026-10-10.log", "backup-2026-01-01.log", "notes.txt" }, left);
    }

    [Fact]
    public void 설정의_로그_폴더와_보존_일수를_쓴다()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logs:Path"] = "logs-here", ["Logs:KeepDays"] = "7",
        }).Build();
        using var log = FileLog.From(cfg, _dir);
        Assert.Equal(Path.Combine(_dir, "logs-here"), log.Dir);
        Assert.Equal(7, log.KeepDays);
    }

    private static DefaultHttpContext Context(string accept)
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "0HTEST:00000007" };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/Projects/Detail";
        ctx.Request.Headers.Accept = accept;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task 화면이_JSON_으로_물은_요청의_예외는_오류_번호가_든_JSON_500_으로_돌려준다()
    {
        var ctx = Context("application/json");
        var mw = new JsonErrorMiddleware(_ => throw new InvalidOperationException("예상 못한 오류"), NullLogger<JsonErrorMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("0HTEST:00000007", doc.RootElement.GetProperty("errorId").GetString());
        Assert.Contains("오류 번호 0HTEST:00000007", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("예상 못한 오류", doc.RootElement.GetProperty("error").GetString());   // 내부 메시지는 로그에만
    }

    [Fact]
    public async Task 크기_한도를_넘은_요청은_서버_오류가_아니라_그_상태_코드로_알린다()
    {
        var ctx = Context("application/json");
        var mw = new JsonErrorMiddleware(_ => throw new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge), NullLogger<JsonErrorMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Contains("크기 한도", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 화면_요청의_예외는_오류_화면이_처리하도록_넘긴다()
    {
        var mw = new JsonErrorMiddleware(_ => throw new InvalidOperationException("x"), NullLogger<JsonErrorMiddleware>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(Context("text/html")));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task 정한_시간을_넘긴_요청은_경로와_시간을_경고로_남긴다()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logs:SlowRequestMs"] = "50" }).Build();
        var logger = new ListLogger<RequestTimingMiddleware>();
        var slow = new RequestTimingMiddleware(async _ => await Task.Delay(120), logger, cfg);
        var fast = new RequestTimingMiddleware(_ => Task.CompletedTask, logger, cfg);

        await fast.InvokeAsync(Context("text/html"));
        Assert.Empty(logger.Lines);
        var ctx = Context("text/html");
        ctx.Request.QueryString = new QueryString("?id=3");
        await slow.InvokeAsync(ctx);
        var (level, text) = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Matches(@"^느린 요청 \d+ms: POST /Projects/Detail\?id=3 → 200$", text);

        // 일부러 오래 걸리는 관리 작업(백업 등)은 경고로 세지 않는다
        var backup = Context("application/json");
        backup.Request.Path = "/Admin/Update";
        backup.Request.QueryString = new QueryString("?handler=Backup");
        await slow.InvokeAsync(backup);
        Assert.Equal(LogLevel.Information, logger.Lines[^1].Level);
    }
}
