using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 의존 관계·기준선·체크리스트·복제·동시 수정·통계 — 실제 DB(SQLite 또는 SQL Server)에서 확인
public class AdvancedTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;                       // 2026-10-09 (금, 한글날)
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public AdvancedTests(TestDb db) => _db = db;

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
        List<int>? pre = null, string? reason = null, int? version = null) =>
        new(id, stageId, title, null, null, start, end, null, null, 0, null, reason, version, pre);

    // 선행 A(10-12~10-14) → 후행 B(10-15~10-16) 를 가진 과제
    private async Task<(int Project, int Stage, int A, int B)> LinkedAsync(string title)
    {
        var id = await NewProjectAsync(title);
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, Day(10, 20), null, null, false));
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(null, stage.Id, "A", Day(10, 12), Day(10, 14)));
        var a = (await ViewAsync(id)).Stages[0].Works.Single().Id;
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(null, stage.Id, "B", Day(10, 15), Day(10, 16), new List<int> { a }));
        var b = (await ViewAsync(id)).Stages[0].Works.Single(w => w.Title == "B").Id;
        return (id, stage.Id, a, b);
    }

    [Fact]
    public async Task 선행_작업이_밀리면_시작_전인_후행_작업이_근무일을_유지한_채_따라_밀린다()
    {
        var (id, stage, a, b) = await LinkedAsync("테스트 의존-밀기");
        var before = await ViewAsync(id);
        Assert.Equal(new[] { a }, before.Stages[0].Works.Single(w => w.Id == b).Predecessors);
        Assert.Single(before.Links);

        // A 를 10-16(금)까지 늘리면 B 는 다음 근무일 10-19(월)부터 2근무일
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage, "A", Day(10, 12), Day(10, 16), reason: "범위 확대"));

        var p = await ViewAsync(id);
        var workB = p.Stages[0].Works.Single(w => w.Id == b);
        Assert.Equal(Day(10, 19), workB.PlanStart);
        Assert.Equal(Day(10, 20), workB.PlanEnd);
        Assert.Equal(2, workB.PlanWorkDays);
        Assert.Contains(p.Activities!, x => x.Type == "workPlanChanged" && x.Text.Contains("선행 작업 연동") && x.Reason == "범위 확대");

        // 단계 목표일(10-20)까지 여유가 없으므로 둘 다 임계
        Assert.Equal(0, workB.Float);
        Assert.True(workB.Critical);
        Assert.True(p.Stages[0].Works.Single(w => w.Id == a).Critical);
        Assert.Equal(2, p.CriticalCount);
    }

    [Fact]
    public async Task 이미_시작한_후행_작업은_밀지_않는다()
    {
        var (id, stage, a, b) = await LinkedAsync("테스트 의존-시작됨");
        using (var s = _db.NewScope())
        {
            var w = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single(x => x.Id == b);
            await s.Projects.SaveWorkAsync(new WorkInput(b, stage, "B", null, null, w.PlanStart, w.PlanEnd, Today, null, 10, null, null, w.Version));
        }
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(Work(a, stage, "A", Day(10, 12), Day(10, 16), reason: "범위 확대"));

        var workB = (await ViewAsync(id)).Stages[0].Works.Single(w => w.Id == b);
        Assert.Equal(Day(10, 15), workB.PlanStart);
    }

    [Fact]
    public async Task 선행보다_이른_일정과_순환하는_의존_관계는_거절한다()
    {
        var (id, stage, a, b) = await LinkedAsync("테스트 의존-거절");

        using (var s = _db.NewScope())
        {
            // 선행 A 가 10-14 에 끝나는데 10-13 에 시작하도록 잡으면 거절
            var early = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveWorkAsync(Work(null, stage, "C", Day(10, 13), Day(10, 14), new List<int> { a })));
            Assert.Equal("planStart", early.Field);
        }
        using (var s = _db.NewScope())
        {
            // A 의 선행으로 B 를 두면 A → B → A
            var cycle = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveWorkAsync(Work(a, stage, "A", Day(10, 12), Day(10, 14), new List<int> { b })));
            Assert.Equal("predecessors", cycle.Field);
        }

        // 거절된 뒤 아무것도 바뀌지 않았다
        var p = await ViewAsync(id);
        Assert.Equal(2, p.Stages[0].Works.Count);
        Assert.Single(p.Links);

        // 작업을 지우면 의존 관계도 함께 지워진다
        using (var s = _db.NewScope())
            await s.Projects.DeleteWorkAsync(a);
        p = await ViewAsync(id);
        Assert.Empty(p.Links);
        Assert.Empty(p.Stages[0].Works.Single().Predecessors);
    }

    [Fact]
    public async Task 의존_관계가_있는_단계와_과제를_지울_수_있다()
    {
        var (id, stage, _, _) = await LinkedAsync("테스트 의존-삭제");
        using (var s = _db.NewScope())
            await s.Projects.DeleteStageAsync(stage);
        Assert.Empty((await ViewAsync(id)).Links);

        var (id2, _, _, _) = await LinkedAsync("테스트 의존-과제삭제");
        using (var s = _db.NewScope())
            await s.Projects.DeleteAsync(id2);
        using var check = _db.NewScope();
        Assert.False(await check.Db.WorkLinks.AnyAsync(l => l.ProjectId == id2));
        Assert.False(await check.Db.WorkItems.AnyAsync(w => w.Stage!.ProjectId == id2));
    }

    [Fact]
    public async Task 기준선을_확정하면_이후_변경이_기준선_대비_일수로_보인다()
    {
        var (id, stage, a, _) = await LinkedAsync("테스트 기준선");
        Assert.Null((await ViewAsync(id)).BaselineAt);

        using (var s = _db.NewScope())
            await s.Projects.SetBaselineAsync(id);

        var p = await ViewAsync(id);
        Assert.NotNull(p.BaselineAt);
        Assert.Equal(Day(10, 20), p.Stages[0].BaselineDate);
        Assert.Equal(0, p.Stages[0].SlipDays);
        Assert.Equal(Day(10, 14), p.Stages[0].Works.Single(w => w.Id == a).BaselineEnd);

        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage, p.Stages[0].Name, Day(10, 27), null, "범위 확대", false, p.Stages[0].Version));

        p = await ViewAsync(id);
        Assert.Equal(7, p.Stages[0].SlipDays);
        Assert.Equal(Day(10, 20), p.BaselineEnd);
        Assert.Equal(7, p.SlipDays);
        Assert.Contains(p.Activities!, x => x.Type == "baselineSet");
    }

    [Fact]
    public async Task 체크리스트가_남아_있으면_단계를_완료할_수_없다()
    {
        var id = await NewProjectAsync("테스트 체크리스트", "물류 자동화");
        var p = await ViewAsync(id);
        var inspect = p.Stages.Single(s => s.Name == "검수");
        Assert.Equal(new[] { "FAT 성적서 확인", "주행 시험 결과 승인", "안전 점검 완료" }, inspect.Checks.Select(c => c.Text).ToArray());

        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveStageAsync(new StageInput(id, inspect.Id, "검수", null, Today, null, false)));
            Assert.Contains("체크리스트 3개", ex.Message);
        }

        using (var s = _db.NewScope())
        {
            foreach (var c in inspect.Checks) await s.Projects.ToggleCheckAsync(c.Id, true);
            await s.Projects.SaveCheckAsync(new CheckInput(inspect.Id, null, "임시 항목"));
        }
        p = await ViewAsync(id);
        inspect = p.Stages.Single(s => s.Name == "검수");
        Assert.Equal((3, 4), (inspect.CheckDone, inspect.CheckTotal));
        Assert.Equal("테스터", inspect.Checks[0].DoneBy);

        using (var s = _db.NewScope())
        {
            await s.Projects.DeleteCheckAsync(inspect.Checks.Single(c => c.Text == "임시 항목").Id);
            await s.Projects.SaveStageAsync(new StageInput(id, inspect.Id, "검수", null, Today, null, false));
        }
        Assert.Equal("done", (await ViewAsync(id)).Stages.Single(s => s.Name == "검수").Status);

        // 완료한 단계의 체크는 해제할 수 없다
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.ToggleCheckAsync(inspect.Checks[0].Id, false));
    }

    [Fact]
    public async Task 과제를_복제하면_일정이_새_착수일에_맞춰_옮겨지고_실적은_비워진다()
    {
        var (id, stage, a, b) = await LinkedAsync("테스트 복제 원본");
        using (var s = _db.NewScope())
        {
            var w = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single(x => x.Id == a);
            await s.Projects.SaveWorkAsync(new WorkInput(a, stage, "A", "홍길동", null, w.PlanStart, w.PlanEnd, Day(10, 12), null, 50, null, null, w.Version));
            await s.Projects.SetBaselineAsync(id);
        }

        int cloneId;
        using (var s = _db.NewScope())
            cloneId = await s.Projects.CloneAsync(new CloneInput(id, "테스트 복제본", Day(11, 1), true));   // 착수일 +31일

        var src = await ViewAsync(id);
        var copy = await ViewAsync(cloneId);
        Assert.Equal("테스트 복제본", copy.Title);
        Assert.Equal("테스터", copy.Owner);
        Assert.Equal(src.Stages.Select(s => s.Name), copy.Stages.Select(s => s.Name));
        // 근무일 거리로 옮긴다: 원본 착수 10-01(목) → 목표 10-20 은 근무일 11일 뒤(10-05·10-09 공휴일).
        // 새 착수 11-01 은 일요일이라 11-02(월)부터 세어 11일 뒤 = 11-17(화)
        Assert.Equal(Day(11, 17), copy.Stages[0].TargetDate);
        Assert.Null(copy.BaselineAt);

        var copyA = copy.Stages[0].Works.Single(w => w.Title == "A");
        var copyB = copy.Stages[0].Works.Single(w => w.Title == "B");
        Assert.Equal((Day(11, 9), Day(11, 11)), (copyA.PlanStart, copyA.PlanEnd));       // 근무일 5일 뒤 시작, 3근무일 유지
        Assert.Equal((Day(11, 12), Day(11, 13)), (copyB.PlanStart, copyB.PlanEnd));      // A 다음 근무일, 2근무일 유지
        Assert.Equal(src.Stages[0].Works.Select(w => w.PlanWorkDays), copy.Stages[0].Works.Select(w => w.PlanWorkDays));
        Assert.DoesNotContain(copy.Stages.SelectMany(s => s.Works), w => w.DepViolation);
        Assert.Equal("홍길동", copyA.Owner);
        Assert.Equal(0, copyA.Progress);
        Assert.Null(copyA.ActualStart);
        Assert.Equal(new[] { copyA.Id }, copyB.Predecessors);
        Assert.NotEqual(a, copyA.Id);
        Assert.Contains(copy.Activities!, x => x.Type == "cloned");

        // 작업을 빼고 복제
        using (var s = _db.NewScope())
            cloneId = await s.Projects.CloneAsync(new CloneInput(id, "테스트 복제본(단계만)", null, false));
        copy = await ViewAsync(cloneId);
        Assert.Equal(0, copy.WorkCount);
        Assert.Empty(copy.Links);
    }

    [Fact]
    public async Task 화면을_연_뒤_다른_사람이_먼저_고쳤으면_저장을_거절한다()
    {
        var (id, stageId, a, _) = await LinkedAsync("테스트 동시 수정");
        var mine = await ViewAsync(id);                 // 내가 화면을 연 시점
        var stage = mine.Stages[0];
        var work = stage.Works.Single(w => w.Id == a);

        // 다른 사람이 같은 단계·작업을 먼저 수정
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, "검토(수정)", stage.TargetDate, null, null, false, stage.Version));
            await s.Projects.SaveWorkAsync(new WorkInput(a, stageId, "A(수정)", null, null, work.PlanStart, work.PlanEnd, null, null, 0, null, null, work.Version));
        }

        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(() =>
                s.Projects.SaveStageAsync(new StageInput(id, stageId, "내 수정", stage.TargetDate, null, null, false, stage.Version)));
            Assert.Equal(id, ex.ProjectId);
        }
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() =>
                s.Projects.SaveWorkAsync(new WorkInput(a, stageId, "내 수정", null, null, work.PlanStart, work.PlanEnd, null, null, 0, null, null, work.Version)));

        // 최신 버전으로 다시 보내면 저장된다
        var fresh = await ViewAsync(id);
        Assert.Equal("검토(수정)", fresh.Stages[0].Name);
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, "내 수정", stage.TargetDate, null, null, false, fresh.Stages[0].Version));
        Assert.Equal("내 수정", (await ViewAsync(id)).Stages[0].Name);
    }

    [Fact]
    public async Task 읽고_저장하는_사이에_끼어든_저장은_DB_가_막는다()
    {
        var (_, stageId, _, _) = await LinkedAsync("테스트 버전 토큰");
        using var first = _db.NewScope();
        using var second = _db.NewScope();
        var s1 = await first.Db.Stages.FirstAsync(s => s.Id == stageId);
        var s2 = await second.Db.Stages.FirstAsync(s => s.Id == stageId);

        s1.Name = "먼저";
        s1.Version++;
        await first.Db.SaveChangesAsync();

        s2.Name = "나중";
        s2.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 조직은_그룹_아래_파트까지만_두고_사용자_소속으로_쓴다()
    {
        using var s = _db.NewScope();
        await s.Masters.SaveAsync(new MasterSave("org", 1, null, null, "품질그룹", null, null, null, null, null, null));
        var group = (await s.Masters.ListAsync("org")).Single(n => n.Name == "품질그룹");
        await s.Masters.SaveAsync(new MasterSave("org", 2, null, group.Id, "검사파트", null, null, null, null, null, null));
        var part = (await s.Masters.ListAsync("org")).Single(n => n.Name == "검사파트");
        Assert.Equal((2, group.Id), (part.Level, part.ParentId!.Value));

        await Assert.ThrowsAsync<RuleException>(() =>
            s.Masters.SaveAsync(new MasterSave("org", 3, null, part.Id, "세부", null, null, null, null, null, null)));

        var user = await s.Users.RegisterAsync("10.9.9.9", "새검사원", part.Id);
        Assert.Equal(part.Id, user.OrgUnitId);
        var node = (await s.Masters.ListAsync("user")).Single(n => n.Name == "새검사원");
        Assert.Equal("품질그룹 · 검사파트", node.Org);

        await Assert.ThrowsAsync<RuleException>(() => s.Users.RegisterAsync("10.9.9.8", "없는소속", 999999));
    }

    [Fact]
    public async Task 체크_항목_템플릿을_고치면_새_과제에_반영된다()
    {
        using (var s = _db.NewScope())
        {
            var nodes = await s.Masters.ListAsync("category");
            var sim = nodes.Single(n => n.Level == 1 && n.Name == "Simulation");
            var verify = nodes.Single(n => n.Level == 2 && n.ParentId == sim.Id && n.Name == "검증");
            await s.Masters.SaveAsync(new MasterSave("category", 3, null, verify.Id, "실측 데이터와 오차 5% 이내", null, null, null, null, null, null));
            await s.Masters.SaveAsync(new MasterSave("category", 3, null, verify.Id, "시나리오 3종 검토", null, null, null, null, null, null));
            var items = (await s.Masters.ListAsync("category")).Where(n => n.Level == 3 && n.ParentId == verify.Id).ToList();
            Assert.Equal(2, items.Count);
            await s.Masters.MoveAsync(new MasterRef("category", 3, items[1].Id, -1));
        }

        var id = await NewProjectAsync("테스트 체크 템플릿", "Simulation");
        var checks = (await ViewAsync(id)).Stages.Single(x => x.Name == "검증").Checks.Select(c => c.Text).ToArray();
        Assert.Equal(new[] { "시나리오 3종 검토", "실측 데이터와 오차 5% 이내" }, checks);
    }

    [Fact]
    public async Task 리드타임_통계는_완료한_단계의_소요일을_유형별로_낸다()
    {
        using var s = _db.NewScope();
        // 다른 테스트가 만든 과제는 빼고 샘플 과제만 본다
        var all = (await s.Projects.AllAsync()).Where(p => !p.Title.StartsWith("테스트")).ToList();
        var groups = Reporting.LeadTimes(all);

        var auto = groups.Single(g => g.Category == "물류 자동화");
        Assert.Equal(new[] { "검토", "품의", "발주", "제작", "검수", "입고", "설치", "안정화" }, auto.Rows.Select(r => r.Stage).ToArray());
        Assert.Equal(1, auto.DoneProjects);
        Assert.Equal(202, auto.AvgTotalDays);            // 완료 과제 1건: 착수 -230 → 마지막 완료 -28

        var review = auto.Rows[0];
        Assert.Equal(4, review.Count);                   // 검토를 끝낸 물류 자동화 과제 4건
        Assert.Equal((14, 18), (review.MinDays, review.MaxDays));
        Assert.Equal(16, review.AvgDays);                // (18 + 14 + 16 + 16) / 4
    }

    [Fact]
    public async Task 주간_보고와_변경_이력은_그_주의_활동을_모은다()
    {
        var (id, stageId, _, _) = await LinkedAsync("테스트 주간 보고");
        using (var s = _db.NewScope())
        {
            var st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, st.Name, Day(10, 22), null, "협력사 일정", false, st.Version));
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "주간 보고용 댓글"));
        }

        using var scope = _db.NewScope();
        var report = await scope.Insights.WeeklyAsync(null, false);
        Assert.Equal((Day(10, 5), Day(10, 11)), (report.From, report.To));      // 오늘(10-09)이 속한 월~일
        var mine = report.Projects.Single(p => p.Id == id);
        Assert.Contains(mine.Changes, c => c.Reason == "협력사 일정");
        Assert.Contains(mine.Comments, c => c.Text == "주간 보고용 댓글");
        Assert.Contains(mine.Next, n => n.Contains("A 종료 목표 10-14"));       // 다음 주(10-12~10-18)에 끝나는 작업
        Assert.Contains(mine.Next, n => n.Contains("B 종료 목표 10-16"));

        // 이 과제는 이번 주(10-09)에 등록했으므로 지난주 보고에는 없다 — 지난 보고에 그 뒤 등록한 과제가 끼지 않는다
        var last = await scope.Insights.WeeklyAsync(Day(9, 30), false);
        Assert.Equal(Day(9, 28), last.From);
        Assert.DoesNotContain(last.Projects, p => p.Id == id);

        var history = await scope.Insights.HistoryAsync(new HistoryFilter(null, null, null, "schedule", "테스트 주간 보고", 1));
        Assert.Single(history.Rows);
        Assert.Equal("협력사 일정", history.Rows[0].Reason);
        Assert.Contains("테스터", history.Users);

        var comments = await scope.Insights.HistoryAsync(new HistoryFilter(Today, Today, "테스터", "comment", "주간 보고용", 1));
        Assert.Single(comments.Rows);
    }

    [Fact]
    public async Task 내_할_일은_내가_담당인_미완료_작업을_마감순으로_나눈다()
    {
        var id = await NewProjectAsync("테스트 내 할 일");
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "밀린 작업", "테스터", null, Day(10, 1), Day(10, 7), Day(10, 1), null, 30, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "이번 주 작업", "다른사람", "테스터", Day(10, 12), Day(10, 14), null, null, 0, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "나중 작업", "테스터", null, Day(11, 2), Day(11, 6), null, null, 0, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "끝난 작업", "테스터", null, Day(10, 1), Day(10, 2), Day(10, 1), Day(10, 2), 100, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "남의 작업", "다른사람", null, Day(10, 12), Day(10, 14), null, null, 0, null, null));
        }

        using var scope = _db.NewScope();
        var my = await scope.Insights.MyAsync();
        Assert.Contains(my.Late, w => w.Title == "밀린 작업" && w.DDay == -2);
        Assert.Contains(my.Soon, w => w.Title == "이번 주 작업" && w.Role == "부담당");
        Assert.Contains(my.Later, w => w.Title == "나중 작업");
        var titles = my.Late.Concat(my.Soon).Concat(my.Later).Select(w => w.Title).ToList();
        Assert.DoesNotContain("끝난 작업", titles);
        Assert.DoesNotContain("남의 작업", titles);
        Assert.Contains(my.Stages, x => x.ProjectId == id && x.Stage == stage.Name);
    }
}
