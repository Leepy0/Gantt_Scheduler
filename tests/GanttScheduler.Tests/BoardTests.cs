using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 과제 종료일(ClosedOn) 규칙과 갱신 경로, 기존 데이터 채우기, 목록 연도 조건, 상황판 데이터 버전
public class ClosedOnRulesTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static Project Make(ProjectState state, params DateOnly?[] done)
    {
        var p = new Project { State = state };
        foreach (var d in done) p.Stages.Add(new Stage { CompletedDate = d });
        return p;
    }

    [Fact]
    public void 완료는_마지막_단계_완료일이고_진행_보류는_없다()
    {
        Assert.Equal(D(9, 20), Rules.ClosedOn(Make(ProjectState.Done, D(9, 1), D(9, 20), D(9, 10)), D(10, 9)));
        Assert.Null(Rules.ClosedOn(Make(ProjectState.Active, D(9, 1), D(9, 20)), D(10, 9)));
        Assert.Null(Rules.ClosedOn(Make(ProjectState.Hold, D(9, 1)), D(10, 9)));
        // 완료인데 덜 끝난 단계가 있는 어긋난 기록은 비워 둔다
        Assert.Null(Rules.ClosedOn(Make(ProjectState.Done, D(9, 1), null), D(10, 9)));
    }

    [Fact]
    public void 중단은_적힌_중단일을_지키고_없으면_오늘이다()
    {
        var p = Make(ProjectState.Dropped, D(9, 1), null);
        Assert.Equal(D(10, 9), Rules.ClosedOn(p, D(10, 9)));
        p.ClosedOn = D(3, 2);
        Assert.Equal(D(3, 2), Rules.ClosedOn(p, D(10, 9)));
    }

    [Fact]
    public void 연도_범위는_1월_1일부터_12월_31일까지다()
    {
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), Rules.YearRange(2025));
    }

    [Fact]
    public void 데이터_버전은_과제_그래프_엔티티만_센다()
    {
        foreach (var e in new object[] { new Project(), new Stage(), new WorkItem(), new CheckItem(), new WorkLink(), new StageLink(), new Attachment(), new Activity() })
            Assert.True(DataVersion.Watches(e), e.GetType().Name);
        foreach (var e in new object[] { new UserAddress(), new AppUser(), new ProjectVisit(), new ProjectWatch(), new Notification(), new Location(), new Holiday() })
            Assert.False(DataVersion.Watches(e), e.GetType().Name);
    }

    [Fact]
    public void 상황판_확인값은_버전과_날짜를_함께_본다()
    {
        Assert.NotEqual(BoardService.Stamp(5, D(10, 9)), BoardService.Stamp(5, D(10, 10)));
        Assert.NotEqual(BoardService.Stamp(5, D(10, 9)), BoardService.Stamp(6, D(10, 9)));
    }
}

public class ClosedOnServiceTests
{
    private static DateOnly Day(int m, int d) => new(2026, m, d);

