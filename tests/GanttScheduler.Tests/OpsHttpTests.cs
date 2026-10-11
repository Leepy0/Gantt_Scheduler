using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GanttScheduler.Tests;

// 운영 가시성을 HTTP 로: 서버 상태 화면(관리자 전용), /health/ready 200·503, 오류 번호(JSON 500·머리글·로그·오류 화면),
// 업데이트 화면의 지금 백업·진단 정보
[Collection(AppCollection.Name)]
public class OpsHttpTests : IClassFixture<AppFactory>
{
    private const string Name = "운영테스터";
    private readonly AppFactory _factory;
    public OpsHttpTests(AppFactory factory) => _factory = factory;

    private static string TokenOf(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    // 이름을 등록한 클라이언트. 시험 DB 에 관리자(테스터)가 이미 있어 처음에는 일반 사용자다
    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var welcome = await client.GetAsync("/Welcome");
        if (welcome.StatusCode == HttpStatusCode.OK)
        {
            var res = await client.PostAsync("/Welcome", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = Name, ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = TokenOf(await welcome.Content.ReadAsStringAsync()),
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        return client;
    }

    private void SetAdmin(bool admin)
    {
        using var _ = _factory.Db.NewScope(Name, admin);
    }

    private static HttpRequestMessage Json(HttpMethod method, string url, string? token = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Accept", "application/json");
        if (token != null) req.Headers.Add("X-CSRF-TOKEN", token);
        if (method == HttpMethod.Post) req.Content = JsonContent.Create(new { });
        return req;
    }

    [Fact]
    public async Task 서버_상태는_관리자만_보고_점검_결과와_최근_경고를_준다()
    {
        var client = await ClientAsync(_factory);
        SetAdmin(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Json(HttpMethod.Get, "/Admin/Status?handler=Data"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Status?handler=Log")).StatusCode);
        Assert.DoesNotContain("서버 상태", await client.GetStringAsync("/Admin"));

        SetAdmin(true);
        try
        {
            var html = await client.GetStringAsync("/Admin/Status");
            Assert.Contains("<h1>서버 상태</h1>", html);
            Assert.Contains("\"checks\":", html);
            Assert.Contains("/Admin/Status", await client.GetStringAsync("/Admin"));   // 관리 화면에서 들어간다

            var data = await (await client.SendAsync(Json(HttpMethod.Get, "/Admin/Status?handler=Data"))).Content.ReadFromJsonAsync<JsonElement>();
            var report = data.GetProperty("report");
            var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("level").GetString());
            Assert.Equal("ok", checks["db"]);
            Assert.Equal("ok", checks["folders"]);
            Assert.True(report.GetProperty("logs").ValueKind == JsonValueKind.Array);
            Assert.DoesNotContain("Password", report.GetProperty("server").GetProperty("db").GetString()!, StringComparison.OrdinalIgnoreCase);

            var log = await client.GetAsync("/Admin/Status?handler=Log");
            Assert.Equal(HttpStatusCode.OK, log.StatusCode);
            Assert.StartsWith("text/plain", log.Content.Headers.ContentType!.ToString());
            Assert.Equal("attachment", log.Content.Headers.ContentDisposition!.DispositionType);
        }
        finally { SetAdmin(false); }
    }

    [Fact]
    public async Task 고아_파일_정리는_관리자만_하고_기록이_있는_파일은_남긴다()
    {
        var client = await ClientAsync(_factory);
        var storage = AppPaths.Uploads(_factory.Services.GetRequiredService<IConfiguration>(), "");
        var dir = Path.Combine(storage, "424242");   // 없는 과제 번호의 폴더 = 기록 없는 파일
        Directory.CreateDirectory(dir);
        var orphan = Path.Combine(dir, "left.pdf");
        File.WriteAllText(orphan, "남은 파일");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        SetAdmin(false);
        var token = TokenOf(await client.GetStringAsync("/Admin/Update"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Json(HttpMethod.Post, "/Admin/Status?handler=CleanOrphans", token))).StatusCode);
        Assert.True(File.Exists(orphan));

        SetAdmin(true);
        try
        {
            token = TokenOf(await client.GetStringAsync("/Admin/Status"));
            var res = await client.SendAsync(Json(HttpMethod.Post, "/Admin/Status?handler=CleanOrphans", token));
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(res.IsSuccessStatusCode, body.ToString());
            Assert.True(body.GetProperty("removed").GetInt32() >= 1);
            Assert.False(File.Exists(orphan));
            Assert.False(Directory.Exists(dir));   // 빈 과제 폴더도 정리
            Assert.Equal(0, body.GetProperty("report").GetProperty("attachments").GetProperty("orphanCount").GetInt32());
        }
        finally { SetAdmin(false); }
    }

    [Fact]
    public async Task 관리자_상단_칩은_서버_문제를_알리고_일반_사용자에게는_보이지_않는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gantt-alert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 방금 실패한 야간 백업 기록이 있는 서버
            File.WriteAllText(Path.Combine(dir, BackupService.ListFile), JsonSerializer.Serialize(new[]
            {
                new BackupEntry("gantt-x", DateTime.UtcNow.AddMinutes(-5), BackupKinds.Nightly, "자동", false, "쓰기 권한 없음", "Sqlite",
                    null, true, null, null, null, 1, null),
            }));
            using var f = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:FilesPath"] = dir })));
            var client = await ClientAsync(f);
            SetAdmin(false);
            Assert.DoesNotContain("id=\"ops-alert\"", await client.GetStringAsync("/Projects"));

            SetAdmin(true);
            try
            {
                var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects"));
                Assert.Contains("id=\"ops-alert\"", html);
                // 칩에는 가장 중요한 것 하나가, title 에는 모든 알림이 보인다 (디스크가 부족한 시험 서버면 그것이 먼저일 수 있다)
                Assert.Matches("id=\"ops-alert\"[^>]*title=\"[^\"]*백업 실패", html);
            }
            finally { SetAdmin(false); }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* 정리 실패는 무시 */ }
        }
    }

    [Fact]
    public async Task 준비_상태는_사용자_확인_없이_DB_와_디스크를_보고_200을_준다()
    {
        // 등록하지 않은 클라이언트도 (감시 도구)
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/health/ready");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(new[] { "db", "disk" }, body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("ok", await client.GetStringAsync("/health"));   // 예전 주소는 그대로
    }

    [Fact]
    public async Task DB_를_쓸_수_없으면_준비_상태는_503이고_원인_문구는_내보내지_않는다()
    {
        using var broken = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            // 열 수 없는 위치의 DB 로 점검하게 바꾼다 (서버 시작은 진짜 DB 로 한다)
            var dead = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "gantt-none-" + Guid.NewGuid().ToString("N"), "x.db")};Mode=ReadOnly").Options;
            s.AddScoped(sp => ActivatorUtilities.CreateInstance<OpsChecks>(sp, new AppDb(dead)));
        }));
        var client = broken.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/health/ready");
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("fail", body.GetProperty("status").GetString());
        Assert.Equal("fail", body.GetProperty("checks")[0].GetProperty("level").GetString());
        Assert.DoesNotContain("x.db", text);
    }

    // 끝까지 처리되지 않은 요청에서 예외를 던지는 시험용 끝단 (화면 코드에는 일부러 터지는 곳이 없다)
    private sealed class Boom : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(ctx => ctx.Request.Path.StartsWithSegments("/__boom") ? throw new InvalidOperationException("시험용 오류") : Task.CompletedTask);
        };
    }

    [Fact]
    public async Task 예기치_않은_오류는_오류_번호를_보이고_같은_번호가_로그에_남는다()
    {
        using var f = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, Boom>()));
        var client = await ClientAsync(f);

        // 화면이 JSON 으로 묻는 요청: {ok:false, error(오류 번호 포함), errorId} 500 + X-Error-Id 머리글
        var res = await client.SendAsync(Json(HttpMethod.Get, "/__boom"));
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("errorId").GetString()!;
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Contains("오류 번호 " + id, body.GetProperty("error").GetString());
        Assert.Equal(id, res.Headers.GetValues(ErrorNumber.Header).Single());

        // 같은 번호로 로그에 남는다 (최근 경고 목록과 파일)
        var log = f.Services.GetRequiredService<FileLog>();
        Assert.Contains(log.Recent(), e => e.Level == "error" && e.RequestId == id && e.Message.Contains("시험용 오류"));
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Contains(id, File.ReadAllText(log.PathOf(KoreaClock.KstDate(DateTime.UtcNow))));

        // 화면 요청(HTML)의 500 에도 오류 번호 머리글이 붙는다 (개발 환경의 오류 화면이라도)
        var page = await client.GetAsync("/__boom");
        Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
        Assert.False(string.IsNullOrEmpty(page.Headers.GetValues(ErrorNumber.Header).Single()));

        // 운영의 오류 화면은 오류 번호와 시각을 보인다 (예외 처리기가 같은 요청으로 다시 그린다)
        var error = WebUtility.HtmlDecode(await client.GetStringAsync("/Error"));
        Assert.Matches("오류 번호 <code>[^<]+</code>", error);
        Assert.Contains("(한국 시간)", error);
        // 없는 화면(404)에는 오류 번호가 없다
        Assert.DoesNotContain("오류 번호", await client.GetStringAsync("/Error?code=404"));
    }

    [Fact]
    public async Task 업데이트_화면에서_지금_백업하면_목록에_남고_진단_정보는_관리자만_본다()
    {
        var client = await ClientAsync(_factory);
        SetAdmin(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Json(HttpMethod.Get, "/Admin/Update?handler=Diagnostics"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Update?handler=DiagnosticsFile")).StatusCode);
        // 일반 사용자에게는 백업 경로·목록을 주지 않는다
        var status = await (await client.SendAsync(Json(HttpMethod.Get, "/Admin/Update?handler=Status"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, status.GetProperty("backup").ValueKind);

        SetAdmin(true);
        try
        {
            var token = TokenOf(await client.GetStringAsync("/Admin/Update"));
            var res = await client.SendAsync(Json(HttpMethod.Post, "/Admin/Update?handler=Backup", token));
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(res.IsSuccessStatusCode, body.ToString());
            var backup = body.GetProperty("backup");
            var first = backup.GetProperty("list")[0];
            Assert.True(first.GetProperty("ok").GetBoolean());
            Assert.Equal("manual", first.GetProperty("kind").GetString());
            Assert.Equal(Name, first.GetProperty("by").GetString());
            Assert.True(File.Exists(first.GetProperty("filesFile").GetString()));

            var diag = await (await client.SendAsync(Json(HttpMethod.Get, "/Admin/Update?handler=Diagnostics"))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("지금 버전", diag.GetProperty("diagnostics").GetProperty("state").GetString());
            var file = await client.GetAsync("/Admin/Update?handler=DiagnosticsFile");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Contains("== 상태 요약 ==", await file.Content.ReadAsStringAsync());
        }
        finally { SetAdmin(false); }
    }
}
