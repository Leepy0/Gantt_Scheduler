using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 단계 끌어 놓기·정렬, 새 단계의 기본(작업 일정에 맞춤), 등록 CSV 의 선행 단계·선행 작업 칸, 단계 막대 시작 보정
public class StageOrderTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public StageOrderTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 1), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    [Fact]
    public async Task 끌어_놓은_순서를_저장하고_자리가_바뀐_단계의_버전만_올린다()
    {
        var id = await NewProjectAsync("테스트 끌어놓기");
        var st = (await ViewAsync(id)).Stages;      // 검토 · 보고 · 적용

        // 옛 버전·모르는 단계가 끼면 409, 아무것도 바뀌지 않는다
        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ReorderStagesAsync(id,
                new() { new(st[2].Id, st[2].Version + 1), new(st[0].Id, st[0].Version), new(st[1].Id, st[1].Version) }));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ReorderStagesAsync(id, new() { new(st[2].Id), new(st[0].Id) }));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ReorderStagesAsync(id, new() { new(st[2].Id), new(st[0].Id), new(999999) }));
        }
        Assert.Equal(new[] { "검토", "보고", "적용" }, (await ViewAsync(id)).Stages.Select(x => x.Name));

        // 적용을 맨 앞으로 끌어 놓음
        using (var s = _db.NewScope())
            await s.Projects.ReorderStagesAsync(id, new() { new(st[2].Id, st[2].Version), new(st[0].Id, st[0].Version), new(st[1].Id, st[1].Version) });
        var after = await ViewAsync(id);
        Assert.Equal(new[] { "적용", "검토", "보고" }, after.Stages.Select(x => x.Name));
        Assert.All(after.Stages, x => Assert.Equal(st.First(o => o.Id == x.Id).Version + 1, x.Version));

        using (var s = _db.NewScope())
        {
            var act = await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == id).OrderBy(a => a.Id).LastAsync();
            Assert.Equal(ActivityType.Edited, act.Type);
            Assert.Equal("단계 순서 변경: 적용 → 검토 → 보고", act.Text);
        }

        // 같은 순서를 다시 보내면 아무 일도 없다
        var count = await CountActivitiesAsync(id);
        using (var s = _db.NewScope())
            await s.Projects.ReorderStagesAsync(id, after.Stages.Select(x => new StageOrderItem(x.Id, x.Version)).ToList());
        Assert.Equal(count, await CountActivitiesAsync(id));
    }

    private async Task<int> CountActivitiesAsync(int id)
    {
        using var s = _db.NewScope();
        return await s.Db.Activities.CountAsync(a => a.ProjectId == id);
    }

    [Fact]
    public async Task 정렬은_종료일순과_시작일순으로_날짜가_없는_단계를_뒤로_보낸다()
    {
        var id = await NewProjectAsync("테스트 정렬");
        var st = (await ViewAsync(id)).Stages;      // 검토 · 보고 · 적용
        using (var s = _db.NewScope())
        {
            // 검토 10-30, 보고 10-10, 적용 날짜 없음
            await s.Projects.SaveStageAsync(new StageInput(id, st[0].Id, "검토", Day(10, 30), null, "정렬 시험", false, st[0].Version, AutoTarget: false));
            await s.Projects.SaveStageAsync(new StageInput(id, st[1].Id, "보고", Day(10, 10), null, "정렬 시험", false, st[1].Version, AutoTarget: false));
            if (st[2].TargetDate != null)
                await s.Projects.SaveStageAsync(new StageInput(id, st[2].Id, "적용", null, null, "정렬 시험", false, st[2].Version, AutoTarget: false));
        }
        using (var s = _db.NewScope()) await s.Projects.SortStagesAsync(id, "end");
        Assert.Equal(new[] { "보고", "검토", "적용" }, (await ViewAsync(id)).Stages.Select(x => x.Name));

        // 시작일순: 작업이 있는 단계는 가장 이른 작업 시작, 없으면 목표일
        var v = await ViewAsync(id);
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, v.Stages.First(x => x.Name == "검토").Id, "먼저 시작", null, null, Day(10, 2), Day(10, 3), null, null, 0, null, null));
        using (var s = _db.NewScope()) await s.Projects.SortStagesAsync(id, "start");
        Assert.Equal(new[] { "검토", "보고", "적용" }, (await ViewAsync(id)).Stages.Select(x => x.Name));

        using var bad = _db.NewScope();
        await Assert.ThrowsAsync<RuleException>(() => bad.Projects.SortStagesAsync(id, "name"));
    }

    [Fact]
    public async Task 새_단계는_기본으로_작업_일정에_맞추고_작업이_없으면_직접_정한_목표일을_받는다()
    {
        var id = await NewProjectAsync("테스트 기본 맞춤");
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, null, "신규", Day(10, 20), null, null, false));
        var added = (await ViewAsync(id)).Stages.Last();
        Assert.True(added.AutoTarget);
        Assert.Equal(Day(10, 20), added.TargetDate);        // 작업이 없어 직접 정한 값을 둔다

        // 작업이 없는 맞춤 단계의 목표일도 직접 바꿀 수 있다
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, added.Id, "신규", Day(10, 22), null, "일정 조정", false, added.Version));
        Assert.Equal(Day(10, 22), (await ViewAsync(id)).Stages.Last().TargetDate);

        // 작업을 넣으면 그 일정으로 맞춰진다
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, added.Id, "첫 작업", null, null, Day(10, 12), Day(10, 15), null, null, 0, null, null));
        Assert.Equal(Day(10, 15), (await ViewAsync(id)).Stages.Last().TargetDate);
    }

    [Fact]
    public async Task 등록_CSV_는_선행_단계와_선행_작업을_나눠_읽고_단계를_비우면_같은_단계로_본다()
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        const string header = "단계,작업명,담당,목표 시작,목표 종료,선행 단계,선행 작업,동시\n";
        var text = header +
            "설계,,,,2026-10-14,,,\n" +
            "설계,현장 실사,,2026-10-02,2026-10-06,,,\n" +
            "설계,사양 검토,,2026-10-07,2026-10-13,,현장 실사,\n" +
            "제작,,,,2026-10-30,,,\n" +
            "제작,사양 검토,,2026-10-15,2026-10-20,,,\n" +                  // 다른 단계에 같은 이름
            "제작,시제품,,2026-10-21,2026-10-28,설계,사양 검토,\n" +          // 다른 단계 지정
            "제작,양산,,2026-10-21,2026-10-29,,사양 검토,\n";                 // 단계 비움: 같은 단계(제작)의 것
        var pv = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), text));
        Assert.Equal(0, pv.ErrorCount);
        var proto = pv.Works.First(w => w.Title == "시제품");
        Assert.Equal(("설계", "사양 검토"), (proto.PredecessorStage, proto.Predecessor));

        // 이름이 같아도 단계를 적으면 가려 읽고, 없는 단계·작업은 문제로 알린다
        var bad = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), header +
            "설계,,,,2026-10-14,,,\n" +
            "설계,사양 검토,,2026-10-07,2026-10-13,없는단계,현장 실사,\n" +
            "설계,현장 실사,,2026-10-02,2026-10-06,설계,없는작업,\n" +
            "설계,검토만,,2026-10-14,2026-10-14,설계,,\n"));
        Assert.Equal(3, bad.ErrorCount);
        Assert.Contains(bad.Works[0].Issues, i => i.Column == "선행 단계");
        Assert.Contains(bad.Works[1].Issues, i => i.Column == "선행 작업명");
        Assert.Contains(bad.Works[2].Issues, i => i.Message.Contains("선행 작업도"));

        // 저장하면 연결이 선행 단계의 작업으로 걸린다
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput("CSV 선행 단계", cat.Id, corp.Id, null, Day(10, 1), null, null, null, null, PasteText: text));
        var v = (await s.Projects.ViewAsync(id))!;
        var all = v.Stages.SelectMany(x => x.Works.Select(w => (Stage: x.Name, Work: w))).ToList();
        var reviewDesign = all.First(x => x.Stage == "설계" && x.Work.Title == "사양 검토").Work;
        var reviewMake = all.First(x => x.Stage == "제작" && x.Work.Title == "사양 검토").Work;
        var protoWork = all.First(x => x.Work.Title == "시제품").Work;
        var mass = all.First(x => x.Work.Title == "양산").Work;
        Assert.Contains(v.Links, l => l.From == reviewDesign.Id && l.To == protoWork.Id);
        Assert.Contains(v.Links, l => l.From == reviewMake.Id && l.To == mass.Id);
        Assert.DoesNotContain(v.Links, l => l.From == reviewDesign.Id && l.To == mass.Id);
    }

    [Fact]
    public async Task 옛_머리글의_선행_작업명_한_칸_CSV_도_읽는다()
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var legacy = "단계,작업명,담당,목표 시작,목표 종료,선행 작업명,동시\n설계,,,,2026-10-14,,\n설계,현장 실사,,2026-10-02,2026-10-06,,\n" +
            "설계,사양 검토,,2026-10-07,2026-10-13,현장 실사,\n교육,,,,2026-10-14,,동시\n";
        var pv = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), legacy));
        Assert.Equal(0, pv.ErrorCount);
        Assert.Equal("현장 실사", pv.Works[1].Predecessor);
        Assert.True(pv.Stages[1].Parallel);
    }

    [Fact]
    public async Task 단계_정보에_작업_시작과_실적_시작이_실려_막대가_작업보다_늦게_시작하지_않게_한다()
    {
        var id = await NewProjectAsync("테스트 막대 시작");
        var st = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, st[1].Id, "일찍", null, null, Day(10, 2), Day(10, 6), Day(10, 2), null, 10, null, null, ClientToday: Day(10, 2)));
        var second = (await ViewAsync(id)).Stages[1];
        Assert.Equal(Day(10, 2), second.WorksFrom);
        Assert.Equal(Day(10, 2), second.WorksActualFrom);
    }
}
