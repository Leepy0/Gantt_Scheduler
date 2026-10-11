using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 보고 정확성(순수 계산): 주간 보고의 결정 단위 세기·밀린 항목·보고 기준일·무변동 접기
public class WeeklyReportTests
{
    private static DateOnly Day(int month, int day) => new(2026, month, day);
    private static readonly DateOnly From = Day(10, 5), To = Day(10, 11);      // 월~일
    private static readonly LocationIndex Locations = new(new[] { new Location { Id = 1, Level = 1, Name = "한국" } });
    private static readonly AppUser Kim = new() { Id = 1, Name = "김담당" };
    private static int _ids = 1000;

    private static Project P(int id, string title, ProjectState state, DateOnly created, params Stage[] stages)
    {
        var p = new Project
        {
            Id = id, Title = title, State = state, CorporationId = 1, StartDate = Day(9, 1), Owner = Kim, OwnerId = Kim.Id,
            CreatedAt = KoreaClock.StartOfDayUtc(created).AddHours(10), Category = new Category { Name = "물류" },
        };
        var order = 0;
        foreach (var s in stages) { s.SortOrder = ++order; s.ProjectId = id; p.Stages.Add(s); }
        return p;
    }

    private static Stage S(string name, DateOnly? target, DateOnly? done = null, params WorkItem[] works)
    {
        var s = new Stage { Id = ++_ids, Name = name, TargetDate = target, CompletedDate = done };
        foreach (var w in works) { w.StageId = s.Id; s.Works.Add(w); }
        return s;
    }

    private static WorkItem W(string title, DateOnly start, DateOnly end, DateOnly? actualEnd = null) =>
        new() { Id = ++_ids, Title = title, PlanStart = start, PlanEnd = end, ActualStart = actualEnd == null ? null : start, ActualEnd = actualEnd, Owner = Kim, OwnerId = Kim.Id };

    private static Activity A(int id, int projectId, ActivityType type, string text, DateOnly day, Guid? batch = null, string? reason = null, int? stageId = null) =>
        new() { Id = id, ProjectId = projectId, Type = type, Text = text, BatchId = batch, Reason = reason, StageId = stageId, User = Kim, UserId = Kim.Id,
                At = KoreaClock.StartOfDayUtc(day).AddHours(9) };

