using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 관리 > 사용자 정리(합치기·이관)·변경 이력·사용 현황 핸들러, 상세의 첨부 상한 데이터
public partial class HttpTests
{
    private async Task<int> PlainUserAsync(string name)
    {
        using var s = _factory.Db.NewScope();
        var u = await s.Db.Users.FirstOrDefaultAsync(x => x.Name == name);
        if (u == null) s.Db.Users.Add(u = new AppUser { Name = name, CreatedAt = DateTime.UtcNow });
        await s.Db.SaveChangesAsync();
        return u.Id;
    }

    [Fact]
    public async Task 관리_화면에서_사용자를_합치고_이관하며_변경_이력과_사용_현황을_본다()
    {
        var client = await RegisteredClientAsync();
        var fromId = await PlainUserAsync("HTTP 합칠 오타");
        var intoId = await PlainUserAsync("HTTP 남길 이름");
        var leaveId = await PlainUserAsync("HTTP 떠나는 사람");
        var id = await OwnProjectAsync("HTTP 이관 과제");
        using (var s = _factory.Db.NewScope())
        {
            (await s.Db.Projects.SingleAsync(p => p.Id == id)).OwnerId = leaveId;
            await s.Db.SaveChangesAsync();
        }

        // 관리자가 아니면: 사용 현황은 조직 집계만, 변경 이력·합치기는 거절
        var token = TokenOf(await client.GetStringAsync("/Admin"));
        var usage = await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Usage");
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("usage").GetProperty("people").ValueKind);
        Assert.True(usage.GetProperty("usage").GetProperty("orgs").GetArrayLength() > 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Admin?handler=Changes")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(AdminPost(token, "Merge", new { fromId, intoId }))).StatusCode);
        Assert.DoesNotContain("data-kind=\"changes\"", await client.GetStringAsync("/Admin"));

        await SetCollabAdminAsync(true);
        try
        {
            var html = await client.GetStringAsync("/Admin");
            Assert.Contains("data-kind=\"changes\"", html);
            Assert.Contains("data-kind=\"usage\"", html);
            token = TokenOf(html);

            // 사용자 목록에 진행 담당 수
            var users = (await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Data&kind=user")).GetProperty("nodes");
            var leave = users.EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == leaveId);
            Assert.Equal(1, leave.GetProperty("openProjects").GetInt32());

            var merge = await client.SendAsync(AdminPost(token, "Merge", new { fromId, intoId, reason = "오타" }));
            Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
            var mj = await merge.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("합쳤습니다", mj.GetProperty("notice").GetString());
            Assert.DoesNotContain(mj.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("id").GetInt32() == fromId);

            // 이관: 넘겨받을 사람이 없으면 400(field toId), 있으면 사용 중지
            var bad = await client.SendAsync(AdminPost(token, "Reassign", new { fromId = leaveId, projects = true, works = true }));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("toId", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
            var ok = await client.SendAsync(AdminPost(token, "Reassign", new { fromId = leaveId, toId = intoId, projects = true, works = true, reason = "이동" }));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var after = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == leaveId);
            Assert.False(after.GetProperty("active").GetBoolean());
            Assert.Equal(0, after.GetProperty("openProjects").GetInt32());

            // 변경 이력: 종류 조건과 고르기 목록
            var changes = await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Changes&kind=user");
            var items = changes.GetProperty("page").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToList();
            Assert.Contains(items, t => t.StartsWith("사용자 병합: HTTP 합칠 오타 → HTTP 남길 이름"));
            Assert.Contains(items, t => t.StartsWith("담당 이관 후 사용 중지: HTTP 떠나는 사람 → HTTP 남길 이름"));
            Assert.Contains(changes.GetProperty("kinds").EnumerateArray(), k => k.GetProperty("key").GetString() == "holiday");
            Assert.Contains(changes.GetProperty("people").EnumerateArray(), p => p.GetProperty("name").GetString() == "통합테스터");

            var full = await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Usage");
            Assert.Contains(full.GetProperty("usage").GetProperty("people").EnumerateArray(), p => p.GetProperty("name").GetString() == "통합테스터");

            // 업데이트 설정 변경도 남는다 (끝나면 원래 설정으로 되돌린다 — 같은 앱을 쓰는 다른 시험)
            var upd = TokenOf(await client.GetStringAsync("/Admin/Update"));
            var original = (await client.GetFromJsonAsync<JsonElement>("/Admin/Update?handler=Status")).GetProperty("status").GetProperty("settings");
            var req = new HttpRequestMessage(HttpMethod.Post, "/Admin/Update?handler=Settings")
            {
                Content = JsonContent.Create(new { autoCheck = false, autoApply = false, windowFrom = 2, windowTo = 5, idleMinutes = 33 }),
            };
            req.Headers.Add("X-CSRF-TOKEN", upd);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
            var updates = await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Changes&kind=update");
            Assert.Contains(updates.GetProperty("page").GetProperty("items").EnumerateArray(),
                i => i.GetProperty("text").GetString()!.StartsWith("업데이트 설정 변경") && i.GetProperty("newValue").GetString()!.Contains("대기 시간: 33분"));
            var restore = new HttpRequestMessage(HttpMethod.Post, "/Admin/Update?handler=Settings") { Content = JsonContent.Create(original) };
            restore.Headers.Add("X-CSRF-TOKEN", upd);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(restore)).StatusCode);
        }
        finally
        {
            await SetCollabAdminAsync(false);
        }
    }

    [Fact]
    public async Task 상세_데이터는_과제_첨부_상한을_싣는다()
    {
        var client = await RegisteredClientAsync();
        var id = await OwnProjectAsync("HTTP 첨부 상한 과제");
        var html = await client.GetStringAsync($"/Projects/Detail?id={id}");
        Assert.Contains("id=\"files-total\"", html);
        Assert.Contains("projectMaxMB", html);
    }
}
