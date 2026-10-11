using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 일정 엔진 관련 HTTP 확인 (같은 앱·사용자 도우미를 쓰도록 HttpTests 의 나머지 부분)
public partial class HttpTests
{
    private static JsonElement StageOf(JsonElement project, string workTitle) =>
        project.GetProperty("stages").EnumerateArray()
            .First(s => s.GetProperty("works").EnumerateArray().Any(w => w.GetProperty("title").GetString() == workTitle));

    [Fact]
    public async Task 범위_밖_날짜는_400_과_필드로_알리고_단계_완료와_함께_남은_작업을_닫는다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 2);
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=2");
        var stage = StageOf(data.GetProperty("project"), "출하 검사");      // 제작 단계: 완료·진행 중·예정 작업
        var stageId = stage.GetProperty("id").GetInt32();
        Assert.Equal(2, stage.GetProperty("openWorks").GetInt32());

        object Body(string? target, string? completed, bool closeWorks) => new
        {
            projectId = 2, id = stageId, name = stage.GetProperty("name").GetString(),
            targetDate = target, completedDate = completed, reason = "범위 확인", cascade = false,
            version = stage.GetProperty("version").GetInt32(), closeWorks,
        };

        // 연도 오타는 저장 전에 거절하고, 상세 화면은 계속 열린다
        var bad = await client.SendAsync(Json(token, "SaveStage", Body("2620-10-01", null, false)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("targetDate", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Projects/Detail?id=2")).StatusCode);

        // 완료하면서 남은 작업을 닫는다 (closeWorks 가 JSON 에서 그대로 묶인다)
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)).ToString("yyyy-MM-dd");   // 앱의 '오늘'(한국 시간)
        var ok = await client.SendAsync(Json(token, "SaveStage", Body(stage.GetProperty("targetDate").GetString(), today, true)));
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, body);
        var after = StageOf(JsonDocument.Parse(body).RootElement.GetProperty("project"), "출하 검사");
        Assert.Equal("done", after.GetProperty("status").GetString());
        Assert.All(after.GetProperty("works").EnumerateArray(), w =>
        {
            Assert.NotEqual(JsonValueKind.Null, w.GetProperty("actualEnd").ValueKind);
            Assert.False(w.GetProperty("late").GetBoolean());
        });
    }

    [Fact]
    public async Task 연쇄_이동의_cascadeWorks_가_JSON_에서_묶여_이후_단계_작업을_옮긴다()
    {
        // 다른 시험이 바꾸지 않는 전용 과제를 만든다 (착수 2026-10-01, 물류: 검토 → 보고 → 적용)
        int id;
        using (var s = _factory.Db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            id = await s.Projects.CreateAsync(new ProjectInput("HTTP 연쇄 작업", cat.Id, corp.Id, null, new DateOnly(2026, 10, 1), null, null, null, null));
            var stages = (await s.Projects.ViewAsync(id))!.Stages;
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, new DateOnly(2026, 10, 14), null, null, false));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stages[1].Id, "HTTP 보고서", null, null,
                new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 20), null, null, 0, null, null));
        }

        var (client, token) = await DetailAsync(await RegisteredClientAsync(), id);
        var data = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={id}");
        var first = data.GetProperty("project").GetProperty("stages")[0];
        Assert.Equal(1, first.GetProperty("laterWorks").GetInt32());

        var res = await client.SendAsync(Json(token, "SaveStage", new
        {
            projectId = id, id = first.GetProperty("id").GetInt32(), name = first.GetProperty("name").GetString(),
            targetDate = "2026-10-21", completedDate = (string?)null, reason = "HTTP 연쇄", cascade = true, cascadeWorks = true,
            version = first.GetProperty("version").GetInt32(),
        }));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body);
        var work = StageOf(JsonDocument.Parse(body).RootElement.GetProperty("project"), "HTTP 보고서")
            .GetProperty("works").EnumerateArray().Single(w => w.GetProperty("title").GetString() == "HTTP 보고서");
        Assert.Equal("2026-10-22", work.GetProperty("planStart").GetString());      // +5근무일, 근무일 기간(4일) 유지
        Assert.Equal("2026-10-27", work.GetProperty("planEnd").GetString());
    }

    private static JsonElement PageData(string html) =>
        JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(html, "<script id=\"page-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value)).RootElement;

    [Fact]
    public async Task 과제_수정_화면은_오류로_다시_그려도_저장된_담당자를_지금_담당으로_알린다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Projects/Edit?id=1");
        var owner = PageData(html).GetProperty("currentOwner").GetString();
        Assert.False(string.IsNullOrEmpty(owner));

        // 담당자를 목록에 없는 이름으로 바꾸고 다른 오류(과제명 없음)로 다시 그린 화면
        var res = await client.PostAsync("/Projects/Edit?id=1", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Title"] = "", ["Form.Owner"] = "오타담당자", ["__RequestVerificationToken"] = TokenOf(html),
        }));
        var again = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("form-error", again);
        // 화면은 입력칸 값('오타담당자')이 아니라 저장된 담당자만 '지금 담당'으로 보므로 새 담당자 안내가 계속 뜬다
        Assert.Equal(owner, PageData(again).GetProperty("currentOwner").GetString());
        using var s = _factory.Db.NewScope();
        Assert.False(await s.Db.Users.AnyAsync(u => u.Name == "오타담당자"));
    }
}
