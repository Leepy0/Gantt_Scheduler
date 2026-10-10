using GanttScheduler.Domain;

namespace GanttScheduler.Tests;

// 상태·진행률·예상 종료 계산 규칙
public class RulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static DateOnly D(int offset) => Today.AddDays(offset);

    private static Project NewProject(params Stage[] stages)
    {
        var p = new Project { State = ProjectState.Active, StartDate = D(-30) };
        var order = 0;
        foreach (var s in stages)
        {
            s.SortOrder = ++order;
            p.Stages.Add(s);
        }
        return p;
    }

    private static WorkItem Work(int planStart, int planEnd, int progress = 0, int? actualStart = null, int? actualEnd = null) => new()
    {
        PlanStart = D(planStart), PlanEnd = D(planEnd), Progress = progress,
        ActualStart = actualStart == null ? null : D(actualStart.Value),
        ActualEnd = actualEnd == null ? null : D(actualEnd.Value),
    };

    [Fact]
    public void 단계_진행률은_완료면_100_작업이_있으면_평균_없으면_0()
    {
        var done = new Stage { CompletedDate = D(-1) };
        var withWorks = new Stage { Works = { Work(-5, 5, 40), Work(-5, 5, 75) } };
        var empty = new Stage();

        Assert.Equal(100, Rules.StageProgress(done));
        Assert.Equal(58, Rules.StageProgress(withWorks));   // (40 + 75) / 2 = 57.5 → 58
        Assert.Equal(0, Rules.StageProgress(empty));
    }

    [Fact]
    public void 과제_진행률은_단계_진행률의_평균()
    {
        var p = NewProject(
            new Stage { CompletedDate = D(-10) },
            new Stage { Works = { Work(-5, 5, 50) } },
            new Stage(),
            new Stage());

        Assert.Equal(38, Rules.ProjectProgress(p));   // (100 + 50 + 0 + 0) / 4 = 37.5 → 38
    }

    [Fact]
    public void 단계_상태는_완료_지연_진행중_예정_순으로_판정()
    {
        var done = new Stage { TargetDate = D(-20), CompletedDate = D(-21) };
        var late = new Stage { TargetDate = D(-3) };
        var planned = new Stage { TargetDate = D(10) };
        var p = NewProject(done, late, planned);

        Assert.Equal(StageStatus.Done, Rules.StatusOf(done, p, Today));
        Assert.Equal(StageStatus.Late, Rules.StatusOf(late, p, Today));
        Assert.Equal(StageStatus.Planned, Rules.StatusOf(planned, p, Today));
        Assert.Equal(3, Rules.DelayDays(p, Today));
        Assert.Same(late, Rules.MostDelayedStage(p, Today));

        // 목표일이 오늘이면 아직 지연이 아니다
        late.TargetDate = Today;
        Assert.Equal(StageStatus.Current, Rules.StatusOf(late, p, Today));
        Assert.Equal(0, Rules.DelayDays(p, Today));
    }

    [Fact]
    public void 보류_과제는_지연으로_세지_않는다()
    {
        var p = NewProject(new Stage { TargetDate = D(-10) });
        p.State = ProjectState.Hold;
        Assert.Equal(0, Rules.DelayDays(p, Today));
    }

    [Fact]
    public void 예상_종료는_작업의_가장_늦은_종료이고_밀린_미완료_작업은_최소_오늘()
    {
        var stage = new Stage { TargetDate = D(2), Works = { Work(-10, -5, 100, -10, -6), Work(-4, 6, 30, -4) } };
        Assert.Equal(D(6), Rules.Forecast(stage, Today));
        Assert.Equal(4, Rules.ForecastOverDays(stage, Today));

        // 미완료 작업의 목표 종료가 이미 지났으면 오늘로 본다
        var overdue = new Stage { TargetDate = D(-1), Works = { Work(-10, -3, 60, -10) } };
        Assert.Equal(Today, Rules.Forecast(overdue, Today));
        Assert.Equal(1, Rules.ForecastOverDays(overdue, Today));

        // 작업이 없거나 완료된 단계는 예상 종료가 없다
        Assert.Null(Rules.Forecast(new Stage { TargetDate = D(3) }, Today));
        Assert.Null(Rules.Forecast(new Stage { CompletedDate = D(-1), Works = { Work(-5, 5) } }, Today));
    }

    [Fact]
    public void 작업_상태는_실적_일자로_판정()
    {
        var planned = Work(1, 5);
        var running = Work(-3, 5, 40, -3);
        var overdue = Work(-10, -2, 40, -10);
        var doneLate = Work(-10, -5, 100, -10, -3);

        Assert.Equal(WorkStatus.Planned, Rules.StatusOf(planned));
        Assert.Equal(WorkStatus.InProgress, Rules.StatusOf(running));
        Assert.False(Rules.IsLate(running, Today));
        Assert.True(Rules.IsLate(overdue, Today));
        Assert.Equal(WorkStatus.Done, Rules.StatusOf(doneLate));
        Assert.True(Rules.IsDoneLate(doneLate));
        Assert.False(Rules.IsLate(doneLate, Today));
    }

    [Fact]
    public void 입고_추천일은_검수일에_해상_운송_소요일을_더한다()
    {
        var inspection = new Stage { Kind = StageKind.Inspection, TargetDate = D(5) };
        var p = NewProject(new Stage(), inspection, new Stage { Kind = StageKind.Inbound });

        Assert.Equal(D(5 + 35), Rules.InboundRecommendation(p, 35));

        // 검수가 끝났으면 완료일 기준
        inspection.CompletedDate = D(3);
        Assert.Equal(D(3 + 35), Rules.InboundRecommendation(p, 35));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(105)]
    [InlineData(42)]
    public void 진행률은_0에서_100까지_5단위만_허용(int value)
    {
        Assert.Throws<RuleException>(() => Rules.ValidateProgress(value));
    }

    [Fact]
    public void 근무일_달력은_주말과_공휴일을_뺀다()
    {
        // 2026-10-09(금) 한글날, 10-10~11 주말
        var cal = new WorkCalendar(new[] { new DateOnly(2026, 10, 9) });
        DateOnly Day(int d) => new(2026, 10, d);

        Assert.False(cal.IsWorkingDay(Day(9)));
        Assert.False(cal.IsWorkingDay(Day(10)));
        Assert.True(cal.IsWorkingDay(Day(12)));

        Assert.Equal(4, cal.WorkingDays(Day(5), Day(9)));          // 월~목
        Assert.Equal(0, cal.WorkingDays(Day(9), Day(11)));
        Assert.Equal(0, cal.WorkingDays(Day(8), Day(7)));          // 거꾸로면 0

        Assert.Equal(Day(12), cal.AddWorkingDays(Day(8), 1));      // 목 다음 근무일은 월
        Assert.Equal(Day(8), cal.AddWorkingDays(Day(12), -1));
        Assert.Equal(Day(10), cal.AddWorkingDays(Day(10), 0));     // 0 이면 휴일이어도 그대로
        Assert.Equal(Day(14), cal.AddWorkingDays(Day(8), 3));

        Assert.Equal(1, cal.Distance(Day(8), Day(12)));
        Assert.Equal(-1, cal.Distance(Day(12), Day(8)));
        Assert.Equal(0, cal.Distance(Day(8), Day(11)));            // 사이에 근무일이 없다

        Assert.Equal(Day(12), cal.EarliestStartAfter(Day(8), 0));
        Assert.Equal(Day(14), cal.EarliestStartAfter(Day(8), 2));
    }

    [Fact]
    public void 여유_일수는_단계_목표일과_후행_작업에서_거슬러_계산한다()
    {
        var cal = WorkCalendar.WeekendsOnly;
        DateOnly Day(int d) => new(2026, 10, d);
        var a = new WorkItem { Id = 1, PlanStart = Day(12), PlanEnd = Day(13) };     // 월~화
        var b = new WorkItem { Id = 2, PlanStart = Day(15), PlanEnd = Day(16) };     // 목~금 (A 뒤에 하루 틈)
        var c = new WorkItem { Id = 3, PlanStart = Day(12), PlanEnd = Day(14) };     // 의존 없음
        var stage = new Stage { Id = 1, TargetDate = Day(20), Works = { a, b, c } };
        var free = new WorkItem { Id = 4, PlanStart = Day(12), PlanEnd = Day(30) };
        var noTarget = new Stage { Id = 2, Works = { free } };
        var p = NewProject(stage, noTarget);
        p.Links.Add(new WorkLink { PredecessorId = 1, SuccessorId = 2 });

        // 여유는 근무일로 센다 (주말 10-17~18 은 넣지 않는다)
        var floats = Rules.Floats(p, cal);
        Assert.Equal(2, floats[2]);                 // B: 금 종료 → 목표일 화(10-20) 까지 근무일 2일(월·화)
        Assert.Equal(3, floats[1]);                 // A: B 시작까지 틈 1근무일 + B 의 여유 2 (자기 단계 여유 5근무일보다 작다)
        Assert.Equal(4, floats[3]);                 // C: 수 종료 → 화까지 4근무일
        Assert.False(floats.ContainsKey(4));        // 목표일 없는 단계는 제약이 없다

        // 목표일이 주말이면 직전 금요일에 끝나는 작업은 여유 0 → 임계
        stage.TargetDate = Day(18);                 // 일요일
        var weekend = Rules.Floats(p, cal);
        Assert.Equal(0, weekend[2]);
        Assert.True(Views.Work(b, Day(9), cal, weekend, stage: stage).Critical);

        // 후행이 선행보다 먼저 시작하면 위반
        Assert.False(Rules.Violates(a, b, 0, cal));
        Assert.True(Rules.Violates(a, b, 2, cal));  // 간격 2근무일이면 10-16 이후여야 한다

        Assert.True(Rules.Reaches(new[] { (1, 2), (2, 3) }, 1, 3));
        Assert.False(Rules.Reaches(new[] { (1, 2), (2, 3) }, 3, 1));
    }

    [Fact]
    public void 단계_막대_구간은_직전_단계_일자에서_이어진다()
    {
        var a = new Stage { Id = 1, Name = "A", TargetDate = D(-20), CompletedDate = D(-18) };
        var b = new Stage { Id = 2, Name = "B", TargetDate = D(-5) };
        var c = new Stage { Id = 3, Name = "C", TargetDate = D(10) };
        var p = NewProject(a, b, c);

        var views = Views.Stages(p, Today, 0, withWorks: false);

        Assert.Equal(p.StartDate, views[0].PlanFrom);
        Assert.Equal(D(-20), views[1].PlanFrom);
        Assert.Equal(D(-5), views[2].PlanFrom);

        // 실행 막대: A 는 착수~완료, B 는 A 완료~오늘(진행 중), C 는 아직 없음
        Assert.Equal((p.StartDate, D(-18)), (views[0].ActualFrom!.Value, views[0].ActualTo!.Value));
        Assert.Equal((D(-18), Today), (views[1].ActualFrom!.Value, views[1].ActualTo!.Value));
        Assert.Null(views[2].ActualFrom);
        Assert.Equal("late", views[1].Status);
        Assert.Equal(5, views[1].LateDays);
    }
}
