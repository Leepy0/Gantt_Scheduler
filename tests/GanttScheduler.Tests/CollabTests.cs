using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GanttScheduler.Tests;

// 협업 규칙 (DB 없음): 새 활동 기준, 댓글 수정 가능 판정
public class CollabRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 새_활동은_마지막으로_본_뒤부터_최근_7일까지만_센다()
    {
        // 처음 보는 과제는 7일 전부터
        Assert.Equal(Now.AddDays(-Rules.NewActivityDays), Rules.NewSince(null, Now));
        // 어제 봤으면 어제부터
        Assert.Equal(Now.AddDays(-1), Rules.NewSince(Now.AddDays(-1), Now));
        // 오래 안 봤으면 7일로 자른다 (수백 건으로 보이지 않게)
        Assert.Equal(Now.AddDays(-7), Rules.NewSince(Now.AddDays(-30), Now));
    }

    [Fact]
    public void 내가_남긴_활동과_댓글_수정_기록은_새_활동이_아니다()
    {
        var since = Now.AddHours(-1);
        Assert.True(Rules.IsNewActivity(ActivityType.Comment, authorId: 2, Now, userId: 1, since));
        Assert.False(Rules.IsNewActivity(ActivityType.Comment, authorId: 1, Now, userId: 1, since));
        Assert.False(Rules.IsNewActivity(ActivityType.Comment, authorId: 2, since, userId: 1, since));     // 본 시각과 같으면 이미 본 것
        Assert.False(Rules.IsNewActivity(ActivityType.CommentChanged, authorId: 2, Now, userId: 1, since));
        Assert.True(Rules.IsNewActivity(ActivityType.WorkPlanChanged, authorId: 2, Now, userId: 1, since));
    }

    [Fact]
    public void 댓글은_작성자만_정한_시간_안에_지우지_않은_것만_고친다()
    {
        Assert.True(Rules.CanEditComment(1, Now, null, 1, Now.AddHours(24), 24));
        Assert.False(Rules.CanEditComment(1, Now, null, 1, Now.AddHours(24).AddMinutes(1), 24));
        Assert.False(Rules.CanEditComment(1, Now, null, 2, Now, 24));
        Assert.False(Rules.CanEditComment(1, Now, Now, 1, Now, 24));
        Assert.False(Rules.CanEditComment(1, Now, null, 1, Now, 0));       // 0 이면 고칠 수 없게
    }

    [Fact]
    public void 알림_한_줄은_대표_활동과_함께_묶인_수()
    {
        var comment = new Activity { Type = ActivityType.Comment, Text = "현장 확인" };
        Assert.Equal("댓글: 현장 확인", ActivityLog.Summary(comment, 1));
        var plan = new Activity { Type = ActivityType.WorkPlanChanged, Text = "[배선] 목표 일정 변경" };
        Assert.Equal("[배선] 목표 일정 변경 외 2건", ActivityLog.Summary(plan, 3));
        Assert.EndsWith("…", ActivityLog.Summary(new Activity { Type = ActivityType.Edited, Text = new string('가', 400) }, 1));
    }

    [Fact]
    public void 메일_주소는_형식이_맞아야_받는다()
    {
        Assert.Equal("kim@example.com", NotifyOptions.NormalizeEmail(" kim@example.com "));
        Assert.Null(NotifyOptions.NormalizeEmail("kim"));
        Assert.Null(NotifyOptions.NormalizeEmail("김물류 <kim@example.com>"));
        Assert.Null(NotifyOptions.NormalizeEmail("kim@localhost"));
        Assert.Null(NotifyOptions.NormalizeEmail(""));
    }
}

