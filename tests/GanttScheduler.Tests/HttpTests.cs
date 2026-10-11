using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GanttScheduler.Tests;

// 앱을 통째로 띄워 HTTP 로 확인한다: 사용자 등록 관문, 각 화면 응답, JSON 핸들러(CSRF 포함)
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly TestDb _db = new();
    // 첨부 파일이 저장소(프로젝트 폴더)에 남지 않게 임시 폴더를 쓴다
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "gantt-uploads-" + Guid.NewGuid().ToString("N"));
    // 앱이 만들어진 뒤에 읽는 설정(TrustForwardedHeaders, Access:…)을 테스트마다 바꿔 끼운다
    private readonly Dictionary<string, string?> _settings;

    public TestDb Db => _db;

    public AppFactory() : this(null) { }

    internal AppFactory(Dictionary<string, string?>? settings)
    {
        _settings = settings ?? new();
        Environment.SetEnvironmentVariable("Storage__Path", _uploads);
        // 업데이트 상태·암호화 키도 임시 폴더에 두고, 테스트 중에는 외부(GitHub)에 묻지 않는다
        Environment.SetEnvironmentVariable("Storage__KeyPath", Path.Combine(_uploads, "keys"));
        Environment.SetEnvironmentVariable("Update__Path", Path.Combine(_uploads, "updates"));
        Environment.SetEnvironmentVariable("Update__Source", "");
        // 파일 로그·백업도 임시 폴더에 (프로젝트 폴더의 App_Data 에 남지 않게)
        Environment.SetEnvironmentVariable("Logs__Path", Path.Combine(_uploads, "logs"));
        Environment.SetEnvironmentVariable("Backup__FilesPath", Path.Combine(_uploads, "backups"));
        // 일별 진척 스냅샷은 배경에서 DB 에 쓴다 — 시험의 DB 명령 수·데이터를 흔들지 않게 끈다(스냅샷 시험은 서비스로 직접 한다)
        Environment.SetEnvironmentVariable("Snapshot__Enabled", "false");
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

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(_settings));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _db.Dispose();
        try { if (Directory.Exists(_uploads)) Directory.Delete(_uploads, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }
}

