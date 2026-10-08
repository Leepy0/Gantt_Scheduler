using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GanttScheduler.Tests;

// 앱을 통째로 띄워 HTTP 로 확인한다: 사용자 등록 관문, 각 화면 응답, JSON 핸들러(CSRF 포함)
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly TestDb _db = new();

    public AppFactory()
    {
        // Program.cs 가 시작하자마자 읽는 설정이라 환경 변수로 넘긴다
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Seed__Demo", "true");
        if (TestDb.UseSqlServer)
        {
            Environment.SetEnvironmentVariable("Database__Provider", "SqlServer");
            Environment.SetEnvironmentVariable("ConnectionStrings__SqlServer", _db.SqlServerConnection);
        }
        else
        {
            Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
            Environment.SetEnvironmentVariable("Database__SqlitePath", _db.SqlitePath);
        }

        // 환경 변수는 프로세스 전체가 공유하므로, 다른 인스턴스가 값을 바꾸기 전에 바로 앱을 띄운다
        _ = Server;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _db.Dispose();
    }
}

public class HttpTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public HttpTests(AppFactory factory) => _factory = factory;

    private static string TokenOf(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    // 이름을 등록한 클라이언트 (테스트 서버의 접속 IP 는 항상 같아서 한 번 등록하면 계속 유지된다)
    private async Task<HttpClient> RegisteredClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var welcome = await client.GetAsync("/Welcome");
        if (welcome.StatusCode == HttpStatusCode.OK)
        {
            var token = TokenOf(await welcome.Content.ReadAsStringAsync());
            var res = await client.PostAsync("/Welcome", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = "통합테스터", ["Dept"] = "물류기술팀", ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = token,
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        return client;
    }

    [Fact]
    public async Task 등록하지_않은_접속은_이름_등록_화면으로_보낸다()
    {
        // 다른 테스트가 먼저 등록했을 수 있으므로 별도 앱으로 확인
        using var fresh = new AppFactory();
        var client = fresh.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await client.GetAsync("/Projects");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/Welcome?returnUrl=", page.Headers.Location!.OriginalString);

        var api = new HttpRequestMessage(HttpMethod.Get, "/Admin?handler=Data&kind=location");
        api.Headers.Add("Accept", "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(api)).StatusCode);

        Assert.Equal("ok", await client.GetStringAsync("/health"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?mine=true")]
    [InlineData("/Projects")]
    [InlineData("/Projects?state=all&q=AGV")]
    [InlineData("/Projects?state=delayed")]
    [InlineData("/Projects/Edit")]
    [InlineData("/Projects/Edit?id=1")]
    [InlineData("/Projects/Detail?id=1")]
    [InlineData("/Projects/Detail?id=2")]
    [InlineData("/Admin")]
    public async Task 화면이_정상_응답한다(string url)
    {
        var client = await RegisteredClientAsync();
        var res = await client.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} → {(int)res.StatusCode}\n{body[..Math.Min(body.Length, 2000)]}");
        Assert.Contains("과제 스케줄러", body);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("category")]
    [InlineData("equipment")]
    [InlineData("container")]
    [InlineData("vendor")]
    [InlineData("user")]
    [InlineData("holiday")]
    public async Task 관리_데이터를_JSON_으로_돌려준다(string kind)
    {
        var client = await RegisteredClientAsync();
        var json = await client.GetFromJsonAsync<JsonElement>($"/Admin?handler=Data&kind={kind}");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("nodes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task 없는_과제는_404()
    {
        var client = await RegisteredClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/Detail?id=999999")).StatusCode);
    }

    [Fact]
    public async Task JSON_핸들러는_CSRF_토큰이_있어야_하고_규칙_위반은_400_으로_알린다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Projects/Detail?id=1");
        var token = TokenOf(html);
        Assert.NotEqual("", token);

        // 토큰 없이는 거절
        var noToken = await client.PostAsJsonAsync("/Projects/Detail?handler=Comment", new { projectId = 1, text = "토큰 없음" });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        HttpRequestMessage Post(string handler, object body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/Projects/Detail?handler=" + handler) { Content = JsonContent.Create(body) };
            req.Headers.Add("X-CSRF-TOKEN", token);
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        var ok = await client.SendAsync(Post("Comment", new { projectId = 1, text = "통합 테스트 댓글" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        var first = json.GetProperty("project").GetProperty("activities")[0];
        Assert.Equal("통합 테스트 댓글", first.GetProperty("text").GetString());
        Assert.Equal("통합테스터", first.GetProperty("user").GetString());
        Assert.Equal("comment", first.GetProperty("type").GetString());
        // 시각은 UTC 로 내려가야 브라우저가 현지 시각으로 바꿀 수 있다
        Assert.EndsWith("Z", first.GetProperty("at").GetString());
        // 날짜는 yyyy-MM-dd 문자열
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", json.GetProperty("project").GetProperty("startDate").GetString()!);

        var bad = await client.SendAsync(Post("Comment", new { projectId = 1, text = "  " }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var err = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(err.GetProperty("ok").GetBoolean());
        Assert.Equal("text", err.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Excel_내보내기()
    {
        var client = await RegisteredClientAsync();
        foreach (var url in new[] { "/Projects?handler=Excel&state=all", "/Projects/Detail?handler=Excel&id=1" })
        {
            var res = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            // xlsx 는 zip 이므로 'PK' 로 시작한다
            Assert.True(bytes.Length > 1000 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', url + " 이 xlsx 가 아님");
        }
    }
}
