using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 일정 엔진 확장의 HTTP 경로: 미리보기(GET ?handler=Preview), 재계획, 선행 연결(유형·간격) JSON, 과제 간 단계 연결
public partial class HttpTests
{
    private static DateOnly EDay(int month, int day) => new(2026, month, day);

    // 다른 시험이 바꾸지 않는 전용 과제: 착수 2026-10-01, 첫 단계 목표 12-30, A → B(끝나면 시작), C(시작일이 이미 지남)
    private async Task<(int Id, int StageId, int A, int B, int C)> EngineProjectAsync(string title)
    {
        using var s = _factory.Db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, EDay(10, 1), null, null, null, null));
        var stage = (await s.Projects.ViewAsync(id))!.Stages[0];
        await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, EDay(12, 30), null, null, false));
        await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "A", null, null, EDay(10, 12), EDay(10, 14), null, null, 0, null, null));
        var a = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single(w => w.Title == "A").Id;
        await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "B", null, null, EDay(10, 15), EDay(10, 16), null, null, 0, null, null, Predecessors: new() { a }));
        await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "C", null, null, EDay(10, 6), EDay(10, 7), null, null, 0, null, null));
        var works = (await s.Projects.ViewAsync(id))!.Stages[0].Works;
        return (id, stage.Id, a, works.Single(w => w.Title == "B").Id, works.Single(w => w.Title == "C").Id);
    }

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task 미리보기는_GET_으로_밀리는_작업과_막는_이유를_돌려주고_저장하지_않는다()
    {
        var x = await EngineProjectAsync("HTTP 미리보기");
        var client = await RegisteredClientAsync();
        int Activities() { using var s = _factory.Db.NewScope(); return s.Db.Activities.Count(a => a.ProjectId == x.Id); }
        var before = Activities();

        var r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=work&id={x.A}&from=2026-10-12&to=2026-10-16"));
        var preview = r.GetProperty("preview");
        Assert.Equal(5, preview.GetProperty("workDays").GetInt32());
        var moved = preview.GetProperty("moved")[0];
        Assert.Equal(("B", "2026-10-19", "2026-10-20"), (moved.GetProperty("title").GetString(), moved.GetProperty("newStart").GetString(), moved.GetProperty("newEnd").GetString()));
        Assert.Equal(2, moved.GetProperty("shift").GetInt32());
        Assert.Equal(before, Activities());

        // 화면이 고른 연결('id:유형:간격')로 본다: 같이 시작이면 B 를 앞당겨도 막지 않는다, 끝나면 시작이면 막는다
        r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=work&id={x.B}&from=2026-10-13&to=2026-10-14&links={x.A}:ss:0"));
        Assert.Equal(0, r.GetProperty("preview").GetProperty("violations").GetArrayLength());
        r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=work&id={x.B}&from=2026-10-13&to=2026-10-14"));
        var issue = r.GetProperty("preview").GetProperty("violations")[0];
        Assert.True(issue.GetProperty("blocking").GetBoolean());
        Assert.Equal("planStart", issue.GetProperty("field").GetString());
        r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=work&id={x.B}&from=2026-10-13&to=2026-10-14&links=-"));
        Assert.Equal(0, r.GetProperty("preview").GetProperty("violations").GetArrayLength());

        // 단계 목표일·휴일 안내
        r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=stage&id={x.StageId}&date=2027-01-04&cascade=true&works=true"));
        Assert.True(r.GetProperty("preview").GetProperty("shift").GetInt32() > 0);
        r = await OkJsonAsync(await client.GetAsync("/Projects/Detail?handler=Preview&kind=day&date=2026-10-09"));
        Assert.Contains("한글날", r.GetProperty("preview").GetProperty("notes")[0].GetString());

        // 잘못된 요청은 400 과 필드
        var bad = await client.GetAsync($"/Projects/Detail?handler=Preview&kind=work&id={x.B}&from=2026-10-13&to=2026-10-14&links=x:fs");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("predecessors", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Projects/Detail?handler=Preview&kind=work&id=" + x.B)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Projects/Detail?handler=Preview&kind=nope")).StatusCode);
        Assert.Equal(before, Activities());
    }

    [Fact]
    public async Task 재계획은_미리본_목록과_사유로_저장하고_선행_연결은_유형과_간격을_JSON_으로_받는다()
    {
        var x = await EngineProjectAsync("HTTP 재계획");
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), x.Id);

        // 시작일이 지난 C 는 앱의 오늘(실제 날짜) 기준 예측으로 옮겨진다
        var r = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=Preview&kind=replan&id={x.Id}"));
        var moved = r.GetProperty("preview").GetProperty("moved").EnumerateArray().ToList();
        Assert.Contains(moved, m => m.GetProperty("id").GetInt32() == x.C && m.GetProperty("startLate").GetBoolean());
        var data = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={x.Id}");
        Assert.Equal(moved.Count, data.GetProperty("project").GetProperty("replanCount").GetInt32());
        var rows = moved.Select(m => new
        {
            workId = m.GetProperty("id").GetInt32(), version = m.GetProperty("version").GetInt32(),
            planStart = m.GetProperty("newStart").GetString(), planEnd = m.GetProperty("newEnd").GetString(),
        }).ToList();
        var version = data.GetProperty("project").GetProperty("version").GetInt32();

        var noReason = await client.SendAsync(Json(token, "Replan", new { projectId = x.Id, reason = "", version, rows }));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var project = (await OkJsonAsync(await client.SendAsync(Json(token, "Replan", new { projectId = x.Id, reason = "오늘 기준 재계획", version, rows })))).GetProperty("project");
        Assert.Equal(0, project.GetProperty("replanCount").GetInt32());
        var c = project.GetProperty("stages")[0].GetProperty("works").EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == x.C);
        Assert.Equal(moved.Single(m => m.GetProperty("id").GetInt32() == x.C).GetProperty("newStart").GetString(), c.GetProperty("planStart").GetString());

        // 선행 연결 JSON: [{predecessorId, lagDays, type}]
        var b = project.GetProperty("stages")[0].GetProperty("works").EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == x.B);
        project = (await OkJsonAsync(await client.SendAsync(Json(token, "SaveWork", new
        {
            id = x.B, stageId = x.StageId, title = "B", planStart = b.GetProperty("planStart").GetString(), planEnd = b.GetProperty("planEnd").GetString(),
            progress = 0, version = b.GetProperty("version").GetInt32(),
            links = new[] { new { predecessorId = x.A, lagDays = 1, type = "ss" } },
        })))).GetProperty("project");
        var link = project.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("to").GetInt32() == x.B);
        Assert.Equal(("ss", 1), (link.GetProperty("type").GetString(), link.GetProperty("lag").GetInt32()));
        Assert.Contains(project.GetProperty("activities").EnumerateArray(), a => a.GetProperty("type").GetString() == "linkChanged");
    }

    [Fact]
    public async Task 과제_간_단계_연결을_검색해_잇고_두_과제_화면에_보이며_해제한다()
    {
        var p = await EngineProjectAsync("HTTP 과제간 선행");
        var q = await EngineProjectAsync("HTTP 과제간 후행");
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), q.Id);

        var found = await OkJsonAsync(await client.GetAsync($"/Projects/Detail?handler=LinkTargets&id={q.Id}&q=" + Uri.EscapeDataString("HTTP 과제간")));
        var target = found.GetProperty("projects").EnumerateArray().Single();
        Assert.Equal(p.Id, target.GetProperty("id").GetInt32());
        Assert.Contains(target.GetProperty("stages").EnumerateArray(), st => st.GetProperty("id").GetInt32() == p.StageId);

        // 같은 날 목표(12-30)라 선행 다음 근무일보다 이르다 → 두 과제 모두 위반으로 본다
        var project = (await OkJsonAsync(await client.SendAsync(Json(token, "AddStageLink",
            new { stageId = q.StageId, otherStageId = p.StageId, role = "pred", lagDays = 0 })))).GetProperty("project");
        Assert.Equal(q.Id, project.GetProperty("id").GetInt32());
        Assert.Equal(1, project.GetProperty("linkViolations").GetInt32());
        var cross = project.GetProperty("stages")[0].GetProperty("crossLinks")[0];
        Assert.Equal(("pred", p.Id, true), (cross.GetProperty("role").GetString(), cross.GetProperty("projectId").GetInt32(), cross.GetProperty("violated").GetBoolean()));
        var other = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={p.Id}");
        Assert.Equal("succ", other.GetProperty("project").GetProperty("stages")[0].GetProperty("crossLinks")[0].GetProperty("role").GetString());

        var dup = await client.SendAsync(Json(token, "AddStageLink", new { stageId = q.StageId, otherStageId = p.StageId, role = "pred", lagDays = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Equal("otherStageId", (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        project = (await OkJsonAsync(await client.SendAsync(Json(token, "RemoveStageLink", new { id = cross.GetProperty("id").GetInt32(), projectId = q.Id })))).GetProperty("project");
        Assert.Equal(0, project.GetProperty("stages")[0].GetProperty("crossLinks").GetArrayLength());
    }
}