    // 체크 항목 없는 새 과제(단계만). 단계 완료를 막는 체크 항목은 지워 둔다
    private static async Task<(int Id, List<int> Stages)> NewProjectAsync(TestDb t)
    {
        using var s = t.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput("종료일 시험", cat.Id, corp.Id, "테스터", Day(8, 3), null, null, null, null, WithTemplateWorks: false));
        s.Db.CheckItems.RemoveRange(s.Db.CheckItems.Where(c => c.Stage!.ProjectId == id));
        await s.Db.SaveChangesAsync();
        var stages = await s.Db.Stages.Where(x => x.ProjectId == id).OrderBy(x => x.SortOrder).Select(x => x.Id).ToListAsync();
        return (id, stages);
    }

    private static async Task<Project> LoadAsync(TestDb t, int id)
    {
        using var db = t.Open();
        return await db.Projects.AsNoTracking().FirstAsync(p => p.Id == id);
    }

    private static async Task CompleteAllAsync(TestDb t, int id, List<int> stages)
    {
        for (var i = 0; i < stages.Count; i++)
        {
            using var s = t.NewScope();
            var st = await s.Db.Stages.AsNoTracking().FirstAsync(x => x.Id == stages[i]);
            await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, st.TargetDate, Day(9, 1).AddDays(i * 3), null, false));
        }
    }

    [Fact]
    public async Task 모든_단계를_완료하면_종료일이_생기고_완료일을_고치면_따라간다()
    {
        using var t = new TestDb();
        var (id, stages) = await NewProjectAsync(t);
        Assert.Null((await LoadAsync(t, id)).ClosedOn);

        await CompleteAllAsync(t, id, stages);
        var p = await LoadAsync(t, id);
        Assert.Equal(ProjectState.Done, p.State);
        Assert.Equal(Day(9, 1).AddDays((stages.Count - 1) * 3), p.ClosedOn);

        var lastId = stages[^1];
        // 완료 상태로 마지막 단계 완료일만 고친다
        using (var s = t.NewScope())
        {
            var last = await s.Db.Stages.AsNoTracking().FirstAsync(x => x.Id == lastId);
            await s.Projects.SaveStageAsync(new StageInput(id, last.Id, last.Name, last.TargetDate, Day(10, 7), null, false));
        }
        Assert.Equal(Day(10, 7), (await LoadAsync(t, id)).ClosedOn);

        // 완료 취소 → 진행으로 돌아가고 종료일은 없다
        using (var s = t.NewScope())
        {
            var last = await s.Db.Stages.AsNoTracking().FirstAsync(x => x.Id == lastId);
            await s.Projects.SaveStageAsync(new StageInput(id, last.Id, last.Name, last.TargetDate, null, null, false));
        }
        p = await LoadAsync(t, id);
        Assert.Equal(ProjectState.Active, p.State);
        Assert.Null(p.ClosedOn);
    }

    [Fact]
    public async Task 중단하면_오늘이_종료일이고_재개하면_지운다()
    {
        using var t = new TestDb();
        var (id, _) = await NewProjectAsync(t);
        t.Clock.Today = Day(10, 5);
        using (var s = t.NewScope()) await s.Projects.SetStateAsync(id, ProjectState.Dropped, "사업 취소");
        Assert.Equal(Day(10, 5), (await LoadAsync(t, id)).ClosedOn);

        // 날이 바뀌어도 중단일은 그대로(다른 정보를 고쳐도)
        t.Clock.Today = Day(10, 9);
        using (var s = t.NewScope()) await s.Projects.SetStateAsync(id, ProjectState.Hold, "보류로 전환");
        Assert.Null((await LoadAsync(t, id)).ClosedOn);
        using (var s = t.NewScope()) await s.Projects.SetStateAsync(id, ProjectState.Dropped, "다시 중단");
        Assert.Equal(Day(10, 9), (await LoadAsync(t, id)).ClosedOn);
        using (var s = t.NewScope()) await s.Projects.SetStateAsync(id, ProjectState.Active, "재개");
        Assert.Null((await LoadAsync(t, id)).ClosedOn);
    }

    [Fact]
    public async Task 완료한_과제를_중단하면_중단한_날이_종료일이다()
    {
        using var t = new TestDb();
        var (id, stages) = await NewProjectAsync(t);
        await CompleteAllAsync(t, id, stages);
        using (var s = t.NewScope()) await s.Projects.SetStateAsync(id, ProjectState.Dropped, "철회");
        var p = await LoadAsync(t, id);
        Assert.Equal(ProjectState.Dropped, p.State);
        Assert.Equal(t.Clock.Today, p.ClosedOn);
    }

    [Fact]
    public async Task 기존_데이터의_종료일은_한_번_채우고_다시_불러도_같다()
    {
        using var t = new TestDb();
        var (id, stages) = await NewProjectAsync(t);
        await CompleteAllAsync(t, id, stages);
        int dropped;
        using (var s = t.NewScope())
        {
            dropped = await s.Db.Projects.Where(p => p.Id != id && p.State == ProjectState.Active).Select(p => p.Id).FirstAsync();
            await s.Projects.SetStateAsync(dropped, ProjectState.Dropped, "취소");
        }
        // 종료일 열이 생기기 전의 데이터처럼 비운다. 중단 활동 시각은 9월 3일(한국 날짜)로
        using (var db = t.Open())
        {
            await db.Projects.Where(p => p.ClosedOn != null).ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosedOn, (DateOnly?)null));
            var act = await db.Activities.Where(a => a.ProjectId == dropped && a.Type == ActivityType.StateChanged).OrderByDescending(a => a.Id).FirstAsync();
            act.At = new DateTime(2026, 9, 2, 16, 0, 0, DateTimeKind.Utc);   // 한국 9월 3일 01시
            await db.SaveChangesAsync();
        }

        using (var db = t.Open())
        {
            var filled = await ClosedDates.FillAsync(db, t.Clock.Today);
            Assert.True(filled >= 2, $"채운 과제 {filled}건");
        }
        Assert.Equal(Day(9, 1).AddDays((stages.Count - 1) * 3), (await LoadAsync(t, id)).ClosedOn);
        Assert.Equal(Day(9, 3), (await LoadAsync(t, dropped)).ClosedOn);
        using (var db = t.Open())
        {
            Assert.Equal(0, await ClosedDates.FillAsync(db, t.Clock.Today.AddDays(30)));
            // 진행·보류 과제에는 넣지 않는다
            Assert.False(await db.Projects.AnyAsync(p => (p.State == ProjectState.Active || p.State == ProjectState.Hold) && p.ClosedOn != null));
        }
    }

    [Fact]
    public async Task 목록의_완료_탭은_종료_연도로_거른다()
    {
        using var t = new TestDb();
        var (a, sa) = await NewProjectAsync(t);
        var (b, sb) = await NewProjectAsync(t);
        await CompleteAllAsync(t, a, sa);
        await CompleteAllAsync(t, b, sb);
        using (var db = t.Open())
        {
            // b 는 지난해에 끝난 과제로 만든다
            await db.Projects.Where(p => p.Id == b).ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosedOn, new DateOnly(2025, 12, 30)));
        }
        using var s = t.NewScope();
        var thisYear = await s.Dashboard.ListAsync(new ProjectFilter(null, null, null, "done", false, ClosedYear: 2026));
        var lastYear = await s.Dashboard.ListAsync(new ProjectFilter(null, null, null, "done", false, ClosedYear: 2025));
        var all = await s.Dashboard.ListAsync(new ProjectFilter(null, null, null, "done", false));
        Assert.Contains(thisYear, v => v.Id == a);
        Assert.DoesNotContain(thisYear, v => v.Id == b);
        Assert.Contains(lastYear, v => v.Id == b);
        Assert.DoesNotContain(lastYear, v => v.Id == a);
        Assert.Contains(all, v => v.Id == a);
        Assert.Contains(all, v => v.Id == b);
        // 대시보드 '올해 완료'도 같은 종료일로 센다
        var dash = await s.Dashboard.BuildAsync(new DashboardQuery());
        Assert.Equal(thisYear.Count, dash.Kpi.DoneThisYear);
    }

    [Fact]
    public async Task 데이터_버전은_과제를_바꾸는_저장에서만_오른다()
    {
        using var t = new TestDb();
        var version = new DataVersion();
        var start = version.Value;
        using (var db = t.Open(version))
        {
            var addr = new UserAddress { UserId = t.Me.Id, Ip = "10.9.9.9", LastSeenAt = t.Clock.UtcNow };
            db.UserAddresses.Add(addr);
            await db.SaveChangesAsync();
            addr.LastSeenAt = t.Clock.UtcNow.AddDays(1);
            await db.SaveChangesAsync();
            await db.SaveChangesAsync();   // 바뀐 것 없음
        }
        Assert.Equal(start, version.Value);

        using (var db = t.Open(version))
        {
            var p = await db.Projects.FirstAsync();
            p.Title += " (수정)";
            await db.SaveChangesAsync();
        }
        Assert.Equal(start + 1, version.Value);

        using (var db = t.Open(version))
        {
            var w = await db.WorkItems.FirstAsync();
            w.Progress = w.Progress == 50 ? 75 : 50;
            await db.SaveChangesAsync();
        }
        Assert.Equal(start + 2, version.Value);
    }
}
