using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 조기 경보 규칙(Rules.RiskReasons·IsStale)과 빠른 갱신 규칙(Rules.WorkActuals) — DB 없이
public class EarlyWarningRulesTests
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
            s.Name = "단계" + order;
            p.Stages.Add(s);
        }
        return p;
    }

    private static WorkItem Work(int planStart, int planEnd, int? actualStart = null, int? actualEnd = null) => new()
    {
        PlanStart = D(planStart), PlanEnd = D(planEnd),
        ActualStart = actualStart == null ? null : D(actualStart.Value), ActualEnd = actualEnd == null ? null : D(actualEnd.Value),
    };

    [Fact]
    public void 예상_종료가_목표일을_하루라도_넘으면_위험_이유이고_같으면_아니다()
    {
        var onTime = NewProject(new Stage { TargetDate = D(5), Works = { Work(1, 5) } });
        Assert.Empty(Rules.RiskReasons(onTime, Today));

        var over = NewProject(new Stage { TargetDate = D(5), Works = { Work(1, 6) } });
        var reasons = Rules.RiskReasons(over, Today);
        var r = Assert.Single(reasons);
        Assert.Equal(("forecast", 1), (r.Kind, r.Value));
        Assert.Contains("'단계1'", r.Text);
        Assert.Equal(1, Rules.RiskDays(reasons));
        Assert.True(Rules.AtRisk(Rules.DelayDays(over, Today), reasons));
    }

    [Fact]
    public void 전진_계산을_주면_선행_지연이_후행_단계의_예상_초과로_전파된다()
    {
        // 선행 A(진행 중, 목표 종료 어제 → 오늘까지)가 늦어 후행 B(끝나면 시작)가 단계 목표일을 넘는다
        var a = Work(-5, -1, actualStart: -5);
        a.Id = 1;
        var b = Work(0, 4);
        b.Id = 2;
        var stage = new Stage { TargetDate = D(4), Works = { a, b } };
        var p = NewProject(stage);
        p.Links.Add(new WorkLink { PredecessorId = 1, SuccessorId = 2, Predecessor = a, Successor = b });
        var cal = WorkCalendar.WeekendsOnly;

        var simple = Rules.RiskReasons(p, Today);
        Assert.DoesNotContain(simple, x => x.Kind == "forecast");     // 작업별 간단한 계산으로는 B 가 목표 안
        var forward = Rules.RiskReasons(p, Today, Rules.ForwardPass(p, cal, Today));
        Assert.Contains(forward, x => x.Kind == "forecast" && x.Value > 0);
        Assert.Contains(forward, x => x.Kind == "lateWorks" && x.Value == 1);
    }

    [Fact]
    public void 기한_지난_미완료_작업만_세고_완료_작업과_완료_단계의_작업은_빼다()
    {
        var p = NewProject(
            new Stage { TargetDate = D(-10), CompletedDate = D(-10), Works = { Work(-20, -12) } },   // 단계 완료로 닫힘
            new Stage { TargetDate = D(20), Works = { Work(-6, -2), Work(-6, -1), Work(-6, -3, -6, -3), Work(-1, 0) } });
        var r = Assert.Single(Rules.RiskReasons(p, Today), x => x.Kind == "lateWorks");
        Assert.Equal(2, r.Value);
        Assert.Equal("기한 지난 작업 2건", r.Text);
    }

    [Fact]
    public void 기준선_대비_밀림은_RiskSlipDays_이상부터_이유다()
    {
        Project Slip(int days) => NewProject(new Stage { TargetDate = D(30 + days), BaselineDate = D(30) });
        Assert.DoesNotContain(Rules.RiskReasons(Slip(Rules.RiskSlipDays - 1), Today), x => x.Kind == "slip");
        var r = Assert.Single(Rules.RiskReasons(Slip(Rules.RiskSlipDays), Today), x => x.Kind == "slip");
        Assert.Equal(Rules.RiskSlipDays, r.Value);
        // 예상 지연 일수는 예상 초과만 센다
        Assert.Equal(0, Rules.RiskDays(Rules.RiskReasons(Slip(10), Today)));
    }

    [Fact]
    public void 과제_간_연결_위반은_센_경로에서만_이유가_되고_진행이_아닌_과제는_이유가_없다()
    {
        var p = NewProject(new Stage { TargetDate = D(10) });
        Assert.Empty(Rules.RiskReasons(p, Today, linkViolations: null));
        Assert.Empty(Rules.RiskReasons(p, Today, linkViolations: 0));
        Assert.Equal(2, Assert.Single(Rules.RiskReasons(p, Today, linkViolations: 2)).Value);

        var hold = NewProject(new Stage { TargetDate = D(5), Works = { Work(-6, -1) } });
        hold.State = ProjectState.Hold;
        Assert.Empty(Rules.RiskReasons(hold, Today, linkViolations: 3));
    }

    [Fact]
    public void 지연_과제는_이유가_있어도_지연_위험이_아니다()
    {
        var p = NewProject(new Stage { TargetDate = D(-1), Works = { Work(-5, -2) } });
        var reasons = Rules.RiskReasons(p, Today);
        Assert.NotEmpty(reasons);
        Assert.True(Rules.DelayDays(p, Today) > 0);
        Assert.False(Rules.AtRisk(Rules.DelayDays(p, Today), reasons));
    }

    [Fact]
    public void 정체는_진행_과제의_마지막_변경이_StaleDays_일을_넘었을_때()
    {
        var now = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
        var edge = now.AddDays(-Rules.StaleDays);
        Assert.False(Rules.IsStale(ProjectState.Active, edge, now));
        Assert.True(Rules.IsStale(ProjectState.Active, edge.AddSeconds(-1), now));
        Assert.False(Rules.IsStale(ProjectState.Hold, edge.AddDays(-30), now));
        Assert.False(Rules.IsStale(ProjectState.Done, edge.AddDays(-30), now));
    }

    [Fact]
    public void 작업_실적_규칙은_종료가_있으면_100_진행률만이면_접속자의_오늘_시작()
    {
        Assert.Equal((100, (DateOnly?)D(-3)), Rules.WorkActuals(30, D(-3), D(-1), null, Today));
        Assert.Equal((40, (DateOnly?)D(1)), Rules.WorkActuals(40, null, null, D(1), Today));       // 해외 PC 의 하루 다른 날짜
        Assert.Equal((40, (DateOnly?)Today), Rules.WorkActuals(40, null, null, D(5), Today));      // 허용 범위 밖이면 한국 날짜
        Assert.Equal((0, (DateOnly?)null), Rules.WorkActuals(0, null, null, null, Today));
        Assert.Equal("actualStart", Assert.Throws<RuleException>(() => Rules.ValidateActuals(null, D(1), 50)).Field);
        Assert.Equal("actualStart", Assert.Throws<RuleException>(() => Rules.ValidateActuals(D(2), D(1), 50)).Field);
        Assert.Equal("progress", Assert.Throws<RuleException>(() => Rules.ValidateActuals(null, null, 33)).Field);
    }
}