    private static (List<Project> All, List<Activity> Acts) Sample()
    {
        // A: 지금은 지연이 아니지만(첫 단계를 10-15 에 완료) 그 주 마지막 날(10-11)에는 첫 단계가 10일 밀려 있었다
        var a = P(1, "A 진행 과제", ProjectState.Active, Day(9, 1),
            S("설계", Day(10, 1), Day(10, 15), W("도면", Day(10, 1), Day(10, 6), actualEnd: Day(10, 9))),
            S("제작", Day(10, 20), null, W("가공", Day(10, 5), Day(10, 8))),
            S("설치", Day(10, 30)));
        // B: 지금은 완료지만 마지막 단계를 그 주가 지난 뒤(10-14) 끝냈다 — 그 주에는 진행 중이었고 지연이었다
        var b = P(2, "B 지금은 완료", ProjectState.Done, Day(8, 1), S("검토", Day(9, 20), Day(9, 25)), S("적용", Day(10, 9), Day(10, 14)));
        // C: 그 주가 끝난 뒤 등록 — 지난 주 보고에는 없던 과제
        var c = P(3, "C 나중 등록", ProjectState.Active, Day(10, 15), S("검토", Day(10, 2)));
        // D: 아무 일 없는 진행 과제 — 표로 접힌다
        var d = P(4, "D 조용한 과제", ProjectState.Active, Day(9, 1), S("검토", Day(11, 30)));
        // E: 그 주 전에 끝난 완료 과제 — 빠진다
        var e = P(5, "E 오래전 완료", ProjectState.Done, Day(6, 1), S("검토", Day(7, 1), Day(7, 1)));
        // F: 그 주에 모든 단계를 끝내 자동으로 완료됐다
        var f = P(6, "F 자동 완료", ProjectState.Done, Day(9, 1), S("검토", Day(10, 8), Day(10, 8)));

        var cascade = Guid.NewGuid();
        var push = Guid.NewGuid();
        var acts = new List<Activity>
        {
            A(1, 1, ActivityType.TargetChanged, "[제작] 목표일 2026-10-16 → 2026-10-20", Day(10, 6), cascade, "자재 지연"),
            A(2, 1, ActivityType.TargetChanged, "[설치] 목표일 2026-10-26 → 2026-10-30 (연쇄 이동)", Day(10, 6), cascade, "자재 지연"),
            A(3, 1, ActivityType.WorkPlanChanged, "[가공] 목표 일정 변경", Day(10, 7), push, "범위 확대"),
            A(4, 1, ActivityType.WorkPlanChanged, "[조립] 목표 일정 변경 (선행 작업 연동)", Day(10, 7), push, "범위 확대"),
            A(5, 1, ActivityType.TargetChanged, "[설계] 목표일 변경", Day(10, 8), null, "고객 요청"),
            A(6, 1, ActivityType.StateChanged, "상태 변경: 보류 → 진행", Day(10, 8)),
            A(7, 1, ActivityType.BaselineSet, "기준선 다시 확정", Day(10, 9), reason: "계약 변경"),
            A(8, 6, ActivityType.StageCompleted, "[검토] 완료 (2026-10-08)", Day(10, 8), stageId: f.Stages[0].Id),
            A(9, 6, ActivityType.StateChanged, ProjectService.AutoDoneText, Day(10, 8)),
        };
        return (new List<Project> { a, b, c, d, e, f }, acts);
    }

    [Fact]
    public void 일정_변경은_결정_단위로_세고_묶음_안_대상은_접는다()
    {
        var (all, acts) = Sample();
        var r = Reporting.Weekly(all, acts, Locations, From, To, Day(10, 21));
        var a = r.Projects.Single(p => p.Id == 1);

        Assert.Equal(3, a.Changes.Count);                         // 연쇄 이동 묶음 1 + 선행 연동 묶음 1 + 단독 1
        Assert.Equal(5, a.ChangeTargets);
        Assert.Equal("[제작] 목표일 2026-10-16 → 2026-10-20", a.Changes[0].Text);   // 먼저 남긴 행(사람이 바꾼 대상)이 대표
        Assert.Equal(new[] { "[설치] 목표일 2026-10-26 → 2026-10-30 (연쇄 이동)" }, a.Changes[0].More);
        Assert.Null(a.Changes[2].More);
        Assert.Equal((3, 5), (r.ChangeCount, r.ChangeTargets));

        // 상태·기준선과 자동 완료는 일정 변경에 섞이지 않고 따로 센다
        Assert.Equal(2, a.States!.Count);
        Assert.Equal(2, r.StateCount);
        Assert.Equal(1, r.Completed);
        Assert.Contains(r.Projects.Single(p => p.Id == 6).States!, x => x.Text == ProjectService.AutoDoneText);
    }

