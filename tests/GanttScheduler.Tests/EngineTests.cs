using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 일정 엔진 확장 — 의존 유형(SS·FF)·간격, 오늘 기준 예측(ForwardPass), 과제 간 단계 의존 판정 (순수 규칙)
public class EngineRulesTests
{
    private static readonly WorkCalendar Cal = WorkCalendar.WeekendsOnly;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static WorkItem W(int id, string title, DateOnly start, DateOnly end, DateOnly? actualStart = null, DateOnly? actualEnd = null) =>
        new() { Id = id, Title = title, PlanStart = start, PlanEnd = end, ActualStart = actualStart, ActualEnd = actualEnd };

    private static Project ProjectOf(DateOnly? target, params WorkItem[] works)
    {
        var p = new Project { State = ProjectState.Active, StartDate = Day(10, 1) };
        var stage = new Stage { Id = 1, Name = "단계", SortOrder = 1, TargetDate = target };
        foreach (var w in works) stage.Works.Add(w);
        p.Stages.Add(stage);
        return p;
    }

    private static WorkLink Link(Project p, WorkItem from, WorkItem to, LinkType type = LinkType.FS, int lag = 0)
    {
        var l = new WorkLink { PredecessorId = from.Id, SuccessorId = to.Id, Type = type, LagDays = lag };
        p.Links.Add(l);
        return l;
    }

    [Fact]
    public void 유형별_가장_이른_시작일은_EarliestStart_하나가_정한다()
    {
        // 선행 10-12(월)~10-14(수), 후행 근무일 3일
        DateOnly Earliest(LinkType type, int lag) =>
            Rules.EarliestStart(new WorkLink { Type = type, LagDays = lag }, Day(10, 12), Day(10, 14), 3, Cal);

        Assert.Equal(Day(10, 15), Earliest(LinkType.FS, 0));      // 끝난 다음 근무일
        Assert.Equal(Day(10, 19), Earliest(LinkType.FS, 2));      // + 간격 2근무일 (주말 건너뜀)
        Assert.Equal(Day(10, 12), Earliest(LinkType.SS, 0));      // 같은 날 시작해도 된다
        Assert.Equal(Day(10, 14), Earliest(LinkType.SS, 2));
        Assert.Equal(Day(10, 12), Earliest(LinkType.FF, 0));      // 10-14 에 끝나려면 3근무일 전 10-12 시작
        Assert.Equal(Day(10, 13), Earliest(LinkType.FF, 1));      // 10-15 에 끝나려면 10-13 시작

        // 선행이 휴일(토요일)에 시작하면 다음 근무일부터 센다
        Assert.Equal(Day(10, 12), Rules.EarliestStart(new WorkLink { Type = LinkType.SS }, Day(10, 10), Day(10, 14), 1, Cal));
        // FF 의 끝 기준: 선행 종료일 + 간격, 근무일로 맞춤
        Assert.Equal(Day(10, 16), Rules.EarliestEnd(new WorkLink { Type = LinkType.FF, LagDays = 2 }, Day(10, 14), Cal));
        Assert.Equal(Day(10, 12), Rules.EarliestEnd(new WorkLink { Type = LinkType.FF }, Day(10, 10), Cal));
    }

    [Fact]
    public void 같이_시작은_선행_시작에_맞춰_밀고_여유도_같은_기준으로_센다()
    {
        var a = W(1, "A", Day(10, 12), Day(10, 16));
        var b = W(2, "B", Day(10, 12), Day(10, 13));
        var p = ProjectOf(Day(10, 30), a, b);
        var link = Link(p, a, b, LinkType.SS, 1);

        Assert.True(Rules.Violates(link, a, b, Cal));             // 선행 시작 + 1근무일(10-13) 전에 시작
        var moves = Rules.Reschedule(p, Cal, new[] { a });
        var m = Assert.Single(moves);
        Assert.Equal((Day(10, 13), Day(10, 14)), (m.NewStart, m.NewEnd));   // 근무일 2일 그대로

        // 선행이 한 주 밀리면 시작 기준으로 따라간다
        b.PlanStart = Day(10, 13);
        b.PlanEnd = Day(10, 14);
        a.PlanStart = Day(10, 19);
        a.PlanEnd = Day(10, 23);
        m = Assert.Single(Rules.Reschedule(p, Cal, new[] { a }));
        Assert.Equal((Day(10, 20), Day(10, 21)), (m.NewStart, m.NewEnd));

        // 여유: 후행이 단계 목표일까지 꽉 차면 같이 시작하는 선행도 여유 0(밀리면 후행이 밀린다)
        var c = W(3, "C", Day(10, 12), Day(10, 14));
        var d = W(4, "D", Day(10, 12), Day(10, 30));
        var q = ProjectOf(Day(10, 30), c, d);
        Link(q, c, d, LinkType.SS);
        var floats = Rules.Floats(q, Cal);
        Assert.Equal(0, floats[4]);
        Assert.Equal(0, floats[3]);
    }

