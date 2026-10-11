using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 단계 일정 계획의 순수 규칙: 목표일 초안(Rules.DraftTargets), 근무일 간격(Rules.TargetGaps), 기본 기간 힌트(Reporting.StageLeadHints)
public class StagePlanRulesTests
{
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    // 2026-10-01 은 목요일. 10-03(토) 개천절, 10-05(월) 대체공휴일, 10-09(금) 한글날
    private static readonly WorkCalendar Cal = new(new[] { Day(10, 3), Day(10, 5), Day(10, 9) });

    [Fact]
    public void 초안은_앞_단계_목표일에서_근무일만큼_뒤로_주말과_공휴일을_건너뛴다()
    {
        var drafts = Rules.DraftTargets(Day(10, 1), new int?[] { 3, 2, null, 1, 0 }, Cal);

        Assert.Equal(Day(10, 7), drafts[0]);    // 10-02(금), 10-06(화), 10-07(수) — 주말·대체공휴일 건너뜀
        Assert.Equal(Day(10, 12), drafts[1]);   // 10-08(목), 10-12(월) — 한글날·주말 건너뜀
        Assert.Null(drafts[2]);                  // 기본 기간이 없으면 비운다
        Assert.Equal(Day(10, 13), drafts[3]);   // 빈 단계 앞의 마지막 목표일(10-12)부터 센다
        Assert.Equal(Day(10, 13), drafts[4]);   // 0 이면 앞 단계와 같은 날
    }

    [Fact]
    public void 착수일이_휴일이면_다음_근무일부터_센다()
    {
        Assert.Equal(Day(10, 6), Rules.DraftTargets(Day(10, 3), new int?[] { 1 }, Cal)[0]);   // 토요일 착수 → 화요일(월요일은 대체공휴일)
    }

    [Fact]
    public void 사람이_정한_줄은_그대로_두고_그_뒤_단계만_거기서_이어_센다()
    {
        var days = new int?[] { 3, 2, null, 1 };
        var kept = Rules.DraftTargets(Day(10, 1), days, Cal, new Dictionary<int, DateOnly?> { [1] = Day(10, 20) });
        Assert.Equal(new DateOnly?[] { Day(10, 7), Day(10, 20), null, Day(10, 21) }, kept);

        // 빈 값으로 정한 줄도 지킨다(초안으로 다시 채우지 않는다). 다음 단계는 착수일부터 센다
        var cleared = Rules.DraftTargets(Day(10, 1), days, Cal, new Dictionary<int, DateOnly?> { [0] = null });
        Assert.Null(cleared[0]);
        Assert.Equal(Day(10, 6), cleared[1]);
    }

    [Fact]
    public void 템플릿은_순서대로_계산하고_초안을_그대로_두면_간격이_기본_기간과_같다()
    {
        var templates = new[]
        {
            new StageTemplate { Id = 3, Name = "적용", SortOrder = 3, DefaultWorkDays = 5 },
            new StageTemplate { Id = 1, Name = "검토", SortOrder = 1, DefaultWorkDays = 3 },
            new StageTemplate { Id = 2, Name = "보고", SortOrder = 2 },
        };
        var drafts = Rules.DraftTargets(Day(10, 1), templates, Cal);
        Assert.Equal(new DateOnly?[] { Day(10, 7), null, Day(10, 15) }, drafts);   // 10-08, 10-12(한글날 건너뜀), 10-13, 10-14, 10-15

        // 초안과 간격은 같은 거리(앞 단계 목표일부터의 근무일)를 쓴다 — 기본 기간 = 간격
        var gaps = Rules.TargetGaps(Day(10, 1), drafts, Cal);
        Assert.Equal(new int?[] { 3, null, 5 }, gaps);
        var many = new int?[] { 10, 0, 7, null, 40, 1 };
        Assert.Equal(many, Rules.TargetGaps(Day(10, 12), Rules.DraftTargets(Day(10, 12), many, Cal), Cal));
    }

