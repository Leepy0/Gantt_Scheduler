using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 조기 경보·목록 정리·내 할 일 빠른 갱신의 HTTP 경로
public partial class HttpTests
{
    private static HttpRequestMessage MyProgress(string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/My?handler=Progress") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 내_할_일_빠른_갱신은_다시_나눈_행을_돌려주고_어긋난_버전은_409_와_최신_행을_준다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/My");
        var token = TokenOf(html);
        int workId;
        using (var s = _factory.Db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            var today = DateOnly.FromDateTime(KoreaClock.ToKst(DateTime.UtcNow));      // 앱은 실제 시각을 쓴다
            var id = await s.Projects.CreateAsync(new ProjectInput("HTTP 빠른 갱신", cat.Id, corp.Id, null, today.AddDays(-5), null, null, null, null));
            var stage = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "빠른 갱신 작업", "통합테스터", null, today.AddDays(-2), today.AddDays(3), null, null, 0, null, null));
            workId = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single().Id;
        }
        // 내 할 일에 그 작업이 빠른 갱신 값(버전)과 함께 실린다
        Assert.Contains($"data-my-work=\"{workId}\"", await client.GetStringAsync("/My"));

        var ok = await OkJsonAsync(await client.SendAsync(MyProgress(token, new { workId, version = 0, progress = 50, note = "절반 진행" })));
        var work = ok.GetProperty("work");
        Assert.Equal((50, 1, "soon"), (work.GetProperty("progress").GetInt32(), work.GetProperty("version").GetInt32(), work.GetProperty("group").GetString()));
        Assert.NotEqual(JsonValueKind.Null, work.GetProperty("actualStart").ValueKind);
        Assert.True(ok.TryGetProperty("today", out _));

        // 다른 화면이 먼저 고친 뒤 옛 버전으로 보내면 409 와 최신 행
        var stale = await client.SendAsync(MyProgress(token, new { workId, version = 0, progress = 75 }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("conflict").GetBoolean());
        Assert.Equal((50, 1), (body.GetProperty("work").GetProperty("progress").GetInt32(), body.GetProperty("work").GetProperty("version").GetInt32()));

        // 규칙 위반은 400 과 칸 이름
        var bad = await client.SendAsync(MyProgress(token, new { workId, version = 1, progress = 33 }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("progress", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());

        // 오늘 완료하면 내 할 일에서 빠진다(work = null)
        var today2 = DateOnly.FromDateTime(KoreaClock.ToKst(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        var done = await OkJsonAsync(await client.SendAsync(MyProgress(token, new { workId, version = 1, progress = 100, actualEnd = today2 })));
        Assert.Equal(JsonValueKind.Null, done.GetProperty("work").ValueKind);
    }

    [Fact]
    public async Task 목록은_위험_정체_탭과_머리글_정렬_링크_aria_sort_를_그린다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Projects?state=all&sort=title");
        Assert.Contains("value=\"risk\"", html);
        Assert.Contains("value=\"stale\"", html);
        Assert.Contains("aria-sort=\"ascending\"", html);
        // 같은 열을 다시 누르면 반대 방향
        Assert.Contains("sort=title&amp;desc=true", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Projects?state=risk")).StatusCode);
        var excel = await client.GetAsync("/Projects?handler=Excel&state=all&sort=end&desc=true");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);

        var dash = await client.GetStringAsync("/?scope=all&sort=recent");
        Assert.Contains("id=\"risk-card\"", dash);
        Assert.Contains("data-kpi-risk", dash);
        Assert.Contains("data-scope=\"all\"", dash);
    }
}
