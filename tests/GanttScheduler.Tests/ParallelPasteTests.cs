using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GanttScheduler.Tests;

// 동시 진행 단계(규칙 23)·작업 일정에 맞춘 목표일(규칙 1 의 예외)의 순수 규칙
public class ParallelStageRulesTests
{
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    // 2026-10-01 은 목요일. 10-03(토) 개천절, 10-05(월) 대체공휴일, 10-09(금) 한글날
    private static readonly WorkCalendar Cal = new(new[] { Day(10, 3), Day(10, 5), Day(10, 9) });
    private static readonly bool[] Flags = { false, true, false };

    [Fact]
    public void 동시_진행_단계는_묶음_기준일에서_세고_묶음_뒤_단계는_가장_늦은_목표일부터_센다()
    {
        // A(3) · B(2, A 와 동시) · C(4)
        var drafts = Rules.DraftTargets(Day(10, 1), new int?[] { 3, 2, 4 }, Cal, null, Flags);
        Assert.Equal(Day(10, 7), drafts[0]);    // 10-02, 10-06, 10-07
        Assert.Equal(Day(10, 6), drafts[1]);    // 착수일부터: 10-02, 10-06 (A 뒤가 아니다)
        Assert.Equal(Day(10, 14), drafts[2]);   // max(A, B) = 10-07 부터: 10-08, 10-12, 10-13, 10-14

        // 간격도 같은 기준: B 는 착수일부터 +2, C 는 10-07 부터 +4. 표시가 없으면 B 는 A 보다 이르다(-1)
        Assert.Equal(new int?[] { 3, 2, 4 }, Rules.TargetGaps(Day(10, 1), drafts, Cal, Flags));
        Assert.Equal(new int?[] { 3, -1, 5 }, Rules.TargetGaps(Day(10, 1), drafts, Cal));

        // 템플릿 작업의 단계 시작: B 는 A 와 같은 날, C 는 10-07 다음 근무일
        Assert.Equal(new[] { Day(10, 1), Day(10, 1), Day(10, 8) }, Rules.StageStarts(Day(10, 1), drafts, Cal, Flags));
        // 표시가 없으면 예전과 같다 (B 는 A 목표일 다음, C 는 B 목표일 다음)
        Assert.Equal(new[] { Day(10, 1), Day(10, 8), Day(10, 7) }, Rules.StageStarts(Day(10, 1), drafts, Cal));
    }

    [Fact]
    public void 첫_단계의_동시_표시는_뜻이_없고_템플릿의_표시는_순서대로_읽는다()
    {
        var first = Rules.DraftTargets(Day(10, 1), new int?[] { 3, 2 }, Cal, null, new[] { true, false });
        Assert.Equal(new DateOnly?[] { Day(10, 7), Day(10, 12) }, first);

        var templates = new[]
        {
            new StageTemplate { Id = 2, Name = "모델링", SortOrder = 2, DefaultWorkDays = 2, Parallel = true },
            new StageTemplate { Id = 1, Name = "데이터 수집", SortOrder = 1, DefaultWorkDays = 3 },
            new StageTemplate { Id = 3, Name = "검증", SortOrder = 3, DefaultWorkDays = 4 },
        };
        Assert.Equal(new DateOnly?[] { Day(10, 7), Day(10, 6), Day(10, 14) }, Rules.DraftTargets(Day(10, 1), templates, Cal));
    }

    private static Project Three(bool parallelB, bool parallelC)
    {
        var p = new Project { State = ProjectState.Active, StartDate = Day(10, 1) };
        p.Stages.Add(new Stage { Id = 1, Name = "A", SortOrder = 1, TargetDate = Day(10, 7), CompletedDate = Day(10, 7) });
        p.Stages.Add(new Stage { Id = 2, Name = "B", SortOrder = 2, TargetDate = Day(10, 14), Parallel = parallelB });
        p.Stages.Add(new Stage { Id = 3, Name = "C", SortOrder = 3, TargetDate = Day(10, 16), Parallel = parallelC });
        p.Stages.Add(new Stage { Id = 4, Name = "D", SortOrder = 4, TargetDate = Day(10, 30) });
        return p;
    }

