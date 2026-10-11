using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 협업의 HTTP 경로: 상세 데이터(새 활동 기준·알릴 사람), 활동 조건, 관심 과제, 댓글 수정·삭제, 읽지 않은 수(/api/unread), 알림 목록, 관리 화면의 메일
public partial class HttpTests
{
    private static string PageDataOf(string html) =>
        Regex.Match(html, "<script id=\"page-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

    private async Task<int> UnreadAsync(HttpClient client)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/unread");
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("X-Background", "1");
        var res = await client.SendAsync(req);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        return (await OkJsonAsync(res)).GetProperty("unread").GetInt32();
    }

    [Fact]
    public async Task 멘션하면_읽지_않은_수가_늘고_알림을_누르면_읽음으로_바꾸고_그_활동으로_간다()
    {
        var client = await RegisteredClientAsync();
        int me, projectId;
        using (var s = _factory.Db.NewScope())
        {
            me = await s.Db.Users.Where(u => u.Name == "통합테스터").Select(u => u.Id).SingleAsync();
            projectId = await s.Db.Projects.Where(p => p.Title == "미주 A제품 AGV 도입 검토").Select(p => p.Id).SingleAsync();
        }
        var before = await UnreadAsync(client);
        using (var s = _factory.Db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(projectId, null, null, "HTTP 멘션 확인", new() { me }));
        Assert.Equal(before + 1, await UnreadAsync(client));

        // 알림 목록 화면
        var page = await client.GetStringAsync("/My/Notifications?unread=true");
        Assert.Contains("HTTP 멘션 확인", WebUtility.HtmlDecode(page));
        Assert.Contains("멘션", WebUtility.HtmlDecode(page));
        int noteId, activityId;
        using (var s = _factory.Db.NewScope())
        {
            var n = await s.Db.Notifications.Where(x => x.UserId == me && x.Text == "댓글: HTTP 멘션 확인").SingleAsync();
            (noteId, activityId) = (n.Id, n.ActivityId!.Value);
        }
        // 누르면(?handler=Go) 읽음으로 바꾸고 과제 상세의 그 활동으로
        var go = await client.GetAsync($"/My/Notifications?handler=Go&id={noteId}");
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
        Assert.Equal($"/Projects/Detail?id={projectId}#a{activityId}", go.Headers.Location!.OriginalString);
        Assert.Equal(before, await UnreadAsync(client));
        // 남의 알림·없는 알림은 목록으로 돌려보낸다
        Assert.Equal("/My/Notifications", (await client.GetAsync("/My/Notifications?handler=Go&id=999999")).Headers.Location!.OriginalString);

        // 모두 읽음
        using (var s = _factory.Db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(projectId, null, null, "HTTP 두 번째", new() { me }));
        var token = TokenOf(page);
        var all = new HttpRequestMessage(HttpMethod.Post, "/My/Notifications?handler=ReadAll") { Content = JsonContent.Create(new { }) };
        all.Headers.Add("X-CSRF-TOKEN", token);
        all.Headers.Add("Accept", "application/json");
        Assert.Equal(0, (await OkJsonAsync(await client.SendAsync(all))).GetProperty("unread").GetInt32());
        Assert.Equal(0, await UnreadAsync(client));

        // 1분 확인(/api/unread)은 배경 요청이라 사용으로 치지 않는다 — 등록하지 않은 기기는 401
        using var fresh = new AppFactory();
        var anon = fresh.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/unread");
        req.Headers.Add("Accept", "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task 상세는_새_활동_기준과_알릴_사람을_싣고_활동_조건으로_나눠_받는다()
    {
        var client = await RegisteredClientAsync();
        int projectId;
        using (var s = _factory.Db.NewScope())
            projectId = await s.Db.Projects.Where(p => p.Title == "본사 2공장 물류 동선 Simulation").Select(p => p.Id).SingleAsync();

        var html = await client.GetStringAsync($"/Projects/Detail?id={projectId}");
        var data = JsonDocument.Parse(PageDataOf(html)).RootElement;
        Assert.True(data.GetProperty("seen").TryGetProperty("since", out _));
        Assert.Contains(data.GetProperty("people").EnumerateArray(), p => p.GetProperty("name").GetString() == "최모델");
        Assert.Equal(data.GetProperty("people").GetArrayLength(), data.GetProperty("users").GetArrayLength());
        Assert.Equal(6, data.GetProperty("kinds").GetArrayLength());
        var first = data.GetProperty("project").GetProperty("activities")[0];
        Assert.True(first.TryGetProperty("kind", out _));
        Assert.True(first.TryGetProperty("canEdit", out _));
        Assert.Contains("id=\"watch-btn\"", html);
        Assert.Contains("aria-pressed=\"false\"", html);
        using (var s = _factory.Db.NewScope())
        {
            var me = await s.Db.Users.Where(u => u.Name == "통합테스터").Select(u => u.Id).SingleAsync();
            Assert.True(await s.Db.ProjectVisits.AnyAsync(v => v.UserId == me && v.ProjectId == projectId));
        }

        // 종류 조건 (변경 이력과 같은 분류)
        var token = TokenOf(html);
        await client.SendAsync(Json(token, "Comment", new { projectId, text = "조건 확인용 댓글" }));
        var r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Activities&id={projectId}&kind=comment"));
        Assert.All(r.GetProperty("activities").EnumerateArray(), a => Assert.Equal("comment", a.GetProperty("kind").GetString()));
        r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Activities&id={projectId}&q={Uri.EscapeDataString("조건 확인용")}"));
        var mine = Assert.Single(r.GetProperty("activities").EnumerateArray());
        Assert.True(mine.GetProperty("mine").GetBoolean());
        Assert.True(mine.GetProperty("canEdit").GetBoolean());

        // 관심 과제 별: 상태를 돌려주고, 목록에서도 눌린 별로 보인다
        r = await OkJsonAsync(await client.SendAsync(Json(token, "Watch", new { projectId, on = true })));
        Assert.True(r.GetProperty("watched").GetBoolean());
        var list = await client.GetStringAsync("/Projects");
        Assert.Matches($"data-watch=\"{projectId}\"[^>]*aria-pressed=\"true\"", list);
        Assert.Contains("관심 과제", WebUtility.HtmlDecode(await client.GetStringAsync("/My")));
        r = await OkJsonAsync(await client.SendAsync(Json(token, "Watch", new { projectId, on = false })));
        Assert.False(r.GetProperty("watched").GetBoolean());
        var missing = await client.SendAsync(Json(token, "Watch", new { projectId = 999999, on = true }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // 댓글 고치기·지우기 → 최신 과제를 돌려준다
        var commentId = mine.GetProperty("id").GetInt32();
        r = await OkJsonAsync(await client.SendAsync(Json(token, "EditComment", new { id = commentId, text = "조건 확인용 댓글(고침)", mentions = Array.Empty<int>() })));
        var edited = r.GetProperty("project").GetProperty("activities").EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == commentId);
        Assert.Equal("조건 확인용 댓글(고침)", edited.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        r = await OkJsonAsync(await client.SendAsync(Json(token, "DeleteComment", new { id = commentId })));
        var deleted = r.GetProperty("project").GetProperty("activities").EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == commentId);
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        Assert.Equal(ProjectService.DeletedCommentText, deleted.GetProperty("text").GetString());
        var again = await client.SendAsync(Json(token, "DeleteComment", new { id = commentId }));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // 없는 사람을 알릴 사람으로 고르면 400(field mentions)
        var bad = await client.SendAsync(Json(token, "Comment", new { projectId, text = "멘션 오류", mentions = new[] { 999999 } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("mentions", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task 목록과_대시보드는_새_활동_수를_보이고_내_할_일은_새_소식을_보인다()
    {
        var client = await RegisteredClientAsync();
        int me, projectId;
        using (var s = _factory.Db.NewScope())
        {
            me = await s.Db.Users.Where(u => u.Name == "통합테스터").Select(u => u.Id).SingleAsync();
            projectId = await s.Db.Projects.Where(p => p.Title == "유럽 C제품 창고 입출고 개선").Select(p => p.Id).SingleAsync();
        }
        await client.GetStringAsync($"/Projects/Detail?id={projectId}");      // 방문
        using (var s = _factory.Db.NewScope("이자동"))
        {
            s.Db.ProjectWatches.Add(new ProjectWatch { UserId = me, ProjectId = projectId, CreatedAt = DateTime.UtcNow });
            await s.Db.SaveChangesAsync();
        }
        // 앱은 실제 시각을 쓴다 — 방문 뒤에 남긴 활동이 되도록 실제 시각으로 남긴다
        var saved = _factory.Db.Clock.UtcNow;
        try
        {
            _factory.Db.Clock.UtcNow = DateTime.UtcNow.AddSeconds(1);
            using (var s = _factory.Db.NewScope("이자동"))
                await s.Projects.CommentAsync(new CommentInput(projectId, null, null, "유럽 현장 소식"));
        }
        finally { _factory.Db.Clock.UtcNow = saved; }

        var list = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects?state=all"));
        Assert.Matches("유럽 C제품 창고 입출고 개선</span>\\s*<span class=\"row-sub\">[^<]*<span class=\"new-count\"[^>]*>새 활동 1</span>", list);
        var dash = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("새 활동 1", dash);
        var my = WebUtility.HtmlDecode(await client.GetStringAsync("/My"));
        Assert.Contains("새 소식", my);
        Assert.Contains("유럽 현장 소식", my);
        Assert.Contains("#a", my);
    }

    [Fact]
    public async Task 관리자는_사용자_메일_주소를_정하고_메일_서버가_없으면_시험_메일은_이유를_알린다()
    {
        var client = await RegisteredClientAsync();
        await SetCollabAdminAsync(true);
        try
        {
        var html = await client.GetStringAsync("/Admin");
        var token = TokenOf(html);
        int userId;
        int? org;
        using (var s = _factory.Db.NewScope())
            (userId, org) = await s.Db.Users.Where(u => u.Name == "박설비").Select(u => new ValueTuple<int, int?>(u.Id, u.OrgUnitId)).SingleAsync();

        HttpRequestMessage Post(string handler, object body) => AdminPost(token, handler, body);

        var bad = await client.SendAsync(Post("Save", new { kind = "user", level = 1, id = userId, name = "박설비", orgUnitId = org, email = "주소 아님" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("email", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        var ok = await OkJsonAsync(await client.SendAsync(Post("Save", new { kind = "user", level = 1, id = userId, name = "박설비", orgUnitId = org, email = "park@example.com" })));
        Assert.Equal("park@example.com", ok.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == userId).GetProperty("email").GetString());
        // 메일 주소를 보내지 않은 저장(다른 화면)은 그대로 둔다, 빈 값은 지운다
        await OkJsonAsync(await client.SendAsync(Post("Save", new { kind = "user", level = 1, id = userId, name = "박설비", orgUnitId = org })));
        using (var s = _factory.Db.NewScope())
            Assert.Equal("park@example.com", (await s.Db.Users.SingleAsync(u => u.Id == userId)).Email);
        await OkJsonAsync(await client.SendAsync(Post("Save", new { kind = "user", level = 1, id = userId, name = "박설비", orgUnitId = org, email = "" })));
        using (var s = _factory.Db.NewScope())
            Assert.Null((await s.Db.Users.SingleAsync(u => u.Id == userId)).Email);

        // 시험 메일: 테스트 서버에는 메일 서버 설정이 없다
        var test = await client.SendAsync(Post("TestMail", new { email = "admin@example.com" }));
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        Assert.Contains("Notify:Smtp", await test.Content.ReadAsStringAsync());
        }
        finally { await SetCollabAdminAsync(false); }
        // 관리자가 아니면 시험 메일을 보낼 수 없다
        var denied = await client.SendAsync(AdminPost(TokenOf(await client.GetStringAsync("/Admin")), "TestMail", new { email = "admin@example.com" }));
        Assert.Contains("관리자만", await denied.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage AdminPost(string token, string handler, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=" + handler) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    // 통합테스터를 잠시 관리자로 (테스트 DB 에는 이미 다른 관리자가 있어 등록한 사람은 관리자가 아니다)
    private async Task SetCollabAdminAsync(bool admin)
    {
        using var s = _factory.Db.NewScope();
        var u = await s.Db.Users.SingleAsync(x => x.Name == "통합테스터");
        u.IsAdmin = admin;
        await s.Db.SaveChangesAsync();
    }
}