    [Fact]
    public void 같이_끝은_후행_종료를_선행_종료_뒤로_맞추고_기간은_지킨다()
    {
        var a = W(1, "A", Day(10, 12), Day(10, 16));
        var b = W(2, "B", Day(10, 12), Day(10, 14));            // 근무일 3일
        var p = ProjectOf(Day(10, 30), a, b);
        var link = Link(p, a, b, LinkType.FF);

        Assert.True(Rules.Violates(link, a, b, Cal));             // 10-14 에 끝나 선행 종료(10-16)보다 이름
        var m = Assert.Single(Rules.Reschedule(p, Cal, new[] { a }));
        Assert.Equal((Day(10, 14), Day(10, 16)), (m.NewStart, m.NewEnd));
        b.PlanStart = m.NewStart;
        b.PlanEnd = m.NewEnd;
        Assert.False(Rules.Violates(link, a, b, Cal));
        // 더 늦게 끝나는 것은 괜찮다(이후에 끝나면 된다)
        b.PlanEnd = Day(10, 20);
        Assert.False(Rules.Violates(link, a, b, Cal));

        // 여유: 선행이 밀리면 후행 끝이 밀린다 — 후행의 여유와 같다
        var c = W(3, "C", Day(10, 12), Day(10, 16));
        var d = W(4, "D", Day(10, 12), Day(10, 16));
        var q = ProjectOf(Day(10, 30), c, d);
        Link(q, c, d, LinkType.FF);
        var floats = Rules.Floats(q, Cal);
        Assert.Equal(10, floats[4]);
        Assert.Equal(10, floats[3]);
    }

    [Fact]
    public void 전진_계산은_진행_중_지연을_후행으로_전파하고_시작_지연을_표시하며_저장값은_바꾸지_않는다()
    {
        var today = Day(10, 14);                                   // 수요일
        var a = W(1, "A", Day(10, 5), Day(10, 9), actualStart: Day(10, 5));         // 진행 중, 목표 종료 지남
        var b = W(2, "B", Day(10, 12), Day(10, 13));                                 // 미착수, 시작일 지남
        var c = W(3, "C", Day(10, 14), Day(10, 16));                                 // 미착수, 오늘 시작 예정
        var d = W(4, "D", Day(10, 1), Day(10, 2), Day(10, 1), Day(10, 5));          // 완료 — 실적 그대로
        var p = ProjectOf(Day(10, 30), a, b, c, d);
        Link(p, a, b);
        Link(p, b, c);
        var closed = new Stage { Id = 2, Name = "완료 단계", SortOrder = 2, CompletedDate = Day(10, 8) };
        var f = W(5, "F", Day(10, 5), Day(10, 12));                                 // 단계 완료로 끝남
        closed.Works.Add(f);
        p.Stages.Add(closed);

        var x = Rules.ForwardPass(p, Cal, today);

        Assert.Equal((Day(10, 5), today), (x[a].Start, x[a].End));                  // 종료 = max(목표 종료, 오늘)
        Assert.False(x[a].StartLate);
        Assert.Equal((Day(10, 15), Day(10, 16)), (x[b].Start, x[b].End));          // A 의 예상 종료 다음 근무일, 기간 2일
        Assert.True(x[b].StartLate);
        Assert.Same(a, x[b].Cause);
        Assert.Equal((Day(10, 19), Day(10, 21)), (x[c].Start, x[c].End));          // B 의 예상 지연이 이어진다
        Assert.False(x[c].StartLate);
        Assert.Same(b, x[c].Cause);
        Assert.Equal((Day(10, 1), Day(10, 5)), (x[d].Start, x[d].End));
        Assert.Equal(Day(10, 8), x[f].End);                                          // 단계 완료일에 끝난 것으로 본다

        // 저장값은 그대로
        Assert.Equal((Day(10, 12), Day(10, 13)), (b.PlanStart, b.PlanEnd));
        Assert.Equal(Day(10, 9), a.PlanEnd);

        // 단계 예상 종료: 전파한 값(10-21)과 작업마다 따로 본 값(10-16)
        var stage = p.Stages[0];
        Assert.Equal(Day(10, 21), Rules.Forecast(stage, today, x));
        Assert.Equal(Day(10, 16), Rules.Forecast(stage, today));
        Assert.Null(Rules.Forecast(closed, today, x));

        // 재계획 대상: 미착수·단계 미완료인데 예상이 목표와 다른 것만 (진행 중 A, 완료 D, 완료 단계 F 는 아님)
        var moves = Rules.ReplanMoves(p, Cal, today, x);
        Assert.Equal(new[] { "B", "C" }, moves.Select(m => m.Work.Title));
        Assert.True(moves[0].StartLate);

        // 화면 모델: 예상 일정과 예상 종료가 목표보다 늦은 근무일. B 는 목표 종료(10-13)도 지나 지연(빨강)이 먼저다
        var view = Views.Work(b, today, Cal, stage: stage, expected: x[b]);
        Assert.True(view.StartLate);
        Assert.Equal((Day(10, 15), Day(10, 16)), (view.ExpectedStart, view.ExpectedEnd));
        Assert.Equal(3, view.ExpectedSlip);
        Assert.Equal("alarm", view.Chip.Cls);
        // 시작일만 지났으면 '시작 지연'(노랑 — 아직 목표 종료 전이라 위험)
        var startLate = Views.Work(W(8, "G", Day(10, 13), Day(10, 16)), today, Cal, stage: stage,
            expected: new Rules.Expected(today, Day(10, 19), true, null));
        Assert.Equal(("warn", "시작 지연", 1), (startLate.Chip.Cls, startLate.Chip.Text, startLate.ExpectedSlip));
        // 예측이 없는 경로(대시보드 등)는 시작 지연을 표시하지 않는다
        Assert.False(Views.Work(W(8, "G", Day(10, 13), Day(10, 16)), today, Cal, stage: stage).StartLate);
    }