    [Fact]
    public void 묶음의_미완료_단계는_모두_진행_중이고_연쇄_이동_범위에서_빠진다()
    {
        var p = Three(parallelB: false, parallelC: true);     // B·C 가 한 묶음
        Assert.Equal(new[] { "B", "C" }, Rules.CurrentStages(p).Select(s => s.Name));
        Assert.Equal(StageStatus.Current, Rules.StatusOf(p.Stages[1], p, Day(10, 8)));
        Assert.Equal(StageStatus.Current, Rules.StatusOf(p.Stages[2], p, Day(10, 8)));
        Assert.Equal(StageStatus.Planned, Rules.StatusOf(p.Stages[3], p, Day(10, 8)));
        Assert.Equal("B", Rules.CurrentStage(p)!.Name);

        // B 의 뒤 단계는 D 뿐 (C 는 동시 진행이라 함께 옮기지 않는다). C 의 뒤도 D
        Assert.Equal(new[] { "D" }, Rules.LaterStages(p, p.Stages[1]).Select(s => s.Name));
        Assert.Equal(new[] { "D" }, Rules.LaterStages(p, p.Stages[2]).Select(s => s.Name));
        Assert.Equal(new[] { "B", "C", "D" }, Rules.LaterStages(p, p.Stages[0]).Select(s => s.Name));
        // C 의 작업은 B 의 연쇄 이동에 끌려가지 않는다
        p.Stages[2].Works.Add(new WorkItem { Id = 10, Title = "c1", PlanStart = Day(10, 8), PlanEnd = Day(10, 9) });
        p.Stages[3].Works.Add(new WorkItem { Id = 11, Title = "d1", PlanStart = Day(10, 20), PlanEnd = Day(10, 21) });
        Assert.Equal(new[] { "d1" }, Rules.CascadeWorks(p, p.Stages[1]).Select(w => w.Title));

        // 묶음에 들어온 날은 묶음 첫 단계 앞까지의 완료일 — C 도 A 의 완료일
        Assert.Equal(Day(10, 7), Rules.StageEntered(p, p.Stages[2]));

        var plain = Three(parallelB: false, parallelC: false);
        Assert.Equal(new[] { "B" }, Rules.CurrentStages(plain).Select(s => s.Name));
        Assert.Equal(StageStatus.Planned, Rules.StatusOf(plain.Stages[2], plain, Day(10, 8)));
    }

    [Fact]
    public void 화면_모델은_묶음의_시작을_같이_쓰고_현재_단계_이름을_이어_적는다()
    {
        var p = Three(parallelB: false, parallelC: true);
        var index = new LocationIndex(new List<Location>());
        var view = Views.Project(p, Day(10, 8), index, detail: false, withWorks: false, calendar: Cal);
        Assert.Equal("B · C", view.CurrentStage);
        Assert.Equal(Day(10, 16), view.CurrentTarget);           // 묶음에서 가장 늦은 목표일
        Assert.True(view.Stages[2].Parallel);
        Assert.False(view.Stages[1].Parallel);
        // 목표 막대: B·C 모두 A 의 목표일부터, D 는 묶음에서 가장 늦은 C 의 목표일부터
        Assert.Equal(Day(10, 7), view.Stages[1].PlanFrom);
        Assert.Equal(Day(10, 7), view.Stages[2].PlanFrom);
        Assert.Equal(Day(10, 16), view.Stages[3].PlanFrom);
        // 간격: B·C 모두 A 목표일(10-07)부터 — B 10-14 는 4 근무일(10-08, 12, 13, 14 — 한글날·주말 제외), C 10-16 은 6 근무일
        Assert.Equal(4, view.Stages[1].GapWorkDays);
        Assert.Equal(6, view.Stages[2].GapWorkDays);
        // D 는 묶음에서 가장 늦은 C(10-16)부터: 10-19 ~ 10-30 = 10 근무일
        Assert.Equal(10, view.Stages[3].GapWorkDays);
    }

