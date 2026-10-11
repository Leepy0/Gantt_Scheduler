using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 휴지통 화면·과제 삭제 핸들러·자동 정리, 기준선 비교 차수(?compare=n)
public partial class HttpTests
{
    // 통합테스터가 등록한 '물류' 과제 (등록자라 지울 수 있다)
    private async Task<int> OwnProjectAsync(string title)
    {
        using var s = _factory.Db.NewScope("통합테스터");
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, "통합테스터", new KoreaClock().Today, null, null, null, null));
    }

    // 이 앱이 시작할 때 읽은 첨부 폴더 (환경 변수는 다른 팩토리가 바꿀 수 있어 앱 설정에서 읽는다)
    private string UploadRoot() => _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:Path"]!;

    private static HttpRequestMessage TrashJson(string token, string handler, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/Admin/Trash?handler=" + handler) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 과제_삭제는_휴지통으로_보내고_관리자가_복원하거나_영구_삭제한다()
    {
        var client = await RegisteredClientAsync();
        var id = await OwnProjectAsync("HTTP 휴지통 과제");
        var (_, token) = await DetailAsync(client, id);
        var up = await client.SendAsync(Upload(token, id, "휴지통.txt", System.Text.Encoding.UTF8.GetBytes("첨부")));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var fileId = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("attachments")[0].GetProperty("id").GetInt32();
        var version = (await (await client.GetAsync($"/Projects/Detail?handler=Data&id={id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("version").GetInt32();

        // 옛 버전으로 지우면 409, 맞는 버전이면 휴지통으로
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Json(token, "Delete", new { id, reason = "중복", version = version - 1 }))).StatusCode);
        var del = await client.SendAsync(Json(token, "Delete", new { id, reason = "중복", version }));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("/Projects", (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("redirect").GetString());

        // 상세·데이터·첨부·목록에서 빠진다. 첨부 파일은 그대로 남아 있다
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Projects/Detail?id={id}")).StatusCode);
        var data = new HttpRequestMessage(HttpMethod.Get, $"/Projects/Detail?handler=Data&id={id}");
        data.Headers.Add("Accept", "application/json");
        var missing = await client.SendAsync(data);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("missing").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Projects/Files?handler=Download&id=" + fileId)).StatusCode);
        Assert.DoesNotContain("HTTP 휴지통 과제", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Projects?state=all")));
        var folder = Path.Combine(UploadRoot(), id.ToString());
        Assert.True(Directory.Exists(folder));

        // 휴지통 화면은 관리자만
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Trash")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TrashJson(token, "Restore", new { id }))).StatusCode);
        using (_factory.Db.NewScope("통합테스터", admin: true)) { }
        try
        {
            var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Trash"));
            Assert.Contains("HTTP 휴지통 과제", html);
            Assert.Contains("사유: 중복", html);
            Assert.Contains($"data-restore=\"{id}\"", html);
            Assert.Contains("id=\"trash-link\"", await client.GetStringAsync("/Admin"));
            var trashToken = TokenOf(html);
            // 화면이 본 과제 버전 (복원·영구 삭제 때 다시 보낸다)
            var seen = int.Parse(System.Text.RegularExpressions.Regex.Match(html, $"data-purge=\"{id}\" data-version=\"(\\d+)\"").Groups[1].Value);

            var restore = await client.SendAsync(TrashJson(trashToken, "Restore", new { id }));
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Projects/Detail?id={id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Projects/Files?handler=Download&id=" + fileId)).StatusCode);
            // 휴지통에 없는 과제는 영구 삭제하지 않는다
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TrashJson(trashToken, "Purge", new { id }))).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Json(token, "Delete", new { id }))).StatusCode);
            // 낡은 휴지통 화면(복원 전에 본 삭제 건)에서 누른 영구 삭제는 409 — 그 사이 복원·다시 지운 새 삭제 건을 확인 없이 지우지 않는다
            var stalePurge = await client.SendAsync(TrashJson(trashToken, "Purge", new { id, version = seen }));
            Assert.Equal(HttpStatusCode.Conflict, stalePurge.StatusCode);
            Assert.True((await stalePurge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conflict").GetBoolean());
            var fresh = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Trash"));
            var now = int.Parse(System.Text.RegularExpressions.Regex.Match(fresh, $"data-purge=\"{id}\" data-version=\"(\\d+)\"").Groups[1].Value);
            Assert.True(now > seen);
            var purge = await client.SendAsync(TrashJson(trashToken, "Purge", new { id, version = now }));
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
            Assert.False(Directory.Exists(folder));
            var after = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Trash"));
            Assert.DoesNotContain($"data-restore=\"{id}\"", after);
            Assert.Contains("HTTP 휴지통 과제", after);       // 영구 삭제 기록
            using var s = _factory.Db.NewScope();
            Assert.False(await s.Db.Projects.AnyAsync(p => p.Id == id));
            Assert.Equal("통합테스터", (await s.Db.DeletedProjectLogs.SingleAsync(l => l.ProjectId == id)).PurgedBy);
        }
        finally
        {
            using (_factory.Db.NewScope("통합테스터", admin: false)) { }
        }
    }

    [Fact]
    public async Task 자동_정리는_보관_기간이_지난_과제를_지운_사람_이름으로_영구_삭제한다()
    {
        var client = await RegisteredClientAsync();
        var oldId = await OwnProjectAsync("HTTP 자동 정리 오래됨");
        var freshId = await OwnProjectAsync("HTTP 자동 정리 최근");
        var (_, token) = await DetailAsync(client, oldId);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Upload(token, oldId, "정리.txt", new byte[] { 1, 2 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Json(token, "Delete", new { id = oldId }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Json(token, "Delete", new { id = freshId }))).StatusCode);
        using (var s = _factory.Db.NewScope())
        {
            var row = await s.Db.Projects.SingleAsync(p => p.Id == oldId);
            row.DeletedAt = DateTime.UtcNow.AddDays(-40);
            s.Db.SaveChanges();
        }

        var worker = new TrashWorker(_factory.Services.GetRequiredService<IServiceScopeFactory>(), new TrashOptions(30), new KoreaClock(),
            NullLogger<TrashWorker>.Instance);
        Assert.True(await worker.RunOnceAsync() >= 1);
        // 끈 서버(KeepDays 0)는 아무것도 하지 않는다
        Assert.Equal(0, await new TrashWorker(_factory.Services.GetRequiredService<IServiceScopeFactory>(), new TrashOptions(0), new KoreaClock(),
            NullLogger<TrashWorker>.Instance).RunOnceAsync());

        using (var s = _factory.Db.NewScope())
        {
            Assert.False(await s.Db.Projects.AnyAsync(p => p.Id == oldId));
            Assert.True(await s.Db.Projects.AnyAsync(p => p.Id == freshId && p.DeletedAt != null));
            var log = await s.Db.DeletedProjectLogs.SingleAsync(l => l.ProjectId == oldId);
            Assert.Equal(ProjectService.AutoPurgeBy, log.PurgedBy);
            Assert.Equal("통합테스터", log.DeletedBy);
        }
        Assert.False(Directory.Exists(Path.Combine(UploadRoot(), oldId.ToString())));
    }

    [Fact]
    public async Task 기준선_비교_차수는_조회와_저장_응답에_같이_쓰인다()
    {
        var client = await RegisteredClientAsync();
        var id = await OwnProjectAsync("HTTP 기준선 차수");
        using (var s = _factory.Db.NewScope("통합테스터"))
        {
            var st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, new KoreaClock().Today.AddDays(10), null, null, false, st.Version));
            await s.Projects.SetBaselineAsync(id);
            st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, new KoreaClock().Today.AddDays(13), null, "지연", false, st.Version));
            await s.Projects.SetBaselineAsync(id, "재확정");
            st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, new KoreaClock().Today.AddDays(15), null, "또 지연", false, st.Version));
        }
        var (_, token) = await DetailAsync(client, id);

        var latest = (await (await client.GetAsync($"/Projects/Detail?handler=Data&id={id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project");
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("compareNo").ValueKind);
        Assert.Equal(2, latest.GetProperty("baselines").GetArrayLength());
        Assert.Equal(2, latest.GetProperty("stages")[0].GetProperty("slipDays").GetInt32());

        var first = (await (await client.GetAsync($"/Projects/Detail?handler=Data&compare=1&id={id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project");
        Assert.Equal(1, first.GetProperty("compareNo").GetInt32());
        Assert.Equal(5, first.GetProperty("stages")[0].GetProperty("slipDays").GetInt32());

        // 저장 응답도 화면이 고른 차수로 센다
        var res = await client.SendAsync(Json(token, "Comment&compare=1", new { projectId = id, text = "차수 확인" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("compareNo").GetInt32());
    }
}