// 조기 경보·목록 정리·빠른 갱신의 서비스 경로 (DB)
public class EarlyWarningServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;

    public EarlyWarningServiceTests(TestDb db) => _db = db;

    // 한국 '물류' 과제: 첫 단계 목표 = 오늘 + target, 작업 하나(목표 workStart~workEnd, 담당 = 지금 사용자)
    private async Task<(int Id, int StageId, int WorkId)> ProjectAsync(string title, int target, int workStart, int workEnd, string? vendor = null, string? owner = "테스터")
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Today.AddDays(-20), vendor, null, null, null));
        var stage = (await s.Projects.ViewAsync(id))!.Stages[0];
        await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, Today.AddDays(target), null, null, false));
        await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, title + " 작업", owner, null, Today.AddDays(workStart), Today.AddDays(workEnd), null, null, 0, null, null));
        var work = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single();
        return (id, stage.Id, work.Id);
    }

    private async Task<List<ProjectView>> ListAsync(string state, string? q = null, string? sort = null, bool desc = false)
    {
        using var s = _db.NewScope();
        return await s.Dashboard.ListAsync(new ProjectFilter(q, null, null, state, false, Sort: sort, Desc: desc));
    }

    [Fact]
    public async Task 목록의_위험_탭은_지연_전_위험_과제만_정체_탭은_오래_안_바뀐_과제만()
    {
        var risky = await ProjectAsync("경보 위험 과제", target: 10, workStart: -5, workEnd: -1);      // 기한 지난 작업
        var calm = await ProjectAsync("경보 평온 과제", target: 10, workStart: 1, workEnd: 3);
        var late = await ProjectAsync("경보 지연 과제", target: -2, workStart: -6, workEnd: -3);       // 이미 지연 — 위험 탭이 아니다

        var risk = await ListAsync("risk");
        var mine = Assert.Single(risk, v => v.Id == risky.Id);
        Assert.True(mine.AtRisk);
        Assert.Contains(mine.Risks, r => r.Kind == "lateWorks");
        Assert.DoesNotContain(risk, v => v.Id == calm.Id || v.Id == late.Id);
        Assert.All(risk, v => Assert.True(v.DelayDays == 0 && v.Risks.Count > 0));

        // 정체: 변경 시각을 15일 전으로 돌리면 정체, 댓글 하나(변경)로 풀린다
        using (var s = _db.NewScope())
        {
            var p = await s.Db.Projects.FirstAsync(x => x.Id == calm.Id);
            p.UpdatedAt = _db.Clock.UtcNow.AddDays(-(Rules.StaleDays + 1));
            await s.Db.SaveChangesAsync();
        }
        Assert.Contains(await ListAsync("stale"), v => v.Id == calm.Id && v.Stale);
        Assert.DoesNotContain(await ListAsync("stale"), v => v.Id == risky.Id);
        using (var s = _db.NewScope())
            await s.Projects.CommentAsync(new CommentInput(calm.Id, null, null, "현장 확인"));
        Assert.DoesNotContain(await ListAsync("stale"), v => v.Id == calm.Id);
    }

    [Fact]
    public async Task 대시보드는_위험_카드_KPI_최근_사유와_간트_범위를_같은_판정으로_채운다()
    {
        var risky = await ProjectAsync("대시 위험 과제", target: 10, workStart: -5, workEnd: -1);
        var far = await ProjectAsync("대시 먼 과제", target: 60, workStart: 40, workEnd: 45);
        // 시험 시계는 멈춰 있어 다른 과제와 변경 시각이 같다 — 댓글은 조금 뒤 시각으로 남겨 '최근 변경' 순서를 가린다
        var clock = _db.Clock.UtcNow;
        _db.Clock.UtcNow = clock.AddMinutes(5);
        try
        {
            using var s = _db.NewScope();
            await s.Projects.CommentAsync(new CommentInput(risky.Id, null, null, "자재 입고 지연, 다음 주 재개"));
        }
        finally { _db.Clock.UtcNow = clock; }

        using var c = _db.NewScope();
        var all = await c.Dashboard.BuildAsync(new DashboardQuery(Scope: "all"));
        var risk = Assert.Single(all.Risk!, v => v.Id == risky.Id);
        Assert.True(all.Risk!.Count == all.Kpi.Risk, $"위험 카드 {all.Risk.Count}건, KPI {all.Kpi.Risk} — " + string.Join(", ", all.Risk.Select(v => v.Title)));
        Assert.Equal("자재 입고 지연, 다음 주 재개", all.Notes![risky.Id].Text);
        Assert.Contains(all.Timeline, t => t.Id == far.Id);
        Assert.True(all.Timeline.Single(t => t.Id == risky.Id).AtRisk);

        // 기본 범위('주요')는 지연·위험·4주 안 목표만 — 두 달 뒤 목표인 과제는 빠진다
        var focus = await c.Dashboard.BuildAsync(new DashboardQuery());
        Assert.DoesNotContain(focus.Timeline, t => t.Id == far.Id);
        Assert.Contains(focus.Timeline, t => t.Id == risky.Id);
        Assert.Equal(all.TimelineTotal, focus.TimelineTotal);
        var delayed = await c.Dashboard.BuildAsync(new DashboardQuery(Scope: "delayed"));
        Assert.All(delayed.Timeline, t => Assert.True(t.DelayDays > 0));

        // 정렬: 최근 변경 순이면 방금 댓글을 단 과제가 맨 앞
        var recent = await c.Dashboard.BuildAsync(new DashboardQuery(Scope: "all", Sort: "recent"));
        Assert.Equal(risky.Id, recent.Timeline[0].Id);
    }

    [Fact]
    public async Task 목록_정렬은_머리글_키와_방향을_따르고_Excel_도_같은_순서다()
    {
        await ProjectAsync("정렬 가 과제", target: 30, workStart: 1, workEnd: 2);
        await ProjectAsync("정렬 하 과제", target: 5, workStart: 1, workEnd: 2);

        var byTitle = await ListAsync("all", sort: "title");
        Assert.Equal(byTitle.Select(v => v.Title).OrderBy(t => t, StringComparer.Ordinal), byTitle.Select(v => v.Title));
        var byTitleDesc = await ListAsync("all", sort: "title", desc: true);
        Assert.Equal(byTitle.Select(v => v.Id).Reverse(), byTitleDesc.Select(v => v.Id));

        // 목표 종료: 값 없는 과제는 방향과 관계없이 뒤
        foreach (var desc in new[] { false, true })
        {
            var byEnd = await ListAsync("all", sort: "end", desc: desc);
            var dated = byEnd.TakeWhile(v => v.EndTarget != null).ToList();
            Assert.All(byEnd.Skip(dated.Count), v => Assert.Null(v.EndTarget));
            var ends = dated.Select(v => v.EndTarget!.Value).ToList();
            Assert.Equal(desc ? ends.OrderByDescending(x => x) : ends.OrderBy(x => x), ends);
        }

        // Excel 은 목록과 같은 순서로 내보낸다
        var rows = await ListAsync("active", sort: "progress", desc: true);
        var bytes = new ExcelExporter().ProjectList(rows, Today, "정렬 시험");
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var ws = book.Worksheets.First();
        var titles = Enumerable.Range(5, rows.Count).Select(r => ws.Cell(r, 2).GetString()).ToList();
        Assert.Equal(rows.Select(v => v.Title), titles);
    }

    [Fact]
    public async Task 검색은_협력사와_작업_담당_이름까지_찾는다()
    {
        var byVendor = await ProjectAsync("검색 협력사 과제", target: 20, workStart: 1, workEnd: 2, vendor: "한빛정밀");
        var byWorker = await ProjectAsync("검색 작업자 과제", target: 20, workStart: 1, workEnd: 2, owner: "작업담당검색");

        Assert.Equal(new[] { byVendor.Id }, (await ListAsync("all", q: "한빛정밀")).Select(v => v.Id));
        Assert.Equal(new[] { byWorker.Id }, (await ListAsync("all", q: "작업담당검")).Select(v => v.Id));
        Assert.Contains(await ListAsync("all", q: "검색 협력사"), v => v.Id == byVendor.Id);
    }

    [Fact]
    public async Task 빠른_갱신은_작업_저장과_같은_규칙으로_진행률과_실적을_저장하고_버전과_활동을_남긴다()
    {
        var x = await ProjectAsync("빠른 갱신 과제", target: 20, workStart: 1, workEnd: 5);
        int version;
        using (var s = _db.NewScope())
        {
            // 진행률만 올리면 접속자의 오늘(한국 날짜 -1 — 해외 PC, 허용 범위 안) 시작으로
            await s.Projects.UpdateWorkProgressAsync(x.WorkId, 0, 25, clientToday: Today.AddDays(-1));
        }
        using (var s = _db.NewScope())
        {
            var w = await s.Db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == x.WorkId);
            Assert.Equal((25, (DateOnly?)Today.AddDays(-1), 1), (w.Progress, w.ActualStart, w.Version));
            version = w.Version;
            var edited = await s.Db.Activities.Where(a => a.WorkItemId == x.WorkId && a.Type == ActivityType.WorkEdited).ToListAsync();
            Assert.Contains(edited, a => a.Text.Contains("진행률 0% → 25%") && a.Text.Contains("실적 시작"));

            // 화면이 본 버전이 다르면 409
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.UpdateWorkProgressAsync(x.WorkId, 0, 50));
        }

        // 진행 내용만: 작업 댓글로 남고 작업 버전은 그대로
        using (var s = _db.NewScope())
            await s.Projects.UpdateWorkProgressAsync(x.WorkId, version, 25, note: "배선 절반 끝");
        using (var s = _db.NewScope())
        {
            Assert.Equal(version, (await s.Db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == x.WorkId)).Version);
            Assert.True(await s.Db.Activities.AnyAsync(a => a.WorkItemId == x.WorkId && a.Type == ActivityType.Comment && a.Text == "배선 절반 끝"));
            // 바뀐 것이 없으면 아무것도 남기지 않는다
            var before = await s.Db.Activities.CountAsync(a => a.ProjectId == x.Id);
            await s.Projects.UpdateWorkProgressAsync(x.WorkId, version, 25);
            Assert.Equal(before, await s.Db.Activities.CountAsync(a => a.ProjectId == x.Id));
            // 5 단위가 아니면 거절
            Assert.Equal("progress", (await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateWorkProgressAsync(x.WorkId, version, 33))).Field);
        }

        // 오늘 완료: 종료가 있으면 진행률은 100, 실적 시작은 그대로 둔다(null = 그대로)
        using (var s = _db.NewScope())
            await s.Projects.UpdateWorkProgressAsync(x.WorkId, version, 60, actualEnd: Today);
        using (var s = _db.NewScope())
        {
            var w = await s.Db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == x.WorkId);
            Assert.Equal((100, (DateOnly?)Today.AddDays(-1), (DateOnly?)Today, version + 1), (w.Progress, w.ActualStart, w.ActualEnd, w.Version));
        }
    }

    [Fact]
    public async Task 빠른_갱신은_실적_순서와_날짜_범위를_작업_저장처럼_검사한다()
    {
        var x = await ProjectAsync("빠른 갱신 검사 과제", target: 20, workStart: 1, workEnd: 5);
        using var s = _db.NewScope();
        await s.Projects.UpdateWorkProgressAsync(x.WorkId, 0, 0, actualStart: Today);
        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateWorkProgressAsync(x.WorkId, 1, 50, actualEnd: Today.AddDays(-1)));
        Assert.Equal("actualStart", ex.Field);
        ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateWorkProgressAsync(x.WorkId, 1, 50, actualEnd: new DateOnly(2099, 12, 31)));
        Assert.Equal("actualEnd", ex.Field);
    }

    [Fact]
    public async Task 내_할_일_한_행은_저장_뒤_다시_나눈_묶음을_돌려준다()
    {
        var x = await ProjectAsync("내 할 일 이동 과제", target: 40, workStart: 20, workEnd: 25);
        using var s = _db.NewScope();
        var row = (await s.Insights.MyWorkAsync(x.Id, x.WorkId))!;
        Assert.Equal(("later", 0), (row.Group, row.Version));
        Assert.Contains((await s.Insights.MyAsync()).Later, w => w.WorkId == x.WorkId);

        // 묶음은 목표 일정으로 나눈다(Reporting.MyGroup) — 진행률을 올려도 '이후'에 남고, 새 버전·실적 시작을 싣는다
        await s.Projects.UpdateWorkProgressAsync(x.WorkId, row.Version, 10, clientToday: Today);
        using var c = _db.NewScope();
        row = (await c.Insights.MyWorkAsync(x.Id, x.WorkId))!;
        Assert.Equal(("later", 1, (DateOnly?)Today, 10), (row.Group, row.Version, row.ActualStart, row.Progress));
        Assert.Equal("soon", Reporting.MyGroup(false, 20, Today.AddDays(-1), Today));       // 시작했어야 하는 작업은 이번 주
        Assert.Equal("soon", Reporting.MyGroup(false, Rules.SoonDays, Today.AddDays(3), Today));
        Assert.Equal("late", Reporting.MyGroup(true, -1, Today.AddDays(-5), Today));

        // 완료하면 내 할 일에서 빠진다
        await c.Projects.UpdateWorkProgressAsync(x.WorkId, row.Version, 100, actualEnd: Today);
        using var d = _db.NewScope();
        Assert.Null(await d.Insights.MyWorkAsync(x.Id, x.WorkId));
    }

    [Fact]
    public async Task 주간_보고는_이번_주에만_예상_지연_칩을_싣고_그런_과제를_카드로_보인다()
    {
        var x = await ProjectAsync("주간 위험 과제", target: 3, workStart: 1, workEnd: 6);     // 단계 목표 +3, 작업 끝 +6 → 예상 +3
        using var s = _db.NewScope();
        var report = await s.Insights.WeeklyAsync(null, false);
        var p = Assert.Single(report.Projects, w => w.Id == x.Id);
        Assert.Equal(3, p.RiskDays);
        Assert.True(p.Card);
        Assert.Equal("예상 지연 +3일", p.RiskChip!.Text);

        // Excel 도 같은 카드 기준이라 그 과제가 첫 시트에 오르고, 상태 칸에 예상 지연을 적는다(왜 접히지 않았는지 보이게)
        using (var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(new ExcelExporter().Weekly(report))))
        {
            var ws = book.Worksheet("주간 보고");
            var row = ws.RowsUsed().Single(r => r.Cell(1).GetString() == "주간 위험 과제");
            Assert.Contains("예상 지연 +3일", row.Cell(5).GetString());
        }

        var past = await s.Insights.WeeklyAsync(Today.AddDays(-14), false);
        Assert.All(past.Projects, w => Assert.Equal(0, w.RiskDays));
    }
}
