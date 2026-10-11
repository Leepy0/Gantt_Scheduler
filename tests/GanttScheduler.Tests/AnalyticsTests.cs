using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 분석 확장(순수 계산): 추세 집계·보고 기간·미달·진척 증감·결정 묶음·밀린 일수·파레토·부하·막대 범위·수량 표
public class AnalyticsRulesTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static ProjectSnapshot Snap(int project, DateOnly date, ProjectState state = ProjectState.Active, int delay = 0, bool risk = false, int progress = 50,
        int category = 1, int corp = 1, int? org = null) =>
        new() { ProjectId = project, Date = date, State = state, DelayDays = delay, AtRisk = risk, Progress = progress, CategoryId = category, CorporationId = corp, OrgUnitId = org };

    [Fact]
    public void 추세는_날짜마다_진행_지연_위험_평균_진행률을_세고_1년은_주마다_마지막_기록으로_줄인다()
    {
        var rows = new[]
        {
            Snap(1, D(10, 5), delay: 2, progress: 40), Snap(2, D(10, 5), risk: true, progress: 60), Snap(3, D(10, 5), ProjectState.Hold),
            Snap(1, D(10, 7), delay: 3, progress: 50), Snap(2, D(10, 7), progress: 70),
            Snap(1, D(10, 13), progress: 80),
        };
        var daily = Reporting.Trend(rows, weekly: false);
        Assert.Equal(3, daily.Count);
        Assert.Equal(new TrendPoint(D(10, 5), 2, 1, 1, 50), daily[0]);       // 보류는 진행 과제가 아니다
        Assert.Equal(new TrendPoint(D(10, 7), 2, 1, 0, 60), daily[1]);
        // 기록이 없는 날(10-6, 10-8~12)은 점이 없다 — 지어내지 않는다
        var weekly = Reporting.Trend(rows, weekly: true);
        Assert.Equal(new[] { D(10, 7), D(10, 13) }, weekly.Select(p => p.Date));
    }

    [Fact]
    public void 스냅샷_조직_범위는_하위_파트까지_포함한다()
    {
        var units = new[] { new OrgUnit { Id = 1, Level = 1 }, new OrgUnit { Id = 2, ParentId = 1, Level = 2 }, new OrgUnit { Id = 3, Level = 1 } };
        var scope = Reporting.OrgScope(1, units);
        Assert.Equal(new[] { 1, 2 }, scope.OrderBy(x => x));
        var rows = new[] { Snap(1, D(10, 5), org: 2), Snap(2, D(10, 5), org: 3), Snap(3, D(10, 5)) };
        Assert.Equal(new[] { 1 }, Reporting.Filter(rows, new AnalyticsFilter(OrgUnitId: 1), scope).Select(s => s.ProjectId));
        Assert.True(Rules.InOrg(new OrgUnit { Id = 2, ParentId = 1 }, 1));
        Assert.False(Rules.InOrg(null, 1));
    }

    [Fact]
    public void 보고_기간은_주_월_직접_지정이고_직접_지정은_상한으로_줄인다()
    {
        var today = D(10, 9);
        Assert.Equal((D(10, 5), D(10, 11), "week", (string?)null), Reporting.Period(null, null, null, null, today));
        Assert.Equal((D(2, 1), D(2, 28), "month", (string?)null), Reporting.Period("month", D(2, 14), null, null, today));
        var custom = Reporting.Period("custom", null, D(10, 20), D(10, 1), today);
        Assert.Equal((D(10, 1), D(10, 20)), (custom.From, custom.To));      // 뒤바뀐 날짜는 바로 잡는다
        var big = Reporting.Period("custom", null, new DateOnly(2026, 1, 1), D(10, 9), today);
        Assert.Equal(Reporting.MaxReportDays, big.To.DayNumber - big.From.DayNumber + 1);
        Assert.NotNull(big.Notice);
    }

    [Fact]
    public void 기간_미달은_목표가_기간_안이고_기준일까지_지났는데_끝나지_않은_것만()
    {
        var today = D(10, 9);
        // 이번 주(10-5~11): 10-7 목표 미완료는 미달, 오늘(10-9)·내일 목표는 아직 아니다
        Assert.True(Rules.MissedIn(D(10, 7), null, null, D(10, 5), D(10, 11), today, today));
        Assert.False(Rules.MissedIn(D(10, 9), null, null, D(10, 5), D(10, 11), today, today));
        Assert.False(Rules.MissedIn(D(10, 7), D(10, 8), null, D(10, 5), D(10, 11), today, today));
        Assert.False(Rules.MissedIn(D(10, 7), null, D(10, 8), D(10, 5), D(10, 11), today, today));      // 단계 완료로 함께 끝남
        Assert.False(Rules.MissedIn(D(10, 2), null, null, D(10, 5), D(10, 11), today, today));          // 기간 밖
        // 지난 주(9-28~10-4): 기준일 10-4 — 그 뒤(10-6)에 끝낸 것은 그때 미달이었다
        Assert.True(Rules.MissedIn(D(10, 4), D(10, 6), null, D(9, 28), D(10, 4), D(10, 4), today));
    }

    [Fact]
    public void 진척_증감은_작업_진행률_변경을_과제_진행률_비중으로_환산한다()
    {
        var p = new Project();
        var s1 = new Stage { Id = 1 };
        s1.Works.Add(new WorkItem { Id = 11 });
        s1.Works.Add(new WorkItem { Id = 12 });
        var s2 = new Stage { Id = 2 };
        s2.Works.Add(new WorkItem { Id = 21 });
        p.Stages.AddRange(new[] { s1, s2 });
        // 11: 0→40 → 단계1 +20 → 과제 +10, 21: 50→70 → 단계2 +20 → 과제 +10, 지운 작업(99)은 뺀다
        Assert.Equal(20, Rules.ProgressGain(p, new[] { (11, 0, 40), (21, 50, 70), (99, 0, 100) }));
        var acts = new[]
        {
            new Activity { Type = ActivityType.WorkEdited, WorkItemId = 11, OldValue = "0", NewValue = "40" },
            new Activity { Type = ActivityType.WorkEdited, WorkItemId = 12, Text = "옛 기록(값 없음)" },
            new Activity { Type = ActivityType.Comment, WorkItemId = 12, OldValue = "1", NewValue = "2" },
        };
        Assert.Equal(new[] { (11, 0, 40) }, Reporting.ProgressChanges(acts).ToArray());
    }

    [Fact]
    public void 일정_변경은_결정_단위로_묶고_밀린_일수는_새값_빼기_이전값의_최댓값()
    {
        Assert.Equal(3, Rules.ScheduleSlip("2026-10-01", "2026-10-04"));
        Assert.Equal(5, Rules.ScheduleSlip("2026-10-01~2026-10-05", "2026-10-03~2026-10-10"));     // 작업은 종료일
        Assert.Equal(-2, Rules.ScheduleSlip("2026-10-05", "2026-10-03"));
        Assert.Null(Rules.ScheduleSlip("미정", "2026-10-03"));
        Assert.Null(Rules.ScheduleSlip("2026-10-03", "미정"));

        var batch = Guid.NewGuid();
        var at = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var acts = new[]
        {
            new ChangeActivity(1, 7, ActivityType.TargetChanged, "2026-10-01", "2026-10-04", batch, 3, "자재", at),
            new ChangeActivity(2, 7, ActivityType.TargetChanged, "2026-10-10", "2026-10-17", batch, null, "자재", at),
            new ChangeActivity(3, 7, ActivityType.WorkPlanChanged, "2026-10-01~2026-10-02", "2026-10-01~2026-10-01", null, null, null, at.AddHours(1)),
            new ChangeActivity(4, 7, ActivityType.TargetChanged, "2026-10-01", "미정", null, 3, null, at.AddHours(2)),
            new ChangeActivity(5, 7, ActivityType.Comment, null, null, null, null, null, at),
        };
        var d = Reporting.ScheduleChanges(acts);
        Assert.Equal(3, d.Count);
        Assert.Equal((7, 3, 2), (d[0].Slip!.Value, d[0].ReasonCode!.Value, d[0].Targets));
        Assert.Equal(-1, d[1].Slip);
        Assert.Null(d[2].Slip);

        var reasons = new Dictionary<int, (string, bool)> { [3] = ("자재·납품 지연", true), [4] = ("기타", false) };
        var pareto = Reporting.Pareto(d.Append(new ChangeDecision(7, at, 1, 4, null, 1)), reasons);
        Assert.Equal(new[] { "자재·납품 지연", "기타" }, pareto.Select(x => x.Name));
        Assert.Equal((2, 66.7, 66.7), (pareto[0].Count, pareto[0].Share, pareto[0].Cumulative));
        Assert.Equal(100, pareto[1].Cumulative);
        Assert.False(pareto[1].Active);
    }

    [Fact]
    public void 변경_분석은_월별_결정과_누적_밀림_상위_과제를_센다()
    {
        var oct = new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc);
        var sep = new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc);
        var decisions = new List<ChangeDecision>
        {
            new(1, sep, 4, null, null, 1), new(1, oct, -3, null, null, 1), new(2, oct, 2, 5, null, 2), new(9, oct, 10, null, null, 1),
        };
        var projects = new Dictionary<int, (string, int, int)> { [1] = ("가", 1, 10), [2] = ("나", 2, 10) };   // 9 는 조건 밖
        var v = Reporting.Changes(decisions, projects, new Dictionary<int, string> { [1] = "물류", [2] = "용기" }, new Dictionary<int, string> { [10] = "한국" },
            new Dictionary<int, (string, bool)> { [5] = ("기타", true) }, D(5, 1), D(10, 9), 6, new AnalyticsFilter(), new(), new(), new());
        Assert.Equal(new[] { "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10" }, v.Months);
        Assert.Equal(3, v.Total);
        Assert.Equal((1, 4, 4), (v.ByMonth[4].Count, v.ByMonth[4].Slip, v.ByMonth[4].Cumulative));
        Assert.Equal((2, 2, 6), (v.ByMonth[5].Count, v.ByMonth[5].Slip, v.ByMonth[5].Cumulative));   // 당긴 것(-3)은 빼지 않는다
        Assert.Equal("가", v.Top[0].Project);
        Assert.Equal(2, v.Top[0].Decisions);
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1 }, v.ByCategory.Single(r => r.Name == "물류").Counts);
        Assert.Equal(2, v.Unclassified);
    }

    [Fact]
    public void 부하는_담당_1_부담당_0_5로_주마다_근무일을_나누고_공휴일은_가용일에서_뺀다()
    {
        var cal = new WorkCalendar(new[] { D(10, 9) });           // 한글날(금)
        var monday = D(10, 5);
        var load = Rules.Load(new[]
        {
            new Rules.LoadAssignment(1, Rules.OwnerWeight, D(10, 5), D(10, 13)),       // 이번 주 4일(공휴일 제외) + 다음 주 2일
            new Rules.LoadAssignment(1, Rules.CoOwnerWeight, D(10, 5), D(10, 9)),      // 부담당 4일 × 0.5
            new Rules.LoadAssignment(2, Rules.OwnerWeight, D(9, 1), D(9, 30)),         // 범위 밖
        }, monday, 2, cal);
        Assert.False(load.ContainsKey(2));
        var week = load[1];
        Assert.Equal((6.0, 4), (week[0].Assigned, week[0].Available));
        Assert.True(week[0].Over);
        Assert.Equal((2.0, 5), (week[1].Assigned, week[1].Available));
        Assert.False(week[1].Over);
        Assert.Equal(D(10, 12), week[1].Week);
    }

    [Fact]
    public void 부하_배정은_진행_과제의_열린_작업만_예상_일정으로_오늘부터()
    {
        var today = D(10, 7);
        var p = new Project { State = ProjectState.Active, StartDate = D(9, 1) };
        var s = new Stage { Id = 1, Name = "제작" };
        s.Works.Add(new WorkItem { Id = 1, OwnerId = 5, CoOwnerId = 6, PlanStart = D(9, 28), PlanEnd = D(10, 2) });   // 늦어짐 — 오늘부터
        s.Works.Add(new WorkItem { Id = 2, OwnerId = 5, PlanStart = D(10, 1), PlanEnd = D(10, 2), ActualStart = D(10, 1), ActualEnd = D(10, 2) });
        p.Stages.Add(s);
        var list = Rules.LoadAssignments(p, WorkCalendar.WeekendsOnly, today).ToList();
        Assert.Equal(2, list.Count);
        Assert.All(list, a => Assert.True(a.Start >= today));
        Assert.Contains(list, a => a.UserId == 6 && a.Weight == Rules.CoOwnerWeight);
        p.State = ProjectState.Hold;
        Assert.Empty(Rules.LoadAssignments(p, WorkCalendar.WeekendsOnly, today));
    }

    [Fact]
    public void 대시보드_막대는_진행_보류와_올해_완료만_센다()
    {
        var today = D(10, 9);
        Project P(int id, ProjectState state, DateOnly? done = null)
        {
            var p = new Project { Id = id, State = state, CategoryId = 1, CorporationId = 1, Category = new Category { Id = 1, Name = "물류" }, StartDate = D(1, 1) };
            p.Stages.Add(new Stage { Id = id * 10, Name = "검토", TargetDate = D(12, 1), CompletedDate = done });
            return p;
        }
        var all = new List<Project>
        {
            P(1, ProjectState.Active), P(2, ProjectState.Hold), P(3, ProjectState.Done, D(3, 1)), P(4, ProjectState.Done, new DateOnly(2025, 3, 1)),
            P(5, ProjectState.Dropped),
        };
        var v = Dashboard.Build(all, new LocationIndex(new[] { new Location { Id = 1, Level = 1, Name = "한국" } }), today);
        var bar = v.ByCategory.Single();
        Assert.Equal((3, 1, 1, 1), (bar.Total, bar.OnTrack, bar.Done, bar.Paused));
        Assert.Equal(new[] { 1, 2, 3 }, all.Where(p => Dashboard.InBars(p, today)).Select(p => p.Id));
    }

    [Fact]
    public void 법인_기기_수량은_진행과_완료를_나누고_중단은_뺀다()
    {
        var m = Reporting.Equipment(new[]
        {
            (1, ProjectState.Active, 10, 3), (1, ProjectState.Hold, 10, 2), (1, ProjectState.Done, 11, 4), (2, ProjectState.Dropped, 10, 9),
        }, new[] { new EquipmentColumn(10, "AGV"), new EquipmentColumn(11, "AMR"), new EquipmentColumn(12, "리프터") },
            new LocationIndex(new[] { new Location { Id = 1, Level = 1, Name = "한국" }, new Location { Id = 2, Level = 1, Name = "베트남" } }));
        Assert.Equal(new[] { "AGV", "AMR" }, m.Columns.Select(c => c.Name));            // 쓰인 종류만
        var row = Assert.Single(m.Rows);
        Assert.Equal(new[] { 5, 0 }, row.Active);
        Assert.Equal(new[] { 0, 4 }, row.Done);
        Assert.Equal((5, 4), (m.ActiveTotal(0), m.DoneTotal(1)));
    }

    [Fact]
    public void 스냅샷_시각_판정은_오늘_기록이_없으면_바로_있으면_정한_시각_뒤_한_번()
    {
        var o = new SnapshotOptions(true, 18);
        var morning = new DateTime(2026, 10, 9, 9, 0, 0);
        Assert.True(SnapshotWorker.Due(o, morning, null, hasToday: false));
        Assert.False(SnapshotWorker.Due(o, morning, morning, hasToday: true));
        var evening = new DateTime(2026, 10, 9, 18, 5, 0);
        Assert.True(SnapshotWorker.Due(o, evening, morning, hasToday: true));
        Assert.False(SnapshotWorker.Due(o, evening.AddMinutes(10), evening, hasToday: true));
        Assert.False(SnapshotWorker.Due(o with { Enabled = false }, morning, null, hasToday: false));
    }
}