// 협업 서비스: 알림 생성 대상, 멘션, 댓글 수정·삭제, 방문·새 활동, 새 소식, 관심 과제, 활동 조건
public class CollabTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;
    private DateOnly D(int offset) => Today.AddDays(offset);

    public CollabTests(TestDb db) => _db = db;

    // 테스터(관리자)가 담당인 새 과제('물류' 유형)와 첫 단계
    private async Task<(int Id, int StageId)> NewProjectAsync(string title)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, D(-5), null, null, null, null));
        return (id, (await s.Projects.ViewAsync(id))!.Stages[0].Id);
    }

    private async Task<int> UserIdAsync(string name)
    {
        using var s = _db.NewScope();
        var id = await s.Db.Users.Where(u => u.Name == name).Select(u => u.Id).FirstOrDefaultAsync();
        if (id != 0) return id;
        using var made = _db.NewScope(name);      // 없으면 만든다(관리자 아님)
        return made.Me.Id;
    }

    private async Task<List<Notification>> NotesAsync(string name, int projectId)
    {
        var userId = await UserIdAsync(name);
        using var s = _db.NewScope();
        return await s.Db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.ProjectId == projectId).OrderBy(n => n.Id).ToListAsync();
    }

    private async Task<WorkView> WorkAsync(int projectId, string title)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(projectId))!.Stages.SelectMany(x => x.Works).Single(w => w.Title == title);
    }

    private async Task<int> CommentIdAsync(int projectId, string text)
    {
        using var s = _db.NewScope();
        return await s.Db.Activities.Where(a => a.ProjectId == projectId && a.Type == ActivityType.Comment && a.Text == text).Select(a => a.Id).SingleAsync();
    }

    [Fact]
    public async Task 남이_내_작업을_바꾸면_담당에게_알리고_자기_활동은_알리지_않는다()
    {
        var (id, stageId) = await NewProjectAsync("알림 내 작업");
        await UserIdAsync("작업담당");

        // 테스터(과제 담당)가 '작업담당' 의 작업을 만든다 → 작업담당에게 '내 작업', 남긴 본인(테스터)에게는 없음
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "배선 점검", "작업담당", null, D(1), D(3), null, null, 0, null, null));
        var mine = Assert.Single(await NotesAsync("작업담당", id));
        Assert.Equal(NotifyKinds.Work, mine.Kind);
        Assert.Contains("작업 추가: 배선 점검", mine.Text);
        Assert.Null(mine.ReadAt);
        Assert.Empty(await NotesAsync("테스터", id));

        // 작업담당이 자기 작업 진행률을 올리면 → 과제 담당(테스터)에게 '내 과제', 작업담당 본인에게는 없음
        var w = await WorkAsync(id, "배선 점검");
        using (var s = _db.NewScope("작업담당"))
            await s.Projects.SaveWorkAsync(new WorkInput(w.Id, stageId, w.Title, "작업담당", null, w.PlanStart, w.PlanEnd, null, null, 30, null, null, w.Version));
        var owner = Assert.Single(await NotesAsync("테스터", id));
        Assert.Equal(NotifyKinds.Project, owner.Kind);
        Assert.Contains("진행률 0% → 30%", owner.Text);
        Assert.Single(await NotesAsync("작업담당", id));

        // 알림은 그 활동과 같은 저장에 들어가 활동을 가리킨다
        using (var s = _db.NewScope())
        {
            var act = await s.Db.Activities.SingleAsync(a => a.Id == owner.ActivityId);
            Assert.Equal(ActivityType.WorkEdited, act.Type);
            Assert.Equal(w.Id, act.WorkItemId);
        }

        // 담당을 다른 사람에게 넘기면 새 담당과 이전 담당 모두 받는다(새 담당은 저장 전 값으로는 알 수 없어 따로 더한다)
        await UserIdAsync("새담당");
        w = await WorkAsync(id, "배선 점검");
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(w.Id, stageId, w.Title, "새담당", null, w.PlanStart, w.PlanEnd, w.ActualStart, null, 30, null, null, w.Version));
        Assert.Equal(NotifyKinds.Work, Assert.Single(await NotesAsync("새담당", id)).Kind);
        Assert.Equal(2, (await NotesAsync("작업담당", id)).Count);

        // 작업을 지우면 담당에게 알린다
        using (var s = _db.NewScope())
            await s.Projects.DeleteWorkAsync(w.Id);
        Assert.Contains("작업 삭제: 배선 점검", (await NotesAsync("새담당", id)).Last().Text);
    }

    [Fact]
    public async Task 자동_밀기로_바뀐_작업_담당에게_알리고_한_저장은_사람마다_한_건으로_묶는다()
    {
        var (id, stageId) = await NewProjectAsync("알림 자동 밀기");
        await UserIdAsync("후행담당");
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "선행", "테스터", null, D(1), D(2), null, null, 0, null, null));
            var a = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single(w => w.Title == "선행").Id;
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "후행", "후행담당", null, D(3), D(4), null, null, 0, null, null, Predecessors: new() { a }));
        }
        var before = (await NotesAsync("후행담당", id)).Count;

        // 과제·작업과 관계없는 김물류가 선행을 늦추면 후행이 밀린다
        var pred = await WorkAsync(id, "선행");
        using (var s = _db.NewScope("김물류"))
            await s.Projects.SaveWorkAsync(new WorkInput(pred.Id, stageId, pred.Title, "테스터", null, pred.PlanStart.AddDays(7), pred.PlanEnd.AddDays(7), null, null, 0, null, "자재 지연", pred.Version));

        var pushed = (await NotesAsync("후행담당", id)).Skip(before).ToList();
        var n = Assert.Single(pushed);
        Assert.Equal(NotifyKinds.Work, n.Kind);
        Assert.Contains("[후행] 목표 일정", n.Text);
        Assert.Contains("선행 작업 연동", n.Text);

        // 과제 담당(테스터)은 내 작업(선행)과 내 과제(후행 밀림) 두 활동을 한 건으로: 가장 직접적인 이유(내 작업)와 '외 1건'
        var owner = Assert.Single(await NotesAsync("테스터", id));
        Assert.Equal(NotifyKinds.Work, owner.Kind);
        Assert.Contains("[선행] 목표 일정", owner.Text);
        Assert.EndsWith("외 1건", owner.Text);
        Assert.Empty(await NotesAsync("김물류", id));
    }

    [Fact]
    public async Task 관심_과제에_남이_활동하면_알리고_빼면_알리지_않는다()
    {
        var (id, _) = await NewProjectAsync("알림 관심 과제");
        using (var s = _db.NewScope("관심자"))
        {
            Assert.True(await s.Projects.WatchAsync(id, true));
            Assert.True(await s.Projects.WatchAsync(id, true));       // 두 번 눌러도 그대로
            Assert.Contains(id, await s.Projects.WatchedIdsAsync());
        }
        using (var s = _db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "현장 확인했습니다"));

        var n = Assert.Single(await NotesAsync("관심자", id));
        Assert.Equal(NotifyKinds.Watch, n.Kind);
        Assert.Equal("댓글: 현장 확인했습니다", n.Text);
        // 과제 담당도 받는다
        Assert.Equal(NotifyKinds.Project, Assert.Single(await NotesAsync("테스터", id)).Kind);

        using (var s = _db.NewScope("관심자"))
        {
            Assert.False(await s.Projects.WatchAsync(id, false));
            Assert.DoesNotContain(id, await s.Projects.WatchedIdsAsync());
        }
        using (var s = _db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "두 번째"));
        Assert.Single(await NotesAsync("관심자", id));

        // 관심 과제·방문은 개인 설정이라 과제 활동을 남기지 않는다
        using (var s = _db.NewScope())
            Assert.Equal(3, await s.Db.Activities.CountAsync(a => a.ProjectId == id));   // 등록·댓글 2

        await Assert.ThrowsAsync<RuleException>(async () =>
        {
            using var s = _db.NewScope("관심자");
            await s.Projects.WatchAsync(999999, true);
        });
    }

    [Fact]
    public async Task 사용_중지된_사람에게는_알리지_않는다()
    {
        var (id, _) = await NewProjectAsync("알림 사용 중지");
        using (var s = _db.NewScope("떠난관심자")) await s.Projects.WatchAsync(id, true);
        using (var s = _db.NewScope())
        {
            var u = await s.Db.Users.SingleAsync(x => x.Name == "떠난관심자");
            u.IsActive = false;
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "공지"));
        Assert.Empty(await NotesAsync("떠난관심자", id));
    }

    [Fact]
    public async Task 멘션은_고른_사람에게_알리고_칩으로_보이며_없는_사람과_사용_중지된_사람은_거절한다()
    {
        var (id, _) = await NewProjectAsync("알림 멘션");
        var target = await UserIdAsync("멘션대상");
        using (var s = _db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "@멘션대상 검토 부탁", new() { target, target }));

        var n = Assert.Single(await NotesAsync("멘션대상", id));
        Assert.Equal(NotifyKinds.Mention, n.Kind);
        Assert.Equal(await CommentIdAsync(id, "@멘션대상 검토 부탁"), n.ActivityId);
        using (var s = _db.NewScope())
        {
            var c = (await s.Projects.ActivitiesAsync(id))!.Items.Single(a => a.Type == "comment");
            Assert.Equal(new MentionView(target, "멘션대상"), Assert.Single(c.Mentions!));
            Assert.Equal("comment", c.Kind);
            Assert.Equal(1, await s.Db.ActivityMentions.CountAsync(m => m.ActivityId == c.Id));
        }

        var count = 0;
        using (var s = _db.NewScope()) count = await s.Db.Activities.CountAsync(a => a.ProjectId == id);
        using (var s = _db.NewScope("김물류"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, "x", new() { 999999 })));
            Assert.Equal("mentions", ex.Field);
        }
        using (var s = _db.NewScope())
        {
            s.Db.Users.Add(new AppUser { Name = "퇴사자", IsActive = false, CreatedAt = _db.Clock.UtcNow });
            await s.Db.SaveChangesAsync();
        }
        var gone = await UserIdAsync("퇴사자");
        using (var s = _db.NewScope("김물류"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, "x", new() { gone })));
            Assert.Contains("사용 중지", ex.Message);
        }
        using (var s = _db.NewScope()) Assert.Equal(count, await s.Db.Activities.CountAsync(a => a.ProjectId == id));
    }

    [Fact]
    public async Task 댓글_수정은_작성자만_시간_안에_원문을_남기고_새로_고른_사람에게_한_번만_알린다()
    {
        var (id, _) = await NewProjectAsync("댓글 수정");
        using (var s = _db.NewScope("김물류"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "처음 글"));
        var commentId = await CommentIdAsync(id, "처음 글");
        var target = await UserIdAsync("수정멘션");

        using (var s = _db.NewScope("김물류"))
            await s.Projects.EditCommentAsync(new CommentEditInput(commentId, "고친 글", new() { target }));
        using (var s = _db.NewScope("김물류"))
            await s.Projects.EditCommentAsync(new CommentEditInput(commentId, "다시 고친 글", new()));
        // 뺐다가 다시 골라도 같은 댓글로 같은 사람에게 두 번 알리지 않는다 (알림 열쇠 a{댓글}:u{사람})
        using (var s = _db.NewScope("김물류"))
            await s.Projects.EditCommentAsync(new CommentEditInput(commentId, "다시 고친 글", new() { target }));
        // 바뀐 것이 없으면 기록하지 않는다
        using (var s = _db.NewScope("김물류"))
            await s.Projects.EditCommentAsync(new CommentEditInput(commentId, "다시 고친 글", new() { target }));

        using (var s = _db.NewScope())
        {
            var c = await s.Db.Activities.SingleAsync(a => a.Id == commentId);
            Assert.Equal("다시 고친 글", c.Text);
            Assert.Equal("처음 글", c.OldValue);           // 원문은 처음 것 그대로
            Assert.NotNull(c.EditedAt);
            var changes = await s.Db.Activities.Where(a => a.ProjectId == id && a.Type == ActivityType.CommentChanged).ToListAsync();
            Assert.Equal(3, changes.Count);
            Assert.All(changes, x => Assert.Equal(commentId.ToString(), x.NewValue));
            var view = (await s.Projects.ActivitiesAsync(id))!.Items;
            var shown = view.Single(a => a.Id == commentId);
            Assert.NotNull(shown.EditedAt);
            Assert.False(shown.CanEdit);                    // 테스터는 작성자가 아니다
            Assert.Equal(commentId, view.First(a => a.Type == "commentChanged").Ref);
            Assert.Equal("comment", view.First(a => a.Type == "commentChanged").Kind);
        }
        using (var s = _db.NewScope("김물류"))
            Assert.True((await s.Projects.ActivitiesAsync(id))!.Items.Single(a => a.Id == commentId).CanEdit);

        var m = Assert.Single(await NotesAsync("수정멘션", id));
        Assert.Equal(NotifyKinds.Mention, m.Kind);
        Assert.Equal(commentId, m.ActivityId);
        Assert.Equal($"a{commentId}:u{target}", m.Key);
        Assert.Equal("댓글: 고친 글", m.Text);
        // 고친 기록은 과제 담당·관심자에게 알리지 않는다
        Assert.Single(await NotesAsync("테스터", id));      // 처음 댓글만

        using (var s = _db.NewScope("이자동"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.EditCommentAsync(new CommentEditInput(commentId, "가로채기")));
            Assert.Contains("남긴 사람만", ex.Message);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.DeleteCommentAsync(commentId));
            Assert.Contains("남긴 사람만", ex.Message);
        }

        // 시간이 지나면 작성자도 고치거나 지울 수 없다 (Comments:EditHours, 기본 24시간)
        var saved = _db.Clock.UtcNow;
        try
        {
            _db.Clock.UtcNow = saved.AddHours(24).AddMinutes(1);
            using var s = _db.NewScope("김물류");
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.EditCommentAsync(new CommentEditInput(commentId, "늦게 고침")));
            Assert.Contains("24시간", ex.Message);
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.DeleteCommentAsync(commentId));
            Assert.False((await s.Projects.ActivitiesAsync(id))!.Items.Single(a => a.Id == commentId).CanEdit);
            // 설정한 시간을 따른다
            var longer = new ProjectService(s.Db, _db.Clock, s.Me, s.Users, comments: new CommentOptions(48));
            await longer.EditCommentAsync(new CommentEditInput(commentId, "설정 안에서 고침"));
        }
        finally { _db.Clock.UtcNow = saved; }

        // 댓글이 아닌 활동은 고칠 수 없다
        using (var s = _db.NewScope())
        {
            var created = await s.Db.Activities.Where(a => a.ProjectId == id && a.Type == ActivityType.Created).Select(a => a.Id).SingleAsync();
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.EditCommentAsync(new CommentEditInput(created, "x")));
            Assert.Contains("댓글만", ex.Message);
        }
    }

    [Fact]
    public async Task 댓글을_지우면_본문을_자리_표시로_바꾸고_행과_기록은_남는다()
    {
        var (id, _) = await NewProjectAsync("댓글 삭제");
        var target = await UserIdAsync("삭제멘션");
        using (var s = _db.NewScope("이자동"))
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "잘못 쓴 글", new() { target }));
        var commentId = await CommentIdAsync(id, "잘못 쓴 글");
        using (var s = _db.NewScope("이자동"))
            await s.Projects.EditCommentAsync(new CommentEditInput(commentId, "잘못 쓴 글 고침", new() { target }));
        using (var s = _db.NewScope("이자동"))
            await s.Projects.DeleteCommentAsync(commentId);

        using (var s = _db.NewScope())
        {
            var c = await s.Db.Activities.SingleAsync(a => a.Id == commentId);
            Assert.Equal(ProjectService.DeletedCommentText, c.Text);
            Assert.Null(c.OldValue);                         // 지운 글은 원문도 남기지 않는다
            Assert.NotNull(c.DeletedAt);
            Assert.False(await s.Db.ActivityMentions.AnyAsync(m => m.ActivityId == commentId));
            var last = await s.Db.Activities.Where(a => a.ProjectId == id).OrderByDescending(a => a.Id).FirstAsync();
            Assert.Equal(ActivityType.CommentChanged, last.Type);
            Assert.Equal("댓글 삭제", last.Text);
            Assert.Equal(await UserIdAsync("이자동"), last.UserId);
            var view = (await s.Projects.ActivitiesAsync(id))!.Items.Single(a => a.Id == commentId);
            Assert.True(view.Deleted);
            Assert.Null(view.Mentions);
        }
        // 그 댓글을 가리키던 알림의 글도 지운다
        Assert.Equal("댓글: " + ProjectService.DeletedCommentText, Assert.Single(await NotesAsync("삭제멘션", id)).Text);

        using (var s = _db.NewScope("이자동"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.EditCommentAsync(new CommentEditInput(commentId, "되살리기")));
            Assert.Contains("이미 지운", ex.Message);
        }
        // 주간 보고의 진행 내용에서는 빠진다
        using (var s = _db.NewScope())
        {
            var weekly = await s.Insights.WeeklyAsync(Today, false);
            Assert.DoesNotContain(weekly.Projects.Where(p => p.Id == id).SelectMany(p => p.Comments), x => x.Text == ProjectService.DeletedCommentText);
        }
    }

    [Fact]
    public async Task 읽지_않은_수와_읽음_처리는_내_알림만_다룬다()
    {
        var (id, _) = await NewProjectAsync("알림 읽음");
        using (var s = _db.NewScope("읽는사람")) await s.Projects.WatchAsync(id, true);
        using (var s = _db.NewScope("김물류")) await s.Projects.CommentAsync(new CommentInput(id, null, null, "하나"));
        using (var s = _db.NewScope("김물류")) await s.Projects.CommentAsync(new CommentInput(id, null, null, "둘"));

        using (var s = _db.NewScope("읽는사람"))
        {
            var notes = new NotificationService(s.Db, _db.Clock, s.Me);
            Assert.Equal(2, await notes.UnreadAsync());
            var page = await notes.ListAsync();
            Assert.Equal(2, page.Unread);
            Assert.Equal(new[] { "댓글: 둘", "댓글: 하나" }, page.Items.Select(n => n.Text).ToArray());
            Assert.All(page.Items, n => Assert.Equal("알림 읽음", n.Project));
            Assert.All(page.Items, n => Assert.Equal("김물류", n.Actor));
            Assert.All(page.Items, n => Assert.Equal("관심 과제", n.KindLabel));

            var first = page.Items[0];
            Assert.NotNull(await notes.ReadAsync(first.Id));
            Assert.Equal(1, await notes.UnreadAsync());
            Assert.Single((await notes.ListAsync(unreadOnly: true)).Items);
            // 한 쪽씩: beforeId 다음부터
            var rest = await notes.ListAsync(beforeId: first.Id, take: 1);
            Assert.Equal("댓글: 하나", Assert.Single(rest.Items).Text);
            Assert.False(rest.More);

            // 남의 알림은 읽음으로 바꾸지 못한다
            using (var other = _db.NewScope("이자동"))
                Assert.Null(await new NotificationService(other.Db, _db.Clock, other.Me).ReadAsync(page.Items[1].Id));
            Assert.Equal(1, await notes.UnreadAsync());

            Assert.Equal(1, await notes.ReadAllAsync());
            Assert.Equal(0, await notes.UnreadAsync());
            Assert.Equal(0, await notes.ReadAllAsync());
        }
    }

    [Fact]
    public async Task 첨부_활동도_같은_경로로_알린다()
    {
        var (id, _) = await NewProjectAsync("첨부 알림");
        using (var s = _db.NewScope("김물류"))
            await Files(s).SaveAsync(id, null, "성적서.pdf", 3, new MemoryStream(new byte[] { 1, 2, 3 }));
        var n = Assert.Single(await NotesAsync("테스터", id));
        Assert.Equal(NotifyKinds.Project, n.Kind);
        Assert.Equal("첨부 추가: 성적서.pdf", n.Text);
    }

    [Fact]
    public async Task 새_과제를_남이_등록해_주면_담당에게_알린다()
    {
        await UserIdAsync("등록받은사람");
        int id;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            id = await s.Projects.CreateAsync(new ProjectInput("남이 등록한 과제", cat.Id, corp.Id, "등록받은사람", D(-1), null, null, null, null));
        }
        var n = Assert.Single(await NotesAsync("등록받은사람", id));
        Assert.Equal(NotifyKinds.Project, n.Kind);
        Assert.Equal("과제 등록", n.Text);
        Assert.NotNull(n.ActivityId);
    }

    [Fact]
    public async Task 상세를_열면_방문_시각이_바뀌고_그_뒤에_남이_남긴_활동만_새_활동으로_센다()
    {
        var (id, _) = await NewProjectAsync("새 활동 수");
        var saved = _db.Clock.UtcNow;
        try
        {
            using (var s = _db.NewScope("방문자"))
            {
                // 처음 보는 과제: 최근 7일의 남의 활동(등록)이 새 활동
                Assert.Equal(1, (await s.Projects.NewCountsAsync()).GetValueOrDefault(id));
                var opened = (await s.Projects.OpenAsync(id))!.Value;
                Assert.Equal(saved.AddDays(-Rules.NewActivityDays), opened.Since);
                Assert.True(opened.View.Activities!.Single().New);
                Assert.False(opened.Watched);
                Assert.Equal(0, (await s.Projects.NewCountsAsync()).GetValueOrDefault(id));
                Assert.Null(await s.Projects.OpenAsync(999999));
            }

            _db.Clock.UtcNow = saved.AddHours(1);
            using (var s = _db.NewScope("김물류"))
            {
                await s.Projects.CommentAsync(new CommentInput(id, null, null, "새 글 1"));
                await s.Projects.CommentAsync(new CommentInput(id, null, null, "새 글 2"));
            }
            using (var s = _db.NewScope("방문자"))
                await s.Projects.CommentAsync(new CommentInput(id, null, null, "내 글"));       // 내 것은 세지 않는다
            using (var s = _db.NewScope("방문자"))
                Assert.Equal(2, (await s.Projects.NewCountsAsync())[id]);

            _db.Clock.UtcNow = saved.AddHours(2);
            using (var s = _db.NewScope("방문자"))
            {
                var opened = (await s.Projects.OpenAsync(id))!.Value;
                Assert.Equal(saved, opened.Since);           // 지난번에 연 시각
                var acts = opened.View.Activities!;
                Assert.Equal(new[] { "새 글 2", "새 글 1" }, acts.Where(a => a.New).Select(a => a.Text).ToArray());
                Assert.True(acts.Single(a => a.Text == "내 글").Mine);
                Assert.False(acts.Single(a => a.Text == "내 글").New);
                Assert.Equal(0, (await s.Projects.NewCountsAsync()).GetValueOrDefault(id));
                // 화면이 '더 보기'에 처음 기준을 함께 보내면 다음 쪽도 같은 기준으로 표시한다
                var page = await s.Projects.ActivitiesAsync(id, take: 1, since: opened.Since);
                Assert.True(page!.Items.Single().New || page.Items.Single().Mine);
            }
            using (var s = _db.NewScope())
                Assert.Equal(1, await s.Db.ProjectVisits.CountAsync(v => v.ProjectId == id));     // 열 때마다 새 행이 아니라 갱신
        }
        finally { _db.Clock.UtcNow = saved; }
    }

    [Fact]
    public async Task 새_소식은_내_작업_내_과제_관심_과제_멘션에_남이_남긴_활동이고_보면_사라진다()
    {
        var me = await UserIdAsync("소식받는사람");
        // 내 작업: 테스터 과제의 '소식 후행'(소식받는사람 담당)이 선행 지연으로 밀린다
        var (workProject, stageId) = await NewProjectAsync("소식 내 작업");
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "소식 선행", "테스터", null, D(1), D(2), null, null, 0, null, null));
            var a = (await s.Projects.ViewAsync(workProject))!.Stages[0].Works.Single(w => w.Title == "소식 선행").Id;
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "소식 후행", "소식받는사람", null, D(3), D(4), null, null, 0, null, null, Predecessors: new() { a }));
        }
        // 관심 과제와 멘션, 그리고 관계없는 과제
        var (watchProject, _) = await NewProjectAsync("소식 관심 과제");
        var (mentionProject, _) = await NewProjectAsync("소식 멘션");
        var (otherProject, _) = await NewProjectAsync("소식 관계없음");
        using (var s = _db.NewScope("소식받는사람")) await s.Projects.WatchAsync(watchProject, true);
        // 내가 연 뒤에 생긴 것만 보이도록 모두 한 번 열어 둔다
        using (var s = _db.NewScope("소식받는사람"))
            foreach (var p in new[] { workProject, watchProject, mentionProject, otherProject }) await s.Projects.OpenAsync(p);

        var saved = _db.Clock.UtcNow;
        try
        {
            _db.Clock.UtcNow = saved.AddMinutes(5);
            var pred = await WorkAsync(workProject, "소식 선행");
            using (var s = _db.NewScope("김물류"))
            {
                await s.Projects.SaveWorkAsync(new WorkInput(pred.Id, stageId, pred.Title, "테스터", null, pred.PlanStart.AddDays(7), pred.PlanEnd.AddDays(7), null, null, 0, null, "설계 변경", pred.Version));
                await s.Projects.CommentAsync(new CommentInput(watchProject, null, null, "관심 과제 소식"));
                await s.Projects.CommentAsync(new CommentInput(mentionProject, null, null, "확인 부탁", new() { me }));
                await s.Projects.CommentAsync(new CommentInput(otherProject, null, null, "관계없는 소식"));
            }
            using (var s = _db.NewScope("소식받는사람"))
                await s.Projects.CommentAsync(new CommentInput(watchProject, null, null, "내가 쓴 글"));

            using (var s = _db.NewScope("소식받는사람"))
            {
                var news = await s.Projects.NewsAsync();
                Assert.DoesNotContain(news, n => n.ProjectId == otherProject);
                Assert.DoesNotContain(news, n => n.Text == "내가 쓴 글");
                var push = Assert.Single(news, n => n.ProjectId == workProject);
                Assert.Equal(NotifyKinds.Work, push.Why);
                Assert.Contains("[소식 후행] 목표 일정", push.Text);
                Assert.Equal(NotifyKinds.Watch, Assert.Single(news, n => n.ProjectId == watchProject).Why);
                var mention = Assert.Single(news, n => n.ProjectId == mentionProject);
                Assert.Equal(NotifyKinds.Mention, mention.Why);
                Assert.Equal("멘션", mention.WhyLabel);
                Assert.Equal("comment", mention.Type);

                // 그 과제를 열어 보면 사라진다
                _db.Clock.UtcNow = saved.AddMinutes(6);
                await s.Projects.OpenAsync(watchProject);
                Assert.DoesNotContain(await s.Projects.NewsAsync(), n => n.ProjectId == watchProject);

                var watched = await s.Projects.WatchedProjectsAsync();
                var w = Assert.Single(watched);
                Assert.Equal(("소식 관심 과제", 0), (w.Title, w.NewCount));
            }
        }
        finally { _db.Clock.UtcNow = saved; }
    }

    [Fact]
    public async Task 활동_조건은_종류_단계_검색으로_거르고_조건대로_나눠_받는다()
    {
        var (id, stageId) = await NewProjectAsync("활동 조건");
        using (var s = _db.NewScope())
        {
            await s.Projects.CommentAsync(new CommentInput(id, stageId, null, "검수 일정 공유"));
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "자재 입고 확인"));
            await s.Projects.CommentAsync(new CommentInput(id, null, null, "검수 결과 양호"));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "검수 준비", null, null, D(1), D(2), null, null, 0, null, null));
        }
        using (var s = _db.NewScope())
        {
            var comments = (await s.Projects.ActivitiesAsync(id, filter: new ActivityFilter("comment", null, null)))!.Items;
            Assert.Equal(3, comments.Count);
            Assert.All(comments, a => Assert.Equal("comment", a.Type));

            var stage = (await s.Projects.ActivitiesAsync(id, filter: new ActivityFilter(null, stageId, null)))!.Items;
            Assert.Equal(new[] { "[검토] 작업 추가: 검수 준비", "검수 일정 공유" }, stage.Select(a => a.Text).ToArray());

            var found = (await s.Projects.ActivitiesAsync(id, filter: new ActivityFilter("comment", null, "검수")))!.Items;
            Assert.Equal(new[] { "검수 결과 양호", "검수 일정 공유" }, found.Select(a => a.Text).ToArray());
            // 사람 이름으로도 찾는다
            Assert.Equal(5, (await s.Projects.ActivitiesAsync(id, filter: new ActivityFilter(null, null, "테스터")))!.Items.Count);

            // 조건을 건 채로 나눠 받기
            var first = (await s.Projects.ActivitiesAsync(id, take: 1, filter: new ActivityFilter("comment", null, "검수")))!;
            Assert.True(first.More);
            var next = (await s.Projects.ActivitiesAsync(id, beforeId: first.Items[0].Id, take: 1, filter: new ActivityFilter("comment", null, "검수")))!;
            Assert.Equal("검수 일정 공유", Assert.Single(next.Items).Text);
            Assert.False(next.More);

            // 모르는 종류는 조건으로 쓰지 않는다
            Assert.Equal(5, (await s.Projects.ActivitiesAsync(id, filter: new ActivityFilter("nope", null, null)))!.Items.Count);
        }
    }

    private AttachmentService Files(TestDb.Scope s)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "gantt-collab-" + Guid.NewGuid().ToString("N")),
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
