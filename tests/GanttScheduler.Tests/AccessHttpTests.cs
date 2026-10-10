using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 앱 팩토리는 환경 변수로 DB 를 넘기므로, 팩토리를 만드는 시험 클래스는 모두 이 묶음에 넣어 차례로 돌린다
[CollectionDefinition(Name)]
public class AppCollection
{
    public const string Name = "app";
}

// 사용자 식별·접속 제어를 HTTP 로 확인한다. 다른 PC 의 접속은 TrustForwardedHeaders 를 켜고 X-Forwarded-For 로 흉내 낸다
[Collection(AppCollection.Name)]
public class AccessHttpTests
{
    private static string TokenOf(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private static AppFactory Forwarded(string? allowed = null)
    {
        var settings = new Dictionary<string, string?> { ["TrustForwardedHeaders"] = "true" };
        if (allowed != null) settings["Access:AllowedNetworks"] = allowed;
        return new AppFactory(settings);
    }

    // ip 를 주면 그 주소에서 접속한 것처럼 (프록시가 붙이는 X-Forwarded-For)
    private static HttpClient Client(AppFactory f, string? ip = null)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (ip != null) c.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return c;
    }

    private static async Task<HttpResponseMessage> PostWelcomeAsync(HttpClient c, string name, bool switchUser = false, string handler = "", bool leaveAdmin = false)
    {
        var html = await (await c.GetAsync("/Welcome?switch=1")).Content.ReadAsStringAsync();
        var form = new Dictionary<string, string> { ["Name"] = name, ["ReturnUrl"] = "/Projects", ["__RequestVerificationToken"] = TokenOf(html) };
        if (switchUser) form["switch"] = "1";
        if (leaveAdmin) form["LeaveAdmin"] = "true";
        return await c.PostAsync("/Welcome" + handler, new FormUrlEncodedContent(form));
    }

    private static async Task<string?> UserOfIpAsync(AppFactory f, string ip)
    {
        using var s = f.Db.NewScope();
        return (await s.Db.UserAddresses.Include(a => a.User).FirstOrDefaultAsync(a => a.Ip == ip))?.User?.Name;
    }

