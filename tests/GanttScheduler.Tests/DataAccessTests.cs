using System.Text.Json;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 성능 회귀 시험용 대형 과제: 작업 200개·활동 3,000건(같은 시각에 남은 활동 묶음 포함)·의존 관계·첨부 1건.
// 서비스를 거치지 않고 DB 에 바로 넣는다(빠르게 만들기 위해서 — 계산 규칙을 시험하는 데이터가 아니다)
public static class BigProject
{
    public const int Works = 200;
    public const int Activities = 3000;
    public const string OtherUser = "대형사용자";
    public static readonly ActivityType[] Types = Enum.GetValues<ActivityType>();

    public sealed record Info(int Id, int FirstWorkId, int FirstWorkActivities, int OtherUserActivities, int TargetChanges);

    public static async Task<Info> CreateAsync(TestDb db, string title = "대형 과제")
    {
        using var ctx = db.Open();
        var cat = await ctx.Categories.OrderBy(c => c.Id).FirstAsync();
        var corp = await ctx.Locations.OrderBy(l => l.Id).FirstAsync(l => l.Level == 1);
        var me = await ctx.Users.FirstAsync(u => u.Id == db.Me.Id);
        var other = await ctx.Users.FirstOrDefaultAsync(u => u.Name == OtherUser)
                    ?? ctx.Users.Add(new AppUser { Name = OtherUser, CreatedAt = db.Clock.UtcNow }).Entity;
        var start = db.Clock.Today.AddDays(-200);
        var p = new Project
        {
            Title = title, CategoryId = cat.Id, CorporationId = corp.Id, LocationId = corp.Id, Owner = me, State = ProjectState.Active,
            StartDate = start, Description = new string('설', 500), CreatedAt = db.Clock.UtcNow, CreatedById = me.Id, UpdatedAt = db.Clock.UtcNow,
        };
        for (var si = 0; si < 5; si++)
        {
            var stage = new Stage { Name = $"단계{si + 1}", SortOrder = si + 1, TargetDate = start.AddDays(60 * (si + 1)) };
            stage.Checks.Add(new CheckItem { Text = $"확인 {si + 1}-1", SortOrder = 1 });
            stage.Checks.Add(new CheckItem { Text = $"확인 {si + 1}-2", SortOrder = 2 });
            for (var wi = 0; wi < Works / 5; wi++)
            {
                var n = si * (Works / 5) + wi;
                stage.Works.Add(new WorkItem
                {
                    Title = $"작업 {n:000} 설비 배치 검토", Owner = n % 2 == 0 ? me : other, PlanStart = start.AddDays(n), PlanEnd = start.AddDays(n + 3),
                    Progress = n % 3 == 0 ? 50 : 0, ActualStart = n % 3 == 0 ? start.AddDays(n) : null, Note = n % 5 == 0 ? "메모 " + n : null,
                    CreatedAt = db.Clock.UtcNow,
                });
            }
            p.Stages.Add(stage);
        }
        ctx.Projects.Add(p);
        await ctx.SaveChangesAsync();

        var works = p.Stages.OrderBy(s => s.SortOrder).SelectMany(s => s.Works.OrderBy(w => w.PlanStart)).ToList();
        // 같은 단계 안에서 앞 작업 → 다음 작업 의존 (단계마다 10개)
        foreach (var s in p.Stages)
        {
            var list = s.Works.OrderBy(w => w.PlanStart).Take(11).ToList();
            for (var i = 0; i + 1 < list.Count; i++)
                ctx.WorkLinks.Add(new WorkLink { ProjectId = p.Id, PredecessorId = list[i].Id, SuccessorId = list[i + 1].Id });
        }
        ctx.Attachments.Add(new Attachment
        {
            ProjectId = p.Id, FileName = "도면.pdf", StoredName = "none.pdf", ContentType = "application/pdf", Size = 100,
            UploadedById = me.Id, UploadedAt = db.Clock.UtcNow,
        });

        // 활동: 세 건씩 같은 시각(한 번에 남긴 변경 묶음처럼) — 쪽 경계가 같은 시각 묶음을 가르는지 시험한다
        var baseAt = db.Clock.UtcNow.AddMinutes(-Activities);
        int firstWork = 0, otherCount = 0, targets = 0;
        for (var i = 0; i < Activities; i++)
        {
            var type = Types[i % Types.Length];
            int? workId = i % 25 == 0 ? works[0].Id : i % 4 == 0 ? works[i % Works].Id : null;
            if (workId == works[0].Id) firstWork++;
            var byOther = i % 3 == 1;
            if (byOther) otherCount++;
            if (type == ActivityType.TargetChanged) targets++;
            ctx.Activities.Add(new Activity
            {
                ProjectId = p.Id, Type = type, Text = $"활동 {i} 내용 AGV-{i % 7}", Reason = i % 10 == 0 ? "사유 " + i : null,
                WorkItemId = workId, UserId = byOther ? other.Id : me.Id, At = baseAt.AddMinutes(i / 3),
            });
        }
        await ctx.SaveChangesAsync();
        return new Info(p.Id, works[0].Id, firstWork, otherCount, targets);
    }
}

