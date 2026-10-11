using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 화면 사용성·정확성: 표시 규칙 모으기(임박·초과 완료·기준선 대비·상태 칩), 접속자의 오늘, 삭제 권한, 변경 시각
public class UsabilityRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Theory]
    [InlineData(0, "2026-10-09")]      // 같은 날
    [InlineData(-1, "2026-10-08")]     // 해외(한국보다 늦은 시간대) 저녁: 한국은 이미 다음 날
    [InlineData(1, "2026-10-10")]
    [InlineData(2, "2026-10-09")]      // PC 시계가 틀렸다 — 한국 날짜를 쓴다
    [InlineData(-30, "2026-10-09")]
    public void 접속자의_오늘은_한국_날짜_하루_안일_때만_받는다(int offset, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), Rules.UserToday(Today.AddDays(offset), Today));
    }

    [Fact]
    public void 화면이_오늘을_보내지_않으면_한국_날짜() => Assert.Equal(Today, Rules.UserToday(null, Today));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void 임박은_오늘부터_SoonDays_일까지(int daysLeft, bool soon)
    {
        Assert.Equal(7, Rules.SoonDays);
        Assert.Equal(soon, Rules.IsSoon(daysLeft));
    }

    [Fact]
    public void 남은_날_칩은_지연_임박_보통을_한_곳에서_정한다()
    {
        Assert.Equal(new ChipView("alarm", "alert", "지연 D+3"), Views.DueChip(-3, late: true));
        Assert.Equal(new ChipView("warn", "clock", "오늘"), Views.DueChip(0, late: false));
        Assert.Equal(new ChipView("warn", "clock", "D-7"), Views.DueChip(7, late: false));
        Assert.Equal(new ChipView("", null, "D-8"), Views.DueChip(8, late: false));
    }

    [Fact]
    public void 상태_칩과_표_글자는_같은_말을_쓴다()
    {
        Assert.Equal("지연 D+4", Views.ProjectChip(ProjectState.Active, 4).Text);
        Assert.Equal("지연", Views.StateLabel(ProjectState.Active, 4));
        Assert.Equal("보류", Views.StateLabel(ProjectState.Hold, 0));
        Assert.Equal(new ChipView("accent", null, "진행"), Views.ProjectChip(ProjectState.Active, 0));
        Assert.Equal("진행 중", Views.StageChip(StageStatus.Current, 0).Text);
        Assert.Equal("지연 D+2", Views.StageChip(StageStatus.Late, 2).Text);
        Assert.Equal("지연", Views.WorkChip(late: true, WorkStatus.InProgress, doneLate: false, closedByStage: false).Text);
        Assert.Equal(new ChipView("warn", "clock", "초과 완료"), Views.WorkChip(false, WorkStatus.Done, doneLate: true, closedByStage: false));
        Assert.Equal("완료(단계)", Views.WorkChip(false, WorkStatus.Planned, false, closedByStage: true).Text);
        Assert.Equal("예정", Views.WorkChip(false, WorkStatus.Planned, false, false).Text);
    }

    [Fact]
    public void 단계_초과_완료와_작업_기준선_대비_지연_일수()
    {
        Assert.True(Rules.IsDoneLate(new Stage { TargetDate = Today, CompletedDate = Today.AddDays(1) }));
        Assert.False(Rules.IsDoneLate(new Stage { TargetDate = Today, CompletedDate = Today }));
        Assert.False(Rules.IsDoneLate(new Stage { TargetDate = null, CompletedDate = Today }));
        Assert.True(Rules.IsLate(new Stage { TargetDate = Today.AddDays(-1) }, Today));
        Assert.False(Rules.IsLate(new Stage { TargetDate = Today.AddDays(-1), CompletedDate = Today }, Today));

        var w = new WorkItem { PlanStart = Today.AddDays(-5), PlanEnd = Today.AddDays(-2), BaselineEnd = Today.AddDays(-4) };
        Assert.Equal(2, Rules.SlipDays(w));
        Assert.Null(Rules.SlipDays(new WorkItem { PlanEnd = Today }));
        Assert.Equal(2, Rules.LateDays(w, null, Today));
        // 완료된 단계의 작업은 지연이 아니다
        Assert.Equal(0, Rules.LateDays(w, new Stage { CompletedDate = Today }, Today));
    }

    [Fact]
    public void 삭제_권한은_담당자_등록자_관리자_첨부는_올린_사람_과제_담당자_관리자()
    {
        var p = new Project { OwnerId = 1, CreatedById = 2 };
        Assert.True(Rules.CanDeleteProject(p, 1, false));
        Assert.True(Rules.CanDeleteProject(p, 2, false));
        Assert.True(Rules.CanDeleteProject(p, 9, true));
        Assert.False(Rules.CanDeleteProject(p, 3, false));
        Assert.False(Rules.CanDeleteProject(p, null, false));

        var a = new Attachment { UploadedById = 5 };
        Assert.True(Rules.CanDeleteAttachment(a, 1, 5, false));
        Assert.True(Rules.CanDeleteAttachment(a, 1, 1, false));
        Assert.True(Rules.CanDeleteAttachment(a, 1, 7, true));
        Assert.False(Rules.CanDeleteAttachment(a, 1, 7, false));
    }
}

