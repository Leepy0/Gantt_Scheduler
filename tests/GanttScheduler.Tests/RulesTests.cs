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