    [Fact]
    public void 지난_주_보고는_그_주_마지막_날_기준으로_지연과_밀린_항목을_센다()
    {
        var (all, acts) = Sample();
        var r = Reporting.Weekly(all, acts, Locations, From, To, Day(10, 21));
        Assert.Equal(Day(10, 11), r.AsOf);
        Assert.True(r.Past);

        // 그 뒤 등록한 과제(C)와 그 전에 끝난 과제(E)는 없다
        Assert.DoesNotContain(r.Projects, p => p.Id is 3 or 5);

        var a = r.Projects.Single(p => p.Id == 1);
        Assert.Equal(10, a.DelayDays);                            // 설계 목표 10-01, 완료 10-15 → 10-11 기준 10일
        Assert.Equal("지연", a.StateLabel);
        Assert.Equal(new[] { ("단계", "설계", 10), ("작업", "가공", 3) },
            a.Overdue!.Select(x => (x.Kind, x.Title, x.LateDays)).ToArray());
        Assert.Equal("지연 D+10", a.Overdue![0].Chip.Text);
        Assert.Contains(a.Done, x => x.Text == "작업 완료: 도면" && x.Date == Day(10, 9));

        // 지금은 완료지만 그 주에는 진행 중이던 과제: 활동이 없어도 지연 카드로 남는다
        var b = r.Projects.Single(p => p.Id == 2);
        Assert.Equal((2, "지연"), (b.DelayDays, b.StateLabel));
        Assert.True(b.Card);
        Assert.Equal((3, 2), (r.Active, r.Delayed));               // A·B·D 가 진행, A·B 가 지연 (F 는 그 주에 완료)
    }

    [Fact]
    public void 이번_주_보고는_오늘_기준이고_무변동_과제는_표로_접힌다()
    {
        var (all, acts) = Sample();
        var today = Day(10, 9);
        var r = Reporting.Weekly(all, acts, Locations, From, To, today);
        Assert.Equal(today, r.AsOf);
        Assert.False(r.Past);

        // 오늘 기준이면 지금 판정(Rules.DelayDays)과 같다
        foreach (var p in r.Projects)
            Assert.Equal(Rules.DelayDays(all.Single(x => x.Id == p.Id), today), p.DelayDays);

        var d = r.Projects.Single(p => p.Id == 4);
        Assert.True(d.Quiet);
        Assert.False(d.Card);
        Assert.Contains(r.Folded(), p => p.Id == 4);
        Assert.DoesNotContain(r.Cards(), p => p.Id == 4);
        Assert.Contains(r.Cards(), p => p.Id == 1);

        // 그 주 안에 완료한 단계는 완료일로 실적에 오르고, 완료 활동을 남긴 사람을 함께 보인다
        var f = r.Projects.Single(p => p.Id == 6);
        Assert.Contains(f.Done, x => x.Text == "[검토] 단계 완료" && x.User == "김담당");
    }

    [Fact]
    public void 결정_수는_묶음을_하나로_센다()
    {
        var b = Guid.NewGuid();
        Assert.Equal(3, Rules.DecisionCount(new Guid?[] { null, b, b, b, null }));
        Assert.Equal(0, Rules.DecisionCount(Array.Empty<Guid?>()));
    }
}

// 리드타임: 근무일 열, 0일 표본, 처음 목표·기준선 대비
public class LeadTimeTests
{
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static Project Done(int id, ProjectState state, params Stage[] stages)
    {
        var p = new Project { Id = id, CategoryId = 1, Category = new Category { Id = 1, Name = "물류" }, State = state, StartDate = Day(10, 1) };
        var order = 0;
        foreach (var s in stages) { s.SortOrder = ++order; p.Stages.Add(s); }
        return p;
    }