    [Fact]
    public void 간격은_앞에서_정한_마지막_목표일부터_세고_순서가_뒤바뀌면_음수다()
    {
        var gaps = Rules.TargetGaps(Day(10, 1), new DateOnly?[] { Day(10, 7), null, Day(10, 12), Day(10, 6) }, Cal);
        Assert.Equal(new int?[] { 3, null, 2, -3 }, gaps);
    }

    [Fact]
    public void 기본_기간은_0에서_999_근무일만_받는다()
    {
        Rules.ValidateDefaultWorkDays(null);
        Rules.ValidateDefaultWorkDays(0);
        Rules.ValidateDefaultWorkDays(Rules.MaxDefaultWorkDays);
        Assert.Equal("defaultWorkDays", Assert.Throws<RuleException>(() => Rules.ValidateDefaultWorkDays(-1)).Field);
        Assert.Equal("defaultWorkDays", Assert.Throws<RuleException>(() => Rules.ValidateDefaultWorkDays(1000)).Field);
    }

    [Fact]
    public void 추천_입고일은_저장_전_단계_순서로도_같은_규칙이다()
    {
        var stages = new List<Stage>
        {
            new() { Name = "검수", Kind = StageKind.Inspection, SortOrder = 1, TargetDate = Day(10, 20) },
            new() { Name = "입고", Kind = StageKind.Inbound, SortOrder = 2 },
        };
        Assert.Equal(Day(11, 1), Rules.InboundRecommendation(stages, 12));
        stages[0].CompletedDate = Day(10, 22);      // 완료일 우선
        Assert.Equal(Day(11, 3), Rules.InboundRecommendation(stages, 12));
        var p = new Project();
        stages.ForEach(p.Stages.Add);
        Assert.Equal(Rules.InboundRecommendation(stages, 12), Rules.InboundRecommendation(p, 12));

        // 아직 검증하지 않은 날짜(초안·미리보기의 9999-12-31 같은 오타)로 불려도 넘치지 않는다 — 추천하지 않을 뿐
        stages[0].CompletedDate = null;
        stages[0].TargetDate = DateOnly.MaxValue.AddDays(-5);
        Assert.Null(Rules.InboundRecommendation(stages, 12));
        Assert.Equal(DateOnly.MaxValue, Rules.InboundRecommendation(stages, 5));
    }

    private static Project Done(int categoryId, ProjectState state, params (string Name, DateOnly Done)[] stages)
    {
        var p = new Project { CategoryId = categoryId, State = state, StartDate = Day(10, 1) };
        var order = 0;
        foreach (var (name, done) in stages) p.Stages.Add(new Stage { Name = name, SortOrder = ++order, CompletedDate = done });
        return p;
    }

    [Fact]
    public void 기본_기간_힌트는_완료_과제의_같은_유형_같은_이름_단계를_근무일로_평균한다()
    {
        var projects = new List<Project>
        {
            Done(1, ProjectState.Done, ("검토", Day(10, 7)), ("보고", Day(10, 12)), ("적용", Day(10, 14))),   // 3 · 2 · 2
            Done(1, ProjectState.Done, ("검토", Day(10, 8)), ("보고", Day(10, 8)), ("적용", Day(10, 13))),    // 4 · 0(뺌) · 2
            // 순서를 거슬러 완료: 보고(음수)는 빼고, 적용은 가장 늦은 완료일(검토 10-14)부터 센다 → 2 (10-06 부터면 7)
            Done(1, ProjectState.Done, ("검토", Day(10, 14)), ("보고", Day(10, 6)), ("적용", Day(10, 16))),   // 7 · 음수(뺌) · 2
            Done(1, ProjectState.Active, ("검토", Day(10, 30)), ("보고", Day(11, 2)), ("적용", Day(11, 3))),  // 진행 과제는 보지 않는다
            Done(2, ProjectState.Done, ("검토", Day(10, 2)), ("보고", Day(10, 6))),                            // 다른 유형
        };

        var hints = Reporting.StageLeadHints(projects, Cal);

        Assert.Equal(new StageLeadHint(4.7, 3), hints[(1, "검토")]);      // (3 + 4 + 7) / 3
        Assert.Equal(new StageLeadHint(2.0, 3), hints[(1, "적용")]);
        Assert.False(hints.ContainsKey((1, "보고")));                     // 남은 표본 1건 — 3건 미만은 보이지 않는다
        Assert.False(hints.ContainsKey((2, "검토")));
    }
}