    [Fact]
    public void 전진_계산은_같이_끝_선행이_늦어지면_진행_중_후행의_예상_종료를_늘린다()
    {
        var today = Day(10, 14);
        var a = W(1, "A", Day(10, 12), Day(10, 16));                                // 미착수, 시작일 지남 → 10-14~10-20
        var b = W(2, "B", Day(10, 12), Day(10, 16), actualStart: Day(10, 12));      // 진행 중
        var p = ProjectOf(Day(10, 30), a, b);
        Link(p, a, b, LinkType.FF);
        var x = Rules.ForwardPass(p, Cal, today);
        Assert.Equal((Day(10, 14), Day(10, 20)), (x[a].Start, x[a].End));
        Assert.Equal(Day(10, 20), x[b].End);
        Assert.Same(a, x[b].Cause);
    }

    [Fact]
    public void 전진_계산은_순환이_있어도_멈추지_않고_모든_작업을_돌려준다()
    {
        var a = W(1, "A", Day(10, 12), Day(10, 13));
        var b = W(2, "B", Day(10, 14), Day(10, 15));
        var p = ProjectOf(null, a, b);
        Link(p, a, b);
        Link(p, b, a);
        var x = Rules.ForwardPass(p, Cal, Day(10, 9));
        Assert.Equal(2, x.Count);
    }

    [Fact]
    public void 과제_간_단계_연결은_선행_완료일_또는_목표일_다음_근무일과_간격으로_판정한다()
    {
        var pred = new Stage { Id = 1, Name = "선행", TargetDate = Day(10, 16) };    // 금요일
        var succ = new Stage { Id = 2, Name = "후행", TargetDate = Day(10, 19) };
        var link = new StageLink { PredecessorStageId = 1, SuccessorStageId = 2 };

        Assert.Equal(Day(10, 19), Rules.StageLinkEarliest(link, pred, Cal));
        Assert.False(Rules.StageLinkViolated(link, pred, succ, Cal));
        succ.TargetDate = Day(10, 16);
        Assert.True(Rules.StageLinkViolated(link, pred, succ, Cal));

        link.LagDays = 2;
        succ.TargetDate = Day(10, 20);
        Assert.Equal(Day(10, 21), Rules.StageLinkEarliest(link, pred, Cal));
        Assert.True(Rules.StageLinkViolated(link, pred, succ, Cal));

        // 선행이 완료됐으면 완료일 기준 (일찍 끝나면 풀린다)
        pred.CompletedDate = Day(10, 14);
        Assert.Equal(Day(10, 19), Rules.StageLinkEarliest(link, pred, Cal));
        Assert.False(Rules.StageLinkViolated(link, pred, succ, Cal));

        // 후행이 완료됐거나 어느 쪽이든 날짜가 없으면 판정하지 않는다
        pred.CompletedDate = null;
        succ.CompletedDate = Day(10, 20);
        Assert.False(Rules.StageLinkViolated(link, pred, succ, Cal));
        succ.CompletedDate = null;
        succ.TargetDate = null;
        Assert.False(Rules.StageLinkViolated(link, pred, succ, Cal));
        pred.TargetDate = null;
        succ.TargetDate = Day(10, 1);
        Assert.Null(Rules.StageLinkEarliest(link, pred, Cal));
        Assert.False(Rules.StageLinkViolated(link, pred, succ, Cal));
    }

    [Fact]
    public void 유형_입력과_간격_범위를_검사한다()
    {
        Assert.Equal(LinkType.FS, Rules.ParseLinkType(null));
        Assert.Equal(LinkType.FS, Rules.ParseLinkType(" "));
        Assert.Equal(LinkType.SS, Rules.ParseLinkType("ss"));
        Assert.Equal(LinkType.FF, Rules.ParseLinkType("FF"));
        Assert.Equal("predecessors", Assert.Throws<RuleException>(() => Rules.ParseLinkType("xx")).Field);
        Assert.Throws<RuleException>(() => Rules.ParseLinkType("7"));
        Rules.ValidateLag(0);
        Rules.ValidateLag(Rules.MaxLagDays);
        Assert.Throws<RuleException>(() => Rules.ValidateLag(-1));
        Assert.Equal("lagDays", Assert.Throws<RuleException>(() => Rules.ValidateLag(Rules.MaxLagDays + 1, "lagDays")).Field);
        Assert.Equal("끝나면 시작", Rules.LinkText(LinkType.FS, 0));
        Assert.Equal("같이 시작 +2근무일", Rules.LinkText(LinkType.SS, 2));
        Assert.Equal("ss", Views.Key(LinkType.SS));
    }
}

