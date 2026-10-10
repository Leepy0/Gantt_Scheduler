using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 일정이 지켜야 할 불변식 — 순수 규칙 테스트와 DB 서비스 테스트가 함께 쓴다
public static class ScheduleAssert
{
    // 미착수 작업(완료 단계 제외)은 선행 위반이 없고, 모든 작업은 근무일 기간이 1일 이상이다
    public static void AssertSchedulable(Project p, WorkCalendar cal)
    {
        var stageOf = p.Stages.SelectMany(s => s.Works.Select(w => (w, s))).ToDictionary(x => x.w, x => x.s);
        foreach (var w in stageOf.Keys)
            Assert.True(cal.WorkingDays(w.PlanStart, w.PlanEnd) > 0, $"근무일 기간이 0: {w.Title} {w.PlanStart}~{w.PlanEnd}");
        foreach (var l in p.Links)
        {
            var from = l.Predecessor ?? stageOf.Keys.First(w => w.Id == l.PredecessorId);
            var to = l.Successor ?? stageOf.Keys.First(w => w.Id == l.SuccessorId);
            if (Rules.IsMovable(to, stageOf[to]))
                Assert.False(Rules.Violates(l, from, to, cal), $"의존 위반: {from.Title}({from.PlanEnd}) → {to.Title}({to.PlanStart})");
        }
    }

    // 화면 모델 + 저장된 이력 기준: 위반 없음, 근무일 기간 > 0, 같은 묶음(BatchId) 안에서 작업당 활동 1건
    public static async Task AssertSchedulableAsync(TestDb db, int projectId)
    {
        using var s = db.NewScope();
        var view = (await s.Projects.ViewAsync(projectId))!;
        foreach (var w in view.Stages.SelectMany(x => x.Works))
        {
            Assert.True(w.PlanWorkDays > 0, $"근무일 기간이 0: {w.Title}");
            Assert.False(w.DepViolation, $"의존 위반이 남음: {w.Title} {w.PlanStart}~{w.PlanEnd}");
        }
        var acts = await s.Db.Activities.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.BatchId != null && a.WorkItemId != null).ToListAsync();
        foreach (var g in acts.GroupBy(a => (a.BatchId, a.WorkItemId)))
            Assert.True(g.Count() == 1, $"같은 묶음에 작업 활동이 {g.Count()}건: " + string.Join(" / ", g.Select(a => a.Text)));
    }
}

// 일정 엔진 순수 함수: 근무일 달력, 의존 밀기(Reschedule), 날짜 범위, 완료 단계 판정
public class ScheduleRulesTests
{
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static WorkItem W(int id, string title, DateOnly start, DateOnly end, DateOnly? actualStart = null) =>
        new() { Id = id, Title = title, PlanStart = start, PlanEnd = end, ActualStart = actualStart };

    private static Project ProjectOf(params WorkItem[] works)
    {
        var p = new Project { State = ProjectState.Active, StartDate = Day(10, 1) };
        var stage = new Stage { Id = 1, Name = "단계", SortOrder = 1 };
        foreach (var w in works) stage.Works.Add(w);
        p.Stages.Add(stage);
        return p;
    }

    private static void Link(Project p, WorkItem from, WorkItem to, int lag = 0) =>
        p.Links.Add(new WorkLink { PredecessorId = from.Id, SuccessorId = to.Id, LagDays = lag });

    private static void Apply(IEnumerable<Rules.ScheduleMove> moves)
    {
        foreach (var m in moves) { m.Work.PlanStart = m.NewStart; m.Work.PlanEnd = m.NewEnd; }
    }

    // ── 근무일 달력 ─────────────────────────────────────────────────────

    // 바꾸기 전의 하루씩 세는 구현 — 새 구현이 같은 답을 내는지 비교하는 기준
    private sealed class NaiveCalendar
    {
        private readonly HashSet<DateOnly> _h;
        public NaiveCalendar(IEnumerable<DateOnly> h) => _h = new HashSet<DateOnly>(h);
        public bool IsWorkingDay(DateOnly d) => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday && !_h.Contains(d);
        public int WorkingDays(DateOnly from, DateOnly to)
        {
            var n = 0;
            for (var d = from; d <= to; d = d.AddDays(1)) if (IsWorkingDay(d)) n++;
            return n;
        }
        public DateOnly AddWorkingDays(DateOnly d, int n)
        {
            var step = n < 0 ? -1 : 1;
            var left = Math.Abs(n);
            while (left > 0) { d = d.AddDays(step); if (IsWorkingDay(d)) left--; }
            return d;
        }
        public int Distance(DateOnly a, DateOnly b) => b >= a ? WorkingDays(a.AddDays(1), b) : -WorkingDays(b.AddDays(1), a);
    }

