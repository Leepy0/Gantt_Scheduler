using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 단계 일정 계획 HTTP 확인: 상세의 단계 일정 표(저장·미리보기), 등록 화면의 목표일 초안·초기 목표일, 관리 화면 기본 기간
public partial class HttpTests
{
    // 다른 시험이 바꾸지 않는 전용 과제 (착수 2026-10-01, 물류: 검토 → 보고 → 적용)
    private async Task<int> PlanProjectAsync(string title, string category = "물류", string corporation = "한국")
    {
        using var s = _factory.Db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == corporation);
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, new DateOnly(2026, 10, 1), null, null, null, null));
    }

    [Fact]
    public async Task 단계_일정_표는_JSON_으로_한_번에_저장하고_사유가_없거나_버전이_다르면_거절한다()
    {
        var id = await PlanProjectAsync("HTTP 단계 일정 표");
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), id);
        var stages = (await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={id}")).GetProperty("project").GetProperty("stages");
        int Sid(int i) => stages[i].GetProperty("id").GetInt32();

        // 처음 정하는 목표일: 사유 없이 저장되고 최신 과제(간격 포함)를 돌려준다
        var ok = await client.SendAsync(Json(token, "SaveSchedule", new
        {
            projectId = id, reason = (string?)null,
            rows = new object[]
            {
                new { stageId = Sid(0), version = 0, targetDate = "2026-10-07", completedDate = (string?)null },
                new { stageId = Sid(1), version = 0, targetDate = "2026-10-14", completedDate = (string?)null },
            },
        }));
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, body);
        var saved = JsonDocument.Parse(body).RootElement.GetProperty("project").GetProperty("stages");
        Assert.Equal("2026-10-07", saved[0].GetProperty("targetDate").GetString());
        Assert.Equal(3, saved[0].GetProperty("gapWorkDays").GetInt32());
        Assert.Equal(4, saved[1].GetProperty("gapWorkDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved[2].GetProperty("gapWorkDays").ValueKind);

        // 이미 정한 목표일을 사유 없이 바꾸면 400 과 사유 칸
        var noReason = await client.SendAsync(Json(token, "SaveSchedule", new
        {
            projectId = id, rows = new[] { new { stageId = Sid(0), version = 1, targetDate = "2026-10-08", completedDate = (string?)null } },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        // 연 뒤에 다른 화면이 먼저 저장했으면 409
        var stale = await client.SendAsync(Json(token, "SaveSchedule", new
        {
            projectId = id, reason = "일정 조정", rows = new[] { new { stageId = Sid(0), version = 0, targetDate = "2026-10-08", completedDate = (string?)null } },
        }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True((await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conflict").GetBoolean());

        // 미리보기: 저장하지 않고 고친 날짜로 간격을 다시 계산한다
        var preview = await client.SendAsync(Json(token, "SchedulePreview", new
        {
            projectId = id, rows = new[] { new { stageId = Sid(2), targetDate = "2026-10-21", completedDate = (string?)null } },
        }));
        var rows = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows");
        Assert.Equal(Sid(2), rows[2].GetProperty("stageId").GetInt32());
        Assert.Equal(5, rows[2].GetProperty("gapWorkDays").GetInt32());
        var last = Sid(2);
        using var s = _factory.Db.NewScope();
        Assert.Null((await s.Db.Stages.FirstAsync(x => x.Id == last)).TargetDate);
    }

    [Fact]
    public async Task 초안과_미리보기는_범위_밖_날짜를_그_줄을_가리켜_400_으로_알린다()
    {
        var client = await RegisteredClientAsync();
        int categoryId, locationId, inspection, before;
        using (var s = _factory.Db.NewScope())
        {
            var cat = await s.Db.Categories.Include(c => c.StageTemplates).FirstAsync(c => c.Name == "물류 자동화");
            categoryId = cat.Id;
            var ordered = cat.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();
            var i = ordered.FindIndex(t => t.Kind == GanttScheduler.Domain.StageKind.Inspection);
            inspection = ordered[i].Id;
            before = ordered[i - 1].Id;
            locationId = (await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "베트남")).Id;   // 해상 운송 12일 — 추천 입고일을 계산한다
        }

        // 사람이 고친 목표일이 연도 9999 — 검수일 + 해상 운송이 날짜 범위를 넘어 500 이 나던 경로. 등록할 때와 같은 범위 검사로 막는다
        foreach (var (template, date) in new[] { (inspection, "9999-12-31"), (before, "9999-12-20") })
        {
            var res = await client.GetAsync($"/Projects/Edit?handler=Draft&categoryId={categoryId}&start=2026-10-12&locationId={locationId}&keep={template}:{date}");
            var text = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, text);
            Assert.Equal($"stageTargets.{template}", JsonDocument.Parse(text).RootElement.GetProperty("field").GetString());
        }

        // 상세의 단계 일정 표 미리보기도 저장과 같은 범위 검사 (화면은 실패를 조용히 넘기고 저장할 때 이유를 보인다)
        var id = await PlanProjectAsync("HTTP 미리보기 범위 밖", "물류 자동화", "베트남");
        var (detail, token) = await DetailAsync(client, id);
        var stages = (await detail.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={id}")).GetProperty("project").GetProperty("stages");
        var sid = stages.EnumerateArray().First(x => x.GetProperty("kind").GetString() == "inspection").GetProperty("id").GetInt32();
        var preview = await detail.SendAsync(Json(token, "SchedulePreview", new
        {
            projectId = id, rows = new[] { new { stageId = sid, targetDate = "9999-12-31", completedDate = (string?)null } },
        }));
        var body = await preview.Content.ReadAsStringAsync();
        Assert.True(preview.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Equal($"targetDate.{sid}", JsonDocument.Parse(body).RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task 등록_화면은_목표일_초안을_주고_고친_값으로_등록하면_목표일로_정해진다()
    {
        var client = await RegisteredClientAsync();
        int categoryId, locationId;
        List<int> templates;
        using (var s = _factory.Db.NewScope())
        {
            var cat = await s.Db.Categories.Include(c => c.StageTemplates).FirstAsync(c => c.Name == "물류");
            categoryId = cat.Id;
            templates = cat.StageTemplates.OrderBy(t => t.SortOrder).Select(t => t.Id).ToList();
            locationId = (await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국")).Id;
        }

        // 초안: 착수 10-12(월), 기본 기간 검토 10 · 보고 5 · 적용 15. 사람이 보고를 비웠으면(keep) 적용은 검토부터 센다
        var draft = await client.GetFromJsonAsync<JsonElement>(
            $"/Projects/Edit?handler=Draft&categoryId={categoryId}&start=2026-10-12&locationId={locationId}&keep={templates[1]}:");
        var rows = draft.GetProperty("draft").GetProperty("rows");
        Assert.Equal("2026-10-26", rows[0].GetProperty("targetDate").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("targetDate").ValueKind);
        Assert.True(rows[1].GetProperty("kept").GetBoolean());
        Assert.Equal("2026-11-16", rows[2].GetProperty("targetDate").GetString());      // 10-26 + 15근무일
        Assert.Equal(15, rows[2].GetProperty("gapWorkDays").GetInt32());

        var bad = await client.GetAsync($"/Projects/Edit?handler=Draft&categoryId={categoryId}&start=2620-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // 등록: 표의 값(Form.StageTargets)을 그대로 목표일로 정한다
        var html = await client.GetStringAsync("/Projects/Edit");
        var res = await client.PostAsync("/Projects/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Title"] = "HTTP 초안 등록", ["Form.CategoryId"] = categoryId.ToString(), ["Form.LocationId"] = locationId.ToString(),
            ["Form.StartDate"] = "2026-10-12",
            ["Form.StageTargets[0].TemplateId"] = templates[0].ToString(), ["Form.StageTargets[0].TargetDate"] = "2026-10-26",
            ["Form.StageTargets[1].TemplateId"] = templates[1].ToString(), ["Form.StageTargets[1].TargetDate"] = "", ["Form.StageTargets[1].Kept"] = "true",
            ["Form.StageTargets[2].TemplateId"] = templates[2].ToString(), ["Form.StageTargets[2].TargetDate"] = "2026-11-20",
            ["__RequestVerificationToken"] = TokenOf(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        using (var s = _factory.Db.NewScope())
        {
            var p = await s.Db.Projects.Include(x => x.Stages).Include(x => x.Activities).FirstAsync(x => x.Title == "HTTP 초안 등록");
            Assert.Equal(new DateOnly?[] { new(2026, 10, 26), null, new(2026, 11, 20) }, p.Stages.OrderBy(x => x.SortOrder).Select(x => x.TargetDate));
            Assert.Equal(2, p.Activities.Count(a => a.Text.Contains("목표일 설정") && a.Reason == null));
        }

        // 범위 밖 목표일은 그 줄을 가리켜 다시 그리고, 사람이 고친 줄 표시(Kept)를 지킨다
        html = await client.GetStringAsync("/Projects/Edit");
        res = await client.PostAsync("/Projects/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Title"] = "HTTP 초안 범위 밖", ["Form.CategoryId"] = categoryId.ToString(), ["Form.LocationId"] = locationId.ToString(),
            ["Form.StartDate"] = "2026-10-12",
            ["Form.StageTargets[0].TemplateId"] = templates[0].ToString(), ["Form.StageTargets[0].TargetDate"] = "2620-10-26", ["Form.StageTargets[0].Kept"] = "true",
            ["__RequestVerificationToken"] = TokenOf(html),
        }));
        var again = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var meta = PageData(again);
        Assert.Equal($"stageTargets.{templates[0]}", meta.GetProperty("errorField").GetString());
        Assert.True(meta.GetProperty("form").GetProperty("stageTargets")[0].GetProperty("kept").GetBoolean());

        // 수정 화면에는 초안 표가 없다 (상세의 단계 일정 편집을 쓴다)
        Assert.DoesNotContain("id=\"draft\"", await client.GetStringAsync("/Projects/Edit?id=1"));
        Assert.Contains("stage-draft.js", html);
    }

    [Fact]
    public async Task 관리_화면의_단계_템플릿에는_기본_기간이_실린다()
    {
        var client = await RegisteredClientAsync();
        var nodes = (await client.GetFromJsonAsync<JsonElement>("/Admin?handler=Data&kind=category")).GetProperty("nodes");
        var review = nodes.EnumerateArray().First(n => n.GetProperty("level").GetInt32() == 2 && n.GetProperty("name").GetString() == "제작");
        Assert.Equal(40, review.GetProperty("defaultWorkDays").GetInt32());       // 샘플 기준 정보: 물류 자동화 제작 40근무일
        Assert.Equal(JsonValueKind.Null, review.GetProperty("lead").ValueKind);    // 완료 과제 표본이 3건 미만
    }
}
