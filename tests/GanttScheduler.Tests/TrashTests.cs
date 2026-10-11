using System.Text;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GanttScheduler.Tests;

// 휴지통(소프트 삭제·복원·영구 삭제)과 기준선 차수 이력
public class TrashTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;
    private DateOnly D(int offset) => Today.AddDays(offset);

    public TrashTests(TestDb db) => _db = db;

    // 테스터(관리자)가 담당인 새 과제('물류' 유형)와 첫 단계
    private async Task<(int Id, int StageId)> NewProjectAsync(string title)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, D(-10), null, null, null, null));
        return (id, (await s.Projects.ViewAsync(id))!.Stages[0].Id);
    }

    private async Task<int> AddWorkAsync(int stageId, string title, DateOnly from, DateOnly to)
    {
        using var s = _db.NewScope();
        await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, title, "테스터", null, from, to, null, null, 0, null, null));
        return await s.Db.WorkItems.Where(w => w.StageId == stageId && w.Title == title).Select(w => w.Id).FirstAsync();
    }

    private async Task<ProjectView> ViewAsync(int id, int? compare = null)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id, compare: compare))!;
    }

    [Fact]
    public async Task 휴지통에_보낸_과제는_모든_조회에서_빠지고_복원하면_돌아온다()
    {
        var (id, stageId) = await NewProjectAsync("휴지통 조회 과제");
        await AddWorkAsync(stageId, "늦은 작업", D(-6), D(-2));          // 내 할 일 '지연'
        var otherId = (await NewProjectAsync("휴지통 연결 상대")).Id;

        // 다른 사람의 댓글 → 담당(테스터)에게 알림·새 소식
        using (var colleague = _db.NewScope("휴지통 동료"))
            await colleague.Projects.CommentAsync(new CommentInput(id, null, null, "현장 확인 부탁"));
        int fileId;
        using (var s = _db.NewScope())
        {
            await s.Projects.WatchAsync(id, true);
            var files = Files(s);
            await files.SaveAsync(id, null, "도면.txt", 3, new MemoryStream(Encoding.UTF8.GetBytes("abc")));
            fileId = await s.Db.Attachments.Where(a => a.ProjectId == id).Select(a => a.Id).FirstAsync();
            Assert.NotNull(await files.FindAsync(fileId));
        }
        int leadBefore;
        using (var s = _db.NewScope())
            leadBefore = (await s.Insights.LeadTimesAsync()).Single(g => g.Category == "물류").Projects;

        // 지울 권한이 없으면 거절
        using (var stranger = _db.NewScope("휴지통 남"))
            await Assert.ThrowsAsync<RuleException>(() => stranger.Projects.DeleteAsync(id));
        // 화면이 본 버전이 다르면 409
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.DeleteAsync(id, null, version: 999));

        using (var s = _db.NewScope())
            await s.Projects.DeleteAsync(id, "중복 등록", version: (await s.Projects.ViewAsync(id))!.Version);

        using (var s = _db.NewScope())
        {
            // 표시만 — 행·활동·첨부는 남아 있다
            var row = await s.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == id);
            Assert.NotNull(row.DeletedAt);
            Assert.Equal(s.Me.Id, row.DeletedById);
            Assert.Equal("중복 등록", row.DeleteReason);
            Assert.True(await s.Db.Activities.AnyAsync(a => a.ProjectId == id && a.Type == ActivityType.ProjectDeleted && a.Reason == "중복 등록"));
            Assert.True(await s.Db.Attachments.AnyAsync(a => a.ProjectId == id));

            // 상세·활동·변경 확인·수정
            Assert.Null(await s.Projects.ViewAsync(id));
            Assert.Null(await s.Projects.ActivitiesAsync(id));
            Assert.Null(await s.Projects.StampAsync(id));
            Assert.Null(await s.Projects.LoadInfoAsync(id));
            Assert.Null(await s.Projects.OpenAsync(id));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveStageAsync(new StageInput(id, null, "새 단계", null, null, null, false)));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, "댓글")));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.DeleteAsync(id));
            // 복제 원본
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.CloneAsync(new CloneInput(id, "사본", D(0), true)));
            // 목록·대시보드
            Assert.DoesNotContain(await s.Projects.AllAsync(), p => p.Id == id);
            Assert.DoesNotContain(await s.Dashboard.ListAsync(new ProjectFilter(null, null, null, null, false)), p => p.Id == id);
            Assert.DoesNotContain((await s.Dashboard.BuildAsync(false)).Timeline, t => t.Id == id);
            // 내 할 일·주간 보고·리드타임·변경 이력
            var my = await s.Insights.MyAsync();
            Assert.DoesNotContain(my.Late.Concat(my.Soon).Concat(my.Later), w => w.ProjectId == id);
            Assert.DoesNotContain((await s.Insights.WeeklyAsync(Today, false)).Projects, p => p.Id == id);
            Assert.Equal(leadBefore - 1, (await s.Insights.LeadTimesAsync()).Single(g => g.Category == "물류").Projects);
            Assert.Equal(0, (await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, null, 1, id))).Total);
            Assert.DoesNotContain((await s.Insights.HistoryAsync(new HistoryFilter(D(-30), Today, null, null, "휴지통 조회", 1))).Rows, r => r.ProjectId == id);
            // 첨부 내려받기·올리기
            var files = Files(s);
            Assert.Null(await files.FindAsync(fileId));
            await Assert.ThrowsAsync<RuleException>(() => files.SaveAsync(id, null, "b.txt", 1, new MemoryStream(new byte[] { 1 })));
            // 새 소식·새 활동 수·관심 과제·알림
            Assert.DoesNotContain(await s.Projects.NewsAsync(), n => n.ProjectId == id);
            Assert.False((await s.Projects.NewCountsAsync()).ContainsKey(id));
            Assert.DoesNotContain(id, await s.Projects.WatchedIdsAsync());
            Assert.DoesNotContain(await s.Projects.WatchedProjectsAsync(), w => w.Id == id);
            var notes = new NotificationService(s.Db, _db.Clock, s.Me);
            Assert.DoesNotContain((await notes.ListAsync()).Items, n => n.ProjectId == id);
            // 과제 간 연결 상대 검색
            Assert.DoesNotContain(await s.Projects.LinkTargetsAsync(otherId, "휴지통 조회"), p => p.Id == id);
            // 휴지통 목록
            var trash = Assert.Single(await s.Projects.TrashAsync(), t => t.Id == id);
            Assert.Equal("테스터", trash.DeletedBy);
            Assert.Equal("중복 등록", trash.Reason);
            Assert.Equal(1, trash.Attachments);
        }

        // 복원은 지울 권한이 있는 사람만
        using (var stranger = _db.NewScope("휴지통 남"))
            await Assert.ThrowsAsync<RuleException>(() => stranger.Projects.RestoreAsync(id));
        using (var s = _db.NewScope())
            await s.Projects.RestoreAsync(id);
        using (var s = _db.NewScope())
        {
            var view = await s.Projects.ViewAsync(id);
            Assert.NotNull(view);
            Assert.Contains(view!.Activities!, a => a.Type == "projectRestored");
            Assert.Contains(await s.Projects.AllAsync(), p => p.Id == id);
            Assert.Contains(id, await s.Projects.WatchedIdsAsync());
            Assert.NotNull(await Files(s).FindAsync(fileId));
            Assert.Contains((await new NotificationService(s.Db, _db.Clock, s.Me).ListAsync()).Items, n => n.ProjectId == id);
            Assert.DoesNotContain(await s.Projects.TrashAsync(), t => t.Id == id);
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.RestoreAsync(id));       // 이미 복원됨
        }
    }

    [Fact]
    public async Task 휴지통에_있는_과제와의_단계_연결은_남기되_위반으로_세지_않는다()
    {
        var (pred, predStage) = await NewProjectAsync("휴지통 선행 과제");
        var (succ, succStage) = await NewProjectAsync("휴지통 후행 과제");
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(pred, predStage, "검토", D(20), null, null, false, (await s.Projects.ViewAsync(pred))!.Stages[0].Version));
            await s.Projects.SaveStageAsync(new StageInput(succ, succStage, "검토", D(5), null, null, false, (await s.Projects.ViewAsync(succ))!.Stages[0].Version));
            await s.Projects.AddStageLinkAsync(new StageLinkInput(succStage, predStage, "pred", 0));
        }
        Assert.Equal(1, (await ViewAsync(succ)).LinkViolations);

        using (var s = _db.NewScope())
            await s.Projects.DeleteAsync(pred);
        var view = await ViewAsync(succ);
        Assert.Equal(0, view.LinkViolations);
        Assert.Empty(view.Stages[0].CrossLinks);
        using (var s = _db.NewScope())
        {
            Assert.True(await s.Db.StageLinks.AnyAsync(l => l.PredecessorStageId == predStage));
            // 휴지통의 과제와는 새로 잇지 않는다
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddStageLinkAsync(new StageLinkInput(succStage, predStage, "succ", 0)));
            await s.Projects.RestoreAsync(pred);
        }
        Assert.Equal(1, (await ViewAsync(succ)).LinkViolations);
    }

    [Fact]
    public async Task 영구_삭제는_관리자만_휴지통의_과제를_지우고_기록을_남긴다()
    {
        var (id, stageId) = await NewProjectAsync("휴지통 영구 삭제");
        await AddWorkAsync(stageId, "작업", D(1), D(3));
        using (var s = _db.NewScope())
        {
            // 휴지통에 없는 과제는 영구 삭제하지 않는다
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.PurgeAsync(id));
            await s.Projects.SetBaselineAsync(id);
            await s.Projects.DeleteAsync(id, "취소");
        }
        // 담당자라도 관리자가 아니면 영구 삭제할 수 없다
        using (var owner = _db.NewScope("테스터", admin: false))
            await Assert.ThrowsAsync<RuleException>(() => owner.Projects.PurgeAsync(id));
        _db.NewScope("테스터", admin: true).Dispose();

        PurgeLogView log;
        using (var s = _db.NewScope())
            log = await s.Projects.PurgeAsync(id);
        Assert.Equal("휴지통 영구 삭제", log.Title);
        Assert.Equal("테스터", log.PurgedBy);
        Assert.Equal("테스터", log.DeletedBy);
        Assert.Equal("취소", log.Reason);
        using (var s = _db.NewScope())
        {
            Assert.False(await s.Db.Projects.AnyAsync(p => p.Id == id));
            Assert.False(await s.Db.Activities.AnyAsync(a => a.ProjectId == id));
            Assert.False(await s.Db.Baselines.AnyAsync(b => b.ProjectId == id));
            var row = await s.Db.DeletedProjectLogs.SingleAsync(l => l.ProjectId == id);
            Assert.Equal("휴지통 영구 삭제", row.Title);
            Assert.Contains(await s.Projects.PurgeLogAsync(), l => l.ProjectId == id);
        }
    }

    [Fact]
    public async Task 보관_기간이_지난_과제만_정리_대상이다()
    {
        var (old, _) = await NewProjectAsync("휴지통 오래됨");
        var (fresh, _) = await NewProjectAsync("휴지통 최근");
        using (var s = _db.NewScope())
        {
            await s.Projects.DeleteAsync(old);
            await s.Projects.DeleteAsync(fresh);
            var oldRow = await s.Db.Projects.SingleAsync(p => p.Id == old);
            oldRow.DeletedAt = _db.Clock.UtcNow.AddDays(-31);
            var freshRow = await s.Db.Projects.SingleAsync(p => p.Id == fresh);
            freshRow.DeletedAt = _db.Clock.UtcNow.AddDays(-29);
            s.Db.SaveChanges();
        }
        var options = new TrashOptions(30);
        using (var s = _db.NewScope())
        {
            var expired = await s.Projects.ExpiredAsync(options.CutoffUtc(_db.Clock.UtcNow));
            Assert.Contains(expired, e => e.Id == old && e.DeletedById == s.Me.Id);
            Assert.DoesNotContain(expired, e => e.Id == fresh);
        }
        Assert.False(new TrashOptions(0).AutoPurge);
        Assert.Null(new TrashOptions(0).PurgeAt(_db.Clock.UtcNow));
        Assert.Equal(_db.Clock.UtcNow.AddDays(30), options.PurgeAt(_db.Clock.UtcNow));
    }

    // ── 기준선 차수 ──────────────────────────────────────────────────

    [Fact]
    public async Task 기준선은_확정할_때마다_새_차수가_되고_고른_차수_대비로_센다()
    {
        var (id, stageId) = await NewProjectAsync("차수 과제");
        var workId = await AddWorkAsync(stageId, "차수 작업", D(1), D(5));
        using (var s = _db.NewScope())
        {
            var st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, st.Name, D(10), null, null, false, st.Version));
            await s.Projects.SetBaselineAsync(id);                                   // 1차: 목표 D+10, 작업 D+1~D+5
        }
        using (var s = _db.NewScope())
        {
            var st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, st.Name, D(14), null, "협력사 지연", false, st.Version));
            await s.Projects.SetBaselineAsync(id, "계약 변경");                     // 2차: 목표 D+14
            st = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveStageAsync(new StageInput(id, stageId, st.Name, D(17), null, "추가 지연", false, st.Version));
        }

        var latest = await ViewAsync(id);
        Assert.Equal(new[] { 2, 1 }, latest.Baselines!.Select(b => b.No));
        Assert.Equal("계약 변경", latest.Baselines![0].Reason);
        Assert.Equal("테스터", latest.Baselines![1].User);
        Assert.Null(latest.CompareNo);
        Assert.Equal(3, latest.Stages[0].SlipDays);                                  // 최신(2차) 대비
        Assert.Contains(latest.Activities!, a => a.Text.StartsWith("기준선 다시 확정 (2차"));

        var first = await ViewAsync(id, compare: 1);
        Assert.Equal(1, first.CompareNo);
        Assert.Equal(7, first.Stages[0].SlipDays);                                   // 1차 대비
        Assert.Equal(D(10), first.Stages[0].BaselineDate);
        Assert.Equal(7, first.SlipDays);
        Assert.Equal(D(5), first.Stages[0].Works.Single(w => w.Id == workId).BaselineEnd);
        // 최신 차수나 없는 차수를 고르면 최신 그대로
        Assert.Null((await ViewAsync(id, compare: 2)).CompareNo);
        Assert.Null((await ViewAsync(id, compare: 9)).CompareNo);
        Assert.Equal(3, (await ViewAsync(id, compare: 9)).Stages[0].SlipDays);

        // 차수에 없던 작업은 그 차수 대비 값이 없다
        var later = await AddWorkAsync(stageId, "나중 작업", D(6), D(8));
        using (var s = _db.NewScope())
            await s.Projects.SetBaselineAsync(id, "범위 추가");                      // 3차: 나중 작업 포함
        var cmp2 = await ViewAsync(id, compare: 2);
        Assert.Null(cmp2.Stages[0].Works.Single(w => w.Id == later).BaselineEnd);
        Assert.NotNull((await ViewAsync(id)).Stages[0].Works.Single(w => w.Id == later).BaselineEnd);

        // 단계를 지워도 차수 기록은 남는다(느슨한 참조)
        using (var s = _db.NewScope())
        {
            var st = (await s.Projects.ViewAsync(id))!.Stages[0];
            foreach (var w in st.Works) await s.Projects.DeleteWorkAsync(w.Id);
            await s.Projects.DeleteStageAsync(stageId, (await s.Projects.ViewAsync(id))!.Stages[0].Version);
        }
        using (var s = _db.NewScope())
        {
            Assert.Equal(3, await s.Db.BaselineItems.CountAsync(i => i.StageId == stageId));
            Assert.Equal(3, await s.Db.BaselineItems.CountAsync(i => i.WorkItemId == workId));
        }
        Assert.Equal(3, (await ViewAsync(id, compare: 1)).Baselines!.Count);

        // 복제는 기준선을 가져가지 않는다(규칙 15)
        int clone;
        using (var s = _db.NewScope())
            clone = await s.Projects.CloneAsync(new CloneInput(id, "차수 사본", D(0), true));
        var cv = await ViewAsync(clone);
        Assert.Empty(cv.Baselines!);
        Assert.Null(cv.BaselineAt);
    }

    [Fact]
    public async Task 기존_기준선은_시작할_때_한_번만_1차로_옮긴다()
    {
        using var s = _db.NewScope();
        // 샘플 데이터는 차수 이력 없이 기준선 열만 채운 과제가 있다(이전 버전의 데이터와 같은 모양)
        var pending = await s.Db.Projects.Where(p => p.BaselineAt != null && !s.Db.Baselines.Any(b => b.ProjectId == p.Id)).Select(p => p.Id).ToListAsync();
        Assert.NotEmpty(pending);
        var moved = await BaselineHistory.EnsureFirstRoundsAsync(s.Db);
        Assert.Equal(pending.Count, moved);
        Assert.Equal(0, await BaselineHistory.EnsureFirstRoundsAsync(s.Db));            // 멱등

        var pid = pending[0];
        var p = await s.Db.Projects.AsNoTracking().Include(x => x.Stages).ThenInclude(x => x.Works).SingleAsync(x => x.Id == pid);
        var round = await s.Db.Baselines.AsNoTracking().Include(b => b.Items).SingleAsync(b => b.ProjectId == pid);
        Assert.Equal(1, round.No);
        Assert.Equal(p.BaselineAt, round.At);
        foreach (var st in p.Stages)
            Assert.Equal(st.BaselineDate, round.Items.Single(i => i.StageId == st.Id).End);
        foreach (var w in p.Stages.SelectMany(x => x.Works).Where(w => w.BaselineEnd != null))
            Assert.Equal((w.BaselineStart, w.BaselineEnd), round.Items.Where(i => i.WorkItemId == w.Id).Select(i => (i.Start, i.End)).Single());

        // 옮긴 뒤 다시 확정하면 2차
        using var t = _db.NewScope();
        await t.Projects.SetBaselineAsync(pid, "이전 뒤 재확정");
        Assert.Equal(new[] { 2, 1 }, (await t.Projects.BaselinesAsync(pid)).Select(b => b.No));
    }

    [Fact]
    public void 기준선_대비는_고른_기준값으로_센다()
    {
        Assert.Equal(3, Rules.SlipDays(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 10)));
        Assert.Equal(-2, Rules.SlipDays(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 10)));
        Assert.Null(Rules.SlipDays(null, new DateOnly(2026, 10, 10)));
        Assert.Null(Rules.SlipDays(new DateOnly(2026, 10, 10), null));

        var p = new Project { Stages = { new Stage { Id = 1, TargetDate = new DateOnly(2026, 10, 20), BaselineDate = new DateOnly(2026, 10, 18),
            Works = { new WorkItem { Id = 5, PlanStart = new DateOnly(2026, 10, 12), PlanEnd = new DateOnly(2026, 10, 16) },
                      new WorkItem { Id = 6, PlanStart = new DateOnly(2026, 10, 12), PlanEnd = new DateOnly(2026, 10, 14), BaselineEnd = new DateOnly(2026, 10, 14) } } } } };
        var items = Rules.BaselineItems(p);
        Assert.Equal(3, items.Count);
        var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Rules.UseBaseline(p, new[] { new BaselineItem { StageId = 1, End = new DateOnly(2026, 10, 15) }, new BaselineItem { WorkItemId = 5, Start = new DateOnly(2026, 10, 10), End = new DateOnly(2026, 10, 14) } }, at);
        Assert.Equal(at, p.BaselineAt);
        Assert.Equal(5, Rules.SlipDays(p.Stages[0]));
        Assert.Equal(2, Rules.SlipDays(p.Stages[0].Works[0]));
        Assert.Null(Rules.SlipDays(p.Stages[0].Works[1]));            // 그 차수에 없던 작업
    }

    private AttachmentService Files(TestDb.Scope s)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "gantt-trash-test"),
        }).Build();
        return new AttachmentService(s.Db, _db.Clock, s.Me, cfg, new Env());
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GanttScheduler";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