    [Fact]
    public void 리드타임은_근무일과_처음_목표_기준선_대비를_함께_내고_0일_표본을_따로_센다()
    {
        var review1 = new Stage { Id = 11, Name = "검토", CompletedDate = Day(10, 7), TargetDate = Day(10, 6), BaselineDate = Day(10, 5) };
        var report1 = new Stage { Id = 12, Name = "보고", CompletedDate = Day(10, 7), TargetDate = Day(10, 9) };      // 앞 단계와 같은 날 완료 — 0일 표본
        var review2 = new Stage { Id = 21, Name = "검토", CompletedDate = Day(10, 9), TargetDate = Day(10, 9) };
        var all = new List<Project>
        {
            Done(1, ProjectState.Done, review1, report1),
            Done(2, ProjectState.Active, review2, new Stage { Id = 22, Name = "보고" }),
        };
        var firsts = new Dictionary<int, DateOnly> { [11] = Day(10, 2) };   // 검토(과제 1)는 처음에 10-02 로 정했다가 미뤘다

        var group = Assert.Single(Reporting.LeadTimes(all, WorkCalendar.WeekendsOnly, firsts));
        var review = group.Rows[0];
        Assert.Equal(("검토", 2), (review.Stage, review.Count));
        Assert.Equal(7, review.AvgDays);                  // 달력일 6 · 8
        Assert.Equal(5, review.AvgWorkDays);              // 근무일 4 · 6 (주말 제외)
        Assert.Equal((6, 8), (review.MinDays, review.MaxDays));
        Assert.Equal((0.5, 1, 2), (review.AvgOver!.Value, review.LateCount, review.OverCount));   // 지금 목표 대비 +1 · 0
        Assert.Equal((2.5, 2), (review.AvgOverFirst!.Value, review.FirstCount));                // 처음 목표 대비 +5 · 0 (바꾼 적 없으면 지금 목표)
        Assert.Equal((2.0, 1), (review.AvgOverBaseline!.Value, review.BaselineCount));          // 기준선이 있는 단계만

        var report = group.Rows[1];
        Assert.Equal((1, 1, 0.0), (report.Count, report.ZeroCount, report.AvgDays));
        Assert.Equal(-2, report.AvgOver);                 // 목표 대비는 0일 표본도 센다

        // 0일 표본 제외: 소요일 평균·표본에서 빠지고 0일 표본 수는 그대로 보인다
        var excluded = Reporting.LeadTimes(all, WorkCalendar.WeekendsOnly, firsts, excludeZero: true).Single().Rows[1];
        Assert.Equal((0, 1), (excluded.Count, excluded.ZeroCount));
        Assert.Equal(-2, excluded.AvgOver);
    }

    [Fact]
    public void 순서를_거슬러_완료한_단계_뒤는_가장_늦은_완료일부터_센다()
    {
        var p = Done(1, ProjectState.Done,
            new Stage { Id = 1, Name = "검토", CompletedDate = Day(10, 14) },
            new Stage { Id = 2, Name = "보고", CompletedDate = Day(10, 6) },
            new Stage { Id = 3, Name = "적용", CompletedDate = Day(10, 16) });
        var samples = Reporting.LeadSamples(p, WorkCalendar.WeekendsOnly);
        Assert.Equal(new[] { (13, 9, false), (0, -6, true), (2, 2, false) }, samples.Select(x => (x.Days, x.WorkDays, x.Zero)).ToArray());
    }
}

// 보고 정확성(DB): 주간 보고·상세 목표일 변경 수가 실제 저장 경로의 묶음과 맞는지
public class WeeklyServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public WeeklyServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 1), null, null, null, null));
    }

    [Fact]
    public async Task 연쇄_이동과_선행_연동은_주간_보고와_상세에서_결정_1건이다()
    {
        var id = await NewProjectAsync("테스트 주간 결정");
        ProjectView v;
        using (var s = _db.NewScope()) v = (await s.Projects.ViewAsync(id))!;
        var st = v.Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, st[0].Id, st[0].Name, Day(10, 14), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, st[1].Id, st[1].Name, Day(10, 28), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, st[2].Id, st[2].Name, Day(11, 11), null, null, false));
        }
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, st[0].Id, st[0].Name, Day(10, 21), null, "자료 보완", true));

        using var scope = _db.NewScope();
        var view = (await scope.Projects.ViewAsync(id))!;
        Assert.Equal(1, view.TargetChanges);

        var report = await scope.Insights.WeeklyAsync(null, false);
        var mine = report.Projects.Single(p => p.Id == id);
        var decision = Assert.Single(mine.Changes);
        Assert.Equal(3, decision.Targets);                       // 직접 바꾼 검토 + 연쇄 이동 2건
        Assert.Equal("자료 보완", decision.Reason);
        Assert.Equal(2, decision.More!.Count);
    }
}