// 분석 확장(DB 서비스): 스냅샷 멱등·삭제 제외, 사유 분류 저장, 진척 기록, 보고 조직 범위·전주 대비, 병목, 이력 Excel
public class AnalyticsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private SnapshotService Snapshots(TestDb.Scope s) => new(s.Db, s.Projects, _db.Clock);
    private AnalyticsService Analytics(TestDb.Scope s) => new(s.Db, s.Projects, _db.Clock);

    [Fact]
    public async Task 스냅샷은_같은_날_다시_돌면_덮어쓰고_휴지통_중단_과제는_남기지_않는다()
    {
        int first, activeCount;
        using (var s = _db.NewScope())
        {
            first = await Snapshots(s).TakeAsync();
            activeCount = await s.Db.LiveProjects.CountAsync(p => p.State == ProjectState.Active || p.State == ProjectState.Hold);
            Assert.True(first >= activeCount && first > 0);
            Assert.Equal(first, await Snapshots(s).TakeAsync());
            Assert.Equal(first, await s.Db.ProjectSnapshots.CountAsync());
            Assert.True(await Snapshots(s).HasTodayAsync());
        }
        int victim;
        using (var s = _db.NewScope())
        {
            victim = await s.Db.Projects.Where(p => p.State == ProjectState.Active).Select(p => p.Id).FirstAsync();
            await s.Projects.DeleteAsync(victim, "시험");
        }
        using (var s = _db.NewScope())
        {
            Assert.Equal(first - 1, await Snapshots(s).TakeAsync());
            Assert.False(await s.Db.ProjectSnapshots.AnyAsync(x => x.ProjectId == victim));
            var row = await s.Db.ProjectSnapshots.FirstAsync();
            Assert.Equal(_db.Clock.Today, row.Date);
        }
        // 다음 날은 새 행 — 지난 날 행은 그대로 남는다
        _db.Clock.Today = _db.Clock.Today.AddDays(1);
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(1);
        using (var s = _db.NewScope())
        {
            await Snapshots(s).TakeAsync();
            Assert.Equal(2, await s.Db.ProjectSnapshots.Select(x => x.Date).Distinct().CountAsync());
            var trend = await Analytics(s).TrendAsync(null, new AnalyticsFilter());
            Assert.Equal(2, trend.Points.Count);
        }
    }

    [Fact]
    public async Task 사유_분류는_일정_변경_활동에_남고_사용_중지된_분류는_거절한다()
    {
        int projectId, code;
        using (var s = _db.NewScope())
        {
            var reasons = await s.Projects.DelayReasonsAsync();
            Assert.True(reasons.Count >= 5);
            code = reasons[0].Id;
            var p = await s.Db.Projects.Include(x => x.Stages).FirstAsync(x => x.State == ProjectState.Active && x.Stages.Any(st => st.TargetDate != null && st.CompletedDate == null));
            projectId = p.Id;
            var st = p.Stages.First(x => x.TargetDate != null && x.CompletedDate == null);
            await s.Projects.SaveStageAsync(new StageInput(p.Id, st.Id, st.Name, st.TargetDate!.Value.AddDays(5), null, "자재 지연", false, st.Version, ReasonCode: code));
        }
        using (var s = _db.NewScope())
        {
            var act = await s.Db.Activities.Where(a => a.ProjectId == projectId && a.Type == ActivityType.TargetChanged).OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal(code, act.ReasonCode);
            var changes = await Analytics(s).ChangesAsync(12, new AnalyticsFilter());
            Assert.Contains(changes.Pareto, r => r.Code == code && r.Count == 1);

            await s.Masters.ToggleAsync(new MasterRef("reason", 1, code, 0, false));
            Assert.DoesNotContain(await s.Projects.DelayReasonsAsync(), r => r.Id == code);
            var p = await s.Db.Projects.Include(x => x.Stages).FirstAsync(x => x.Id == projectId);
            var st = p.Stages.First(x => x.TargetDate != null && x.CompletedDate == null);
            var ex = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveStageAsync(new StageInput(p.Id, st.Id, st.Name, st.TargetDate!.Value.AddDays(1), null, "또", false, st.Version, ReasonCode: code)));
            Assert.Equal("reasonCode", ex.Field);
        }
    }

    [Fact]
    public void 진척_증감은_단계_완료와_완료_취소도_단계_비중으로_센다()
    {
        var p = new Project();
        var s1 = new Stage { Id = 1 };                 // 작업 없는 단계
        var s2 = new Stage { Id = 2 };
        s2.Works.Add(new WorkItem { Id = 21 });
        p.Stages.AddRange(new[] { s1, s2, new Stage { Id = 3 }, new Stage { Id = 4 } });
        var acts = new[]
        {
            new Activity { Type = ActivityType.StageCompleted, StageId = 1, OldValue = "0", NewValue = "2026-10-08" },   // 0 → 100: 과제 +25
            new Activity { Type = ActivityType.StageReopened, StageId = 2, OldValue = "2026-10-01", NewValue = "60" },   // 100 → 60: 과제 -10
            new Activity { Type = ActivityType.StageCompleted, StageId = 9, OldValue = "0" },                            // 지운 단계는 뺀다
            new Activity { Type = ActivityType.StageCompleted, StageId = 3, NewValue = "2026-10-08" },                   // 값 없는 옛 기록은 셀 수 없다
        };
        Assert.Equal(15, Rules.ProgressGain(p, Array.Empty<(int, int, int)>(), Reporting.StageProgressChanges(acts)));
    }

    [Fact]
    public async Task 작업_없이_단계를_완료하면_보고의_진척이_오르고_완료를_취소하면_돌아간다()
    {
        int projectId, stageId, before, stages;
        int Gain(WeeklyReport r) => r.Projects.Single(x => x.Id == projectId).ProgressGain;
        using (var s = _db.NewScope())
        {
            var p = await s.Db.Projects.Include(x => x.Stages).ThenInclude(st => st.Works).Include(x => x.Stages).ThenInclude(st => st.Checks)
                .Where(x => x.State == ProjectState.Active).ToListAsync();
            var (proj, st) = p.SelectMany(x => x.Stages.Where(y => y.CompletedDate == null && Rules.WorksProgress(y) <= 50).Select(y => (x, y))).First();
            projectId = proj.Id;
            stageId = st.Id;
            before = Rules.WorksProgress(st);
            stages = proj.Stages.Count;
            foreach (var c in st.Checks) c.IsDone = true;
            await s.Db.SaveChangesAsync();
        }
        int g0;
        using (var s = _db.NewScope()) g0 = Gain(await s.Insights.WeeklyAsync(null, false));
        using (var s = _db.NewScope())
        {
            var st = await s.Db.Stages.FirstAsync(x => x.Id == stageId);
            // 작업을 닫지 않고 완료 — 작업 진행률 변경 기록은 없지만 단계 진행률은 100 이 된다
            await s.Projects.SaveStageAsync(new StageInput(projectId, st.Id, st.Name, st.TargetDate, _db.Clock.Today, null, false, st.Version));
            var act = await s.Db.Activities.Where(a => a.StageId == stageId && a.Type == ActivityType.StageCompleted).OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal(before.ToString(), act.OldValue);
        }
        int g1;
        using (var s = _db.NewScope()) g1 = Gain(await s.Insights.WeeklyAsync(null, false));
        var expected = (100 - before) / (double)stages;
        Assert.InRange(g1 - g0, (int)Math.Floor(expected) - 1, (int)Math.Ceiling(expected) + 1);
        Assert.True(g1 > g0);
        using (var s = _db.NewScope())
        {
            var st = await s.Db.Stages.FirstAsync(x => x.Id == stageId);
            await s.Projects.SaveStageAsync(new StageInput(projectId, st.Id, st.Name, st.TargetDate, null, null, false, st.Version));
        }
        using (var s = _db.NewScope())
            Assert.InRange(Gain(await s.Insights.WeeklyAsync(null, false)) - g0, -1, 1);   // 올랐다가 같은 만큼 내려간다
    }

    [Fact]
    public async Task 진행률_변경은_이전_새_값을_남기고_보고에_과제별_진척으로_오른다()
    {
        int projectId;
        using (var s = _db.NewScope())
        {
            var w = await s.Db.WorkItems.Include(x => x.Stage).ThenInclude(st => st!.Project)
                .Where(x => x.ActualEnd == null && x.Stage!.CompletedDate == null && x.Stage.Project!.State == ProjectState.Active && x.Progress < 50).FirstAsync();
            projectId = w.Stage!.ProjectId;
            var old = w.Progress;
            await s.Projects.UpdateWorkProgressAsync(w.Id, w.Version, old + 50);
            var act = await s.Db.Activities.Where(a => a.WorkItemId == w.Id && a.Type == ActivityType.WorkEdited).OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal(old.ToString(), act.OldValue);
            Assert.Equal((old + 50).ToString(), act.NewValue);
        }
        using (var s = _db.NewScope())
        {
            var report = await s.Insights.WeeklyAsync(null, false);
            var p = report.Projects.Single(x => x.Id == projectId);
            Assert.True(p.ProgressGain > 0);
            Assert.StartsWith("진척 +", p.GainText);
        }
    }

    [Fact]
    public async Task 기간_보고는_조직_범위와_다음_기간_길이를_따르고_스냅샷이_있으면_전주_대비를_싣는다()
    {
        using var s = _db.NewScope();
        var group = await s.Db.OrgUnits.FirstAsync(o => o.Name == "물류기술그룹");
        var today = _db.Clock.Today;
        var month = Reporting.Period("month", today, null, null, today);
        var all = await s.Insights.ReportAsync(month.From, month.To, new ProjectFilter(null, null, null, null, false), "month");
        Assert.Equal("월간 보고", all.Title);
        Assert.Equal(month.From.AddMonths(2).AddDays(-1), all.NextTo);   // 다음 달 말일(같은 일수를 더하지 않는다)
        Assert.Null(all.ActiveDelta);           // 스냅샷이 없으면 비운다
        var org = await s.Insights.ReportAsync(month.From, month.To, new ProjectFilter(null, null, null, null, false, OrgUnitId: group.Id), "month");
        Assert.True(org.Projects.Count <= all.Projects.Count && org.Projects.Count > 0);
        Assert.All(org.Projects, p => Assert.NotEqual("박설비", p.Owner));
        var facility = await s.Db.OrgUnits.FirstAsync(o => o.Name == "설비기술그룹");
        var other = await s.Insights.ReportAsync(month.From, month.To, new ProjectFilter(null, null, null, null, false, OrgUnitId: facility.Id), "month");
        Assert.All(other.Projects, p => Assert.Equal("박설비", p.Owner));
        Assert.Equal(all.Projects.Count, org.Projects.Count + other.Projects.Count + all.Projects.Count(p => p.Owner is not ("김물류" or "이자동" or "최모델" or "박설비")));
        Assert.All(all.DelaySummary!, x => Assert.True(x.DelayDays > 0 || x.RiskDays > 0));

        // 지난주 금요일 스냅샷: 지금 지연인 과제 하나를 그때는 지연이 아니었다고 두면 '새로 지연', 그 반대는 '지연 해소'
        var week = Reporting.WeekOf(today);
        var report0 = await s.Insights.WeeklyAsync(null, false);
        var delayed = report0.Projects.First(p => p.DelayDays > 0);
        var calm = report0.Projects.First(p => p.DelayDays == 0 && p.StateLabel == "진행");
        s.Db.ProjectSnapshots.AddRange(
            new ProjectSnapshot { ProjectId = delayed.Id, Date = week.From.AddDays(-3), State = ProjectState.Active, DelayDays = 0, CategoryId = 1, CorporationId = 1 },
            new ProjectSnapshot { ProjectId = calm.Id, Date = week.From.AddDays(-3), State = ProjectState.Active, DelayDays = 4, CategoryId = 1, CorporationId = 1 });
        await s.Db.SaveChangesAsync();
        var report = await s.Insights.WeeklyAsync(null, false);
        Assert.Contains(report.NewDelayed!, f => f.ProjectId == delayed.Id);
        Assert.Contains(report.Resolved!, f => f.ProjectId == calm.Id);
        Assert.Equal(report.Active - 2, report.ActiveDelta);
        Assert.Equal(report.Delayed - 1, report.DelayedDelta);
    }

    [Fact]
    public async Task 리드타임에_진행_과제의_지금_단계_체류를_싣는다()
    {
        using var s = _db.NewScope();
        var groups = await s.Insights.LeadTimesAsync();
        var active = await s.Db.Projects.CountAsync(p => p.State == ProjectState.Active && p.DeletedAt == null && p.Stages.Any(st => st.CompletedDate == null));
        Assert.Equal(active, groups.Sum(g => g.Rows.Sum(r => r.Active)));
        Assert.All(groups, g => Assert.Equal(g.Rows.Sum(r => r.ActiveOver), g.Over!.Count));
    }

    [Fact]
    public async Task 부하_화면은_조직_트리_순서로_사람을_묶고_이름만_있는_담당자도_포함한다()
    {
        using var s = _db.NewScope();
        var view = await Analytics(s).LoadAsync(12, null);
        Assert.Equal(12, view.WeekCount);
        var people = view.Sections.SelectMany(x => x.People).ToList();
        Assert.NotEmpty(people);
        Assert.Contains(people, p => !p.HasIp);
        var group = await s.Db.OrgUnits.FirstAsync(o => o.Name == "설비기술그룹");
        var narrowed = await Analytics(s).LoadAsync(8, group.Id);
        Assert.All(narrowed.Sections.SelectMany(x => x.People), p => Assert.Equal("박설비", p.Name));
        Assert.True((await s.Insights.MyAsync(people[0].UserId)).Late.Count + (await s.Insights.MyAsync(people[0].UserId)).Soon.Count
                    + (await s.Insights.MyAsync(people[0].UserId)).Later.Count > 0);
    }

    [Fact]
    public async Task 변경_이력_Excel_은_지금_조건_그대로_모든_행을_싣는다()
    {
        using var s = _db.NewScope();
        var filter = new HistoryFilter(_db.Clock.Today.AddDays(-60), _db.Clock.Today, null, "schedule", null, 1);
        var page = await s.Insights.HistoryAsync(filter);
        var all = await s.Insights.HistoryExportAsync(filter);
        Assert.Equal(page.Total, all.Rows.Count);
        Assert.All(all.Rows, r => Assert.Equal("schedule", r.Kind));
        var bytes = new ExcelExporter().History(all.Rows, "구분: 일정 변경", all.Total);
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var ws = book.Worksheet("변경 이력");
        Assert.Contains("구분: 일정 변경", ws.Cell(2, 1).GetString());
        Assert.Equal(all.Rows.Count, ws.RowsUsed().Count() - 3);       // 제목·부제·머리글
    }

    [Fact]
    public async Task 보고_Excel_첫_장은_요약이다()
    {
        using var s = _db.NewScope();
        var report = await s.Insights.WeeklyAsync(null, false);
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(new ExcelExporter().Weekly(report)));
        Assert.Equal("요약", book.Worksheets.First().Name);
        var text = string.Join("\n", book.Worksheet("요약").CellsUsed().Select(c => c.GetString()));
        Assert.Contains("지연·위험 요약", text);
        Assert.Contains("이번 주 미달", text);
        Assert.NotEmpty(new PdfExporter(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Weekly(report));
    }
}