    [Fact]
    public void 근무일_계산은_하루씩_세던_결과와_같다()
    {
        Assert.Equal(DayOfWeek.Monday, DateOnly.MinValue.DayOfWeek);    // 주 단위 산술의 전제
        var rnd = new Random(20261009);
        var baseDay = new DateOnly(2025, 1, 1);
        for (var round = 0; round < 40; round++)
        {
            // 평일·주말이 섞인 공휴일, 연속 공휴일 묶음도 넣는다
            var holidays = new List<DateOnly>();
            for (var i = 0; i < rnd.Next(0, 40); i++) holidays.Add(baseDay.AddDays(rnd.Next(0, 1200)));
            var run = baseDay.AddDays(rnd.Next(0, 1200));
            for (var i = 0; i < rnd.Next(0, 12); i++) holidays.Add(run.AddDays(i));
            var cal = new WorkCalendar(holidays);
            var naive = new NaiveCalendar(holidays);

            for (var i = 0; i < 150; i++)
            {
                var a = baseDay.AddDays(rnd.Next(-30, 1230));
                var b = a.AddDays(rnd.Next(-200, 200));
                var n = rnd.Next(-150, 150);
                Assert.Equal(naive.IsWorkingDay(a), cal.IsWorkingDay(a));
                Assert.Equal(naive.WorkingDays(a, b), cal.WorkingDays(a, b));
                Assert.Equal(naive.AddWorkingDays(a, n), cal.AddWorkingDays(a, n));
                Assert.Equal(naive.Distance(a, b), cal.Distance(a, b));
                Assert.Equal(naive.AddWorkingDays(a, 1 + Math.Max(0, n % 4)), cal.EarliestStartAfter(a, n % 4));
                Assert.Equal(naive.IsWorkingDay(a) ? a : naive.AddWorkingDays(a, 1), cal.NextWorkingDay(a));
            }
        }
    }

    [Fact]
    public void 근무일_계산은_날짜_끝에서_넘치지_않는다()
    {
        var cal = new WorkCalendar(new[] { new DateOnly(2026, 10, 9) });
        var max = DateOnly.MaxValue;
        var min = DateOnly.MinValue;

        Assert.True(cal.WorkingDays(Day(10, 12), max) > 0);
        Assert.Equal(0, cal.WorkingDays(max, Day(10, 12)));
        Assert.True(cal.Distance(min, max) > 0);
        Assert.Equal(max, cal.AddWorkingDays(max.AddDays(-3), 10));        // 범위를 넘으면 끝에서 멈춘다
        Assert.Equal(max, cal.EarliestStartAfter(max, 0));
        Assert.Equal(min, cal.AddWorkingDays(min.AddDays(2), -10));
        Assert.Equal(min.AddDays(1), cal.AddWorkingDays(min, 1));         // 0001-01-01(월) 다음 근무일은 화요일
    }

    [Fact]
    public void 근무일_거리_옮기기는_기준일_사이의_근무일_간격을_지킨다()
    {
        var cal = new WorkCalendar(new[] { Day(10, 5), Day(10, 9) });
        // 같은 기준이면 근무일은 그대로, 휴일은 직전 근무일 자리
        Assert.Equal(Day(10, 14), cal.Shift(Day(10, 14), Day(10, 1), Day(10, 1)));
        Assert.Equal(Day(10, 16), cal.Shift(Day(10, 18), Day(10, 1), Day(10, 1)));
        // 10-01 → 10-12 는 근무일 5일(10-05·10-09 공휴일). 11-02 에서 5근무일 뒤는 11-09
        Assert.Equal(Day(11, 9), cal.Shift(Day(10, 12), Day(10, 1), Day(11, 2)));
        Assert.Equal(cal.Distance(Day(10, 1), Day(10, 12)), cal.Distance(Day(11, 2), cal.Shift(Day(10, 12), Day(10, 1), Day(11, 2))));
        // 기준보다 앞선 날짜도 같은 간격으로
        Assert.Equal(Day(10, 28), cal.Shift(Day(9, 30), Day(10, 1), Day(10, 29)));
    }

    // ── 의존 밀기 ───────────────────────────────────────────────────────

    // A→B, A→C, B→D, C→D, D→E : 선행이 모이는 D 는 두 선행이 모두 정해진 뒤 한 번만 계산해야 E 까지 맞게 밀린다
    private static (Project P, WorkItem A, WorkItem B, WorkItem C, WorkItem D, WorkItem E) Diamond()
    {
        var a = W(1, "A", Day(10, 12), Day(10, 14));           // 월~수
        var b = W(2, "B", Day(10, 15), Day(10, 21));           // 5근무일
        var c = W(3, "C", Day(10, 15), Day(10, 22));           // 6근무일
        var d = W(4, "D", Day(10, 23), Day(10, 26));           // 금~월 2근무일
        var e = W(5, "E", Day(10, 27), Day(10, 27));
        var p = ProjectOf(a, b, c, d, e);
        Link(p, a, b); Link(p, a, c); Link(p, b, d); Link(p, c, d); Link(p, d, e);
        return (p, a, b, c, d, e);
    }

