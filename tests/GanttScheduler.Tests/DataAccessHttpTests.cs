using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GanttScheduler.Tests;

// 요청 하나가 DB 에 보낸 명령 수를 센다(시험 프로젝트에만 둔다 — 운영 경로에는 넣지 않는다).
// 요청 머리글 X-Count-Commands 의 값별로 모은다: 같은 서버의 다른 요청·배경 작업이 섞이지 않게
public sealed class CommandCounter : DbCommandInterceptor
{
    public const string Header = "X-Count-Commands";
    // HttpContextAccessor 의 값은 정적 AsyncLocal 이라 새로 만든 인스턴스로도 지금 요청을 본다(앱에 AddHttpContextAccessor 가 있어야 채워진다)
    private static readonly HttpContextAccessor Http = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _byTag = new();

    public IReadOnlyList<string> Of(string tag) => _byTag.TryGetValue(tag, out var q) ? q.ToList() : new List<string>();

    private void Hit(DbCommand command)
    {
        var tag = Http.HttpContext?.Request.Headers[Header].ToString();
        if (!string.IsNullOrEmpty(tag)) _byTag.GetOrAdd(tag, _ => new()).Enqueue(command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Hit(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Hit(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Hit(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Hit(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Hit(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Hit(command); return ValueTask.FromResult(result); }
}

// 명령 수를 세는 앱. 앱 팩토리는 환경 변수로 DB 를 넘기므로 만들자마자 띄운다
public sealed class CountingApp : IDisposable
{
    public AppFactory Inner { get; } = new();
    public CommandCounter Counter { get; } = new();
    public WebApplicationFactory<Program> App { get; }
    public TestDb Db => Inner.Db;

    public CountingApp()
    {
        App = Inner.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.ConfigureDbContext<AppDb>(o => o.AddInterceptors(Counter));
        }));
        _ = App.Server;
    }

    public void Dispose()
    {
        App.Dispose();
        Inner.Dispose();
    }
}

// 응답 크기·DB 명령 수·압축·JSON 인코딩·나눠 받기를 HTTP 로 확인한다. 정확한 값이 아니라 상한으로 단언한다 —
// EF 버전이 바뀌면 분할 쿼리 수가 조금 달라질 수 있다. 실패하면 실제 명령 목록을 보여 준다
[Collection(AppCollection.Name)]
public class DataAccessHttpTests : IClassFixture<CountingApp>
{
    private readonly CountingApp _app;
    public DataAccessHttpTests(CountingApp app) => _app = app;

    private static string TokenOf(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private static string PageData(string html) =>
        Regex.Match(html, "<script id=\"page-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

    private async Task<HttpClient> ClientAsync()
    {
        var client = _app.App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var welcome = await client.GetAsync("/Welcome");
        if (welcome.StatusCode == HttpStatusCode.OK)
        {
            var res = await client.PostAsync("/Welcome", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = "성능시험", ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = TokenOf(await welcome.Content.ReadAsStringAsync()),
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        return client;
    }

    // tag 를 붙여 보내고 그 요청이 보낸 DB 명령 목록을 돌려준다
    private async Task<(HttpResponseMessage Res, IReadOnlyList<string> Commands)> CountAsync(HttpClient client, HttpRequestMessage req, string tag)
    {
        req.Headers.Add(CommandCounter.Header, tag);
        var res = await client.SendAsync(req);
        await res.Content.LoadIntoBufferAsync();
        return (res, _app.Counter.Of(tag));
    }

    // SQLite("Holidays")·SQL Server([pm].[Holidays]) 모두에서 그 표를 읽는 명령인지
    private static bool FromTable(string sql, string table) => Regex.IsMatch(sql, "FROM [^\\s]*\\b" + table + "\\b");
    private static bool Mentions(string sql, string table) => Regex.IsMatch(sql, "\\b" + table + "\\b");

    private static void AssertAtMost(int max, IReadOnlyList<string> commands, string what) =>
        Assert.True(commands.Count <= max, $"{what}: DB 명령 {commands.Count}개 (상한 {max})\n" + string.Join("\n---\n", commands));

    [Fact]
    public async Task 상세_GET_변경_대시보드의_DB_명령_수에_상한이_있다()
    {
        var big = await BigProject.CreateAsync(_app.Db, "HTTP 대형 과제 명령 수");
        var client = await ClientAsync();
        await client.GetAsync("/");      // 처음 한 번 읽는 것(관리자 칩 등)을 빼고 잰다

        var (detail, detailCommands) = await CountAsync(client, new HttpRequestMessage(HttpMethod.Get, $"/Projects/Detail?id={big.Id}"), "detail");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        // 사용자 확인 1 + 과제 그래프(분할) 8 + 위치·공휴일 2 + 활동 건수 2·최근 활동 1 + 사용자 이름 1 ≈ 15
        AssertAtMost(18, detailCommands, "상세 화면");
        // 공휴일은 뷰 계산(달력)과 화면 데이터(목록)가 함께 쓴다 — 한 번만 읽는다
        Assert.True(detailCommands.Count(c => FromTable(c, "Holidays")) == 1, string.Join("\n---\n", detailCommands));

        // 대표 변경 1건: 체크 — 변경용 그래프(단계·작업·체크·의존)만 읽고, 응답은 추적 없이 새로 읽는다
        var html = await detail.Content.ReadAsStringAsync();
        int checkId;
        using (var s = _app.Db.NewScope())
            checkId = await s.Db.CheckItems.Where(c => c.Stage!.ProjectId == big.Id).OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
        var toggle = new HttpRequestMessage(HttpMethod.Post, "/Projects/Detail?handler=ToggleCheck") { Content = JsonContent.Create(new { id = checkId, done = true }) };
        toggle.Headers.Add("X-CSRF-TOKEN", TokenOf(html));
        toggle.Headers.Add("Accept", "application/json");
        var (changed, changeCommands) = await CountAsync(client, toggle, "toggle");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        // 사용자 1 + 체크→과제 1 + 변경 그래프(분할) 5 + 저장 + 응답(상세와 같은 조회) 13 ≈ 23. 예전에는 활동·첨부까지 두 번 읽었다
        AssertAtMost(26, changeCommands, "체크 변경");
        // 변경하면서 활동·첨부를 통째로 읽지 않는다 (활동 표를 읽는 것은 응답의 건수·최근 활동 조회뿐)
        // 활동 표를 건드리는 명령: 응답의 건수 2·최근 활동 1, 저장(INSERT) 1 — 그래프에 활동을 싣으면 분할 쿼리가 늘어 넘는다
        Assert.True(changeCommands.Count(c => Mentions(c, "Activities")) <= 4, string.Join("\n---\n", changeCommands));
        var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(BigProject.Activities + 1, body.GetProperty("project").GetProperty("activityTotal").GetInt32());

        var (dash, dashCommands) = await CountAsync(client, new HttpRequestMessage(HttpMethod.Get, "/"), "dashboard");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        // 사용자 1 + 과제 그래프(분할) 3 + 위치·공휴일 2 = 6
        AssertAtMost(10, dashCommands, "대시보드");
    }

    [Fact]
    public async Task 대형_과제_상세는_최근_활동만_싣고_응답을_압축한다()
    {
        var big = await BigProject.CreateAsync(_app.Db, "HTTP 대형 과제 크기");
        var client = await ClientAsync();

        var html = await client.GetStringAsync($"/Projects/Detail?id={big.Id}");
        var data = PageData(html);
        var project = JsonDocument.Parse(data).RootElement.GetProperty("project");
        Assert.Equal(50, project.GetProperty("activities").GetArrayLength());
        Assert.Equal(BigProject.Activities, project.GetProperty("activityTotal").GetInt32());
        Assert.Equal(BigProject.Works, project.GetProperty("workCount").GetInt32());
        // 작업 200개의 일정은 다 싣고 활동은 최근 50건만 — 활동 3,000건을 다 실으면 1MB 가까이 된다
        Assert.True(data.Length < 250_000, $"상세 데이터 {data.Length}B");

        // 같은 JSON 을 압축해서 보낸다 (Accept-Encoding)
        var plainReq = new HttpRequestMessage(HttpMethod.Get, $"/Projects/Detail?handler=Data&id={big.Id}");
        plainReq.Headers.Add("Accept", "application/json");
        var plain = await client.SendAsync(plainReq);
        var plainBytes = (await plain.Content.ReadAsByteArrayAsync()).Length;
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        foreach (var encoding in new[] { "br", "gzip" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/Projects/Detail?handler=Data&id={big.Id}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("Accept-Encoding", encoding);
            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(encoding, Assert.Single(res.Content.Headers.ContentEncoding));
            var bytes = (await res.Content.ReadAsByteArrayAsync()).Length;
            Assert.True(bytes * 4 < plainBytes, $"{encoding}: {bytes}B / 압축 전 {plainBytes}B");
        }
        // 화면(HTML)과 스크립트도 압축한다
        var page = new HttpRequestMessage(HttpMethod.Get, "/js/detail.js");
        page.Headers.Add("Accept-Encoding", "gzip");
        Assert.Equal("gzip", Assert.Single((await client.SendAsync(page)).Content.Headers.ContentEncoding));

        // 모든 응답에 서버 처리 시간 (브라우저 개발자 도구의 Timing 에 보인다)
        var timed = await client.GetAsync("/");
        Assert.StartsWith("total;dur=", timed.Headers.GetValues("Server-Timing").Single());
    }

    [Fact]
    public async Task 페이지에_심는_JSON_은_한글을_그대로_두고_HTML_특수문자는_계속_이스케이프한다()
    {
        const string title = "한글 </script><script>alert(1)</script> & \"인용\" '작은'";
        int id;
        using (var s = _app.Db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1);
            id = await s.Projects.CreateAsync(new Services.ProjectInput(title, cat.Id, corp.Id, null, _app.Db.Clock.Today, null, "설명 <b>굵게</b>", null, null));
        }
        var client = await ClientAsync();
        var html = await client.GetStringAsync($"/Projects/Detail?id={id}");
        var data = PageData(html);
        Assert.DoesNotContain("<", data);                     // </script> 로 끊기지 않는다
        Assert.DoesNotContain(">", data);
        Assert.DoesNotContain("&", data);
        Assert.Contains("한글", data);                        // 한글 이 아니라 그대로
        Assert.DoesNotContain("\\uD55C", data, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(title, JsonDocument.Parse(data).RootElement.GetProperty("project").GetProperty("title").GetString());

        // 핸들러가 돌려주는 JSON(MVC)도 같은 인코더
        var req = new HttpRequestMessage(HttpMethod.Get, $"/Projects/Detail?handler=Data&id={id}");
        req.Headers.Add("Accept", "application/json");
        var raw = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("한글", raw);
        Assert.DoesNotContain("</script>", raw);
        Assert.Equal(title, JsonDocument.Parse(raw).RootElement.GetProperty("project").GetProperty("title").GetString());
    }

    [Fact]
    public async Task 활동은_핸들러로_나눠_받고_변경_이력은_과제로_거른다()
    {
        var big = await BigProject.CreateAsync(_app.Db, "HTTP 대형 과제 활동");
        var client = await ClientAsync();

        var first = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Activities&id={big.Id}");
        Assert.True(first.GetProperty("more").GetBoolean());
        var items = first.GetProperty("activities");
        Assert.Equal(50, items.GetArrayLength());
        var last = items[49].GetProperty("id").GetInt32();
        var next = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Activities&id={big.Id}&beforeId={last}&take=20");
        Assert.Equal(20, next.GetProperty("activities").GetArrayLength());
        Assert.DoesNotContain(next.GetProperty("activities").EnumerateArray(), a => items.EnumerateArray().Any(b => b.GetProperty("id").GetInt32() == a.GetProperty("id").GetInt32()));
        var work = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Activities&id={big.Id}&workId={big.FirstWorkId}");
        Assert.All(work.GetProperty("activities").EnumerateArray(), a => Assert.Equal(big.FirstWorkId, a.GetProperty("workId").GetInt32()));

        var missing = new HttpRequestMessage(HttpMethod.Get, "/Projects/Detail?handler=Activities&id=987654");
        missing.Headers.Add("Accept", "application/json");
        var gone = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.True((await gone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("missing").GetBoolean());

        // 상세 활동 탭의 '이 과제 전체 이력' → 과제 조건이 걸린 변경 이력. 쪽 이동 링크도 과제 조건을 지킨다
        var detail = await client.GetStringAsync($"/Projects/Detail?id={big.Id}");
        Assert.Contains($"/Insights/History?projectId={big.Id}", detail);
        var history = WebUtility.HtmlDecode(await client.GetStringAsync($"/Insights/History?projectId={big.Id}"));
        Assert.Contains("HTTP 대형 과제 활동", history);
        Assert.Contains("모든 과제", history);
        Assert.Contains($"name=\"projectId\" value=\"{big.Id}\"", history);
        Assert.Matches($"href=\"/Insights/History\\?[^\"]*projectId={big.Id}", history);
        Assert.Contains($"1 / {(int)Math.Ceiling(BigProject.Activities / 50.0)}", history);

        // 여러 과제의 이력은 1년까지 — 넘게 고르면 안내
        var wide = WebUtility.HtmlDecode(await client.GetStringAsync("/Insights/History?from=2020-01-01&to=2026-10-01"));
        Assert.Contains("history-notice", wide);
        Assert.Contains("value=\"2025-10-01\"", wide);
    }
}
