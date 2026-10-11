using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 실제 DB(SQLite 또는 SQL Server)에 대고 서비스 동작을 확인한다
public class ServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;
    private DateOnly D(int offset) => Today.AddDays(offset);

    public ServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title = "테스트 과제", string category = "물류 자동화")
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "미주");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, D(-10), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    [Fact]
    public async Task 샘플_데이터_대시보드_집계()
    {
        using var s = _db.NewScope();
        var all = await s.Projects.AllAsync();
        var view = Dashboard.Build(all.Where(p => !p.Title.StartsWith("테스트")).ToList(), await s.Projects.LocationsAsync(), Today);

        Assert.Equal(6, view.Kpi.Active);
        Assert.Equal(2, view.Kpi.Delayed);
        Assert.Equal(1, view.Kpi.Hold);
        Assert.Equal(2, view.Kpi.DoneThisYear);
        Assert.Equal(new[] { "동남아 C제품 2라인 AMR 확대", "유럽 C제품 창고 입출고 개선" }, view.Delayed.Select(p => p.Title).ToArray());
        Assert.Equal(6, view.Delayed[0].DelayDays);
        Assert.Equal("제작", view.Delayed[0].DelayedStage);
    }

    [Fact]
    public async Task 과제를_만들면_유형의_단계가_복사된다()
    {
        var id = await NewProjectAsync();
        var p = await ViewAsync(id);

        Assert.Equal(new[] { "검토", "품의", "발주", "제작", "검수", "입고", "설치", "안정화" }, p.Stages.Select(s => s.Name).ToArray());
        Assert.Equal("미주", p.Corporation);
        Assert.Equal("테스터", p.Owner);
        Assert.Equal("active", p.State);
        Assert.Equal(0, p.Progress);
        Assert.Single(p.Activities!);
    }

    [Fact]
    public async Task 목표일_변경은_사유가_필요하고_이후_단계를_근무일_기준으로_함께_옮긴다()
    {
        var id = await NewProjectAsync();
        var stages = (await ViewAsync(id)).Stages;
        DateOnly Day(int month, int day) => new(2026, month, day);

        // 처음 정하는 목표일은 사유 없이 가능 (모두 수요일)
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, "검토", Day(10, 14), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, stages[1].Id, "품의", Day(10, 28), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, stages[2].Id, "발주", Day(11, 11), null, null, false));
        }

        // 바꿀 때는 사유 필수
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() =>
                s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, "검토", Day(10, 21), null, " ", false)));
            Assert.Equal("reason", ex.Field);
        }

        // 10-14 → 10-21 은 근무일 5일. 이후 단계도 근무일 5일씩 밀린다
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, "검토", Day(10, 21), null, "자료 보완", true));

        var p = await ViewAsync(id);
        Assert.Equal(Day(10, 21), p.Stages[0].TargetDate);
        Assert.Equal(Day(11, 4), p.Stages[1].TargetDate);
        Assert.Equal(Day(11, 18), p.Stages[2].TargetDate);
        Assert.Null(p.Stages[3].TargetDate);

        var changes = p.Activities!.Where(a => a.Type == "targetChanged").ToList();
        Assert.Equal(3, changes.Count);
        Assert.All(changes, a => Assert.Equal("자료 보완", a.Reason));
        Assert.Equal(1, p.TargetChanges);         // 연쇄 이동은 한 번의 결정 — 행 3개(같은 BatchId)를 1회로 센다

        using var check = _db.NewScope();
        var batches = await check.Db.Activities.Where(a => a.ProjectId == id && a.Type == ActivityType.TargetChanged).Select(a => a.BatchId).ToListAsync();
        Assert.Single(batches.Distinct());
    }

    [Fact]
    public async Task 연쇄_이동은_주말과_공휴일을_건너뛴다()
    {
        var id = await NewProjectAsync("테스트 근무일", "물류");
        var stages = (await ViewAsync(id)).Stages;
        DateOnly Day(int month, int day) => new(2026, month, day);

        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, "검토", Day(12, 22), null, null, false));   // 화
            await s.Projects.SaveStageAsync(new StageInput(id, stages[1].Id, "보고", Day(12, 23), null, null, false));   // 수
        }
        // 12-22(화) → 12-24(목): 근무일 2일. 12-23(수)에서 2일 뒤는 12-24(목), 12-25 성탄절·주말을 건너 12-28(월)
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[0].Id, "검토", Day(12, 24), null, "일정 조정", true));

        Assert.Equal(Day(12, 28), (await ViewAsync(id)).Stages[1].TargetDate);
    }

    [Fact]
    public async Task 작업_저장_규칙과_진행률_롤업()
    {
        var id = await NewProjectAsync();
        var stage = (await ViewAsync(id)).Stages[0];

        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "역전", null, null, D(5), D(1), null, null, 0, null, null)));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "단위", null, null, D(1), D(5), null, null, 33, null, null)));
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "종료만", null, null, D(1), D(5), null, D(2), 0, null, null)));
        }

        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "현장 조사", "홍길동", "홍길동", D(-5), D(3), null, null, 40, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "보고서", "임꺽정", null, D(-8), D(-2), D(-8), D(-1), 0, null, null));
        }

        var p = await ViewAsync(id);
        var works = p.Stages[0].Works;
        Assert.Equal(2, works.Count);

        var survey = works.Single(w => w.Title == "현장 조사");
        Assert.Equal("홍길동", survey.Owner);
        Assert.Null(survey.CoOwner);                       // 정·부 담당이 같으면 부담당은 비운다
        Assert.Equal(Today, survey.ActualStart);           // 진행률만 올리면 오늘 시작한 것으로
        Assert.Equal("inprogress", survey.Status);

        var report = works.Single(w => w.Title == "보고서");
        Assert.Equal(100, report.Progress);                // 종료일이 있으면 100%
        Assert.True(report.DoneLate);

        Assert.Equal(70, p.Stages[0].Progress);            // (40 + 100) / 2
        Assert.Equal(9, p.Progress);                       // 70 / 8 단계 = 8.75 → 9
        Assert.Equal(D(3), p.Stages[0].Forecast);

        // 목표 일정 변경은 사유 필수
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(survey.Id, stage.Id, "현장 조사", "홍길동", null, D(-5), D(6), Today, null, 40, null, null)));
            Assert.Equal("reason", ex.Field);
        }

        // 다른 단계로 옮기기
        var second = p.Stages[1];
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(survey.Id, second.Id, "현장 조사", "홍길동", null, D(-5), D(3), Today, null, 40, null, null));

        p = await ViewAsync(id);
        Assert.Single(p.Stages[0].Works);
        Assert.Single(p.Stages[1].Works);

        using (var s = _db.NewScope())
            await s.Projects.DeleteWorkAsync(survey.Id);
        Assert.Empty((await ViewAsync(id)).Stages[1].Works);
    }

    [Fact]
    public async Task 모든_단계를_완료하면_완료_상태가_되고_취소하면_다시_진행()
    {
        var id = await NewProjectAsync("테스트 물류 과제", "물류");
        var stages = (await ViewAsync(id)).Stages;
        Assert.Equal(3, stages.Count);

        using (var s = _db.NewScope())
            foreach (var st in stages)
                await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, null, Today, null, false));

        var p = await ViewAsync(id);
        Assert.Equal("done", p.State);
        Assert.Equal(100, p.Progress);

        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stages[2].Id, stages[2].Name, null, null, null, false));
        Assert.Equal("active", (await ViewAsync(id)).State);
    }

    [Fact]
    public async Task 보류와_중단은_사유가_필요하다()
    {
        var id = await NewProjectAsync();
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SetStateAsync(id, ProjectState.Hold, null));
        using (var s = _db.NewScope())
            await s.Projects.SetStateAsync(id, ProjectState.Hold, "우선순위 조정");
        Assert.Equal("hold", (await ViewAsync(id)).State);
        using (var s = _db.NewScope())
            await s.Projects.SetStateAsync(id, ProjectState.Active, null);
        Assert.Equal("active", (await ViewAsync(id)).State);
    }

    [Fact]
    public async Task 단계_추가_순서_이동_삭제()
    {
        var id = await NewProjectAsync("테스트 단계 편집", "물류");
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, null, "사후 점검", D(40), null, null, false));

        var p = await ViewAsync(id);
        Assert.Equal(new[] { "검토", "보고", "적용", "사후 점검" }, p.Stages.Select(s => s.Name).ToArray());

        using (var s = _db.NewScope())
            await s.Projects.MoveStageAsync(p.Stages[3].Id, -1);
        p = await ViewAsync(id);
        Assert.Equal(new[] { "검토", "보고", "사후 점검", "적용" }, p.Stages.Select(s => s.Name).ToArray());

        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, p.Stages[2].Id, "점검표 작성", null, null, D(1), D(3), null, null, 0, null, null));
            await s.Projects.DeleteStageAsync(p.Stages[2].Id);
        }
        p = await ViewAsync(id);
        Assert.Equal(3, p.Stages.Count);
        Assert.Equal(0, p.WorkCount);
    }

    [Fact]
    public async Task 과제_수정과_기기_내역_교체_그리고_삭제()
    {
        var id = await NewProjectAsync();
        int agv, lifter, vietnam;
        using (var s = _db.NewScope())
        {
            agv = (await s.Db.EquipmentTypes.Include(e => e.Parent).FirstAsync(e => e.Name == "ANT" && e.Parent!.Name == "AGV")).Id;
            lifter = (await s.Db.EquipmentTypes.FirstAsync(e => e.Name == "리프터")).Id;
            vietnam = (await s.Db.Locations.FirstAsync(l => l.Level == 3 && l.Name == "2라인")).Id;
            var cat = (await s.Projects.LoadAsync(id))!.CategoryId;
            await s.Projects.UpdateAsync(id, new ProjectInput("테스트 과제(수정)", cat, vietnam, "새담당", D(-20), "협력사A", "설명",
                new List<ItemInput> { new(agv, 5, null), new(lifter, 1, "예비") }, null));
        }

        var p = await ViewAsync(id);
        Assert.Equal("테스트 과제(수정)", p.Title);
        Assert.Equal("동남아", p.Corporation);
        Assert.Equal("C제품 · 2라인", p.LocationPath);
        Assert.Equal("새담당", p.Owner);
        Assert.Equal(new[] { "AGV · ANT", "리프터" }, p.Equipments!.Select(e => e.Name).OrderBy(n => n).ToArray());

        using (var s = _db.NewScope())
        {
            var cat = (await s.Projects.LoadAsync(id))!.CategoryId;
            await s.Projects.UpdateAsync(id, new ProjectInput("테스트 과제(수정)", cat, vietnam, "새담당", D(-20), "협력사A", "설명",
                new List<ItemInput> { new(lifter, 2, null) }, null));
        }
        p = await ViewAsync(id);
        Assert.Equal(2, Assert.Single(p.Equipments!).Quantity);

        using (var s = _db.NewScope())
        {
            await s.Projects.DeleteAsync(id);
            await s.Projects.PurgeAsync(id);
        }
        using (var s = _db.NewScope())
        {
            Assert.Null(await s.Projects.ViewAsync(id));
            Assert.False(await s.Db.Stages.AnyAsync(x => x.ProjectId == id));
            Assert.False(await s.Db.Activities.AnyAsync(x => x.ProjectId == id));
        }
    }

    [Fact]
    public async Task 이름_등록은_같은_이름이면_IP만_연결한다()
    {
        using (var s = _db.NewScope())
        {
            var first = await s.Users.RegisterAsync("10.0.0.1", "김물류", null);
            var again = await s.Users.RegisterAsync("10.0.0.2", "김물류", null);
            Assert.Equal(first.Id, again.Id);
        }
        using (var s = _db.NewScope())
        {
            var ips = await s.Db.UserAddresses.Include(a => a.User).Where(a => a.User!.Name == "김물류").Select(a => a.Ip).ToListAsync();
            Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, ips.OrderBy(x => x).ToArray());
        }
    }

    [Fact]
    public async Task 관리_계층_추가와_순서_이동_사용_중지()
    {
        using var s = _db.NewScope();
        await s.Masters.SaveAsync(new MasterSave("location", 1, null, null, "인도", "인도", 20, null, null, null, null));
        var nodes = await s.Masters.ListAsync("location");
        var india = nodes.Single(n => n.Level == 1 && n.Name == "인도");
        Assert.Equal(20, india.OceanLeadDays);
        Assert.Equal("인도", nodes.Last(n => n.Level == 1).Name);

        await s.Masters.MoveAsync(new MasterRef("location", 1, india.Id, -1));
        var corps = (await s.Masters.ListAsync("location")).Where(n => n.Level == 1).Select(n => n.Name).ToList();
        Assert.Equal("인도", corps[^2]);

        await s.Masters.ToggleAsync(new MasterRef("location", 1, india.Id, 0, Active: false));
        Assert.False((await s.Masters.ListAsync("location")).Single(n => n.Id == india.Id && n.Level == 1).Active);

        // 공휴일은 날짜가 겹치면 거절
        await Assert.ThrowsAsync<RuleException>(() =>
            s.Masters.SaveAsync(new MasterSave("holiday", 1, null, null, "중복", null, null, null, null, null, new DateOnly(2026, 10, 9))));
    }
}