// 데이터 접근 방식(나눠 읽기·DB 에서 거르기·변경 경로의 부분 로딩) 회귀 시험. 가짜 EF(섀도 빌드)의 Include 는 아무 일도 하지 않아
// Include 를 빠뜨린 실수가 섀도에서는 보이지 않는다 — 변경 경로마다 결과를 진짜 DB 로 확인한다
public class DataAccessTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;

    public DataAccessTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string category = "물류 자동화")
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "멕시코");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Today.AddDays(-10), null, null, null, null));
    }

    private async Task<List<int>> ExpectedOrderAsync(int projectId, int? workId = null)
    {
        using var s = _db.NewScope();
        var q = s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (workId != null) q = q.Where(a => a.WorkItemId == workId);
        return (await q.Select(a => new { a.Id, a.At }).ToListAsync())
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Select(a => a.Id).ToList();
    }

    [Fact]
    public async Task 상세_뷰는_최근_활동만_싣고_전체_건수와_목표일_변경_횟수는_따로_센다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 뷰");
        var expected = await ExpectedOrderAsync(big.Id);

        using var s = _db.NewScope();
        var view = (await s.Projects.ViewAsync(big.Id))!;
        Assert.Equal(ProjectService.ActivityPageSize, view.Activities!.Count);
        Assert.Equal(expected.Take(ProjectService.ActivityPageSize), view.Activities.Select(a => a.Id));
        Assert.Equal(BigProject.Activities, view.ActivityTotal);
        Assert.Equal(big.TargetChanges, view.TargetChanges);
        Assert.Equal(BigProject.Works, view.WorkCount);
        Assert.Equal(50, view.Links.Count);
        Assert.Single(view.Attachments!);
        Assert.All(view.Activities, a => Assert.False(string.IsNullOrEmpty(a.User)));

        // Excel 은 전체 활동
        var all = (await s.Projects.ViewAsync(big.Id, activityLimit: null))!;
        Assert.Equal(expected, all.Activities!.Select(a => a.Id));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(37)]     // 같은 시각 묶음(3건)을 쪽 경계가 가르도록
    public async Task 활동_더_보기는_쪽_경계에서_빠지거나_겹치지_않는다(int take)
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 더 보기 " + take);
        var expected = await ExpectedOrderAsync(big.Id);

        using var s = _db.NewScope();
        var seen = new List<int>();
        int? before = null;
        var pages = 0;
        while (true)
        {
            var page = (await s.Projects.ActivitiesAsync(big.Id, before, null, take))!;
            pages++;
            Assert.True(page.Items.Count <= take);
            seen.AddRange(page.Items.Select(a => a.Id));
            if (!page.More) break;
            Assert.Equal(take, page.Items.Count);
            before = page.Items[^1].Id;
            Assert.True(pages < 1000);
        }
        Assert.Equal(expected, seen);
        Assert.Equal((int)Math.Ceiling(BigProject.Activities / (double)take), pages);
    }

    [Fact]
    public async Task 작업_시트의_진행_내용은_그_작업의_활동만_나눠_받는다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 작업 활동");
        var expected = await ExpectedOrderAsync(big.Id, big.FirstWorkId);
        Assert.Equal(big.FirstWorkActivities, expected.Count);
        Assert.True(expected.Count > ProjectService.ActivityPageSize);

        using var s = _db.NewScope();
        var first = (await s.Projects.ActivitiesAsync(big.Id, null, big.FirstWorkId))!;
        Assert.True(first.More);
        Assert.All(first.Items, a => Assert.Equal(big.FirstWorkId, a.WorkId));
        var rest = new List<int>(first.Items.Select(a => a.Id));
        var page = first;
        while (page.More)
        {
            page = (await s.Projects.ActivitiesAsync(big.Id, page.Items[^1].Id, big.FirstWorkId))!;
            rest.AddRange(page.Items.Select(a => a.Id));
        }
        Assert.Equal(expected, rest);

        // 없는 과제는 null(화면은 404), 다른 과제의 활동을 커서로 주면 빈 쪽
        Assert.Null(await s.Projects.ActivitiesAsync(987654));
        var otherId = await NewProjectAsync("테스트 다른 과제 활동");
        var otherAct = await s.Db.Activities.Where(a => a.ProjectId == otherId).Select(a => a.Id).FirstAsync();
        var empty = (await s.Projects.ActivitiesAsync(big.Id, otherAct))!;
        Assert.Empty(empty.Items);
        Assert.False(empty.More);
        // 한 번에 받는 수는 상한이 있다
        Assert.Equal(ProjectService.ActivityPageMax, (await s.Projects.ActivitiesAsync(big.Id, null, null, 100_000))!.Items.Count);
    }

    // ── 변경 경로: 그래프 일부만 읽어도 결과가 같은지 ──────────────────────

    [Fact]
    public async Task 댓글과_체크는_읽지_않은_활동_컬렉션에_더해도_저장된다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 댓글");
        using (var s = _db.NewScope())
        {
            // 변경 시각이 갱신되는지 보려고 하루 전으로 돌려 둔다
            var row = await s.Db.Projects.FirstAsync(p => p.Id == big.Id);
            row.UpdatedAt = _db.Clock.UtcNow.AddDays(-1);
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope())
            await s.Projects.CommentAsync(new CommentInput(big.Id, null, big.FirstWorkId, "부분 로딩 댓글"));
        int checkId;
        using (var s = _db.NewScope())
        {
            checkId = await s.Db.CheckItems.Where(c => c.Stage!.ProjectId == big.Id).OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            await s.Projects.ToggleCheckAsync(checkId, true);
        }
        using (var s = _db.NewScope())
        {
            Assert.Equal(BigProject.Activities + 2, await s.Db.Activities.CountAsync(a => a.ProjectId == big.Id));
            var comment = await s.Db.Activities.SingleAsync(a => a.ProjectId == big.Id && a.Text == "부분 로딩 댓글");
            Assert.Equal(big.FirstWorkId, comment.WorkItemId);
            Assert.Equal(_db.Clock.UtcNow, (await s.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == big.Id)).UpdatedAt);
            var check = await s.Db.CheckItems.FirstAsync(c => c.Id == checkId);
            Assert.True(check.IsDone);
            Assert.Equal(_db.Me.Id, check.DoneById);

            // 응답 뷰(추적하지 않는 새 조회)에 방금 남긴 활동이 맨 위에 있다
            var view = (await s.Projects.ViewAsync(big.Id))!;
            Assert.Contains("체크", view.Activities![0].Text);
            Assert.Equal(BigProject.Activities + 2, view.ActivityTotal);
        }
        await Assert.ThrowsAsync<RuleException>(async () =>
        {
            using var s = _db.NewScope();
            await s.Projects.CommentAsync(new CommentInput(987654, null, null, "없는 과제"));
        });
    }

    [Fact]
    public async Task 과제_정보_수정은_내역만_읽어_바꾸고_같은_내역이면_변경으로_치지_않는다()
    {
        var id = await NewProjectAsync("테스트 내역 부분 로딩", "용기");
        int box, cat, loc;
        using (var s = _db.NewScope())
        {
            box = (await s.Db.ContainerTypes.OrderBy(c => c.Id).FirstAsync()).Id;
            var p = (await s.Projects.LoadInfoAsync(id))!;
            cat = p.CategoryId;
            loc = p.LocationId;
            await s.Projects.UpdateAsync(id, new ProjectInput("테스트 내역 부분 로딩", cat, loc, "테스터", p.StartDate, null, null,
                null, new List<ItemInput> { new(box, 3, "앞") }, p.Version));
        }
        int activities;
        using (var s = _db.NewScope())
        {
            var p = (await s.Projects.LoadInfoAsync(id))!;
            Assert.Equal(3, Assert.Single(p.Containers).Quantity);
            Assert.Equal(1, p.Version);
            activities = await s.Db.Activities.CountAsync(a => a.ProjectId == id);
            // 같은 내역으로 다시 저장 — 내역을 읽지 않았다면 기존 행이 남은 채 새 행이 더해진다
            await s.Projects.UpdateAsync(id, new ProjectInput("테스트 내역 부분 로딩", cat, loc, "테스터", p.StartDate, null, null,
                null, new List<ItemInput> { new(box, 3, "앞") }, p.Version));
        }
        using (var s = _db.NewScope())
        {
            var p = (await s.Projects.LoadInfoAsync(id))!;
            Assert.Equal(3, Assert.Single(p.Containers).Quantity);
            Assert.Equal(activities, await s.Db.Activities.CountAsync(a => a.ProjectId == id));
            Assert.Equal(2, p.Version);
        }
    }

    [Fact]
    public async Task 복제는_체크리스트_의존_관계_내역까지_복사한다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 복제");
        int copy;
        using (var s = _db.NewScope())
            copy = await s.Projects.CloneAsync(new CloneInput(big.Id, "대형 과제 복제본", Today, IncludeWorks: true));
        using var c = _db.NewScope();
        var view = (await c.Projects.ViewAsync(copy))!;
        Assert.Equal(BigProject.Works, view.WorkCount);
        Assert.Equal(50, view.Links.Count);
        Assert.All(view.Stages, st => Assert.Equal(2, st.CheckTotal));
        Assert.Equal(1, view.ActivityTotal);                 // 복제 기록 하나 (원본 활동은 복사하지 않는다)
        Assert.Empty(view.Attachments!);
    }

    [Fact]
    public async Task 과제_삭제는_그래프를_읽지_않고_DB_cascade_로_모두_지운다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 삭제");
        using (var s = _db.NewScope())
        {
            await s.Projects.DeleteAsync(big.Id);
            await s.Projects.PurgeAsync(big.Id);
        }
        using var c = _db.NewScope();
        Assert.False(await c.Db.Projects.AnyAsync(p => p.Id == big.Id));
        Assert.False(await c.Db.Stages.AnyAsync(x => x.ProjectId == big.Id));
        Assert.False(await c.Db.WorkItems.AnyAsync(x => x.Stage!.ProjectId == big.Id || x.Id == big.FirstWorkId));
        Assert.False(await c.Db.CheckItems.AnyAsync(x => x.Stage!.ProjectId == big.Id));
        Assert.False(await c.Db.WorkLinks.AnyAsync(x => x.ProjectId == big.Id));
        Assert.False(await c.Db.Attachments.AnyAsync(x => x.ProjectId == big.Id));
        Assert.False(await c.Db.Activities.AnyAsync(x => x.ProjectId == big.Id));
        // 지울 권한이 없으면 거절 (과제 행만 읽어도 권한 판정이 같다)
        var id = await NewProjectAsync("테스트 삭제 권한");
        using var other = _db.NewScope("삭제권한없음");
        await Assert.ThrowsAsync<RuleException>(() => other.Projects.DeleteAsync(id));
    }

    // ── 변경 이력 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 변경_이력은_DB_에서_쪽을_나누고_과제_사람_종류로_거른다()
    {
        var big = await BigProject.CreateAsync(_db, "대형 과제 이력");
        var expected = await ExpectedOrderAsync(big.Id);
        using var s = _db.NewScope();

        // 과제 조건 + 기간을 비우면 그 과제의 처음부터
        var first = await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, null, 1, big.Id));
        Assert.Equal(BigProject.Activities, first.Total);
        Assert.Equal((int)Math.Ceiling(BigProject.Activities / (double)InsightService.HistoryPageSize), first.Pages);
        Assert.Equal(expected.Take(InsightService.HistoryPageSize), first.Rows.Select(r => r.Id));
        Assert.Equal("대형 과제 이력", first.Project);
        Assert.Null(first.Notice);
        Assert.Equal(new[] { BigProject.OtherUser, "테스터" }.OrderBy(n => n), first.Users);
        Assert.All(first.Rows, r => Assert.Equal("대형 과제 이력", r.Project));

        // 쪽을 넘겨도 순서가 이어지고, 범위를 넘는 쪽은 마지막 쪽으로
        var seen = new List<int>();
        for (var page = 1; page <= first.Pages; page++)
            seen.AddRange((await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, null, page, big.Id))).Rows.Select(r => r.Id));
        Assert.Equal(expected, seen);
        var beyond = await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, null, 9999, big.Id));
        Assert.Equal(first.Pages, beyond.Page);

        // 사람
        var byOther = await s.Insights.HistoryAsync(new HistoryFilter(null, null, BigProject.OtherUser, null, null, 1, big.Id));
        Assert.Equal(big.OtherUserActivities, byOther.Total);
        Assert.All(byOther.Rows, r => Assert.Equal(BigProject.OtherUser, r.User));
        Assert.Equal(0, (await s.Insights.HistoryAsync(new HistoryFilter(null, null, "없는사람", null, null, 1, big.Id))).Total);

        // 종류: KindOf 의 기본 분기('구성 변경')에 떨어지는 유형까지 DB 조건으로 같게 고른다
        foreach (var (kind, _) in Reporting.Kinds)
        {
            var want = Enumerable.Range(0, BigProject.Activities).Count(i => Reporting.KindOf(BigProject.Types[i % BigProject.Types.Length]) == kind);
            var got = await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, kind, null, 1, big.Id));
            Assert.Equal(want, got.Total);
            Assert.All(got.Rows, r => Assert.Equal(kind, r.Kind));
        }
        Assert.Equal(0, (await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, "없는종류", null, 1, big.Id))).Total);

        // 글자 검색은 대소문자를 가리지 않는다(메모리에서) — 쪽 나누기도 검색 결과 기준
        var found = await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, "agv-3", 2, big.Id));
        var wantFound = Enumerable.Range(0, BigProject.Activities).Count(i => i % 7 == 3);
        Assert.Equal(wantFound, found.Total);
        Assert.Equal(2, found.Page);
        Assert.All(found.Rows, r => Assert.Contains("AGV-3", r.Text));
        // 과제명으로도 찾는다(과제 조건 없이, 기간 안에서)
        var byTitle = await s.Insights.HistoryAsync(new HistoryFilter(Today.AddDays(-5), Today, null, null, "대형 과제 이력", 1));
        Assert.Equal(BigProject.Activities, byTitle.Total);
    }

    [Fact]
    public async Task 여러_과제의_이력은_1년까지만_보고_과제_하나는_기간_상한이_없다()
    {
        using var s = _db.NewScope();
        var wide = await s.Insights.HistoryAsync(new HistoryFilter(Today.AddDays(-800), Today, null, null, null, 1));
        Assert.Equal(Today.AddYears(-InsightService.HistoryMaxYears), wide.From);
        Assert.NotNull(wide.Notice);
        // 시작·끝을 거꾸로 넣어도 같은 규칙
        var reversed = await s.Insights.HistoryAsync(new HistoryFilter(Today, Today.AddDays(-800), null, null, null, 1));
        Assert.Equal(Today.AddYears(-InsightService.HistoryMaxYears), reversed.From);

        var id = await NewProjectAsync("테스트 이력 기간");
        var one = await s.Insights.HistoryAsync(new HistoryFilter(Today.AddDays(-800), Today, null, null, null, 1, id));
        Assert.Equal(Today.AddDays(-800), one.From);
        Assert.Null(one.Notice);
        Assert.Equal(1, one.Total);
        var missing = await s.Insights.HistoryAsync(new HistoryFilter(null, null, null, null, null, 1, 987654));
        Assert.Null(missing.Project);
        Assert.Equal(0, missing.Total);
    }

    // ── 집계 범위 줄이기: DB 에서 먼저 거른 결과가 전체를 메모리에서 계산한 것과 같다 ──

    [Fact]
    public async Task 목록_상태_탭과_보고는_DB_에서_거른_과제로도_결과가_같다()
    {
        var doneId = await NewProjectAsync("테스트 완료 후 이번 주 댓글", "Simulation");
        using (var s = _db.NewScope())
        {
            foreach (var st in (await s.Projects.ViewAsync(doneId))!.Stages)
                await s.Projects.SaveStageAsync(new StageInput(doneId, st.Id, st.Name, null, Today.AddDays(-1), null, false, st.Version));
            await s.Projects.CommentAsync(new CommentInput(doneId, null, null, "완료 뒤 남긴 말"));
        }

        using var c = _db.NewScope();
        var all = await c.Projects.AllAsync(withLinks: true);
        var locations = await c.Projects.LocationsAsync();
        var risk = new RiskContext(await c.Projects.CalendarAsync(), await c.Projects.AllCrossLinksAsync(), _db.Clock.UtcNow);
        Assert.Contains(all, p => p.Id == doneId && p.State == ProjectState.Done);

        foreach (var (key, _) in Pages.Projects.IndexModel.States)
        {
            foreach (var mine in new[] { false, true })
            {
                var filter = new ProjectFilter(null, null, null, key, mine);
                var want = Dashboard.Filter(all, locations, Today, filter, _db.Me.Id, risk).Select(v => v.Id).ToList();
                var got = (await c.Dashboard.ListAsync(filter)).Select(v => v.Id).ToList();
                Assert.Equal(want, got);
            }
        }
        var cat = all.First(p => p.Id == doneId).CategoryId;
        var corp = all.First(p => p.Id == doneId).CorporationId;
        var narrowed = new ProjectFilter(null, cat, corp, "all", false);
        Assert.Equal(Dashboard.Filter(all, locations, Today, narrowed, _db.Me.Id, risk).Select(v => v.Id),
            (await c.Dashboard.ListAsync(narrowed)).Select(v => v.Id));

        // 주간 보고: 진행 과제 + 그 주에 움직인 과제만 읽어도 같은 보고
        var (from, to) = Reporting.WeekOf(Today);
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));
        var acts = await c.Db.Activities.AsNoTracking().Include(a => a.User).Where(a => a.At >= start && a.At < end).ToListAsync();
        var wantWeekly = Reporting.Weekly(all, acts, locations, from, to, Today, risk.Calendar);
        var gotWeekly = await c.Insights.WeeklyAsync(null, false);
        // 지연·위험 요약의 최근 사유는 서비스가 덧붙인다(DB 조회) — 순수 계산과 견줄 때는 그 칸을 같게 둔다
        Assert.Equal(JsonSerializer.Serialize(wantWeekly), JsonSerializer.Serialize(gotWeekly with { DelaySummary = wantWeekly.DelaySummary }));
        Assert.Contains(gotWeekly.Projects, p => p.Id == doneId);

        // 내 할 일·리드타임
        Assert.Equal(JsonSerializer.Serialize(Reporting.My(all, _db.Me.Id, Today)), JsonSerializer.Serialize(await c.Insights.MyAsync()));
        var cal = await c.Projects.CalendarAsync();
        var firsts = await c.Insights.FirstTargetsAsync();
        foreach (var excludeZero in new[] { false, true })
            Assert.Equal(JsonSerializer.Serialize(Reporting.LeadTimes(all, cal, firsts, excludeZero, Today)), JsonSerializer.Serialize(await c.Insights.LeadTimesAsync(excludeZero)));
    }

    [Fact]
    public async Task 대시보드_간트는_쓰는_값만_실어_과제당_크기가_크게_줄어든다()
    {
        using var s = _db.NewScope();
        var all = await s.Projects.AllAsync();
        var locations = await s.Projects.LocationsAsync();
        var dash = Dashboard.Build(all, locations, Today, new DashboardQuery(Scope: "all"));
        var full = all.Where(p => p.State == ProjectState.Active)
            .Select(p => Views.Project(p, Today, locations, detail: false, withWorks: false)).ToList();
        Assert.Equal(full.Count, dash.Timeline.Count);

        var slim = AppJson.Serialize(dash.Timeline).Length;
        var before = AppJson.Serialize(full).Length;
        Assert.True(slim * 2 < before, $"간트 JSON {slim}B 가 전체 과제 JSON {before}B 의 절반보다 크다");

        // 간트가 그리는 값은 그대로다
        var view = full.First(v => v.Stages.Count > 0);
        var row = dash.Timeline.Single(t => t.Id == view.Id);
        Assert.Equal((view.Title, view.Corporation, view.CurrentStage, view.DelayDays, view.Progress),
            (row.Title, row.Corporation, row.CurrentStage, row.DelayDays, row.Progress));
        Assert.Equal(view.Stages.Select(x => (x.Name, x.Status, x.PlanFrom, x.TargetDate, x.ActualFrom, x.ActualTo, x.CompletedDate, x.LateDays, x.DoneLate)),
            row.Stages.Select(x => (x.Name, x.Status, x.PlanFrom, x.TargetDate, x.ActualFrom, x.ActualTo, x.CompletedDate, x.LateDays, x.DoneLate)));
    }
}