    [Fact]
    public async Task 모든_응답에_보안_헤더를_붙인다()
    {
        using var f = Forwarded("10.20.0.0/16");
        var c = Client(f);
        foreach (var url in new[] { "/health", "/version", "/Welcome", "/css/site.css", "/Projects/Detail?id=999999" })
        {
            var res = await c.GetAsync(url);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("SAMEORIGIN", res.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("same-origin", res.Headers.GetValues("Referrer-Policy").Single());
        }
        // 대역 밖이라 거절한 응답에도
        var denied = await Client(f, "172.16.0.1").GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("SAMEORIGIN", denied.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task 프록시를_믿으면_X_Forwarded_For_의_주소로_사용자를_찾는다()
    {
        using var f = Forwarded();
        var c = Client(f, "10.9.0.1");
        var welcome = WebUtility.HtmlDecode(await (await c.GetAsync("/Welcome")).Content.ReadAsStringAsync());
        Assert.Contains("이 기기(10.9.0.1)", welcome);
        Assert.Equal(HttpStatusCode.Redirect, (await PostWelcomeAsync(c, "원격직원")).StatusCode);
        Assert.Equal("원격직원", await UserOfIpAsync(f, "10.9.0.1"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Projects")).StatusCode);

        // 다른 주소는 아직 모르는 기기
        Assert.Equal(HttpStatusCode.Redirect, (await Client(f, "10.9.0.99").GetAsync("/Projects")).StatusCode);
    }

    [Fact]
    public async Task 관리자_이름으로는_새_기기를_연결하지_못한다()
    {
        using var f = Forwarded();
        var c = Client(f, "10.9.0.2");
        // 테스트 DB 의 관리자 '테스터'
        var res = await PostWelcomeAsync(c, "테스터");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(UserService.AdminLinkRefused, WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
        Assert.Null(await UserOfIpAsync(f, "10.9.0.2"));

        // 다른 관리자가 IP 를 추가해 주면 그 기기에서 관리자로 쓴다 (앱은 실제 시각을 쓰므로 연결 시각도 지금으로)
        f.Db.Clock.UtcNow = DateTime.UtcNow;
        using (var s = f.Db.NewScope())
            await s.Masters.AddAddressAsync(new MasterIp(f.Db.Me.Id, "10.9.0.2"));
        var admin = await c.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.DoesNotContain("조회 전용", await admin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 아는_기기의_등록은_사용자_바꾸기로만_받는다()
    {
        using var f = Forwarded();
        var c = Client(f, "10.9.0.3");
        await PostWelcomeAsync(c, "첫사용자");
        Assert.Equal("첫사용자", await UserOfIpAsync(f, "10.9.0.3"));

        // 아는 기기의 등록 화면은 돌려보내고, 표시 없이 보낸 등록은 무시한다
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Welcome")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostWelcomeAsync(c, "슬쩍바꿈")).StatusCode);
        Assert.Equal("첫사용자", await UserOfIpAsync(f, "10.9.0.3"));

        // 헤더의 내 이름 → 사용자 바꾸기
        var header = await (await c.GetAsync("/Projects")).Content.ReadAsStringAsync();
        Assert.Contains("/Welcome?switch=1&amp;returnUrl=%2FProjects", header);
        var page = WebUtility.HtmlDecode(await (await c.GetAsync("/Welcome?switch=1")).Content.ReadAsStringAsync());
        Assert.Contains("사용자 바꾸기", page);
        Assert.Contains("첫사용자", page);
        Assert.Contains("동명이인", page);

        var res = await PostWelcomeAsync(c, "둘째사용자", switchUser: true);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Projects", res.Headers.Location!.OriginalString);
        Assert.Equal("둘째사용자", await UserOfIpAsync(f, "10.9.0.3"));
    }

    [Fact]
    public async Task 오래_쓰지_않은_IP_연결은_사용자를_다시_확인한다()
    {
        using var f = Forwarded();
        var c = Client(f, "10.9.0.4");
        await PostWelcomeAsync(c, "오래된사람");
        await AgeAsync(f, "10.9.0.4", days: 40);

        var page = await c.GetAsync("/Projects");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/Welcome?returnUrl=%2FProjects", page.Headers.Location!.OriginalString);
        var confirm = await (await c.GetAsync("/Welcome?returnUrl=%2FProjects")).Content.ReadAsStringAsync();
        Assert.Contains("오래된사람 님이 맞습니까?", WebUtility.HtmlDecode(confirm));

        // 화면이 부르는 JSON 은 401 로 알린다
        var api = new HttpRequestMessage(HttpMethod.Get, "/Admin?handler=Data&kind=location");
        api.Headers.Add("Accept", "application/json");
        var denied = await c.SendAsync(api);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("다시 확인", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // 맞습니다 → 마지막 접속을 갱신하고 원래 주소로
        var yes = await c.PostAsync("/Welcome?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ReturnUrl"] = "/Projects", ["__RequestVerificationToken"] = TokenOf(confirm),
        }));
        Assert.Equal(HttpStatusCode.Redirect, yes.StatusCode);
        Assert.Equal("/Projects", yes.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Projects")).StatusCode);
        using (var s = f.Db.NewScope())
            Assert.True(DateTime.UtcNow - (await s.Db.UserAddresses.SingleAsync(a => a.Ip == "10.9.0.4")).LastSeenAt < TimeSpan.FromMinutes(5));

        // 아닙니다 → 내 이름으로 연결
        await AgeAsync(f, "10.9.0.4", days: 40);
        var switchPage = await (await c.GetAsync("/Welcome?switch=1")).Content.ReadAsStringAsync();
        Assert.Contains("사용자 바꾸기", switchPage);
        Assert.Equal(HttpStatusCode.Redirect, (await PostWelcomeAsync(c, "새주인", switchUser: true)).StatusCode);
        Assert.Equal("새주인", await UserOfIpAsync(f, "10.9.0.4"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Projects")).StatusCode);
    }

    [Fact]
    public async Task 관리자_계정은_재확인을_클릭으로_받지_않고_기기를_옮길_때는_확인을_받는다()
    {
        using var f = Forwarded();
        using (var s = f.Db.NewScope())
            await s.Masters.AddAddressAsync(new MasterIp(f.Db.Me.Id, "10.9.3.1"));
        await AgeAsync(f, "10.9.3.1", days: 40);
        var c = Client(f, "10.9.3.1");

        // 'OOO 님이 맞습니까?' 에 '맞습니다' 대신 다시 연결하는 방법을 안내한다
        var confirm = await (await c.GetAsync("/Welcome?returnUrl=%2FProjects")).Content.ReadAsStringAsync();
        var text = WebUtility.HtmlDecode(confirm);
        Assert.Contains("테스터 님이 맞습니까?", text);
        Assert.Contains("id=\"admin-relink\"", text);
        Assert.Contains("admin 테스터 --ip 10.9.3.1", text);
        Assert.DoesNotContain("confirm-yes", text);

        // 화면을 거치지 않고 보낸 '맞습니다'도 받지 않는다
        var yes = await c.PostAsync("/Welcome?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ReturnUrl"] = "/Projects", ["__RequestVerificationToken"] = TokenOf(confirm),
        }));
        Assert.Equal(HttpStatusCode.OK, yes.StatusCode);
        Assert.Contains(UserService.AdminConfirmRefused, WebUtility.HtmlDecode(await yes.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Projects")).StatusCode);

        // 본인이 아니면 내 이름으로: 관리자 기기를 옮기는 결과를 보여 주고, 확인란 없이는 옮기지 않는다
        var switchPage = WebUtility.HtmlDecode(await (await c.GetAsync("/Welcome?switch=1")).Content.ReadAsStringAsync());
        Assert.Contains("id=\"leave-admin\"", switchPage);
        Assert.Contains("다시 관리자로 돌아올 수 없습니다", switchPage);
        var refused = await PostWelcomeAsync(c, "넘겨받은사람", switchUser: true);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("확인란을 선택해 주세요", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        Assert.Equal("테스터", await UserOfIpAsync(f, "10.9.3.1"));

        var moved = await PostWelcomeAsync(c, "넘겨받은사람", switchUser: true, leaveAdmin: true);
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        Assert.Equal("넘겨받은사람", await UserOfIpAsync(f, "10.9.3.1"));
        var admin = await (await c.GetAsync("/Admin")).Content.ReadAsStringAsync();
        Assert.Contains("조회 전용", admin);
    }

    private static async Task AgeAsync(AppFactory f, string ip, int days)
    {
        using var s = f.Db.NewScope();
        (await s.Db.UserAddresses.SingleAsync(a => a.Ip == ip)).LastSeenAt = DateTime.UtcNow.AddDays(-days);
        await s.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task 관리자가_없으면_서버에서_등록한_사람만_관리자가_되고_다른_PC_에는_안내한다()
    {
        using var f = Forwarded();
        using (f.Db.NewScope("테스터", admin: false)) { }

        var remote = Client(f, "10.9.1.1");
        await PostWelcomeAsync(remote, "원격첫사람");
        var admin = WebUtility.HtmlDecode(await (await remote.GetAsync("/Admin")).Content.ReadAsStringAsync());
        Assert.Contains("관리자가 아직 없습니다", admin);
        Assert.Contains("조회 전용", admin);
        Assert.Contains("--ip 10.9.1.1", admin);
        Assert.Contains("http://localhost/", admin);      // 서버 이름·IP 가 아니라 localhost 로 접속해야 서버 자신으로 본다

        // 서버 자신(프록시를 거치지 않은 루프백)에서 등록하면 관리자
        var local = Client(f);
        await PostWelcomeAsync(local, "서버담당자");
        using (var s = f.Db.NewScope())
        {
            Assert.True((await s.Db.Users.SingleAsync(u => u.Name == "서버담당자")).IsAdmin);
            Assert.False((await s.Db.Users.SingleAsync(u => u.Name == "원격첫사람")).IsAdmin);
        }
        Assert.DoesNotContain("관리자가 아직 없습니다", await (await remote.GetAsync("/Admin")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 허용_대역_밖의_접속은_403으로_거절하고_루프백은_항상_받는다()
    {
        using var f = Forwarded("10.20.0.0/16, 192.168.7.7");
        var outside = Client(f, "172.16.0.9");
        foreach (var url in new[] { "/", "/Welcome", "/health", "/version" })
        {
            var res = await outside.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Contains("접속할 수 없습니다", await res.Content.ReadAsStringAsync());
        }
        var json = new HttpRequestMessage(HttpMethod.Get, "/Admin?handler=Data&kind=location");
        json.Headers.Add("Accept", "application/json");
        var jres = await outside.SendAsync(json);
        Assert.Equal(HttpStatusCode.Forbidden, jres.StatusCode);
        Assert.False((await jres.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        Assert.Equal(HttpStatusCode.Redirect, (await Client(f, "10.20.3.4").GetAsync("/")).StatusCode);   // 등록 화면으로
        Assert.Equal(HttpStatusCode.OK, (await Client(f, "192.168.7.7").GetAsync("/Welcome")).StatusCode);
        Assert.Equal("ok", await Client(f).GetStringAsync("/health"));
    }

    [Fact]
    public async Task 상태_확인과_버전은_사용자_DB를_읽지_않는다()
    {
        using var f = Forwarded();
        // 사용자 연결 표를 없애 사용자 조회가 실패하게 만든다
        using (var ctx = f.Db.Open())
            await ctx.Database.ExecuteSqlRawAsync(TestDb.UseSqlServer ? $"DROP TABLE [{AppDb.Schema}].[UserAddresses]" : "DROP TABLE UserAddresses");

        var c = Client(f, "10.9.2.1");
        Assert.Equal("ok", await c.GetStringAsync("/health"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/version")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await c.GetAsync("/Projects")).StatusCode);
    }
}