    [Fact]
    public void 작업에_맞춘_목표일은_가장_늦은_목표_종료일이고_작업이_없으면_없다()
    {
        var s = new Stage { Name = "S", AutoTarget = true };
        Assert.Null(Rules.AutoTargetOf(s));
        s.Works.Add(new WorkItem { PlanStart = Day(10, 2), PlanEnd = Day(10, 8) });
        s.Works.Add(new WorkItem { PlanStart = Day(10, 6), PlanEnd = Day(10, 20) });
        Assert.Equal(Day(10, 20), Rules.AutoTargetOf(s));
        Assert.Equal(Day(10, 2), Rules.WorksStart(s));
        s.AutoTarget = false;
        Assert.Null(Rules.AutoTargetOf(s));      // 표시가 없는 단계는 맞추지 않는다
    }

    [Fact]
    public void 댓글_본문의_이미지_참조를_읽고_평문으로_바꾼다()
    {
        var text = "사진 [이미지:12] 과 [이미지:7] 그리고 [이미지:12] 다시";
        Assert.Equal(new[] { 12, 7 }, CommentText.ImageIds(text));
        Assert.Equal("사진 (이미지) 과 (이미지) 그리고 (이미지) 다시", CommentText.Plain(text));
        Assert.Empty(CommentText.ImageIds("[이미지:abc] 글자"));
        Assert.Equal("[이미지:3]", CommentText.Image(3));
    }
}