// 앱 팩토리는 환경 변수로 DB 를 넘기므로 같은 묶음(차례로 실행)에서만 만든다
[Collection(AppCollection.Name)]
public partial class HttpTests : IClassFixture<AppFactory>
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
                ["Name"] = "통합테스터", ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = token,
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
    [InlineData("/Admin/Update")]
    [InlineData("/My")]
    [InlineData("/Insights/LeadTime")]
    [InlineData("/Insights/Weekly")]
    [InlineData("/Insights/Weekly?day=2026-09-28&mine=true")]
    [InlineData("/Insights/History")]
    [InlineData("/Insights/History?kind=schedule&q=AGV&p=1")]
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
    [InlineData("org")]
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
    public async Task 서버_버전은_등록하지_않아도_물어볼_수_있다()
    {
        // 열려 있는 화면이 주기적으로 묻는 주소라 이름 등록 관문을 거치지 않는다
        using var fresh = new AppFactory();
        var client = fresh.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/version");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString());
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches(@"^\d+\.\d+\.\d+", json.GetProperty("build").GetString()!);
        Assert.StartsWith(json.GetProperty("version").GetString()!, json.GetProperty("build").GetString()!);
        Assert.False(json.GetProperty("updating").GetBoolean());
    }

    [Fact]
    public async Task 업데이트_상태는_누구나_보지만_바꾸는_것은_관리자만_한다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Admin/Update");
        var token = TokenOf(html);
        // 화면은 지금 빌드를 알고 있어야 새 버전으로 바뀐 것을 알아챈다. /version 이 주는 값과 글자 그대로 같아야 한다
        var version = await client.GetFromJsonAsync<JsonElement>("/version");
        var build = Regex.Match(html, "<html[^>]*data-build=\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal(version.GetProperty("build").GetString(), WebUtility.HtmlDecode(build));

        var status = await client.GetFromJsonAsync<JsonElement>("/Admin/Update?handler=Status");
        var st = status.GetProperty("status");
        Assert.Equal("local", st.GetProperty("channel").GetString());
        // 테스트 실행은 로컬 빌드라 스스로 교체하지 않는다
        Assert.False(st.GetProperty("canApply").GetBoolean());
        Assert.Equal("idle", st.GetProperty("phase").GetString());

        // 통합테스터는 관리자가 아니다 (테스트 DB 에 관리자가 이미 있다)
        foreach (var handler in new[] { "Check", "Download", "Apply", "Rollback", "Discard", "Settings", "DismissFailure", "Upload", "Backup" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/Admin/Update?handler=" + handler)
            {
                Content = JsonContent.Create(new { autoCheck = true, autoApply = false, windowFrom = 0, windowTo = 0, idleMinutes = 5 }),
            };
            req.Headers.Add("X-CSRF-TOKEN", token);
            req.Headers.Add("Accept", "application/json");
            var res = await client.SendAsync(req);
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{handler} → {(int)res.StatusCode}");
        }
        var after = await client.GetFromJsonAsync<JsonElement>("/Admin/Update?handler=Status");
        Assert.True(after.GetProperty("status").GetProperty("settings").GetProperty("autoApply").GetBoolean());

        // 토큰 없이는 거절
        var noToken = await client.PostAsJsonAsync("/Admin/Update?handler=Check", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
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
        foreach (var url in new[]
                 {
                     "/Projects?handler=Excel&state=all", "/Projects/Detail?handler=Excel&id=1",
                     "/Projects/Detail?handler=Excel&id=2", "/Insights/Weekly?handler=Excel",
                 })
        {
            var res = await client.GetAsync(url);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} → {(int)res.StatusCode}");
            var bytes = await res.Content.ReadAsByteArrayAsync();
            // xlsx 는 zip 이므로 'PK' 로 시작한다
            Assert.True(bytes.Length > 1000 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', url + " 이 xlsx 가 아님");
        }
    }

    [Fact]
    public async Task PDF_내보내기()
    {
        var client = await RegisteredClientAsync();
        // (주소, 가로 양식 여부)
        foreach (var (url, landscape) in new[]
                 {
                     ("/Projects/Detail?handler=Pdf&id=1", true), ("/Projects/Detail?handler=Pdf&id=2", true),
                     ("/Insights/Weekly?handler=Pdf", false),
                 })
        {
            var res = await client.GetAsync(url);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK,
                $"{url} → {(int)res.StatusCode}\n{System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1000))}");
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            Assert.True(bytes.Length > 1000 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF", url + " 이 PDF 가 아님");

            // 쪽 방향: 과제 일정은 A4 가로(표가 넓다), 주간 보고는 세로
            var box = Regex.Match(System.Text.Encoding.Latin1.GetString(bytes), @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)");
            Assert.True(box.Success, url + " 에서 쪽 크기를 찾지 못함");
            var width = double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var height = double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(landscape ? width > height : height > width, $"{url} 쪽 크기 {width}x{height}");
        }
    }

    private static async Task<(HttpClient Client, string Token)> DetailAsync(HttpClient client, int id)
    {
        var html = await client.GetStringAsync("/Projects/Detail?id=" + id);
        return (client, TokenOf(html));
    }

    private static HttpRequestMessage Json(string token, string handler, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/Projects/Detail?handler=" + handler) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    private static HttpRequestMessage Upload(string token, int projectId, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(projectId.ToString()), "projectId" },
            { new ByteArrayContent(bytes), "file", fileName },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/Projects/Files?handler=Upload") { Content = form };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 첨부_파일을_올리고_내려받고_지운다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 3);
        var content = System.Text.Encoding.UTF8.GetBytes("검수 성적서 내용");

        var up = await client.SendAsync(Upload(token, 3, "검수 성적서.txt", content));
        var upBody = await up.Content.ReadAsStringAsync();
        Assert.True(up.StatusCode == HttpStatusCode.OK, upBody);
        var file = JsonDocument.Parse(upBody).RootElement.GetProperty("project").GetProperty("attachments")
            .EnumerateArray().Single(a => a.GetProperty("fileName").GetString() == "검수 성적서.txt");
        var id = file.GetProperty("id").GetInt32();
        Assert.Equal(content.Length, file.GetProperty("size").GetInt64());
        Assert.Equal("통합테스터", file.GetProperty("uploadedBy").GetString());

        var down = await client.GetAsync("/Projects/Files?handler=Download&id=" + id);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal(content, await down.Content.ReadAsByteArrayAsync());
        // 원래 파일 이름으로 내려받는다
        Assert.Contains("attachment", down.Content.Headers.ContentDisposition?.DispositionType);

        // 실행 파일 같은 허용하지 않는 형식은 거절
        var bad = await client.SendAsync(Upload(token, 3, "setup.exe", new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var del = await client.SendAsync(Json(token, "DeleteFile", new { id }));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var left = (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("attachments");
        Assert.DoesNotContain(left.EnumerateArray(), a => a.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/Files?handler=Download&id=" + id)).StatusCode);
    }

    [Fact]
    public async Task 다른_사람이_먼저_고친_단계를_저장하면_409_로_알린다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 4);
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=4");
        var stage = data.GetProperty("project").GetProperty("stages").EnumerateArray()
            .First(s => s.GetProperty("completedDate").ValueKind == JsonValueKind.Null && s.GetProperty("targetDate").ValueKind != JsonValueKind.Null);
        var version = stage.GetProperty("version").GetInt32();

        object Body(int v, string name) => new
        {
            projectId = 4, id = stage.GetProperty("id").GetInt32(), name,
            targetDate = stage.GetProperty("targetDate").GetString(), completedDate = (string?)null,
            reason = (string?)null, cascade = false, version = v,
        };

        // 화면을 연 뒤 다른 사람이 먼저 저장한 상황: 같은 버전으로 두 번 보낸다
        var first = await client.SendAsync(Json(token, "SaveStage", Body(version, stage.GetProperty("name").GetString() + " (수정)")));
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var second = await client.SendAsync(Json(token, "SaveStage", Body(version, "늦게 도착한 수정")));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("conflict").GetBoolean());
        Assert.Equal(4, json.GetProperty("projectId").GetInt32());

        // 늦은 수정은 반영되지 않았다
        var after = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=4");
        Assert.DoesNotContain(after.GetProperty("project").GetProperty("stages").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "늦게 도착한 수정");
    }
}