    [Fact]
    public void 선행이_모이는_작업은_가장_늦은_선행에_맞춰_한_번만_밀린다()
    {
        var cal = WorkCalendar.WeekendsOnly;
        var (p, a, b, c, d, e) = Diamond();
        ScheduleAssert.AssertSchedulable(p, cal);
        Assert.Empty(Rules.Reschedule(p, cal));                // 이미 맞는 일정은 그대로

        a.PlanEnd = Day(10, 16);                                // A 를 금요일까지 늘린다
        var moves = Rules.Reschedule(p, cal, new[] { a });

        // 순수 함수: 엔티티는 아직 그대로
        Assert.Equal(Day(10, 15), b.PlanStart);
        Assert.Equal(new[] { "B", "C", "D", "E" }, moves.Select(m => m.Work.Title).OrderBy(t => t).ToArray());
        Assert.Equal(moves.Count, moves.Select(m => m.Work).Distinct().Count());   // 작업당 한 번

        var byTitle = moves.ToDictionary(m => m.Work.Title);
        Assert.Equal((Day(10, 19), Day(10, 23)), (byTitle["B"].NewStart, byTitle["B"].NewEnd));
        Assert.Equal((Day(10, 19), Day(10, 26)), (byTitle["C"].NewStart, byTitle["C"].NewEnd));
        Assert.Equal((Day(10, 27), Day(10, 28)), (byTitle["D"].NewStart, byTitle["D"].NewEnd));     // C 기준(늦은 쪽)
        Assert.Same(c, byTitle["D"].Cause);
        Assert.Equal((Day(10, 29), Day(10, 29)), (byTitle["E"].NewStart, byTitle["E"].NewEnd));
        Assert.Equal((Day(10, 23), Day(10, 26)), (byTitle["D"].OldStart, byTitle["D"].OldEnd));

        Apply(moves);
        ScheduleAssert.AssertSchedulable(p, cal);
        Assert.Empty(Rules.Reschedule(p, cal));
    }

    [Fact]
    public void 시작했거나_완료된_단계의_작업은_밀지_않고_당기지도_않는다()
    {
        var cal = WorkCalendar.WeekendsOnly;
        var (p, a, b, c, d, e) = Diamond();
        b.ActualStart = Day(10, 15);                            // B 는 이미 시작
        a.PlanEnd = Day(10, 16);
        var moves = Rules.Reschedule(p, cal, new[] { a });
        Assert.DoesNotContain(moves, m => m.Work == b);
        Assert.Contains(moves, m => m.Work == c);

        // 선행이 당겨져도 후행을 앞으로 끌어오지 않는다
        var (p2, a2, _, _, _, _) = Diamond();
        a2.PlanEnd = Day(10, 12);
        Assert.Empty(Rules.Reschedule(p2, cal, new[] { a2 }));

        // 완료된 단계의 작업은 단계와 함께 끝난 것으로 본다
        var (p3, a3, _, _, _, _) = Diamond();
        p3.Stages[0].CompletedDate = Day(10, 20);
        a3.PlanEnd = Day(10, 16);
        Assert.Empty(Rules.Reschedule(p3, cal, new[] { a3 }));
    }

    [Fact]
    public void 주말과_공휴일을_건너_다음_근무일로_밀고_근무일_기간을_지킨다()
    {
        var cal = new WorkCalendar(new[] { Day(10, 9) });       // 10-09(금) 한글날
        var a = W(1, "A", Day(10, 14), Day(10, 16));            // 수~금
        var b = W(2, "B", Day(10, 15), Day(10, 16));            // 2근무일
        var weekendOnly = W(3, "주말 작업", Day(10, 17), Day(10, 18));   // 토~일: 근무일 0 → 1일로 본다
        var c = W(4, "C", Day(10, 6), Day(10, 8));              // 화~목, 공휴일 앞에서 끝남
        var d = W(5, "D", Day(10, 8), Day(10, 12));             // 목·월 2근무일 (10-09 공휴일)
        var p = ProjectOf(a, b, weekendOnly, c, d);
        Link(p, a, b);
        Link(p, a, weekendOnly, lag: 1);
        Link(p, c, d);

        var moves = Rules.Reschedule(p, cal).ToDictionary(m => m.Work.Title);
        Assert.Equal((Day(10, 19), Day(10, 20)), (moves["B"].NewStart, moves["B"].NewEnd));                 // 금 종료 → 월 시작
        Assert.Equal((Day(10, 20), Day(10, 20)), (moves["주말 작업"].NewStart, moves["주말 작업"].NewEnd));   // 간격 1근무일, 기간 1일
        Assert.Equal((Day(10, 12), Day(10, 13)), (moves["D"].NewStart, moves["D"].NewEnd));                 // 공휴일 건너 월요일, 2근무일
        Apply(moves.Values);
        ScheduleAssert.AssertSchedulable(p, cal);
    }

    [Fact]
    public void 순환이_있어도_멈추고_순환_밖의_작업은_계산한다()
    {
        var cal = WorkCalendar.WeekendsOnly;
        var a = W(1, "A", Day(10, 12), Day(10, 16));
        var b = W(2, "B", Day(10, 13), Day(10, 14));
        var x = W(3, "X", Day(10, 12), Day(10, 12));
        var y = W(4, "Y", Day(10, 12), Day(10, 12));
        var p = ProjectOf(a, b, x, y);
        Link(p, a, b);
        Link(p, x, y);
        Link(p, y, x);                                          // 입력 단계에서 막히지만 방어
        var moves = Rules.Reschedule(p, cal);
        Assert.Equal(new[] { "B" }, moves.Select(m => m.Work.Title).ToArray());
    }