// 서비스: 동시 진행·자동 목표일 저장, 등록 화면 CSV 붙여넣기, 관리 CSV 일괄 입력, 댓글 이미지 참조
public class ParallelPasteServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public ParallelPasteServiceTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string category = "물류", string? paste = null)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 1), null, null, null, null, PasteText: paste));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    [Fact]
    public async Task 단계_시트로_동시_진행을_켜면_연쇄_이동이_그_단계를_건너뛴다()
    {
        var id = await NewProjectAsync("동시 진행 저장");      // 물류: 검토 → 보고 → 적용
        var v = await ViewAsync(id);
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[0].Id, "검토", Day(10, 7), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[1].Id, "보고", Day(10, 8), null, null, false, Parallel: true));
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[2].Id, "적용", Day(10, 21), null, null, false));
        }
        v = await ViewAsync(id);
        Assert.True(v.Stages[1].Parallel);
        Assert.Equal("검토 · 보고", v.CurrentStage);
        Assert.Contains(await ActivitiesAsync(id), a => a.Text.Contains("앞 단계와 동시 진행으로 바꿈"));

        // 검토 목표일을 3 근무일(10-08·12·13 — 한글날·주말 제외) 미루며 연쇄 이동: 보고(동시)는 그대로, 적용만 3 근무일 밀린다(10-22·23·26)
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[0].Id, "검토", Day(10, 13), null, "자료 보완", true, Version: v.Stages[0].Version));
        v = await ViewAsync(id);
        Assert.Equal(Day(10, 8), v.Stages[1].TargetDate);
        Assert.Equal(Day(10, 26), v.Stages[2].TargetDate);

        // 첫 단계에는 동시 진행을 켤 수 없다(뜻이 없다)
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[0].Id, "검토", v.Stages[0].TargetDate, null, null, false, Version: v.Stages[0].Version, Parallel: true));
        Assert.False((await ViewAsync(id)).Stages[0].Parallel);
    }

    private async Task<List<Activity>> ActivitiesAsync(int id)
    {
        using var s = _db.NewScope();
        return await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == id).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task 작업_일정에_맞춘_목표일은_작업이_바뀔_때마다_따라간다()
    {
        var id = await NewProjectAsync("자동 목표일");
        var v = await ViewAsync(id);
        var stage = v.Stages[0];
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "첫 작업", null, null, Day(10, 2), Day(10, 8), null, null, 0, null, null));
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "둘째 작업", null, null, Day(10, 6), Day(10, 14), null, null, 0, null, null));
        }
        v = await ViewAsync(id);
        Assert.Null(v.Stages[0].TargetDate);     // 표시 전에는 사람이 정하지 않은 그대로

        // 켜면 바로 가장 늦은 작업 종료일이 목표일이 되고, 화면이 보낸 목표일은 무시한다
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, "검토", Day(12, 25), null, null, false, Version: v.Stages[0].Version, AutoTarget: true));
        v = await ViewAsync(id);
        Assert.True(v.Stages[0].AutoTarget);
        Assert.Equal(Day(10, 14), v.Stages[0].TargetDate);
        Assert.Equal(Day(10, 2), v.Stages[0].PlanFrom);          // 목표 막대는 작업의 가장 이른 시작부터
        Assert.Equal(Day(10, 14), v.Stages[0].WorksTo);

        // 작업 일정을 늦추면 목표일이 따라가고 '작업 일정에 맞춤' 이력이 남는다(사유 없이)
        var w2 = v.Stages[0].Works.Single(w => w.Title == "둘째 작업");
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(w2.Id, stage.Id, w2.Title, null, null, Day(10, 6), Day(10, 21), null, null, 0, null, "검토 지연", Version: w2.Version));
        v = await ViewAsync(id);
        Assert.Equal(Day(10, 21), v.Stages[0].TargetDate);
        var acts = await ActivitiesAsync(id);
        var sync = acts.Last(a => a.Type == ActivityType.TargetChanged);
        Assert.Contains("작업 일정에 맞춤", sync.Text);
        Assert.Equal("2026-10-14", sync.OldValue);
        Assert.Equal("2026-10-21", sync.NewValue);

        // 작업을 지우면 남은 작업으로 돌아온다. 끄면 그 뒤로는 따라가지 않는다
        w2 = v.Stages[0].Works.Single(w => w.Title == "둘째 작업");
        using (var s = _db.NewScope()) await s.Projects.DeleteWorkAsync(w2.Id, w2.Version);
        v = await ViewAsync(id);
        Assert.Equal(Day(10, 8), v.Stages[0].TargetDate);
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, stage.Id, "검토", Day(10, 8), null, null, false, Version: v.Stages[0].Version, AutoTarget: false));
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "셋째 작업", null, null, Day(10, 20), Day(10, 28), null, null, 0, null, null));
        v = await ViewAsync(id);
        Assert.False(v.Stages[0].AutoTarget);
        Assert.Equal(Day(10, 8), v.Stages[0].TargetDate);
    }

    private const string Paste =
        "단계,작업명,담당,목표 시작,목표 종료,선행 작업명,동시\n" +
        "검토,,,,2026-10-14,,\n" +
        "검토,현장 실사,김물류,2026-10-02,2026-10-06,,\n" +
        "검토,사양 검토,,10/07,10/13,현장 실사,\n" +
        "검수,,,,,,\n" +
        "검수,FAT,,2026-10-15,2026-10-21,,\n" +
        "교육,,,,2026-10-21,,동시\n" +
        "설치,,,,2026-10-30,,\n";

    [Fact]
    public async Task 등록_화면_CSV_붙여넣기는_단계와_작업을_한_번에_만들고_기본_단계를_이어받는다()
    {
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류 자동화");
            var pv = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), Paste));
            Assert.Equal(0, pv.ErrorCount);
            Assert.Equal(new[] { "검토", "검수", "교육", "설치" }, pv.Stages.Select(x => x.Name));
            Assert.Equal(new[] { 2, 5, 7, 8 }, pv.Stages.Select(x => x.Line));
            Assert.True(pv.Stages[1].FromTemplate);
            Assert.Equal("inspection", pv.Stages[1].Kind);          // 기본 단계 '검수'의 종류·체크리스트를 이어받음
            Assert.Equal(3, pv.Stages[1].Checks);
            Assert.True(pv.Stages[1].AutoTarget);                   // 목표일을 적지 않았고 작업이 있다
            Assert.Equal(Day(10, 21), pv.Stages[1].TargetDate);     // 미리보기도 작업 기준 목표일을 보인다
            Assert.True(pv.Stages[2].Parallel);
            Assert.Equal(5, pv.Stages[1].GapWorkDays);              // 검수: 검토 목표일(10-14)부터 10-21 = 5 근무일
            Assert.Equal(5, pv.Stages[2].GapWorkDays);              // 교육(검수와 동시): 같은 기준일(10-14)부터 10-21 = 5 근무일
            Assert.Equal(7, pv.Stages[3].GapWorkDays);              // 설치: 묶음에서 가장 늦은 10-21 부터 10-30 = 7 근무일
            Assert.Equal(3, pv.Works.Count);
            Assert.Equal("현장 실사", pv.Works[1].Predecessor);
            Assert.True(pv.Works[0].NewOwner == false);
            Assert.Contains("붙여 넣은 단계 4개", pv.Note);
        }
    }

    [Fact]
    public async Task 등록_화면_CSV_붙여넣기로_과제를_만들면_기본_단계_대신_붙여_넣은_단계가_된다()
    {
        var id = await NewProjectAsync("CSV 등록", "물류 자동화", Paste);
        var v = await ViewAsync(id);
        Assert.Equal(new[] { "검토", "검수", "교육", "설치" }, v.Stages.Select(x => x.Name));
        Assert.Equal(Day(10, 14), v.Stages[0].TargetDate);
        Assert.False(v.Stages[0].AutoTarget);                    // 목표일을 적은 단계는 사람이 정한 것
        Assert.Equal(2, v.Stages[0].Works.Count);
        Assert.Equal(Day(10, 21), v.Stages[1].TargetDate);       // 작업 기준
        Assert.True(v.Stages[1].AutoTarget);
        Assert.Equal("inspection", v.Stages[1].Kind);
        Assert.Equal(3, v.Stages[1].CheckTotal);
        Assert.True(v.Stages[2].Parallel);
        Assert.Single(v.Links);                                  // 현장 실사 → 사양 검토
        // 담당을 비운 작업은 과제 담당(나)
        Assert.Equal("테스터", v.Stages[1].Works[0].Owner);
        Assert.Equal("김물류", v.Stages[0].Works.Single(w => w.Title == "현장 실사").Owner);
        var acts = await ActivitiesAsync(id);
        Assert.Contains(acts, a => a.Type == ActivityType.Created && a.Text.Contains("CSV 붙여넣기"));
        Assert.Contains(acts, a => a.Text.Contains("[검수] 목표일 설정 2026-10-21 (작업 일정에 맞춤)"));
        Assert.Equal(3, acts.Count(a => a.Type == ActivityType.WorkAdded));
    }

    [Fact]
    public async Task 등록_화면_CSV_붙여넣기는_문제가_있으면_줄마다_알리고_등록을_거절한다()
    {
        var bad = "검토,,,,2026-10-40,,\n검토,작업,,2026-10-02,2026-10-01,,\n검토,후속,,2026-10-05,2026-10-06,없는 선행,\n";
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var pv = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), bad));
        Assert.Equal(3, pv.ErrorCount);
        Assert.Contains(pv.Stages[0].Issues, i => i.Message.Contains("날짜로 읽지"));
        Assert.Contains(pv.Works[0].Issues, i => i.Column == "목표 종료");
        Assert.Contains(pv.Works[1].Issues, i => i.Column == "선행 작업명");

        var ex = await Assert.ThrowsAsync<RuleException>(() =>
            s.Projects.CreateAsync(new ProjectInput("거절", cat.Id, corp.Id, null, Day(10, 1), null, null, null, null, PasteText: bad)));
        Assert.Equal("pasteText", ex.Field);
        Assert.StartsWith("붙여넣기 1번째 줄", ex.Message);
        Assert.False(await s.Db.Projects.AnyAsync(p => p.Title == "거절"));

        // 첫 줄부터 단계가 없으면 그 줄이 문제다
        var noStage = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), ",작업만,,2026-10-02,2026-10-03,,"));
        Assert.Equal(1, noStage.Stages.Count(x => !x.Ok));
    }

    [Fact]
    public async Task 샘플_CSV_는_고른_유형의_기본_단계와_템플릿_작업으로_만든다()
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "용기");
        var text = await s.Projects.PasteSampleAsync(cat.Id, Day(10, 1));
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(ProjectService.PasteColumns.Header, lines[0]);
        Assert.StartsWith("검토,,,,", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("검토,현장 실사,"));
        Assert.Contains(lines, l => l.StartsWith("제작,시제품 검증,") && l.Contains(",시제품 제작,"));
        // 견본은 그대로 붙여 넣어도 문제가 없어야 한다
        var pv = await s.Projects.PreviewProjectPasteAsync(new ProjectPasteInput(cat.Id, Day(10, 1), text));
        Assert.Equal(0, pv.ErrorCount);
        Assert.Equal(6, pv.Stages.Count);

        // Simulation 유형: 모델링이 데이터 수집과 동시 진행이라 '동시' 칸이 채워진다
        var sim = await s.Db.Categories.FirstAsync(c => c.Name == "Simulation");
        Assert.Contains((await s.Projects.PasteSampleAsync(sim.Id, Day(10, 1))).Split('\n'), l => l.StartsWith("모델링,") && l.EndsWith(",동시"));
    }

    [Fact]
    public async Task 관리_CSV_붙여넣기는_미리보기에서_추가_이미_있음_문제를_나누고_적용은_한_번에_한다()
    {
        using var s = _db.NewScope(admin: true);
        var text = "법인,제품군,라인,층,국가,해상 운송일\n한국,,,,,\nCSV법인,C제품,1라인,1층,베트남,25\nCSV법인,C제품,1라인,2층,,\n,빈 법인,,,,\n";
        var pv = await s.Masters.PasteAsync(new MasterPasteInput("location", text));
        Assert.False(pv.Applied);
        Assert.Equal(new[] { "exists", "add", "add", "error" }, pv.Rows.Select(r => r.Action));
        Assert.Equal(1, pv.Errors);
        Assert.False(await s.Db.Locations.AnyAsync(l => l.Name == "CSV법인"));     // 미리보기는 저장하지 않는다

        // 문제가 있으면 적용도 거절하고 아무것도 넣지 않는다
        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.PasteAsync(new MasterPasteInput("location", text, Apply: true)));
        Assert.Contains("아무것도 추가하지 않았습니다", ex.Message);
        Assert.False(await s.Db.Locations.AnyAsync(l => l.Name == "CSV법인"));

        var good = text.Replace(",빈 법인,,,,\n", "");
        var applied = await s.Masters.PasteAsync(new MasterPasteInput("location", good, Apply: true));
        Assert.True(applied.Applied);
        Assert.Equal(2, applied.Added);
        Assert.Equal(1, applied.Skipped);
        var corp = await s.Db.Locations.AsNoTracking().SingleAsync(l => l.Name == "CSV법인");
        Assert.Equal(("베트남", 25, 1), (corp.Country, corp.OceanLeadDays, corp.Level));
        var floors = await s.Db.Locations.AsNoTracking().Where(l => l.Level == 4 && (l.Name == "1층" || l.Name == "2층")).ToListAsync();
        var line = await s.Db.Locations.AsNoTracking().SingleAsync(l => l.Level == 3 && l.Name == "1라인" && l.Parent!.Parent!.Id == corp.Id);
        Assert.Equal(2, floors.Count(f => f.ParentId == line.Id));
        Assert.StartsWith("2건을 추가했습니다", s.Masters.Notice);
        Assert.Contains(await s.Db.MasterChanges.AsNoTracking().ToListAsync(), c => c.Kind == "location" && c.Text == "계층 CSV 붙여넣기: 2건 추가");
    }

    [Fact]
    public async Task 관리_CSV_붙여넣기는_유형의_단계_체크_작업_템플릿과_조직_사용자를_만든다()
    {
        using var s = _db.NewScope(admin: true);
        var cat = string.Join("\n",
            "CSV유형,,물류 자동화,,,,,,,",
            "CSV유형,검토,일반,10,,현장 실사 완료;요구 사항 확정,,,,",
            "CSV유형,검토,,,,,현장 실사,0,3,",
            "CSV유형,검토,,,,,사양 검토,3,5,현장 실사",
            "CSV유형,검수,검수,5,,,,,,",
            "CSV유형,교육,일반,5,동시,,,,,",
            "CSV유형,검토,일반,,,현장 실사 완료,,,,");    // 이미 있는 단계·체크 → 건너뜀
        var r = await s.Masters.PasteAsync(new MasterPasteInput("category", cat, Apply: true));
        Assert.Equal(0, r.Errors);
        Assert.Equal("exists", r.Rows.Last().Action);
        var made = await s.Db.Categories.AsNoTracking().Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems)
            .Include(c => c.StageTemplates).ThenInclude(t => t.Works).AsSplitQuery().SingleAsync(c => c.Name == "CSV유형");
        Assert.Equal(CategoryKind.Automation, made.Kind);
        var review = made.StageTemplates.Single(t => t.Name == "검토");
        Assert.Equal((10, 2, 2), (review.DefaultWorkDays, review.CheckItems.Count, review.Works.Count));
        Assert.Equal(review.Works.Single(w => w.Title == "현장 실사").Id, review.Works.Single(w => w.Title == "사양 검토").PredecessorTemplateWorkId);
        Assert.Equal(StageKind.Inspection, made.StageTemplates.Single(t => t.Name == "검수").Kind);
        Assert.True(made.StageTemplates.Single(t => t.Name == "교육").Parallel);
        Assert.Equal(new[] { "검토", "검수", "교육" }, made.StageTemplates.OrderBy(t => t.SortOrder).Select(t => t.Name));

        // 모르는 종류·없는 선행은 문제
        var bad = await s.Masters.PasteAsync(new MasterPasteInput("category", "CSV유형,발주,이상한,,,,,,,\nCSV유형,검토,,,,,후속,0,1,없는 작업"));
        Assert.Equal(2, bad.Errors);

        // 사용자: 소속(그룹 › 파트)이 없으면 함께 만들고, 같은 이름은 건너뛴다. 메일이 틀리면 문제
        var users = "이름,그룹,파트,메일\nCSV사용자,CSV그룹,CSV파트,csv@company.com\n테스터,,,\n틀린메일,,,not-mail\n";
        var upv = await s.Masters.PasteAsync(new MasterPasteInput("user", users));
        Assert.Equal(new[] { "add", "exists", "error" }, upv.Rows.Select(x => x.Action));
        await s.Masters.PasteAsync(new MasterPasteInput("user", users.Replace("틀린메일,,,not-mail\n", ""), Apply: true));
        var u = await s.Db.Users.AsNoTracking().Include(x => x.OrgUnit).ThenInclude(o => o!.Parent).SingleAsync(x => x.Name == "CSV사용자");
        Assert.Equal(("csv@company.com", "CSV파트", "CSV그룹"), (u.Email, u.OrgUnit!.Name, u.OrgUnit.Parent!.Name));

        // 관리자가 아니면 거절
        using var plain = _db.NewScope("일반 사용자");
        await Assert.ThrowsAsync<RuleException>(() => plain.Masters.PasteAsync(new MasterPasteInput("vendor", "새 협력사")));
        Assert.Contains("협력사", string.Join(",", MasterService.PasteColumns("vendor")));
        Assert.StartsWith("법인,제품군,라인,층,국가,해상 운송일\n", MasterService.PasteSample("location", Day(10, 1)));
    }

    [Fact]
    public async Task 댓글의_이미지_참조는_이_과제의_이미지_첨부만_받는다()
    {
        var id = await NewProjectAsync("댓글 이미지");
        var other = await NewProjectAsync("다른 과제");
        using var s = _db.NewScope();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "gantt-paste-test-" + Guid.NewGuid().ToString("N")),
        }).Build();
        var files = new AttachmentService(s.Db, _db.Clock, s.Me, cfg, new TestEnv());
        // 1x1 PNG 머리만 있는 작은 바이트 — 형식은 확장자로 본다
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var imageId = await files.SaveAsync(id, null, "캡처.png", png.Length, new MemoryStream(png));
        var textId = await files.SaveAsync(id, null, "메모.txt", 3, new MemoryStream(new byte[] { 65, 66, 67 }));
        var otherImage = await files.SaveAsync(other, null, "남의 캡처.png", png.Length, new MemoryStream(png));
        Assert.True(imageId > 0);

        await s.Projects.CommentAsync(new CommentInput(id, null, null, $"현장 사진 {CommentText.Image(imageId)} 확인"));
        var saved = await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == id && a.Type == ActivityType.Comment).OrderByDescending(a => a.Id).FirstAsync();
        Assert.Equal($"현장 사진 [이미지:{imageId}] 확인", saved.Text);

        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, $"남의 것 {CommentText.Image(otherImage)}")));
        Assert.Equal("text", ex.Field);
        ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, $"글 파일 {CommentText.Image(textId)}")));
        Assert.Contains("이미지가 아니라", ex.Message);
        await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, null, "[이미지:999999]")));

        // 알림 한 줄은 평문
        Assert.Equal("댓글: 현장 사진 (이미지) 확인", ActivityLog.Summary(saved, 1));
    }

    private sealed class TestEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GanttScheduler";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