// 단계 일정 계획 서비스: 단계 일정 표 저장·미리보기, 등록 시 초기 목표일, 목표일 초안, 관리 화면 기본 기간
public class StagePlanServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public StagePlanServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string category = "물류", string corporation = "한국", List<StageTargetInput>? targets = null)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == corporation);
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 1), null, null, null, null, StageTargets: targets));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    private async Task SaveAsync(int projectId, string? reason, params StageScheduleRow[] rows)
    {
        using var s = _db.NewScope();
        await s.Projects.SaveStageScheduleAsync(new StageScheduleInput(projectId, rows.ToList(), reason));
    }

    private static StageScheduleRow Row(StageView s, DateOnly? target, DateOnly? completed = null, int? version = null) =>
        new(s.Id, version ?? s.Version, target, completed);

    private async Task<List<Activity>> ActivitiesAsync(int projectId)
    {
        using var s = _db.NewScope();
        return await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task 단계_일정_표는_처음_정하는_목표일을_사유_없이_한_묶음으로_저장하고_간격을_준다()
    {
        var id = await NewProjectAsync("일정 표 처음 설정");
        var st = (await ViewAsync(id)).Stages;          // 물류: 검토 → 보고 → 적용

        await SaveAsync(id, null, Row(st[0], Day(10, 7)), Row(st[1], Day(10, 14)), Row(st[2], Day(10, 21)));

        var after = await ViewAsync(id);
        Assert.Equal(new DateOnly?[] { Day(10, 7), Day(10, 14), Day(10, 21) }, after.Stages.Select(x => x.TargetDate));
        Assert.All(after.Stages, x => Assert.Equal(1, x.Version));
        Assert.Equal(new int?[] { 3, 4, 5 }, after.Stages.Select(x => x.GapWorkDays));     // 착수 10-01 부터 근무일

        var sets = (await ActivitiesAsync(id)).Where(a => a.Text.Contains("목표일 설정")).ToList();
        Assert.Equal(3, sets.Count);
        Assert.All(sets, a => { Assert.Equal(ActivityType.Edited, a.Type); Assert.Null(a.Reason); Assert.NotNull(a.StageId); });
        Assert.Single(sets.Select(a => a.BatchId).Distinct());
        Assert.NotNull(sets[0].BatchId);
    }

    [Fact]
    public async Task 이미_정한_목표일을_바꾸면_사유가_필요하고_바꾼_단계만_한_묶음으로_올린다()
    {
        var id = await NewProjectAsync("일정 표 변경 사유");
        var st = (await ViewAsync(id)).Stages;
        await SaveAsync(id, null, Row(st[0], Day(10, 7)), Row(st[2], Day(10, 21)));
        st = (await ViewAsync(id)).Stages;

        // 사유 없이 기존 목표일을 바꾸고(검토) 지우면(적용) 거절 — 처음 정하는 보고까지 아무것도 바뀌지 않는다
        var ex = await Assert.ThrowsAsync<RuleException>(() =>
            SaveAsync(id, null, Row(st[0], Day(10, 8)), Row(st[1], Day(10, 14)), Row(st[2], null)));
        Assert.Equal("reason", ex.Field);
        var unchanged = await ViewAsync(id);
        Assert.Equal(new DateOnly?[] { Day(10, 7), null, Day(10, 21) }, unchanged.Stages.Select(x => x.TargetDate));
        Assert.Equal(new[] { 1, 0, 1 }, unchanged.Stages.Select(x => x.Version));

        var before = (await ActivitiesAsync(id)).Count;
        await SaveAsync(id, "고객 일정 변경", Row(st[0], Day(10, 8)), Row(st[1], Day(10, 14)), Row(st[2], null));

        var after = await ViewAsync(id);
        Assert.Equal(new DateOnly?[] { Day(10, 8), Day(10, 14), null }, after.Stages.Select(x => x.TargetDate));
        Assert.Equal(new[] { 2, 1, 2 }, after.Stages.Select(x => x.Version));
        var added = (await ActivitiesAsync(id)).Skip(before).ToList();
        Assert.Equal(3, added.Count);
        Assert.Single(added.Select(a => a.BatchId).Distinct());
        var changed = added.Where(a => a.Type == ActivityType.TargetChanged).ToList();
        Assert.Equal(2, changed.Count);
        Assert.All(changed, a => Assert.Equal("고객 일정 변경", a.Reason));
        Assert.Contains(changed, a => a.Text.Contains("2026-10-21 → 미정"));
        Assert.Null(added.Single(a => a.Type == ActivityType.Edited).Reason);    // 보고: 처음 설정은 사유 없이

        // 바꾼 것이 없으면 아무것도 남기지 않는다
        st = after.Stages;
        await SaveAsync(id, null, Row(st[0], Day(10, 8)));
        Assert.Equal(before + 3, (await ActivitiesAsync(id)).Count);
    }

    [Fact]
    public async Task 다른_사람이_먼저_고친_단계가_있으면_충돌로_거절하고_아무것도_바꾸지_않는다()
    {
        var id = await NewProjectAsync("일정 표 충돌");
        var st = (await ViewAsync(id)).Stages;
        await SaveAsync(id, null, Row(st[1], Day(10, 14)));     // 다른 화면이 보고를 먼저 저장

        await Assert.ThrowsAsync<ConflictException>(() =>
            SaveAsync(id, null, Row(st[0], Day(10, 7)), Row(st[1], Day(10, 15), version: st[1].Version)));
        var after = await ViewAsync(id);
        Assert.Null(after.Stages[0].TargetDate);
        Assert.Equal(0, after.Stages[0].Version);

        // 지워진 단계도 충돌로 본다(화면이 최신으로 다시 맞춘다)
        using (var s = _db.NewScope()) await s.Projects.DeleteStageAsync(st[2].Id);
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(id, null, Row(st[2], Day(10, 21))));
    }

    [Fact]
    public async Task 완료는_체크리스트_규칙을_거치고_모든_단계를_끝내면_과제가_완료되고_취소하면_다시_진행된다()
    {
        // 물류 자동화: 검수 단계에 체크 항목 3개
        var auto = await NewProjectAsync("일정 표 체크리스트", "물류 자동화");
        var st = (await ViewAsync(auto)).Stages;
        var inspection = st.Single(x => x.Name == "검수");
        var ex = await Assert.ThrowsAsync<RuleException>(() =>
            SaveAsync(auto, null, Row(st[0], st[0].TargetDate, Day(10, 7)), Row(inspection, inspection.TargetDate, Day(10, 8))));
        Assert.Equal($"completedDate.{inspection.Id}", ex.Field);
        Assert.Contains("'검수' 단계는 체크리스트 3개", ex.Message);
        Assert.Null((await ViewAsync(auto)).Stages[0].CompletedDate);         // 같이 보낸 검토 완료도 저장되지 않음

        var id = await NewProjectAsync("일정 표 완료");
        st = (await ViewAsync(id)).Stages;
        await SaveAsync(id, null, Row(st[0], null, Day(10, 2)), Row(st[1], null, Day(10, 6)), Row(st[2], null, Day(10, 7)));
        var done = await ViewAsync(id);
        Assert.Equal("done", done.State);
        var acts = await ActivitiesAsync(id);
        Assert.Equal(3, acts.Count(a => a.Type == ActivityType.StageCompleted));
        Assert.Single(acts.Where(a => a.Type == ActivityType.StageCompleted).Select(a => a.BatchId).Distinct());
        Assert.Contains(acts, a => a.Type == ActivityType.StateChanged && a.Text.Contains("모든 단계 완료"));

        await SaveAsync(id, null, Row(done.Stages[2], null, null));
        Assert.Equal("active", (await ViewAsync(id)).State);
        Assert.Contains(await ActivitiesAsync(id), a => a.Type == ActivityType.StageReopened && a.BatchId != null);
    }

    [Fact]
    public async Task 완료를_취소하면_그_단계의_미착수_작업을_늦어진_선행에_맞춰_같은_묶음으로_민다()
    {
        var id = await NewProjectAsync("일정 표 완료 취소");
        var st = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, st[0].Id, "선행 A", null, null, Day(10, 12), Day(10, 14), null, null, 0, null, null));
        var a = (await ViewAsync(id)).Stages[0].Works.Single();
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, st[1].Id, "후행 B", null, null, Day(10, 15), Day(10, 16), null, null, 0, null, null,
                Predecessors: new List<int> { a.Id }));

        st = (await ViewAsync(id)).Stages;
        await SaveAsync(id, null, Row(st[1], null, Day(10, 9)));           // 보고 완료 → B 는 밀기 대상이 아니다
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(a.Id, st[0].Id, "선행 A", null, null, Day(10, 12), Day(10, 20), null, null, 0, null, "범위 확대", a.Version));
        Assert.Equal(Day(10, 15), (await ViewAsync(id)).Stages[1].Works.Single().PlanStart);

        st = (await ViewAsync(id)).Stages;
        await SaveAsync(id, null, Row(st[1], null, null));                 // 완료 취소
        var b = (await ViewAsync(id)).Stages[1].Works.Single();
        Assert.Equal((Day(10, 21), Day(10, 22)), (b.PlanStart, b.PlanEnd));
        await ScheduleAssert.AssertSchedulableAsync(_db, id);
        var acts = await ActivitiesAsync(id);
        var reopened = acts.Last(x => x.Type == ActivityType.StageReopened);
        var moved = acts.Last(x => x.Type == ActivityType.WorkPlanChanged && x.WorkItemId == b.Id);
        Assert.Equal(reopened.BatchId, moved.BatchId);
        Assert.Equal("단계 완료 취소로 선행 일정에 맞춤", moved.Reason);
    }

    [Fact]
    public async Task 범위를_벗어난_날짜는_표의_그_칸을_가리켜_거절한다()
    {
        var id = await NewProjectAsync("일정 표 범위");
        var st = (await ViewAsync(id)).Stages;
        var ex = await Assert.ThrowsAsync<RuleException>(() => SaveAsync(id, null, Row(st[0], Day(10, 7)), Row(st[1], new DateOnly(2620, 10, 1))));
        Assert.Equal($"targetDate.{st[1].Id}", ex.Field);
        Assert.Contains("'보고' 목표일", ex.Message);
        ex = await Assert.ThrowsAsync<RuleException>(() => SaveAsync(id, null, Row(st[2], null, new DateOnly(1999, 1, 1))));
        Assert.Equal($"completedDate.{st[2].Id}", ex.Field);
        Assert.Null((await ViewAsync(id)).Stages[0].TargetDate);
    }

    [Fact]
    public async Task 등록할_때_정한_목표일은_사유_없이_목표일_설정으로_기록된다()
    {
        int[] templates;
        using (var s = _db.NewScope())
            templates = await s.Db.StageTemplates.Where(t => t.Category!.Name == "물류").OrderBy(t => t.SortOrder).Select(t => t.Id).ToArrayAsync();

        var id = await NewProjectAsync("등록 초기 목표일", targets: new()
        {
            new(templates[0], Day(10, 7)), new(templates[1], null), new(templates[2], Day(10, 21)),
        });
        var view = await ViewAsync(id);
        Assert.Equal(new DateOnly?[] { Day(10, 7), null, Day(10, 21) }, view.Stages.Select(x => x.TargetDate));
        var sets = (await ActivitiesAsync(id)).Where(a => a.Text.Contains("목표일 설정")).ToList();
        Assert.Equal(new[] { "[검토] 목표일 설정 2026-10-07", "[적용] 목표일 설정 2026-10-21" }, sets.Select(a => a.Text));
        Assert.All(sets, a => { Assert.Equal(ActivityType.Edited, a.Type); Assert.Null(a.Reason); });
        Assert.Equal(0, view.TargetChanges);

        // 범위를 벗어난 초기 목표일은 그 줄을 가리켜 거절하고 과제를 만들지 않는다
        var ex = await Assert.ThrowsAsync<RuleException>(() =>
            NewProjectAsync("등록 범위 밖", targets: new() { new(templates[1], new DateOnly(2620, 1, 1)) }));
        Assert.Equal($"stageTargets.{templates[1]}", ex.Field);
        using var check = _db.NewScope();
        Assert.False(await check.Db.Projects.AnyAsync(p => p.Title == "등록 범위 밖"));
    }

    [Fact]
    public async Task 초안은_기본_기간으로_계산하고_고친_줄_뒤만_다시_세며_법인을_고르면_추천_입고일을_준다()
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.Include(c => c.StageTemplates).FirstAsync(c => c.Name == "물류 자동화");
        var ids = cat.StageTemplates.OrderBy(t => t.SortOrder).Select(t => t.Id).ToList();
        var vn = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "동남아");

        // 착수 10-12(월), 기본 기간 검토 10 · 품의 10 · 발주 5 (샘플 기준 정보)
        var draft = await s.Projects.DraftAsync(cat.Id, Day(10, 12), null);
        Assert.Equal(8, draft.Rows.Count);
        Assert.Null(draft.OceanLeadDays);
        Assert.Equal(new DateOnly?[] { Day(10, 26), Day(11, 9), Day(11, 16) }, draft.Rows.Take(3).Select(r => r.TargetDate));
        Assert.Equal(new int?[] { 10, 10, 5 }, draft.Rows.Take(3).Select(r => r.GapWorkDays));
        var inbound = draft.Rows.Single(r => r.Kind == "inbound");
        Assert.Null(inbound.DefaultWorkDays);
        Assert.Null(inbound.TargetDate);                   // 기본 기간이 없는 입고는 비운다
        Assert.Null(inbound.InboundHint);                  // 법인을 고르기 전에는 추천일이 없다
        Assert.All(draft.Rows, r => Assert.False(r.Kept));

        // 사람이 검토를 10-30 으로 고치면 품의부터 그 날짜에서 다시 센다. 동남아(해상 12일)을 고르면 입고에 추천일
        var kept = await s.Projects.DraftAsync(cat.Id, Day(10, 12), vn.Id, new Dictionary<int, DateOnly?> { [ids[0]] = Day(10, 30) });
        Assert.Equal(new DateOnly?[] { Day(10, 30), Day(11, 13) }, kept.Rows.Take(2).Select(r => r.TargetDate));
        Assert.True(kept.Rows[0].Kept);
        Assert.Equal(12, kept.OceanLeadDays);
        var inspection = kept.Rows.Single(r => r.Kind == "inspection");
        Assert.Equal(inspection.TargetDate!.Value.AddDays(12), kept.Rows.Single(r => r.Kind == "inbound").InboundHint);

        Assert.Equal("categoryId", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.DraftAsync(0, Day(10, 12), null))).Field);
        Assert.Equal("startDate", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.DraftAsync(cat.Id, new DateOnly(2620, 1, 1), null))).Field);
    }

    [Fact]
    public async Task 미리보기는_저장하지_않은_날짜로_간격과_추천_입고일을_다시_계산한다()
    {
        var id = await NewProjectAsync("일정 표 미리보기", "물류 자동화", "동남아");
        var st = (await ViewAsync(id)).Stages;
        var inspection = st.Single(x => x.Name == "검수");
        var inbound = st.Single(x => x.Name == "입고");

        using var s = _db.NewScope();
        var rows = await s.Projects.SchedulePreviewAsync(id, new()
        {
            new(st[0].Id, Day(10, 7), null),
            new(inspection.Id, Day(10, 20), null),
        });
        Assert.Equal(st.Select(x => x.Id), rows.Select(r => r.StageId));
        Assert.Equal(3, rows[0].GapWorkDays);
        Assert.Equal(Day(11, 1), rows.Single(r => r.StageId == inbound.Id).InboundHint);    // 10-20 + 해상 12일
        Assert.Null(rows.Single(r => r.StageId == inspection.Id).InboundHint);
        Assert.Null((await ViewAsync(id)).Stages[0].TargetDate);                           // 저장하지 않는다

        // 휴일 안내(TargetNote)는 서버 달력으로 그 단계 줄에 붙는다 — 화면은 이 값만 그린다. 근무일·목표일 없음은 null
        rows = await s.Projects.SchedulePreviewAsync(id, new()
        {
            new(st[0].Id, Day(10, 9), null),        // 한글날
            new(st[1].Id, Day(10, 10), null),       // 토요일
            new(st[2].Id, Day(10, 12), null),       // 근무일
        });
        Assert.Equal(st.Select(x => x.Id), rows.Select(r => r.StageId));
        Assert.Equal("목표일이 휴일(한글날)입니다.", rows[0].TargetNote);
        Assert.Equal("목표일이 휴일입니다.", rows[1].TargetNote);
        Assert.Null(rows[2].TargetNote);
        Assert.All(rows.Skip(3), r => Assert.Null(r.TargetNote));
    }

    [Fact]
    public async Task 관리_화면에서_기본_기간을_저장하고_완료_과제가_3건이면_과거_평균을_힌트로_준다()
    {
        using (var s = _db.NewScope())
        {
            var template = await s.Db.StageTemplates.Include(t => t.Category).FirstAsync(t => t.Category!.Name == "Simulation" && t.Name == "모델링");
            MasterSave Save(int? days) => new("category", 2, template.Id, template.CategoryId, "모델링", null, null, "normal", null, null, null, days);
            await s.Masters.SaveAsync(Save(12));
            Assert.Equal("defaultWorkDays", (await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save(-1)))).Field);
            Assert.Equal("defaultWorkDays", (await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save(1000)))).Field);
        }

        MasterNode Node(List<MasterNode> nodes, string name) =>
            nodes.Single(n => n.Level == 2 && n.Name == name && n.ParentId == nodes.Single(c => c.Level == 1 && c.Name == "Simulation").Id);

        using (var s = _db.NewScope())
        {
            var nodes = await s.Masters.ListAsync("category");
            Assert.Equal(12, Node(nodes, "모델링").DefaultWorkDays);
            Assert.Null(Node(nodes, "모델링").Lead);       // 완료 과제가 아직 없다
        }

        // Simulation 과제 3건을 모두 완료: 데이터 수집은 착수(10-01)부터 1·2·3 근무일(10-02·10-06·10-07, 10-05 는 대체공휴일)
        foreach (var (title, collected) in new[] { ("힌트 1", Day(10, 2)), ("힌트 2", Day(10, 6)), ("힌트 3", Day(10, 7)) })
        {
            var id = await NewProjectAsync(title, "Simulation");
            var st = (await ViewAsync(id)).Stages;
            await SaveAsync(id, null, Row(st[0], null, collected), Row(st[1], null, Day(10, 14)), Row(st[2], null, Day(10, 15)), Row(st[3], null, Day(10, 16)));
            Assert.Equal("done", (await ViewAsync(id)).State);
        }
        using (var s = _db.NewScope())
        {
            var nodes = await s.Masters.ListAsync("category");
            Assert.Equal(new StageLeadHint(2.0, 3), Node(nodes, "데이터 수집").Lead);    // (1 + 2 + 3) / 3
        }
    }
}