    [Fact]
    public void 저장_전_작업과_연결도_탐색_속성으로_찾는다()
    {
        // 복제·샘플 데이터처럼 Id 가 아직 0 인 작업
        var cal = WorkCalendar.WeekendsOnly;
        var a = W(0, "A", Day(10, 12), Day(10, 16));
        var b = W(0, "B", Day(10, 13), Day(10, 14));
        var p = ProjectOf(a, b);
        p.Links.Add(new WorkLink { Predecessor = a, Successor = b });
        var m = Assert.Single(Rules.Reschedule(p, cal));
        Assert.Same(b, m.Work);
        Assert.Equal((Day(10, 19), Day(10, 20)), (m.NewStart, m.NewEnd));
    }

    // 시드 고정 무작위 DAG: 전체 재계산 뒤 위반 없음 / 기간 보존 / 뒤로만 / 작업당 한 번 / 다시 돌리면 변화 없음.
    // 그 상태에서 작업 하나를 늘리고 그 작업부터 재계산해도 전체에 위반이 남지 않는다
    [Fact]
    public void 무작위_의존_그래프에서도_일정_불변식이_지켜진다()
    {
        for (var seed = 1; seed <= 300; seed++)
        {
            var rnd = new Random(seed);
            var holidays = Enumerable.Range(0, rnd.Next(0, 6)).Select(_ => Day(10, 1).AddDays(rnd.Next(0, 90))).ToList();
            var cal = new WorkCalendar(holidays);
            var p = new Project { State = ProjectState.Active, StartDate = Day(10, 1) };
            var stages = Enumerable.Range(1, rnd.Next(1, 4)).Select(i => new Stage { Id = i, Name = "S" + i, SortOrder = i }).ToList();
            foreach (var st in stages) p.Stages.Add(st);
            var works = new List<WorkItem>();
            var n = rnd.Next(2, 14);
            for (var i = 0; i < n; i++)
            {
                // 입력 화면처럼 근무일에 시작하고 근무일 1일 이상인 작업 (주말에 끝나는 작업은 섞인다)
                var start = cal.NextWorkingDay(Day(10, 1).AddDays(rnd.Next(0, 40)));
                var w = W(i + 1, "W" + (i + 1), start, start.AddDays(rnd.Next(0, 10)),
                    rnd.NextDouble() < 0.15 ? start : null);
                works.Add(w);
                stages[rnd.Next(stages.Count)].Works.Add(w);
            }
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    if (rnd.NextDouble() < 0.3) Link(p, works[i], works[j], rnd.NextDouble() < 0.2 ? rnd.Next(1, 3) : 0);

            var before = works.ToDictionary(w => w, w => (w.PlanStart, Days: Rules.WorkDays(w, cal)));
            var moves = Rules.Reschedule(p, cal);
            Assert.Equal(moves.Count, moves.Select(m => m.Work).Distinct().Count());
            foreach (var m in moves)
            {
                Assert.True(m.NewStart > before[m.Work].PlanStart, $"seed {seed}: 앞으로 당김");
                Assert.Null(m.Work.ActualStart);
                Assert.Equal(before[m.Work].Days, cal.WorkingDays(m.NewStart, m.NewEnd));
            }
            Apply(moves);
            ScheduleAssert.AssertSchedulable(p, cal);
            Assert.Empty(Rules.Reschedule(p, cal));

            var changed = works[rnd.Next(n)];
            changed.PlanEnd = cal.AddWorkingDays(changed.PlanEnd, rnd.Next(1, 8));
            Apply(Rules.Reschedule(p, cal, new[] { changed }));
            ScheduleAssert.AssertSchedulable(p, cal);
        }
    }

    // ── 날짜 범위 ───────────────────────────────────────────────────────

    [Fact]
    public void 날짜는_절대_범위와_착수일_기준_범위_안에서만_받는다()
    {
        var start = Day(10, 1);
        Assert.Equal((new DateOnly(2025, 10, 1), new DateOnly(2036, 10, 1)), Rules.DateRange(start));
        Rules.ValidateDate(new DateOnly(2025, 10, 1), start, "목표일", "targetDate");
        Rules.ValidateDate(new DateOnly(2036, 10, 1), start, "목표일", "targetDate");
        Rules.ValidateDate(null, start, "목표일", "targetDate");

        var early = Assert.Throws<RuleException>(() => Rules.ValidateDate(new DateOnly(2025, 9, 30), start, "목표일", "targetDate"));
        Assert.Equal("targetDate", early.Field);
        Assert.Contains("2025-10-01 ~ 2036-10-01", early.Message);
        Assert.Throws<RuleException>(() => Rules.ValidateDate(new DateOnly(2036, 10, 2), start, "목표일", "targetDate"));
        Assert.Throws<RuleException>(() => Rules.ValidateDate(DateOnly.MaxValue, start, "목표 종료일", "planEnd"));

        // 절대 범위가 먼저: 2095 년 착수여도 2099-12-31 을 넘지 못한다
        Assert.Equal(new DateOnly(2099, 12, 31), Rules.DateRange(new DateOnly(2095, 1, 1)).Max);
        // 범위 밖 옛 데이터의 착수일이어도 계산이 넘치지 않는다
        Assert.Equal(Rules.MaxDate, Rules.DateRange(DateOnly.MaxValue).Max);

        var today = Day(10, 9);
        Rules.ValidateStartDate(new DateOnly(2016, 10, 9), today);
        var startEx = Assert.Throws<RuleException>(() => Rules.ValidateStartDate(new DateOnly(2062, 10, 1), today));
        Assert.Equal("startDate", startEx.Field);
        Assert.Throws<RuleException>(() => Rules.ValidateStartDate(new DateOnly(2016, 10, 8), today));
    }

