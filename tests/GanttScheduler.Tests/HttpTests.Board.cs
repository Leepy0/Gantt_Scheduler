using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GanttScheduler.Tests;

// TV 상황판(등록 없이 열림·데이터 버전 확인), 도움말 링크, 빈 대시보드·등록 화면의 첫 설정 안내
public partial class HttpTests
{
    private static async Task<string> StampAsync(HttpClient client)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/Board?handler=Stamp");
        req.Headers.Add("Accept", "application/json");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString()!;
    }

    [Fact]
    public async Task 상황판은_이름_등록_없이_열리고_사용자를_만들지_않는다()
    {
        // 다른 시험이 이 IP 를 등록했을 수 있어 새 앱으로 본다
        using var fresh = new AppFactory();
        var client = fresh.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        int users;
        using (var db = fresh.Db.Open()) users = await db.Users.CountAsync();

        var res = await client.GetAsync("/Board");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Contains("과제 상황판", html);
        Assert.Contains("data-board-kpi=\"delayed\"", html);
        Assert.Contains("지연·위험 과제", html);
        Assert.Contains("이번 주 목표 단계", html);
        Assert.Contains("법인별 현황", html);
        // 사람 이름 칸·메뉴가 없는 읽기 전용 화면
        Assert.DoesNotContain("사용자 바꾸기", html);

        var stamp = await StampAsync(client);
        Assert.False(string.IsNullOrEmpty(stamp));
        using (var db = fresh.Db.Open())
        {
            Assert.Equal(users, await db.Users.CountAsync());
            Assert.False(await db.UserAddresses.AnyAsync(a => a.User!.Name == ""));
        }
        // 다른 화면은 여전히 이름 등록으로 보낸다
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Boardx")).StatusCode);
    }

    [Fact]
    public async Task 상황판_확인값은_과제가_바뀔_때만_달라진다()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var before = await StampAsync(client);

        // 개인 기록(마지막 접속 시각)만 바뀌면 그대로
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var user = await db.Users.FirstAsync();
            db.UserAddresses.Add(new UserAddress { UserId = user.Id, Ip = "10.77.0." + Random.Shared.Next(1, 250), LastSeenAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(before, await StampAsync(client));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var p = await db.Projects.OrderBy(x => x.Id).FirstAsync();
            p.Description = "상황판 확인값 시험 " + Guid.NewGuid().ToString("N")[..6];
            await db.SaveChangesAsync();
        }
        Assert.NotEqual(before, await StampAsync(client));
    }

    [Fact]
    public async Task 도움말과_도움말_링크가_있다()
    {
        var client = await RegisteredClientAsync();
        var dash = await client.GetStringAsync("/");
        Assert.Contains("id=\"help-link\"", dash);
        Assert.Contains("data-help=\"risk\"", dash);
        Assert.Contains("id=\"board-link\"", dash);
        Assert.Contains("target=\"_blank\"", dash);

        var help = WebUtility.HtmlDecode(await client.GetStringAsync("/Help"));
        foreach (var (id, title) in GanttScheduler.Pages.HelpModel.Sections)
        {
            Assert.Contains($"id=\"{id}\"", help);
            Assert.Contains($"href=\"#{id}\"", help);
            Assert.Contains(title, help);
        }
        // 다른 화면의 '?' 링크는 있는 절만 가리킨다
        foreach (var url in new[] { "/", "/My", "/My/Notifications", "/Projects?state=done&q=없는과제이름" })
        {
            var html = await client.GetStringAsync(url);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "data-help=\"([a-z]+)\""))
                Assert.True(GanttScheduler.Pages.HelpModel.Has(m.Groups[1].Value), $"{url}: {m.Groups[1].Value}");
        }
    }

    [Fact]
    public async Task 완료_탭은_올해가_기본이고_연도를_고를_수_있다()
    {
        var client = await RegisteredClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects?state=done"));
        Assert.Contains("id=\"list-year\"", html);
        Assert.Matches($"<option value=\"{new KoreaClock().Today.Year}\" selected", html);
        // 진행 탭에는 연도 고르기가 없다
        Assert.DoesNotContain("id=\"list-year\"", await client.GetStringAsync("/Projects?state=active"));
    }

    [Theory]
    [InlineData("/Projects?state=done&year=10000")]
    [InlineData("/Projects?state=all&doneYear=0")]
    [InlineData("/Projects?doneYear=-1")]
    [InlineData("/Projects?state=dropped&year=-5")]
    [InlineData("/Projects?handler=Excel&state=done&year=10000&doneYear=10000")]
    public async Task 범위_밖_연도_주소도_오류_없이_열린다(string url)
    {
        var client = await RegisteredClientAsync();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        if (url.Contains("handler=Excel")) return;
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        // 잘못 친 연도로 연도 고르기 목록이 수천 줄로 늘지 않는다
        Assert.True(System.Text.RegularExpressions.Regex.Matches(html, "년 종료</option>").Count < 200);
    }

    [Fact]
    public async Task 과제가_없고_법인도_없으면_첫_설정을_안내한다()
    {
        using var fresh = new AppFactory();
        using (var db = fresh.Db.Open())
        {
            // 샘플 없이 막 설치한 서버처럼: 과제는 모두 휴지통, 법인은 사용 중지
            await db.Projects.ExecuteUpdateAsync(u => u.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
            await db.Locations.Where(l => l.Level == 1).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsActive, false));
        }
        var client = fresh.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var welcome = await client.GetAsync("/Welcome");
        var token = TokenOf(await welcome.Content.ReadAsStringAsync());
        await client.PostAsync("/Welcome", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "첫사용자", ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = token }));

        // 일반 사용자: 관리자에게 요청
        var dash = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("id=\"setup-needed\"", dash);
        Assert.Contains("관리자에게 기준 정보 등록을 요청하세요", dash);
        Assert.DoesNotContain("data-setup=\"location\"", dash);
        var edit = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects/Edit"));
        Assert.Contains("id=\"setup-needed\"", edit);

        // 관리자: 관리 > 계층과 첫 설정 점검표로
        using (var db = fresh.Db.Open())
        {
            await db.Users.Where(u => u.Name == "첫사용자").ExecuteUpdateAsync(u => u.SetProperty(x => x.IsAdmin, true));
        }
        dash = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("data-setup=\"location\"", dash);
        Assert.Contains("href=\"/Admin#location\"", dash);
        Assert.Contains("href=\"/Admin/Status\"", dash);
        Assert.Contains("관리 > 계층", dash);

        // 법인이 생기면 등록으로 보낸다
        using (var db = fresh.Db.Open())
        {
            await db.Locations.Where(l => l.Level == 1).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsActive, true));
        }
        dash = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.DoesNotContain("id=\"setup-needed\"", dash);
        Assert.Contains("id=\"first-project\"", dash);
        Assert.DoesNotContain("id=\"setup-needed\"", await client.GetStringAsync("/Projects/Edit"));
    }
}