// 실제 DB 에서: 서버가 채운 표시 값, 접속자 날짜로 넣는 실적 시작, 삭제 권한, 변경 시각(UpdatedAt)
public class UsabilityServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;                       // 2026-10-09

    public UsabilityServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string? user = null)
    {
        using var s = _db.NewScope(user);
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Today.AddDays(-10), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id, string? user = null, bool admin = false)
    {
        using var s = _db.NewScope(user, admin);
        return (await s.Projects.ViewAsync(id))!;
    }

    [Fact]
    public async Task 진행률만_올리면_실적_시작은_접속자의_오늘이고_범위_밖이면_한국_날짜()
    {
        var id = await NewProjectAsync("테스트 접속자 날짜");
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
        {
            // 해외 PC: 한국은 이미 10-09 지만 그 나라는 아직 10-08
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "해외 작업", null, null, Today.AddDays(-3), Today.AddDays(3),
                null, null, 10, null, null, ClientToday: Today.AddDays(-1)));
            // PC 시계가 틀린 경우
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "시계 틀림", null, null, Today.AddDays(-3), Today.AddDays(3),
                null, null, 10, null, null, ClientToday: Today.AddDays(-40)));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "날짜 없음", null, null, Today.AddDays(-3), Today.AddDays(3),
                null, null, 10, null, null));
        }
        var works = (await ViewAsync(id)).Stages[0].Works;
        Assert.Equal(Today.AddDays(-1), works.Single(w => w.Title == "해외 작업").ActualStart);
        Assert.Equal(Today, works.Single(w => w.Title == "시계 틀림").ActualStart);
        Assert.Equal(Today, works.Single(w => w.Title == "날짜 없음").ActualStart);
    }

    [Fact]
    public async Task 서버가_칩_초과_완료_기준선_대비_지연_일수를_채운다()
    {
        var id = await NewProjectAsync("테스트 표시 값");
        var view = await ViewAsync(id);
        var first = view.Stages[0];
        var second = view.Stages[1];
        using (var s = _db.NewScope())
        {
            // 첫 단계: 목표일 지나 완료(초과 완료), 둘째 단계: 목표일 3일 뒤(임박)
            await s.Projects.SaveStageAsync(new StageInput(id, first.Id, first.Name, Today.AddDays(-5), null, null, false));
            await s.Projects.SaveWorkAsync(new WorkInput(null, second.Id, "늦은 작업", null, null, Today.AddDays(-6), Today.AddDays(-2),
                Today.AddDays(-6), null, 50, null, null));
        }
        using (var s = _db.NewScope()) await s.Projects.SetBaselineAsync(id);
        var stages = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, first.Id, first.Name, Today.AddDays(-5), Today.AddDays(-1), null, false,
                stages[0].Version));
            await s.Projects.SaveStageAsync(new StageInput(id, second.Id, second.Name, Today.AddDays(3), null, null, false, stages[1].Version));
            var w = stages[1].Works.Single();
            await s.Projects.SaveWorkAsync(new WorkInput(w.Id, second.Id, w.Title, null, null, w.PlanStart, w.PlanEnd.AddDays(1),
                w.ActualStart, null, 50, null, "범위 확대", w.Version));
        }

        view = await ViewAsync(id);
        var done = view.Stages[0];
        Assert.True(done.DoneLate);
        Assert.Equal(new ChipView("ok", "check", "완료"), done.Chip);
        Assert.Null(done.DueChip);
        var soon = view.Stages[1];
        Assert.False(soon.DoneLate);
        Assert.Equal("D-3", soon.DueChip?.Text);
        var late = soon.Works.Single();
        Assert.True(late.Late);
        Assert.Equal(1, late.LateDays);                 // 목표 종료 10-08 → 오늘 10-09
        Assert.Equal(1, late.SlipDays);                 // 기준선 10-07 → 10-08
        Assert.Equal("지연", late.Chip.Text);
        Assert.Equal(view.DelayDays > 0 ? "지연" : "진행", view.StateLabel);
    }

    [Fact]
    public async Task 대시보드_임박_건수와_다가오는_일정의_임박_표시가_같은_규칙이다()
    {
        using var s = _db.NewScope();
        var dash = await s.Dashboard.BuildAsync(false);
        var all = await s.Projects.AllAsync();
        var expected = all.Where(p => p.State == ProjectState.Active)
            .SelectMany(p => p.Stages).Count(st => st.CompletedDate == null && st.TargetDate != null
                && Rules.IsSoon(Rules.DaysLeft(st.TargetDate.Value, Today)));
        Assert.Equal(expected, dash.Kpi.DueThisWeek);
        Assert.All(dash.Upcoming, u => Assert.Equal(u.Soon ? "warn" : "", u.Chip.Cls));
    }

    [Fact]
    public async Task 삭제_단추_표시는_지금_사용자의_권한을_따른다()
    {
        var id = await NewProjectAsync("테스트 삭제 권한", user: "담당자갑");
        int fileId;
        using (var s = _db.NewScope("담당자갑"))
        {
            var mine = new Attachment { ProjectId = id, FileName = "갑.txt", StoredName = "x.txt", ContentType = "text/plain", Size = 1, UploadedById = s.Me.Id, UploadedAt = _db.Clock.UtcNow };
            s.Db.Attachments.Add(mine);
            await s.Db.SaveChangesAsync();
            fileId = mine.Id;
        }

        var owner = await ViewAsync(id, "담당자갑");
        Assert.True(owner.CanDelete);
        Assert.True(owner.Attachments!.Single(a => a.Id == fileId).CanDelete);

        var other = await ViewAsync(id, "다른사람을");
        Assert.False(other.CanDelete);
        Assert.False(other.Attachments!.Single(a => a.Id == fileId).CanDelete);

        var admin = await ViewAsync(id, "관리자병", admin: true);
        Assert.True(admin.CanDelete);
        Assert.True(admin.Attachments!.Single(a => a.Id == fileId).CanDelete);

        // 표시와 같은 판정으로 서비스도 막는다
        using var deny = _db.NewScope("다른사람을");
        await Assert.ThrowsAsync<RuleException>(() => deny.Projects.DeleteAsync(id));
    }

    [Fact]
    public async Task 과제를_바꾸는_모든_경로가_변경_시각을_갱신한다()
    {
        // 상세 화면의 '다른 사용자가 바꿨습니다' 띠는 Project.UpdatedAt 하나만 본다 — 댓글·체크리스트까지 모두 갱신해야 한다
        var id = await NewProjectAsync("테스트 변경 시각");
        var stage = (await ViewAsync(id)).Stages[0];

        async Task Changes(string what, Func<TestDb.Scope, Task> action)
        {
            DateTime before;
            using (var s = _db.NewScope()) before = (await s.Projects.StampAsync(id))!.Value;
            _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(1);
            using (var s = _db.NewScope()) await action(s);
            using (var s = _db.NewScope())
            {
                var after = (await s.Projects.StampAsync(id))!.Value;
                Assert.True(after > before, what + " 뒤에 변경 시각이 그대로");
                Assert.Equal(DateTimeKind.Utc, after.Kind);
            }
        }

        await Changes("댓글", s => s.Projects.CommentAsync(new CommentInput(id, null, null, "메모")));
        await Changes("단계 목표일", s => s.Projects.SaveStageAsync(new StageInput(id, stage.Id, stage.Name, Today.AddDays(5), null, null, false)));
        await Changes("체크 항목 추가", async s => await s.Projects.SaveCheckAsync(new CheckInput(stage.Id, null, "확인")));
        var check = (await ViewAsync(id)).Stages[0].Checks.Last();
        await Changes("체크", async s => await s.Projects.ToggleCheckAsync(check.Id, true));
        await Changes("체크 삭제", async s => await s.Projects.DeleteCheckAsync(check.Id));
        await Changes("작업 추가", async s => await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "작업", null, null, Today, Today.AddDays(2), null, null, 0, null, null)));
        var work = (await ViewAsync(id)).Stages[0].Works.Single();
        await Changes("작업 삭제", async s => await s.Projects.DeleteWorkAsync(work.Id));
        await Changes("단계 순서", async s => await s.Projects.MoveStageAsync(stage.Id, 1));
        await Changes("기준선", s => s.Projects.SetBaselineAsync(id));
        await Changes("상태", s => s.Projects.SetStateAsync(id, ProjectState.Hold, "대기"));
        var last = (await ViewAsync(id)).Stages.Last();
        await Changes("단계 삭제", async s => await s.Projects.DeleteStageAsync(last.Id));

        using var gone = _db.NewScope();
        Assert.Null(await gone.Projects.StampAsync(987654));
    }
}