    // ── 완료 단계의 작업 ─────────────────────────────────────────────────

    [Fact]
    public void 완료된_단계의_작업은_지연과_임계에서_빠진다()
    {
        var today = Day(10, 20);
        var w = W(1, "남은 작업", Day(10, 5), Day(10, 9));
        var open = new Stage { Id = 1, TargetDate = Day(10, 9), Works = { w } };
        Assert.True(Rules.IsOpen(w, open));
        Assert.True(Rules.IsLate(w, open, today));
        Assert.True(Views.Work(w, today, WorkCalendar.WeekendsOnly, stage: open).Late);

        open.CompletedDate = Day(10, 12);
        Assert.False(Rules.IsOpen(w, open));
        Assert.False(Rules.IsLate(w, open, today));
        Assert.False(Rules.IsMovable(w, open));
        var view = Views.Work(w, today, WorkCalendar.WeekendsOnly, new Dictionary<int, int> { [1] = -3 }, stage: open);
        Assert.False(view.Late);
        Assert.False(view.Critical);
    }
}

// 일정 엔진을 실제 DB 에서 — 서비스가 Reschedule 결과를 저장하고 이력을 남기는 방식
public class ScheduleServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public ScheduleServiceTests(TestDb db) => _db = db;

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

    private static WorkInput Work(int? id, int stageId, string title, DateOnly start, DateOnly end,
        List<int>? pre = null, string? reason = null, int? version = null, DateOnly? actualStart = null) =>
        new(id, stageId, title, null, null, start, end, actualStart, null, 0, null, reason, version, pre);

    private async Task<int> AddWorkAsync(int projectId, int stageId, string title, DateOnly start, DateOnly end, params int[] pre)
    {
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(null, stageId, title, start, end, pre.ToList()));
        return (await ViewAsync(projectId)).Stages.SelectMany(x => x.Works).Single(w => w.Title == title).Id;
    }

    private static WorkView WorkOf(ProjectView p, string title) => p.Stages.SelectMany(s => s.Works).Single(w => w.Title == title);

    [Fact]
    public async Task 선행이_모이는_다이아몬드에서_끝까지_밀고_작업당_이력은_1건()
    {
        var id = await NewProjectAsync("테스트 다이아몬드");
        var stage = (await ViewAsync(id)).Stages[0].Id;
        var a = await AddWorkAsync(id, stage, "A", Day(10, 12), Day(10, 14));
        var b = await AddWorkAsync(id, stage, "B", Day(10, 15), Day(10, 21), a);
        var c = await AddWorkAsync(id, stage, "C", Day(10, 15), Day(10, 22), a);
        var d = await AddWorkAsync(id, stage, "D", Day(10, 23), Day(10, 26), b, c);
        await AddWorkAsync(id, stage, "E", Day(10, 27), Day(10, 27), d);

        var version = WorkOf(await ViewAsync(id), "A").Version;
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage, "A", Day(10, 12), Day(10, 16), reason: "범위 확대", version: version));

        var p = await ViewAsync(id);
        Assert.Equal((Day(10, 19), Day(10, 23)), (WorkOf(p, "B").PlanStart, WorkOf(p, "B").PlanEnd));
        Assert.Equal((Day(10, 19), Day(10, 26)), (WorkOf(p, "C").PlanStart, WorkOf(p, "C").PlanEnd));
        Assert.Equal((Day(10, 27), Day(10, 28)), (WorkOf(p, "D").PlanStart, WorkOf(p, "D").PlanEnd));
        Assert.Equal((Day(10, 29), Day(10, 29)), (WorkOf(p, "E").PlanStart, WorkOf(p, "E").PlanEnd));
        Assert.Equal(1, WorkOf(p, "D").Version);                // 두 번 밀리지 않고 한 번
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        using var check = _db.NewScope();
        var acts = await check.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.WorkPlanChanged).ToListAsync();
        Assert.Equal(5, acts.Count);                            // A 자신 + B·C·D·E 각 1건
        Assert.Single(acts.Select(x => x.BatchId).Distinct());
        Assert.All(acts, x => Assert.Equal("범위 확대", x.Reason));
        Assert.Contains(acts, x => x.WorkItemId == d && x.Text.Contains("10-23~2026-10-26 → 2026-10-27~2026-10-28") && x.Text.Contains("선행 작업 연동"));
    }

    [Fact]
    public async Task 위반이_남은_작업도_일정과_무관한_수정은_저장된다()
    {
        var id = await NewProjectAsync("테스트 위반 정책");
        var stage = (await ViewAsync(id)).Stages[0].Id;
        var a = await AddWorkAsync(id, stage, "A", Day(10, 12), Day(10, 14));
        var b = await AddWorkAsync(id, stage, "B", Day(10, 15), Day(10, 16), a);

        // 공휴일 추가 등으로 위반이 생긴 상태를 만든다 (A 가 B 시작일까지 늘어난 것과 같다)
        using (var s = _db.NewScope())
        {
            var wa = await s.Db.WorkItems.FirstAsync(w => w.Id == a);
            wa.PlanEnd = Day(10, 15);
            await s.Db.SaveChangesAsync();
        }
        var view = WorkOf(await ViewAsync(id), "B");
        Assert.True(view.DepViolation);

        // 제목·메모·진행률만 고치면 저장된다 (위반은 그대로 표시)
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(b, stage, "B(수정)", null, null, view.PlanStart, view.PlanEnd, null, null, 0, "메모", null, view.Version));
        view = WorkOf(await ViewAsync(id), "B(수정)");
        Assert.True(view.DepViolation);

        // 목표 일정을 바꾸는데 여전히 선행보다 이르면 거절
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveWorkAsync(Work(b, stage, "B(수정)", Day(10, 14), Day(10, 16), reason: "당김", version: view.Version)));
            Assert.Equal("planStart", ex.Field);
        }
        // 선행 집합을 바꿀 때도 다시 검사한다 (A 를 빼면 통과)
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(b, stage, "B(수정)", view.PlanStart, view.PlanEnd, new List<int>(), version: view.Version));
        Assert.False(WorkOf(await ViewAsync(id), "B(수정)").DepViolation);
    }

    [Fact]
    public async Task 복제는_근무일_거리로_옮겨_위반이_없고_근무일_기간을_지킨다()
    {
        var id = await NewProjectAsync("테스트 복제 근무일");
        var stages = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, Day(10, 23), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, stages[1].Id, stages[1].Name, Day(10, 31), null, null, false));   // 토요일
        }
        var a = await AddWorkAsync(id, stages[0].Id, "A", Day(10, 12), Day(10, 14));
        var b = await AddWorkAsync(id, stages[0].Id, "B", Day(10, 15), Day(10, 20), a);         // 금요일 걸침 4근무일
        await AddWorkAsync(id, stages[1].Id, "C", Day(10, 21), Day(10, 23), b);

        var src = await ViewAsync(id);
        var cal = await CalendarAsync();
        // 요일이 바뀌는 여러 착수일(주말 포함)로 복제해 본다
        foreach (var start in new[] { Day(11, 1), Day(11, 4), Day(11, 7), Day(12, 21), new DateOnly(2027, 2, 3) })
        {
            int cloneId;
            using (var s = _db.NewScope())
                cloneId = await s.Projects.CloneAsync(new CloneInput(id, "테스트 복제 근무일 사본", start, true));
            var copy = await ViewAsync(cloneId);
            await ScheduleAssert.AssertSchedulableAsync(_db, cloneId);
            var baseNew = cal.NextWorkingDay(start);
            foreach (var w in src.Stages.SelectMany(x => x.Works))
            {
                var c = WorkOf(copy, w.Title);
                Assert.Equal(w.PlanWorkDays, c.PlanWorkDays);
                Assert.True(cal.IsWorkingDay(c.PlanStart), $"{start}: {w.Title} 시작 {c.PlanStart} 가 휴일");
                Assert.True(c.PlanStart >= baseNew);
                Assert.Equal(cal.Distance(Day(10, 1), w.PlanStart), cal.Distance(baseNew, c.PlanStart));
            }
            // 주말 목표일(10-31 토)은 직전 근무일 자리로 옮긴다
            Assert.Equal(cal.Distance(Day(10, 1), Day(10, 30)), cal.Distance(baseNew, copy.Stages[1].TargetDate!.Value));
            Assert.True(cal.IsWorkingDay(copy.Stages[1].TargetDate!.Value));
        }
    }

    private async Task<WorkCalendar> CalendarAsync()
    {
        using var s = _db.NewScope();
        return await s.Projects.CalendarAsync();
    }

    [Fact]
    public async Task 범위를_벗어난_날짜는_필드와_함께_거절한다()
    {
        var id = await NewProjectAsync("테스트 날짜 범위");
        var p = await ViewAsync(id);
        var stage = p.Stages[0];

        async Task<RuleException> Fails(Func<TestDb.Scope, Task> action)
        {
            using var s = _db.NewScope();
            return await Assert.ThrowsAsync<RuleException>(() => action(s));
        }

        Assert.Equal("targetDate", (await Fails(s => s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, new DateOnly(2620, 10, 1), null, null, false)))).Field);
        Assert.Equal("completedDate", (await Fails(s => s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, null, new DateOnly(1999, 1, 1), null, false)))).Field);
        Assert.Equal("targetDate", (await Fails(s => s.Projects.SaveStageAsync(new StageInput(id, null, "새 단계", DateOnly.MaxValue, null, null, false)))).Field);
        Assert.Equal("planEnd", (await Fails(s => s.Projects.SaveWorkAsync(Work(null, stage.Id, "X", Day(10, 12), DateOnly.MaxValue)))).Field);
        Assert.Equal("planStart", (await Fails(s => s.Projects.SaveWorkAsync(Work(null, stage.Id, "X", new DateOnly(2024, 1, 1), Day(10, 12))))).Field);
        Assert.Equal("actualStart", (await Fails(s => s.Projects.SaveWorkAsync(Work(null, stage.Id, "X", Day(10, 12), Day(10, 13), actualStart: new DateOnly(2040, 1, 1))))).Field);

        // 착수일: 등록·수정·복제
        var cat = p.CategoryId;
        Assert.Equal("startDate", (await Fails(s => s.Projects.CreateAsync(new ProjectInput("테스트 범위 밖", cat, p.LocationId, null, new DateOnly(2062, 1, 1), null, null, null, null)))).Field);
        Assert.Equal("startDate", (await Fails(s => s.Projects.CloneAsync(new CloneInput(id, "테스트 범위 밖 복제", new DateOnly(1990, 1, 1), true)))).Field);

        // 착수일을 옮겨 기존 일정이 범위 밖으로 밀려나면 거절
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, new DateOnly(2030, 1, 1), null, null, false));
        var moved = await Fails(s => s.Projects.UpdateAsync(id, new ProjectInput(p.Title, cat, p.LocationId, null, new DateOnly(2031, 6, 1), null, null, null, null)));
        Assert.Equal("startDate", moved.Field);
        Assert.Contains("2030-01-01", moved.Message);

        // 아무것도 저장되지 않았다
        var after = await ViewAsync(id);
        Assert.Equal(Day(10, 1), after.StartDate);
        Assert.Equal(0, after.WorkCount);
    }

    [Fact]
    public async Task 단계를_완료하면서_남은_작업을_완료일로_닫을_수_있다()
    {
        var id = await NewProjectAsync("테스트 완료 단계 작업");
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "밀린 작업", "테스터", null, Day(10, 1), Day(10, 6), null, null, 0, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "진행 중 작업", "테스터", null, Day(10, 2), Day(10, 7), Day(10, 2), null, 40, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "끝난 작업", "테스터", null, Day(10, 1), Day(10, 2), Day(10, 1), Day(10, 2), 100, null, null));
        }
        var p = await ViewAsync(id);
        Assert.Equal(2, p.Stages[0].OpenWorks);
        Assert.True(WorkOf(p, "밀린 작업").Late);

        // 단계만 완료하면 작업은 그대로지만 지연·내 할 일에서 빠진다
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, null, Day(10, 8), null, false, p.Stages[0].Version));
        p = await ViewAsync(id);
        Assert.False(WorkOf(p, "밀린 작업").Late);
        Assert.Null(WorkOf(p, "밀린 작업").ActualEnd);
        Assert.Equal(0, p.Stages[0].OpenWorks);
        using (var s = _db.NewScope())
        {
            var my = await s.Insights.MyAsync();
            Assert.DoesNotContain(my.Late.Concat(my.Soon).Concat(my.Later), w => w.ProjectId == id);
        }

        // 완료를 취소하고, 이번에는 남은 작업을 닫으며 완료
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, null, null, null, false, p.Stages[0].Version));
        p = await ViewAsync(id);
        Assert.True(WorkOf(p, "밀린 작업").Late);
        var versions = p.Stages[0].Works.ToDictionary(w => w.Title, w => w.Version);
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, null, Day(10, 8), null, false, p.Stages[0].Version, CloseWorks: true));

        p = await ViewAsync(id);
        var late = WorkOf(p, "밀린 작업");
        Assert.Equal((Day(10, 1), Day(10, 8), 100), (late.ActualStart!.Value, late.ActualEnd!.Value, late.Progress));   // 실적 시작은 목표 시작일
        var running = WorkOf(p, "진행 중 작업");
        Assert.Equal((Day(10, 2), Day(10, 8), 100), (running.ActualStart!.Value, running.ActualEnd!.Value, running.Progress));
        Assert.Equal(versions["밀린 작업"] + 1, late.Version);
        Assert.Equal(versions["끝난 작업"], WorkOf(p, "끝난 작업").Version);     // 이미 끝난 작업은 그대로
        Assert.Equal("done", p.Stages[0].Status);

        using var check = _db.NewScope();
        var closed = await check.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.WorkEdited && x.Text.Contains("단계 완료로 닫음")).ToListAsync();
        Assert.Equal(2, closed.Count);
        var completed = await check.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.StageCompleted).OrderByDescending(x => x.Id).FirstAsync();
        Assert.All(closed, x => Assert.Equal(completed.BatchId, x.BatchId));
        await ScheduleAssert.AssertSchedulableAsync(_db, id);
    }

    [Fact]
    public async Task 연쇄_이동에_이후_단계의_미착수_작업을_포함할_수_있다()
    {
        var id = await NewProjectAsync("테스트 연쇄 작업");
        var stages = (await ViewAsync(id)).Stages;      // 물류: 검토, 보고, 적용
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, Day(10, 14), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, stages[1].Id, stages[1].Name, Day(10, 28), null, null, false));
        }
        var first = await AddWorkAsync(id, stages[0].Id, "자료 정리", Day(10, 12), Day(10, 14));
        await AddWorkAsync(id, stages[1].Id, "보고서 작성", Day(10, 15), Day(10, 20), first);       // 앞 단계 작업이 선행
        await AddWorkAsync(id, stages[1].Id, "검토 회의", Day(10, 21), Day(10, 22));
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(null, stages[2].Id, "현장 적용", Day(10, 26), Day(10, 30), actualStart: Day(10, 8)));   // 이미 시작
        var p = await ViewAsync(id);
        Assert.Equal(2, p.Stages[0].LaterWorks);        // 시작한 작업은 세지 않는다

        // +5근무일: 이후 단계 목표일과 미착수 작업이 함께 이동
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, Day(10, 21), null, "자료 보완", true, p.Stages[0].Version, CascadeWorks: true));
        p = await ViewAsync(id);
        Assert.Equal(Day(11, 4), p.Stages[1].TargetDate);
        Assert.Equal((Day(10, 22), Day(10, 27)), (WorkOf(p, "보고서 작성").PlanStart, WorkOf(p, "보고서 작성").PlanEnd));
        Assert.Equal((Day(10, 28), Day(10, 29)), (WorkOf(p, "검토 회의").PlanStart, WorkOf(p, "검토 회의").PlanEnd));
        Assert.Equal(Day(10, 26), WorkOf(p, "현장 적용").PlanStart);
        Assert.Equal((Day(10, 12), Day(10, 14)), (WorkOf(p, "자료 정리").PlanStart, WorkOf(p, "자료 정리").PlanEnd));   // 자기 단계 작업은 그대로
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        using (var check = _db.NewScope())
        {
            var batch = (await check.Db.Activities.Where(x => x.ProjectId == id && x.Type == ActivityType.TargetChanged).OrderByDescending(x => x.Id).FirstAsync()).BatchId;
            var moved = await check.Db.Activities.Where(x => x.ProjectId == id && x.BatchId == batch && x.Type == ActivityType.WorkPlanChanged).ToListAsync();
            Assert.Equal(2, moved.Count);
            Assert.All(moved, x => Assert.Equal("자료 보완", x.Reason));
            Assert.All(moved, x => Assert.Contains("연쇄 이동", x.Text));
        }

        // -8근무일로 당기면 '보고서 작성'은 선행(자료 정리, 10-14 종료)보다 앞서게 되므로 10-15 로 다시 맞춘다
        p = await ViewAsync(id);
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, Day(10, 8), null, "일정 당김", true, p.Stages[0].Version, CascadeWorks: true));
        p = await ViewAsync(id);
        var report = WorkOf(p, "보고서 작성");
        Assert.Equal((Day(10, 15), Day(10, 20)), (report.PlanStart, report.PlanEnd));
        Assert.False(report.DepViolation);
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        // 선택하지 않으면 작업은 그대로
        p = await ViewAsync(id);
        var meeting = WorkOf(p, "검토 회의");
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, stages[0].Name, Day(10, 16), null, "재조정", true, p.Stages[0].Version));
        Assert.Equal(meeting.PlanStart, WorkOf(await ViewAsync(id), "검토 회의").PlanStart);
    }

    // 시드 고정 무작위 시나리오: 서비스로 작업·의존을 만들고 목표 일정을 여러 번 바꿔도 불변식이 지켜진다
    [Fact]
    public async Task 무작위로_일정을_바꿔도_서비스가_불변식을_지킨다()
    {
        var rnd = new Random(7);
        var id = await NewProjectAsync("테스트 무작위 일정");
        var stage = (await ViewAsync(id)).Stages[0].Id;
        var ids = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var pre = ids.Where(_ => rnd.NextDouble() < 0.35).ToArray();
            var view = await ViewAsync(id);
            var cal = await CalendarAsync();
            var earliest = pre.Length == 0 ? Day(10, 12)
                : pre.Select(x => cal.EarliestStartAfter(view.Stages.SelectMany(s => s.Works).Single(w => w.Id == x).PlanEnd, 0)).Max();
            var start = cal.NextWorkingDay(earliest.AddDays(rnd.Next(0, 3)));
            ids.Add(await AddWorkAsync(id, stage, "R" + i, start, cal.AddWorkingDays(start, rnd.Next(0, 5)), pre));
        }
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        for (var round = 0; round < 12; round++)
        {
            var view = await ViewAsync(id);
            var pick = ids[rnd.Next(ids.Count)];
            var w = view.Stages.SelectMany(s => s.Works).Single(x => x.Id == pick);
            var cal = await CalendarAsync();
            var end = cal.AddWorkingDays(w.PlanEnd, rnd.Next(-2, 6));
            if (end < w.PlanStart) end = w.PlanStart;
            if (end == w.PlanEnd) continue;
            using (var s = _db.NewScope())
                await s.Projects.SaveWorkAsync(Work(w.Id, stage, w.Title, w.PlanStart, end, reason: "변경 " + round, version: w.Version));
            await ScheduleAssert.AssertSchedulableAsync(_db, id);
        }
    }
}