// 일정 엔진 확장을 실제 DB 에서 — 미리보기 = 저장 결과, 재계획, 간격·유형 입력과 이력, 과제 간 단계 연결
public class EngineServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public EngineServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string category = "물류")
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 1), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    private static WorkView WorkOf(ProjectView p, string title) => p.Stages.SelectMany(s => s.Works).Single(w => w.Title == title);

    private static WorkInput Work(int? id, int stageId, string title, DateOnly start, DateOnly end, List<int>? pre = null,
        string? reason = null, int? version = null, DateOnly? actualStart = null, List<LinkInput>? links = null) =>
        new(id, stageId, title, null, null, start, end, actualStart, null, 0, null, reason, version, pre, Links: links);

    private async Task<int> AddWorkAsync(int projectId, int stageId, string title, DateOnly start, DateOnly end,
        List<LinkInput>? links = null, DateOnly? actualStart = null, params int[] pre)
    {
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(null, stageId, title, start, end, pre.ToList(), actualStart: actualStart, links: links));
        return WorkOf(await ViewAsync(projectId), title).Id;
    }

    private async Task SetTargetAsync(int projectId, StageView stage, DateOnly target)
    {
        using var s = _db.NewScope();
        await s.Projects.SaveStageAsync(new StageInput(projectId, stage.Id, stage.Name, target, null,
            stage.TargetDate == null ? null : "시험", false, stage.Version));
    }

    // 미리보기가 아무것도 저장하지 않았는지 보려고 과제의 활동 수와 작업 버전 합을 센다
    private async Task<(int Activities, int Versions, DateTime? Updated)> FootprintAsync(int projectId)
    {
        using var s = _db.NewScope();
        var acts = await s.Db.Activities.CountAsync(a => a.ProjectId == projectId);
        var versions = (await s.Db.WorkItems.Where(w => w.Stage!.ProjectId == projectId).Select(w => w.Version).ToListAsync()).Sum();
        var updated = await s.Db.Projects.Where(p => p.Id == projectId).Select(p => p.UpdatedAt).FirstAsync();
        return (acts, versions, updated);
    }

    [Fact]
    public async Task 작업_일정_미리보기는_저장_결과와_같고_아무것도_저장하지_않는다()
    {
        var id = await NewProjectAsync("엔진 미리보기 작업");
        var stage = (await ViewAsync(id)).Stages[0];
        await SetTargetAsync(id, stage, Day(10, 23));
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 12), Day(10, 14));
        var b = await AddWorkAsync(id, stage.Id, "B", Day(10, 15), Day(10, 16), pre: a);
        await AddWorkAsync(id, stage.Id, "C", Day(10, 19), Day(10, 20), pre: b);
        var footprint = await FootprintAsync(id);

        PlanPreview preview;
        using (var s = _db.NewScope())
            preview = await s.Projects.PreviewWorkPlanAsync(a, Day(10, 12), Day(10, 16));
        Assert.Equal(footprint, await FootprintAsync(id));

        Assert.Equal(5, preview.WorkDays);
        Assert.Empty(preview.Violations);
        Assert.Equal(new[] { "B", "C" }, preview.Moved.Select(m => m.Title));
        Assert.Equal((Day(10, 19), Day(10, 20)), (preview.Moved[0].NewStart, preview.Moved[0].NewEnd));
        Assert.Equal((Day(10, 21), Day(10, 22)), (preview.Moved[1].NewStart, preview.Moved[1].NewEnd));
        Assert.All(preview.Moved, m => Assert.Equal(2, m.Shift));
        Assert.Equal(new ForecastChange(Day(10, 20), Day(10, 22)), preview.StageForecast);
        Assert.Equal(0, preview.ForecastOver);

        // 미리본 그대로 저장하면 같은 결과다
        var view = await ViewAsync(id);
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage.Id, "A", Day(10, 12), Day(10, 16), reason: "범위 확대", version: WorkOf(view, "A").Version));
        view = await ViewAsync(id);
        foreach (var m in preview.Moved)
            Assert.Equal((m.NewStart, m.NewEnd), (WorkOf(view, m.Title).PlanStart, WorkOf(view, m.Title).PlanEnd));
        Assert.Equal(preview.StageForecast!.After, view.Stages[0].Forecast);
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        // 목표일을 넘기게 늘리면 넘는 일수를 알린다
        using (var s = _db.NewScope())
            preview = await s.Projects.PreviewWorkPlanAsync(a, Day(10, 12), Day(10, 21));
        Assert.Equal(Day(10, 27), preview.StageForecast!.After);
        Assert.Equal(4, preview.ForecastOver);
        var row = Assert.Single(preview.Stages);
        Assert.Equal((4, false), (row.OverAfter, row.Late));
    }

    [Fact]
    public async Task 작업_미리보기는_저장이_거절할_내용을_막는_문제로_알리고_연결을_바꿔_볼_수_있다()
    {
        var id = await NewProjectAsync("엔진 미리보기 거절");
        var stage = (await ViewAsync(id)).Stages[0];
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 12), Day(10, 14));
        var b = await AddWorkAsync(id, stage.Id, "B", Day(10, 15), Day(10, 16), pre: a);

        using var s = _db.NewScope();
        var blocked = await s.Projects.PreviewWorkPlanAsync(b, Day(10, 14), Day(10, 16));
        var issue = Assert.Single(blocked.Violations);
        Assert.True(issue.Blocking);
        Assert.Equal("planStart", issue.Field);
        Assert.Contains("이후에 시작해야", issue.Text);
        Assert.Empty(blocked.Moved);
        Assert.Equal(3, blocked.WorkDays);

        // 같은 날짜라도 '같이 시작'으로 바꾸면 통과 (지금 연결 대신 화면이 고른 연결로 본다)
        var ss = await s.Projects.PreviewWorkPlanAsync(b, Day(10, 14), Day(10, 16), links: new() { new LinkInput(a, 0, "ss") });
        Assert.Empty(ss.Violations);
        // 연결을 모두 빼도 통과
        Assert.Empty((await s.Projects.PreviewWorkPlanAsync(b, Day(10, 14), Day(10, 16), links: new())).Violations);

        // 새 작업(아직 Id 없음): 고른 단계·연결로 본다
        var fresh = await s.Projects.PreviewWorkPlanAsync(null, Day(10, 12), Day(10, 13), stage.Id, new() { new LinkInput(a, 1, "fs") });
        Assert.Contains("간격 1근무일", Assert.Single(fresh.Violations).Text);
        Assert.Empty((await s.Projects.PreviewWorkPlanAsync(null, Day(10, 16), Day(10, 19), stage.Id, new() { new LinkInput(a, 1, "fs") })).Violations);

        // 범위 밖·순서가 뒤집힌 날짜도 저장과 같은 문구·필드로 막는다
        Assert.Equal("planStart", Assert.Single((await s.Projects.PreviewWorkPlanAsync(b, Day(10, 20), Day(10, 16))).Violations).Field);
        Assert.True(Assert.Single((await s.Projects.PreviewWorkPlanAsync(b, new DateOnly(2620, 10, 1), new DateOnly(2620, 10, 2))).Violations).Blocking);
        // 휴일 안내는 서버 달력 기준 (10-09 한글날)
        Assert.Contains((await s.Projects.PreviewWorkPlanAsync(null, Day(10, 9), Day(10, 12), stage.Id)).Notes, n => n.Contains("한글날"));
    }

    [Fact]
    public async Task 단계_목표일_미리보기는_연쇄_이동과_작업_포함을_저장과_같게_계산한다()
    {
        var id = await NewProjectAsync("엔진 미리보기 단계");
        var stages = (await ViewAsync(id)).Stages;
        await SetTargetAsync(id, stages[0], Day(10, 16));
        await SetTargetAsync(id, stages[1], Day(10, 30));
        var a = await AddWorkAsync(id, stages[1].Id, "A", Day(10, 19), Day(10, 21));
        await AddWorkAsync(id, stages[1].Id, "B", Day(10, 22), Day(10, 23), pre: a);
        var footprint = await FootprintAsync(id);

        PlanPreview preview;
        using (var s = _db.NewScope())
            preview = await s.Projects.PreviewStageTargetAsync(stages[0].Id, Day(10, 21), cascade: true, includeWorks: true);
        Assert.Equal(footprint, await FootprintAsync(id));
        Assert.Equal(3, preview.Shift);
        Assert.Empty(preview.Violations);
        Assert.Equal(new[] { "A", "B" }, preview.Moved.Select(m => m.Title));
        var later = preview.Stages.Single(x => x.Id == stages[1].Id);
        Assert.Equal((Day(10, 30), Day(11, 4)), (later.Target, later.NewTarget));
        var main = preview.Stages.Single(x => x.Id == stages[0].Id);
        Assert.Equal((Day(10, 16), Day(10, 21)), (main.Target, main.NewTarget));

        var current = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, current.Id, current.Name, Day(10, 21), null, "미리보기 확인", true, current.Version,
                CascadeWorks: true));
        var view = await ViewAsync(id);
        Assert.Equal(Day(11, 4), view.Stages[1].TargetDate);
        foreach (var m in preview.Moved)
            Assert.Equal((m.NewStart, m.NewEnd), (WorkOf(view, m.Title).PlanStart, WorkOf(view, m.Title).PlanEnd));

        // 작업 포함 없이 연쇄만: 단계만 옮겨지고 작업은 그대로
        using (var s = _db.NewScope())
        {
            var only = await s.Projects.PreviewStageTargetAsync(stages[0].Id, Day(10, 22), cascade: true, includeWorks: false);
            Assert.Empty(only.Moved);
            Assert.Equal(Day(11, 5), only.Stages.Single(x => x.Id == stages[1].Id).NewTarget);
            // 범위 밖이면 막는 문제로
            var bad = await s.Projects.PreviewStageTargetAsync(stages[0].Id, new DateOnly(2620, 1, 1), false, false);
            Assert.Equal("targetDate", Assert.Single(bad.Violations).Field);
        }
    }

    [Fact]
    public async Task 재계획은_예측과_다른_미착수_작업을_한_묶음으로_옮기고_화면이_본_목록과_다르면_충돌로_알린다()
    {
        // 오늘(TestDb) 2026-10-09 는 한글날 — 예측은 다음 근무일(10-12)부터 센다
        var id = await NewProjectAsync("엔진 재계획");
        var stage = (await ViewAsync(id)).Stages[0];
        await SetTargetAsync(id, stage, Day(10, 30));
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 1), Day(10, 7), actualStart: Day(10, 1));    // 진행 중, 목표 종료 지남
        await AddWorkAsync(id, stage.Id, "B", Day(10, 8), Day(10, 8), pre: a);                            // A 뒤, 아직 시작 안 함
        await AddWorkAsync(id, stage.Id, "C", Day(10, 6), Day(10, 7));                                     // 시작일 지남
        await AddWorkAsync(id, stage.Id, "D", Day(10, 20), Day(10, 21));                                   // 제때

        var view = await ViewAsync(id);
        Assert.Equal(2, view.ReplanCount);
        Assert.True(WorkOf(view, "C").StartLate);
        Assert.Equal((Day(10, 12), Day(10, 13)), (WorkOf(view, "C").ExpectedStart, WorkOf(view, "C").ExpectedEnd));
        Assert.Equal(Day(10, 12), WorkOf(view, "B").ExpectedStart);
        Assert.False(WorkOf(view, "D").StartLate);

        PlanPreview preview;
        using (var s = _db.NewScope())
            preview = await s.Projects.ReplanPreviewAsync(id);
        Assert.Equal(new[] { "B", "C" }, preview.Moved.Select(m => m.Title).OrderBy(x => x));
        Assert.All(preview.Moved, m => Assert.True(m.StartLate));
        var rows = preview.Moved.Select(m => new ReplanRow(m.Id, m.Version, m.NewStart, m.NewEnd)).ToList();

        using (var s = _db.NewScope())
        {
            Assert.Equal("reason", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.ReplanAsync(new ReplanInput(id, " ", view.Version, rows)))).Field);
            // 화면이 본 목록과 지금 계산이 다르면(그사이 누가 바꿈) 다시 보게 한다
            var stale = rows.Select(r => r with { PlanStart = r.PlanStart.AddDays(1) }).ToList();
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ReplanAsync(new ReplanInput(id, "오늘 기준", view.Version, stale)));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ReplanAsync(new ReplanInput(id, "오늘 기준", view.Version - 1, rows)));
        }
        using (var s = _db.NewScope())
            await s.Projects.ReplanAsync(new ReplanInput(id, "선행 지연 반영", view.Version, rows));

        var after = await ViewAsync(id);
        Assert.Equal(0, after.ReplanCount);
        Assert.Equal((Day(10, 12), Day(10, 12)), (WorkOf(after, "B").PlanStart, WorkOf(after, "B").PlanEnd));
        Assert.Equal((Day(10, 12), Day(10, 13)), (WorkOf(after, "C").PlanStart, WorkOf(after, "C").PlanEnd));
        Assert.Equal(WorkOf(view, "B").Version + 1, WorkOf(after, "B").Version);
        Assert.Equal((Day(10, 20), WorkOf(view, "D").Version), (WorkOf(after, "D").PlanStart, WorkOf(after, "D").Version));
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        using (var check = _db.NewScope())
        {
            var acts = await check.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.WorkPlanChanged && x.Reason == "선행 지연 반영").ToListAsync();
            Assert.Equal(2, acts.Count);
            Assert.Single(acts.Select(x => x.BatchId).Distinct());
            Assert.All(acts, x => Assert.Contains("재계획", x.Text));
        }
        // 더 옮길 것이 없으면 알린다
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.ReplanAsync(new ReplanInput(id, "다시", after.Version)));
    }

    [Fact]
    public async Task 선행_연결의_간격과_유형을_입력하고_바꿀_때마다_연결_변경_이력을_남긴다()
    {
        var id = await NewProjectAsync("엔진 간격 유형");
        var stage = (await ViewAsync(id)).Stages[0];
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 12), Day(10, 14));

        // 간격 2근무일: 10-15 가 아니라 10-19 부터
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                Work(null, stage.Id, "B", Day(10, 15), Day(10, 16), links: new() { new LinkInput(a, 2) })));
            Assert.Equal("planStart", ex.Field);
            Assert.Contains("간격 2근무일", ex.Message);
        }
        var b = await AddWorkAsync(id, stage.Id, "B", Day(10, 19), Day(10, 20), new() { new LinkInput(a, 2) });
        LinkView Only(ProjectView v) => Assert.Single(v.Links);
        var view = await ViewAsync(id);
        Assert.Equal((a, b, 2, "fs"), (Only(view).From, Only(view).To, Only(view).Lag, Only(view).Type));

        async Task<List<string>> LinkActsAsync()
        {
            using var s = _db.NewScope();
            return await s.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.LinkChanged).OrderBy(x => x.Id).Select(x => x.Text).ToListAsync();
        }
        Assert.Equal(new[] { "[B] 선행 연결 추가: A (끝나면 시작 +2근무일)" }, await LinkActsAsync());

        // 유형 변경
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 19), Day(10, 20), version: WorkOf(view, "B").Version,
                links: new() { new LinkInput(a, 0, "ss") }));
        view = await ViewAsync(id);
        Assert.Equal(("ss", 0), (Only(view).Type, Only(view).Lag));
        Assert.Equal("[B] 선행 연결 변경: A (끝나면 시작 +2근무일 → 같이 시작)", (await LinkActsAsync()).Last());

        // 유형을 모르는 예전 입력(Predecessors)은 있던 연결의 유형·간격을 지운다고 보지 않는다
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 19), Day(10, 20), new List<int> { a }, version: WorkOf(view, "B").Version));
        view = await ViewAsync(id);
        Assert.Equal(("ss", 0), (Only(view).Type, Only(view).Lag));
        Assert.Equal(2, (await LinkActsAsync()).Count);

        // 같이 시작이면 선행이 밀릴 때 시작 기준으로 따라온다 (A 를 10-19 시작으로 → B 는 10-19 시작 이상이면 그대로)
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage.Id, "A", Day(10, 20), Day(10, 22), reason: "순연", version: WorkOf(view, "A").Version));
        view = await ViewAsync(id);
        Assert.Equal((Day(10, 20), Day(10, 21)), (WorkOf(view, "B").PlanStart, WorkOf(view, "B").PlanEnd));

        // 잘못된 간격·유형은 거절
        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 20), Day(10, 21),
                version: WorkOf(view, "B").Version, links: new() { new LinkInput(a, -1) })));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 20), Day(10, 21),
                version: WorkOf(view, "B").Version, links: new() { new LinkInput(a, Rules.MaxLagDays + 1) })));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 20), Day(10, 21),
                version: WorkOf(view, "B").Version, links: new() { new LinkInput(a, 0, "sf") })));
        }

        // 연결 삭제
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(b, stage.Id, "B", Day(10, 20), Day(10, 21), version: WorkOf(view, "B").Version, links: new()));
        Assert.Empty((await ViewAsync(id)).Links);
        Assert.Equal("[B] 선행 연결 삭제: A", (await LinkActsAsync()).Last());
    }

    [Fact]
    public async Task Excel_일정표의_선행_작업에_유형과_간격을_적는다()
    {
        var id = await NewProjectAsync("엔진 Excel 선행");
        var stage = (await ViewAsync(id)).Stages[0];
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 12), Day(10, 14));
        await AddWorkAsync(id, stage.Id, "B", Day(10, 15), Day(10, 16), pre: a);
        await AddWorkAsync(id, stage.Id, "C", Day(10, 14), Day(10, 15), new() { new LinkInput(a, 2, "ss") });
        var bytes = new ExcelExporter().ProjectSchedule(await ViewAsync(id), _db.Clock.Today);
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var cells = book.Worksheets.First().CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("A(같이 시작 +2근무일)", cells);
        Assert.Contains("A", cells);                                   // 끝나면 시작·간격 0 은 이름만
    }

    [Fact]
    public async Task 같이_끝_연결은_후행_종료를_선행_종료_뒤로_검사하고_선행이_늦어지면_민다()
    {
        var id = await NewProjectAsync("엔진 같이 끝");
        var stage = (await ViewAsync(id)).Stages[0];
        var a = await AddWorkAsync(id, stage.Id, "A", Day(10, 12), Day(10, 16));
        var ff = new List<LinkInput> { new(a, 0, "ff") };
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(Work(null, stage.Id, "B", Day(10, 12), Day(10, 14), links: ff)));
            Assert.Contains("같이 끝", ex.Message);
            Assert.Contains("2026-10-16 이후에 끝나야", ex.Message);
        }
        await AddWorkAsync(id, stage.Id, "B", Day(10, 14), Day(10, 16), ff);
        var view = await ViewAsync(id);
        Assert.False(WorkOf(view, "B").DepViolation);
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage.Id, "A", Day(10, 12), Day(10, 19), reason: "연장", version: WorkOf(view, "A").Version));
        view = await ViewAsync(id);
        Assert.Equal((Day(10, 15), Day(10, 19)), (WorkOf(view, "B").PlanStart, WorkOf(view, "B").PlanEnd));
        Assert.Equal("ff", Assert.Single(view.Links).Type);
        await ScheduleAssert.AssertSchedulableAsync(_db, id);
    }

    [Fact]
    public async Task 과제_간_단계_연결은_밀지_않고_두_과제에_위반을_보이며_순환과_중복을_거절한다()
    {
        var p = await NewProjectAsync("엔진 과제간 선행");
        var q = await NewProjectAsync("엔진 과제간 후행");
        var r = await NewProjectAsync("엔진 과제간 셋째");
        var ps = (await ViewAsync(p)).Stages[0];
        var qs = (await ViewAsync(q)).Stages[0];
        var rs = (await ViewAsync(r)).Stages[0];
        await SetTargetAsync(p, ps, Day(10, 16));
        await SetTargetAsync(q, qs, Day(10, 16));
        await SetTargetAsync(r, rs, Day(11, 30));
        DateTime? qUpdated;
        using (var s = _db.NewScope())
            qUpdated = await s.Db.Projects.Where(x => x.Id == q).Select(x => x.UpdatedAt).FirstAsync();

        // q 의 단계에서 'p 단계가 선행'으로 잇는다 (변경 시각이 바뀌는지 보려고 시계를 1분 옮긴다)
        var clock = _db.Clock.UtcNow;
        _db.Clock.UtcNow = clock.AddMinutes(1);
        try
        {
            using var s = _db.NewScope();
            Assert.Equal(q, await s.Projects.AddStageLinkAsync(new StageLinkInput(qs.Id, ps.Id, "pred", 0)));
        }
        finally { _db.Clock.UtcNow = clock; }

        var qv = await ViewAsync(q);
        var pv = await ViewAsync(p);
        var mine = Assert.Single(qv.Stages[0].CrossLinks);
        Assert.Equal(("pred", p, "엔진 과제간 선행", ps.Id, true, Day(10, 19)), (mine.Role, mine.ProjectId, mine.ProjectTitle, mine.StageId, mine.Violated, mine.Earliest));
        var theirs = Assert.Single(pv.Stages[0].CrossLinks);
        Assert.Equal(("succ", q, true), (theirs.Role, theirs.ProjectId, theirs.Violated));
        Assert.Equal((1, 1), (qv.LinkViolations, pv.LinkViolations));
        Assert.Equal(Day(10, 16), qv.Stages[0].TargetDate);                // 자동으로 옮기지 않는다
        Assert.NotEqual(qUpdated, qv.UpdatedAt);                             // 상대 과제 화면의 변경 띠가 뜨도록

        using (var s = _db.NewScope())
        {
            Assert.Single(await s.Db.Activities.Where(a => a.ProjectId == p && a.Type == ActivityType.LinkChanged).ToListAsync());
            Assert.Contains("과제 간 선행 연결", (await s.Db.Activities.FirstAsync(a => a.ProjectId == q && a.Type == ActivityType.LinkChanged)).Text);

            // 거절: 중복, 같은 과제, 같은 단계, 역방향 순환, 세 과제를 도는 순환, 방향 없음, 간격 범위
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(qs.Id, ps.Id, "pred", 1)));
            var sameProject = (await ViewAsync(p)).Stages[1];
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, sameProject.Id, "succ", 0)));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, ps.Id, "succ", 0)));
            var cycle = await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, qs.Id, "pred", 0)));
            Assert.Contains("순환", cycle.Message);
            await s.Projects.AddStageLinkAsync(new StageLinkInput(qs.Id, rs.Id, "succ", 0));            // q → r
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, rs.Id, "pred", 0)));   // r → p 는 순환
            Assert.Equal("role", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, rs.Id, null, 0)))).Field);
            Assert.Equal("lagDays", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(ps.Id, rs.Id, "succ", -1)))).Field);
        }

        // 후행 목표일을 맞추면 풀린다
        await SetTargetAsync(q, (await ViewAsync(q)).Stages[0], Day(10, 19));
        Assert.Equal(0, (await ViewAsync(q)).LinkViolations);

        // 선행 단계 목표일을 늦추는 미리보기: 다른 과제와 새로 어긋남을 (막지 않고) 알린다
        using (var s = _db.NewScope())
        {
            var preview = await s.Projects.PreviewStageTargetAsync(ps.Id, Day(10, 20), false, false);
            var issue = Assert.Single(preview.Violations);
            Assert.False(issue.Blocking);
            Assert.Contains("엔진 과제간 후행", issue.Text);
            Assert.Empty((await s.Projects.PreviewStageTargetAsync(ps.Id, Day(10, 15), false, false)).Violations);

            // 연결 대상 검색: 자기 과제는 빼고 제목으로 찾는다
            var targets = await s.Projects.LinkTargetsAsync(p, "엔진 과제간");
            Assert.DoesNotContain(targets, x => x.Id == p);
            Assert.Contains(targets, x => x.Id == q && x.Stages.Any(st => st.Id == qs.Id));
        }
    }

    [Fact]
    public async Task 과제_간_연결은_해제_단계_삭제_과제_삭제에서_정리되고_복제하지_않는다()
    {
        var p = await NewProjectAsync("엔진 정리 선행");
        var q = await NewProjectAsync("엔진 정리 후행");
        var r = await NewProjectAsync("엔진 정리 셋째");
        var ps = (await ViewAsync(p)).Stages;
        var qs = (await ViewAsync(q)).Stages;
        var rs = (await ViewAsync(r)).Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.AddStageLinkAsync(new StageLinkInput(ps[0].Id, qs[0].Id, "succ", 1));   // p0 → q0
            await s.Projects.AddStageLinkAsync(new StageLinkInput(ps[1].Id, qs[1].Id, "succ", 0));   // p1 → q1
            await s.Projects.AddStageLinkAsync(new StageLinkInput(rs[0].Id, qs[2].Id, "succ", 0));   // r0 → q2
        }
        async Task<int> CountAsync()
        {
            using var s = _db.NewScope();
            return await s.Db.StageLinks.CountAsync(l => new[] { p, q, r }.Contains(l.PredecessorStage!.ProjectId) || new[] { p, q, r }.Contains(l.SuccessorStage!.ProjectId));
        }
        Assert.Equal(3, await CountAsync());

        // 복제는 과제 간 연결을 가져가지 않는다
        int clone;
        using (var s = _db.NewScope())
            clone = await s.Projects.CloneAsync(new CloneInput(p, "엔진 정리 선행 사본", Day(11, 2), true));
        Assert.All((await ViewAsync(clone)).Stages, st => Assert.Empty(st.CrossLinks));
        Assert.Equal(3, await CountAsync());

        // 해제: 두 과제 모두에 남긴다
        var link = (await ViewAsync(p)).Stages[0].CrossLinks.Single();
        using (var s = _db.NewScope())
            Assert.Equal(p, await s.Projects.RemoveStageLinkAsync(new StageLinkRemove(link.Id, p)));
        Assert.Equal(2, await CountAsync());
        using (var s = _db.NewScope())
        {
            Assert.Contains(await s.Db.Activities.Where(a => a.ProjectId == q && a.Type == ActivityType.LinkChanged).Select(a => a.Text).ToListAsync(),
                t => t.Contains("해제"));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.RemoveStageLinkAsync(new StageLinkRemove(link.Id, p)));
        }

        // 단계 삭제: 그 단계의 연결을 먼저 지우고 상대 과제에 이유를 남긴다
        using (var s = _db.NewScope())
            await s.Projects.DeleteStageAsync(ps[1].Id);
        Assert.Equal(1, await CountAsync());
        using (var s = _db.NewScope())
            Assert.Contains(await s.Db.Activities.Where(a => a.ProjectId == q).Select(a => a.Text).ToListAsync(), t => t.Contains("(단계 삭제)"));

        // 과제 삭제: 연결된 단계에 작업과 선행 연결이 있어도 지워진다(과제 삭제는 단계를 읽지 않으므로 지우는 순서를 서비스가 맞춘다)
        var r0a = await AddWorkAsync(r, rs[0].Id, "셋째 A", Day(10, 12), Day(10, 14));
        await AddWorkAsync(r, rs[0].Id, "셋째 B", Day(10, 15), Day(10, 16), pre: r0a);
        await AddWorkAsync(r, rs[1].Id, "셋째 C", Day(10, 19), Day(10, 20), pre: r0a);
        using (var s = _db.NewScope())
            await s.Projects.DeleteAsync(r);
        Assert.Equal(0, await CountAsync());
        using (var s = _db.NewScope())
        {
            Assert.False(await s.Db.Projects.AnyAsync(x => x.Id == r));
            Assert.False(await s.Db.Stages.AnyAsync(x => x.ProjectId == r));
            Assert.Contains(await s.Db.Activities.Where(a => a.ProjectId == q).Select(a => a.Text).ToListAsync(), t => t.Contains($"· {rs[0].Name} (과제 삭제)"));
        }
        Assert.All((await ViewAsync(q)).Stages, st => Assert.Empty(st.CrossLinks));
    }
}
