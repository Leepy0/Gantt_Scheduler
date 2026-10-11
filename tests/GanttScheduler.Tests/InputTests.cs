using ClosedXML.Excel;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 입력 부담 줄이기: 날짜 글자 해석, 템플릿 작업 일정 규칙
public class InputRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Theory]
    [InlineData("2026-10-12", 2026, 10, 12)]
    [InlineData("2026.10.12", 2026, 10, 12)]
    [InlineData("2026.10.12.", 2026, 10, 12)]
    [InlineData("2026/1/5", 2026, 1, 5)]
    [InlineData("2026년 10월 12일", 2026, 10, 12)]
    [InlineData("2026-10-12 (월)", 2026, 10, 12)]
    [InlineData("20261012", 2026, 10, 12)]
    [InlineData("10/12", 2026, 10, 12)]
    [InlineData("10.12", 2026, 10, 12)]
    [InlineData("10월 12일", 2026, 10, 12)]
    [InlineData("10/12/2027", 2027, 10, 12)]
    [InlineData("2026-10-12 0:00:00", 2026, 10, 12)]
    public void 흔한_날짜_형식을_읽는다(string text, int y, int m, int d)
    {
        Assert.True(InputText.TryDate(text, Today, out var date), text);
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("내일")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("13/45")]
    [InlineData("12345")]
    public void 날짜가_아니면_읽지_않는다(string text) => Assert.False(InputText.TryDate(text, Today, out _));

    [Fact]
    public void 붙여넣은_줄은_TAB_없으면_쉼표로_나눈다()
    {
        Assert.Equal(new[] { "A", "김", "10/12" }, InputText.Cells("A\t김\t10/12"));
        Assert.Equal(new[] { "A", "김", "10/12" }, InputText.Cells("A, 김, 10/12"));
        Assert.Equal(new[] { (1, "a"), (3, "b") }, InputText.Lines("a\r\n\r\nb\n  \n"));
    }

    [Fact]
    public void 단계_시작_기준일은_첫_단계는_착수일_이후는_앞_단계_목표일_다음_근무일이다()
    {
        var cal = new WorkCalendar(new[] { new DateOnly(2026, 10, 9) });
        // 착수 10-10(토) → 첫 단계 10-12(월). 앞 단계 목표 10-16(금) → 10-19(월). 목표일이 없는 단계는 앞 기준을 그대로 잇는다
        var starts = Rules.StageStarts(new DateOnly(2026, 10, 10), new DateOnly?[] { new(2026, 10, 16), null, new(2026, 10, 8) }, cal);
        Assert.Equal(new[] { new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19) }, starts);

        // 시작 +2근무일, 기간 3근무일: 10-12 → 10-14(수) ~ 10-16(금). 한글날(10-09)을 건너뛴다
        Assert.Equal((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 16)), Rules.PlaceTemplateWork(new DateOnly(2026, 10, 12), 2, 3, cal));
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12)), Rules.PlaceTemplateWork(new DateOnly(2026, 10, 9), 0, 1, cal));
        Assert.Throws<RuleException>(() => Rules.ValidateTemplateWork(-1, 1));
        Assert.Throws<RuleException>(() => Rules.ValidateTemplateWork(0, 0));
        Rules.ValidateTemplateWork(0, 1);
    }
}

// 붙여넣기 등록·템플릿 작업·복제 위치·Excel 가져오기 (DB)
public class InputServiceTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public InputServiceTests(TestDb db) => _db = db;

    private async Task<(int Id, int StageId)> NewProjectAsync(string title, string category = "물류", bool templateWorks = true, List<StageTargetInput>? targets = null)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, Day(10, 12), null, null, null, null,
            StageTargets: targets, WithTemplateWorks: templateWorks));
        var view = (await s.Projects.ViewAsync(id))!;
        return (id, view.Stages[0].Id);
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    private async Task<(int Works, int Activities, int Users, int Links)> CountsAsync(int projectId)
    {
        using var s = _db.NewScope();
        return (await s.Db.WorkItems.CountAsync(w => w.Stage!.ProjectId == projectId),
                await s.Db.Activities.CountAsync(a => a.ProjectId == projectId),
                await s.Db.Users.CountAsync(),
                await s.Db.WorkLinks.CountAsync(l => l.ProjectId == projectId));
    }

    // ── 붙여넣기 ─────────────────────────────────────────────────

    [Fact]
    public async Task 붙여넣기_미리보기는_형식을_해석하고_줄마다_문제를_알리며_저장하지_않는다()
    {
        var (id, stageId) = await NewProjectAsync("붙여넣기 미리보기");
        var before = await CountsAsync(id);
        var text = string.Join("\n",
            "작업명\t담당\t목표 시작\t목표 종료\t선행 작업명",          // 머리글은 건너뛴다
            "현장 실사\t김물류\t2026-10-12\t2026.10.14",
            "사양 검토\t새사람\t10/15\t10/21\t현장 실사",               // 목록에 없는 담당 → 새로 등록됨
            "잘못된 날짜\t김물류\t2026-10-40\t2026-10-41",
            "순서 거꾸로\t\t2026-10-20\t2026-10-19",
            "범위 밖\t\t2037-10-12\t2037-10-13",
            "모르는 선행\t\t2026-10-20\t2026-10-21\t없는 작업",
            "너무 이른 시작\t\t2026-10-13\t2026-10-13\t현장 실사",        // 선행이 10-14 에 끝나므로 10-15 이후
            "담당 없이\t2026-10-22\t2026-10-23");                       // 둘째 칸이 날짜면 담당 칸을 뺀 것으로 본다

        using var s = _db.NewScope();
        var pv = await s.Projects.PreviewWorksAsync(new PreviewWorksInput(stageId, text));
        Assert.Equal(8, pv.Rows.Count);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8, 9 }, pv.Rows.Select(r => r.Line));
        var ok = pv.Rows[0];
        Assert.True(ok.Ok);
        Assert.Equal((Day(10, 12), Day(10, 14), 3), (ok.PlanStart!.Value, ok.PlanEnd!.Value, ok.WorkDays!.Value));
        Assert.True(pv.Rows[1].Ok);
        Assert.True(pv.Rows[1].NewOwner);
        Assert.Equal(new[] { "새사람" }, pv.NewOwners);
        Assert.Contains(pv.Rows[2].Issues, i => i.Column == "목표 시작" && i.Message.Contains("날짜로 읽지"));
        Assert.Contains(pv.Rows[3].Issues, i => i.Column == "목표 종료" && i.Message.Contains("늦습니다"));
        Assert.Contains(pv.Rows[4].Issues, i => i.Column == "목표 시작" && i.Message.Contains("사이로"));
        Assert.Contains(pv.Rows[5].Issues, i => i.Column == "선행 작업명" && i.Message.Contains("찾지 못했습니다"));
        Assert.Contains(pv.Rows[6].Issues, i => i.Column == "목표 시작" && i.Message.Contains("2026-10-15 이후"));
        Assert.True(pv.Rows[7].Ok);
        Assert.Null(pv.Rows[7].Owner);
        Assert.Equal(5, pv.ErrorCount);

        Assert.Equal(before, await CountsAsync(id));
    }

    [Fact]
    public async Task 붙여넣기_저장은_하나라도_틀리면_아무것도_남기지_않고_맞으면_한_묶음으로_저장한다()
    {
        var (id, stageId) = await NewProjectAsync("붙여넣기 저장");
        var view = await ViewAsync(id);
        var before = await CountsAsync(id);
        var rows = new List<WorkRowInput>
        {
            new("설계", "김물류", "2026-10-12", "2026-10-14", null, 1),
            new("제작", "붙여넣기신규", "2026-10-15", "2026-10-20", "설계", 2),
            new("검사", null, "10/21", null, "제작", 3),                         // 종료를 비우면 하루짜리
        };

        // 한 줄이라도 틀리면(순환) 아무것도 저장하지 않는다 — 작업·연결·활동·새 사용자 모두
        var cyclic = rows.Select(r => r.Title == "설계" ? r with { Predecessor = "검사" } : r).ToList();
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.AddWorksAsync(new AddWorksInput(stageId, cyclic, view.Stages[0].Version)));
            Assert.Contains("순환", ex.Message);
            Assert.Equal("rows", ex.Field);
        }
        Assert.Equal(before, await CountsAsync(id));

        // 그 사이 단계가 바뀌었으면(버전) 409
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.AddWorksAsync(new AddWorksInput(stageId, rows, view.Stages[0].Version + 1)));

        using (var s = _db.NewScope())
            await s.Projects.AddWorksAsync(new AddWorksInput(stageId, rows, view.Stages[0].Version));

        var after = await CountsAsync(id);
        Assert.Equal(before.Works + 3, after.Works);
        Assert.Equal(before.Users + 1, after.Users);
        Assert.Equal(before.Links + 2, after.Links);
        var works = (await ViewAsync(id)).Stages[0].Works;
        var check = works.Single(w => w.Title == "검사");
        Assert.Equal((Day(10, 21), Day(10, 21)), (check.PlanStart, check.PlanEnd));
        Assert.Equal("붙여넣기신규", works.Single(w => w.Title == "제작").Owner);

        using (var s = _db.NewScope())
        {
            var acts = await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == id).OrderBy(a => a.Id).Skip(before.Activities).ToListAsync();
            Assert.Equal(3, acts.Count(a => a.Type == ActivityType.WorkAdded));
            Assert.Equal(2, acts.Count(a => a.Type == ActivityType.LinkChanged));
            Assert.Single(acts.Select(a => a.BatchId).Distinct());
            Assert.NotNull(acts[0].BatchId);
            Assert.All(acts, a => Assert.Equal(stageId, a.StageId));
        }
        await ScheduleAssert.AssertSchedulableAsync(_db, id);
    }

    [Fact]
    public async Task 붙여넣기_선행은_과제에_이미_있는_작업도_이름으로_찾는다()
    {
        var (id, stageId) = await NewProjectAsync("붙여넣기 기존 선행");
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, "기존 작업", null, null, Day(10, 12), Day(10, 13), null, null, 0, null, null));
        using (var s = _db.NewScope())
        {
            var pv = await s.Projects.PreviewWorksAsync(new PreviewWorksInput(stageId, "후속\t\t10/14\t10/15\t기존 작업\n이른 후속\t\t10/13\t10/13\t기존 작업"));
            Assert.True(pv.Rows[0].Ok);
            Assert.False(pv.Rows[1].Ok);
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.PreviewWorksAsync(new PreviewWorksInput(stageId, "  \n ")));
        }
        using (var s = _db.NewScope())
            await s.Projects.AddWorksAsync(new AddWorksInput(stageId, new() { new("후속", null, "10/14", "10/15", "기존 작업", 1) }));
        var view = await ViewAsync(id);
        var pre = view.Stages[0].Works.Single(w => w.Title == "기존 작업").Id;
        var next = view.Stages[0].Works.Single(w => w.Title == "후속").Id;
        Assert.Contains(view.Links!, l => l.From == pre && l.To == next);
    }

    // ── 유형 템플릿 작업 ─────────────────────────────────────────

    [Fact]
    public async Task 등록하면_유형_템플릿_작업을_근무일로_만들고_선행을_잇는다()
    {
        // 용기(샘플): 검토(기본 5) → 품의(10) → 발주(5) → 제작(20)…, 검토: 현장 실사(0,3) → 용기 사양 검토(3,5), 제작: 시제품 제작(0,10) → 시제품 검증(10,5)
        var (id, _) = await NewProjectAsync("템플릿 작업 생성", "용기");
        var view = await ViewAsync(id);
        var review = view.Stages.Single(s => s.Name == "검토").Works;
        Assert.Equal(new[] { "현장 실사", "용기 사양 검토" }, review.Select(w => w.Title));
        Assert.Equal((Day(10, 12), Day(10, 14)), (review[0].PlanStart, review[0].PlanEnd));
        Assert.Equal((Day(10, 15), Day(10, 21)), (review[1].PlanStart, review[1].PlanEnd));
        // 제작은 4번째 단계 — 목표일을 정하지 않았으므로 기본 기간 초안(검토 10-19, 품의 11-02, 발주 11-09)의 다음 근무일 11-10 부터
        var make = view.Stages.Single(s => s.Name == "제작").Works;
        Assert.Equal((Day(11, 10), Day(11, 23)), (make[0].PlanStart, make[0].PlanEnd));
        Assert.Equal((Day(11, 24), Day(11, 30)), (make[1].PlanStart, make[1].PlanEnd));
        Assert.All(review.Concat(make), w => Assert.Equal(view.Owner, w.Owner));
        Assert.Contains(view.Links!, l => l.From == review[0].Id && l.To == review[1].Id);
        Assert.Contains(view.Links!, l => l.From == make[0].Id && l.To == make[1].Id);
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        using var s = _db.NewScope();
        var added = await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == id && a.Type == ActivityType.WorkAdded).ToListAsync();
        Assert.Equal(4, added.Count);
        Assert.All(added, a => Assert.Contains("유형 템플릿", a.Text));
    }

    [Fact]
    public async Task 등록에서_정한_목표일이_템플릿_작업의_단계_시작이_되고_끄면_만들지_않는다()
    {
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.Include(c => c.StageTemplates).FirstAsync(c => c.Name == "용기");
            var order = cat.StageTemplates.OrderBy(t => t.SortOrder).ToList();
            // 발주 목표일을 11-20 으로 정하면 제작 작업은 11-23(월)부터
            var (id, _) = await NewProjectAsync("템플릿 작업 목표일 기준", "용기", targets: new() { new(order[2].Id, Day(11, 20)) });
            var make = (await ViewAsync(id)).Stages.Single(x => x.Name == "제작").Works;
            Assert.Equal(Day(11, 23), make[0].PlanStart);
        }
        var (off, _) = await NewProjectAsync("템플릿 작업 끔", "용기", templateWorks: false);
        Assert.All((await ViewAsync(off)).Stages, x => Assert.Empty(x.Works));
    }

    [Fact]
    public async Task 관리에서_작업_템플릿을_편집하고_어긋난_선행은_등록할_때_밀어_만든다()
    {
        int stageTemplateId;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.Include(c => c.StageTemplates).FirstAsync(c => c.Name == "Simulation");
            stageTemplateId = cat.StageTemplates.OrderBy(t => t.SortOrder).First().Id;     // 데이터 수집
            await s.Masters.SaveAsync(new MasterSave("category", 4, null, stageTemplateId, "자료 요청", null, null, null, null, null, null,
                OffsetWorkDays: 0, DurationWorkDays: 3));
        }
        int first, second;
        using (var s = _db.NewScope())
        {
            first = (await s.Db.TemplateWorks.FirstAsync(w => w.StageTemplateId == stageTemplateId && w.Title == "자료 요청")).Id;
            // 선행(자료 요청, 3일)보다 먼저 시작하도록 적은 템플릿 — 등록할 때 선행 뒤로 밀린다
            await s.Masters.SaveAsync(new MasterSave("category", 4, null, stageTemplateId, "자료 정리", null, null, null, null, null, null,
                OffsetWorkDays: 1, DurationWorkDays: 2, PredecessorId: first));
        }
        using (var s = _db.NewScope())
        {
            second = (await s.Db.TemplateWorks.FirstAsync(w => w.Title == "자료 정리")).Id;
            // 범위·순환 검사
            await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(new MasterSave("category", 4, second, stageTemplateId, "자료 정리", null, null, null, null, null, null,
                OffsetWorkDays: 0, DurationWorkDays: 0)));
            var cycle = await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(new MasterSave("category", 4, first, stageTemplateId, "자료 요청", null, null, null, null, null, null,
                OffsetWorkDays: 0, DurationWorkDays: 3, PredecessorId: second)));
            Assert.Contains("순환", cycle.Message);
            var nodes = await s.Masters.ListAsync("category");
            var node = nodes.Single(n => n.Level == 4 && n.Id == second);
            Assert.Equal((stageTemplateId, 1, 2, (int?)first), (node.ParentId!.Value, node.OffsetWorkDays!.Value, node.DurationWorkDays!.Value, node.PredecessorId));
        }

        var (id, _) = await NewProjectAsync("관리 템플릿 작업", "Simulation");
        var works = (await ViewAsync(id)).Stages[0].Works;
        Assert.Equal((Day(10, 12), Day(10, 14)), (works[0].PlanStart, works[0].PlanEnd));
        Assert.Equal((Day(10, 15), Day(10, 16)), (works[1].PlanStart, works[1].PlanEnd));     // 10-13 시작 → 선행 뒤 10-15 로
        await ScheduleAssert.AssertSchedulableAsync(_db, id);

        // 선행으로 쓰인 템플릿을 지우면 뒤 작업은 선행 없이 남고, 단계 템플릿을 지우면 작업 템플릿도 함께 지워진다
        using (var s = _db.NewScope())
            await s.Masters.ToggleAsync(new MasterRef("category", 4, first, 0));
        using (var s = _db.NewScope())
        {
            Assert.Null((await s.Db.TemplateWorks.SingleAsync(w => w.Id == second)).PredecessorTemplateWorkId);
            await s.Masters.ToggleAsync(new MasterRef("category", 4, second, 0));
            Assert.False(await s.Db.TemplateWorks.AnyAsync(w => w.StageTemplateId == stageTemplateId));
        }
        // 이미 만든 과제의 작업은 그대로다(등록 시점 복사)
        Assert.Equal(2, (await ViewAsync(id)).Stages[0].Works.Count);
    }

    [Fact]
    public async Task 단계_템플릿을_지우면_그_아래_작업_템플릿도_지운다()
    {
        int templateId;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            await s.Masters.SaveAsync(new MasterSave("category", 2, null, cat.Id, "임시 단계", null, null, null, null, null, null));
        }
        using (var s = _db.NewScope())
        {
            templateId = (await s.Db.StageTemplates.FirstAsync(t => t.Name == "임시 단계")).Id;
            await s.Masters.SaveAsync(new MasterSave("category", 4, null, templateId, "가", null, null, null, null, null, null, OffsetWorkDays: 0, DurationWorkDays: 1));
        }
        using (var s = _db.NewScope())
        {
            var a = await s.Db.TemplateWorks.FirstAsync(w => w.StageTemplateId == templateId);
            await s.Masters.SaveAsync(new MasterSave("category", 4, null, templateId, "나", null, null, null, null, null, null, OffsetWorkDays: 1, DurationWorkDays: 1, PredecessorId: a.Id));
        }
        using (var s = _db.NewScope())
            await s.Masters.ToggleAsync(new MasterRef("category", 2, templateId, 0));
        using (var s = _db.NewScope())
        {
            Assert.False(await s.Db.StageTemplates.AnyAsync(t => t.Id == templateId));
            Assert.False(await s.Db.TemplateWorks.AnyAsync(w => w.StageTemplateId == templateId));
        }
    }

    // ── 복제 위치·담당 ───────────────────────────────────────────

    [Fact]
    public async Task 복제는_위치와_담당을_정할_수_있고_기본은_원본_위치와_나다()
    {
        var (id, _) = await NewProjectAsync("복제 위치 원본");
        int vnLine, inactive;
        using (var s = _db.NewScope())
        {
            vnLine = (await s.Db.Locations.FirstAsync(l => l.Name == "2라인" && l.Parent!.Name == "세탁기" && l.Parent.Parent!.Name == "베트남")).Id;
            var off = new Location { Name = "중지 라인", Level = 2, ParentId = (await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국")).Id, IsActive = false };
            s.Db.Locations.Add(off);
            await s.Db.SaveChangesAsync();
            inactive = off.Id;
        }
        int same, moved;
        using (var s = _db.NewScope())
            same = await s.Projects.CloneAsync(new CloneInput(id, "복제 기본", Day(10, 12), true));
        using (var s = _db.NewScope())
            moved = await s.Projects.CloneAsync(new CloneInput(id, "복제 베트남", Day(10, 12), true, vnLine, "복제새담당"));
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CloneAsync(new CloneInput(id, "복제 중지", Day(10, 12), true, inactive)));
            Assert.Equal("locationId", ex.Field);
        }

        var src = await ViewAsync(id);
        var a = await ViewAsync(same);
        Assert.Equal((src.CorporationId, src.LocationId), (a.CorporationId, a.LocationId));
        Assert.Equal("테스터", a.Owner);
        var b = await ViewAsync(moved);
        Assert.Equal("베트남", b.Corporation);
        Assert.Equal(vnLine, b.LocationId);
        Assert.Equal("복제새담당", b.Owner);
    }

    // ── Excel 가져오기 ───────────────────────────────────────────

    private async Task<ImportBook> BookAsync(string[][] projects, string[][]? works = null)
    {
        byte[] template;
        using (var s = _db.NewScope()) template = new ExcelExporter().ImportTemplate(await s.Projects.ImportListsAsync());
        using var wb = new XLWorkbook(new MemoryStream(template));
        void Fill(string sheet, string[][] rows)
        {
            var ws = wb.Worksheet(sheet);
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    ws.Cell(r + 2, c + 1).Value = rows[r][c];
        }
        Fill("과제", projects);
        if (works != null) Fill("작업", works);
        // 날짜 칸 하나는 Excel 날짜 값으로 넣어 본다(셀 서식이 날짜여도 읽는다)
        if (projects.Length > 0 && projects[0].Length > 5 && InputText.TryDate(projects[0][5], Day(10, 9), out var d))
            wb.Worksheet("과제").Cell(2, 6).Value = d.ToDateTime(TimeOnly.MinValue);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return new ProjectImporter().Read(ms);
    }

    private async Task<int> ProjectCountAsync()
    {
        using var s = _db.NewScope();
        return await s.Db.Projects.CountAsync();
    }

    [Fact]
    public async Task 가져오기_양식은_과제_작업_목록_안내_시트를_싣는다()
    {
        using var s = _db.NewScope();
        var bytes = new ExcelExporter().ImportTemplate(await s.Projects.ImportListsAsync());
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(new[] { "과제", "작업", "목록", "안내" }, wb.Worksheets.Select(w => w.Name));
        Assert.Equal(ProjectService.ImportColumns.Project, Enumerable.Range(1, 9).Select(c => wb.Worksheet("과제").Cell(1, c).GetString()));
        Assert.Equal(ProjectService.ImportColumns.Work, Enumerable.Range(1, 7).Select(c => wb.Worksheet("작업").Cell(1, c).GetString()));
        var list = wb.Worksheet("목록");
        Assert.Contains("용기", list.Column(1).CellsUsed().Select(c => c.GetString()));
        Assert.Contains(list.Column(7).CellsUsed(), c => c.GetString() == "세탁기>2라인");
        Assert.Contains("김물류", list.Column(9).CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public async Task 가져오기_미리보기는_행과_열마다_문제를_알리고_아무것도_저장하지_않는다()
    {
        var before = await ProjectCountAsync();
        var book = await BookAsync(new[]
        {
            new[] { "가져오기 정상", "물류", "베트남", "세탁기>2라인", "김물류", "2026-10-12", "대명오토메이션", "설명", "검토=2026-10-20; 보고=10/30" },
            new[] { "가져오기 유형 오류", "없는유형", "한국", "", "", "2026-10-12" },
            new[] { "가져오기 위치 오류", "물류", "한국", "없는 제품군", "", "2026-10-12" },
            new[] { "가져오기 날짜 오류", "물류", "한국", "", "", "언젠가" },
            new[] { "가져오기 단계 오류", "물류", "한국", "", "", "2026-10-12", "", "", "없는단계=2026-10-20" },
            new[] { "가져오기 정상", "물류", "한국", "", "", "2026-10-12" },
        }, new[]
        {
            new[] { "가져오기 정상", "검토", "자료 조사", "가져오기신규", "2026-10-12", "2026-10-13" },
            new[] { "가져오기 정상", "없는단계", "작업", "", "2026-10-12", "2026-10-13" },
            new[] { "없는 과제", "검토", "작업", "", "2026-10-12", "2026-10-13" },
            new[] { "가져오기 정상", "검토", "후속", "", "2026-10-13", "2026-10-13", "자료 조사" },
        });
        using var s = _db.NewScope();
        var pv = await s.Projects.PreviewImportAsync(book);
        Assert.False(pv.Ok);
        bool Has(string sheet, int row, string column) => pv.Issues.Any(i => i.Sheet == sheet && i.Row == row && i.Column == column);
        Assert.True(Has("과제", 3, "유형"));
        Assert.True(Has("과제", 4, "위치"));
        Assert.True(Has("과제", 5, "착수일"));
        Assert.True(Has("과제", 6, "단계 목표일"));
        Assert.True(Has("과제", 7, "과제명"));          // 같은 과제명
        Assert.True(Has("작업", 3, "단계명"));
        Assert.True(Has("작업", 4, "과제명"));
        Assert.True(Has("작업", 5, "목표 시작"));       // 선행(10-13 종료)보다 이른 시작
        Assert.DoesNotContain(pv.Issues, i => i.Row == 2);
        var okRow = pv.Projects.Single(p => p.Row == 2);
        Assert.True(okRow.Ok);
        Assert.Equal("베트남 > 세탁기 > 2라인", okRow.Location);
        Assert.Equal((3, 2), (okRow.Stages, okRow.Targets));
        Assert.Equal(("김물류", false), (okRow.Owner, okRow.NewOwner));       // 기존 사용자 담당은 그 이름으로(올린 사람이 아니라)
        Assert.Equal(new[] { "가져오기신규" }, pv.NewUsers);

        // 문제가 있으면 가져오기도 거절 — 정상 행도 저장하지 않는다
        using (var s2 = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s2.Projects.ImportAsync(book, confirmNewUsers: true));
        Assert.Equal(before, await ProjectCountAsync());
        using (var s3 = _db.NewScope()) Assert.False(await s3.Db.Users.AnyAsync(u => u.Name == "가져오기신규"));
    }

    [Fact]
    public async Task 가져오기는_새_담당자를_확인받고_모든_과제를_한_번에_등록한다()
    {
        var before = await ProjectCountAsync();
        var book = await BookAsync(new[]
        {
            new[] { "가져오기 등록 A", "물류", "베트남", "세탁기>2라인", "가져오기담당", "2026-10-12", "대명오토메이션", "설명 A", "검토=2026-10-20; 보고=10/30" },
            new[] { "가져오기 등록 B", "용기", "한국", "냉장고>3라인>1F", "", "2026.10.12" },
        }, new[]
        {
            new[] { "가져오기 등록 A", "검토", "자료 조사", "가져오기담당", "2026-10-12", "2026-10-14" },
            new[] { "가져오기 등록 A", "검토", "정리", "김물류", "2026-10-15", "", "자료 조사" },
        });

        using (var s = _db.NewScope())
        {
            var pv = await s.Projects.PreviewImportAsync(book);
            Assert.True(pv.Ok, string.Join("\n", pv.Issues.Select(i => $"{i.Sheet} {i.Row} {i.Column} {i.Message}")));
            Assert.Equal(new[] { "가져오기담당" }, pv.NewUsers);
            Assert.Equal(4, pv.Projects.Single(p => p.Title == "가져오기 등록 B").Works);    // 용기 템플릿 작업
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.ImportAsync(book, confirmNewUsers: false));
            Assert.Equal("confirmNewUsers", ex.Field);
        }
        Assert.Equal(before, await ProjectCountAsync());

        ImportResult result;
        using (var s = _db.NewScope()) result = await s.Projects.ImportAsync(book, confirmNewUsers: true, "계획.xlsx");
        Assert.Equal((2, 6, 1), (result.Projects, result.Works, result.NewUsers));
        Assert.Equal(before + 2, await ProjectCountAsync());

        var a = await ViewAsync(result.Ids[0]);
        Assert.Equal(("가져오기 등록 A", "가져오기담당", "베트남", Day(10, 12), "대명오토메이션"), (a.Title, a.Owner, a.Corporation, a.StartDate, a.Vendor));
        Assert.Equal(new DateOnly?[] { Day(10, 20), Day(10, 30), null }, a.Stages.Select(x => x.TargetDate));
        // 작업 시트에 줄이 있는 과제는 템플릿 작업 대신 그 줄로 — 물류에는 템플릿이 없으므로 두 줄만
        Assert.Equal(new[] { "자료 조사", "정리" }, a.Stages[0].Works.Select(w => w.Title));
        Assert.Equal((Day(10, 15), Day(10, 15)), (a.Stages[0].Works[1].PlanStart, a.Stages[0].Works[1].PlanEnd));
        Assert.Single(a.Links!);
        var b = await ViewAsync(result.Ids[1]);
        Assert.Equal("테스터", b.Owner);
        Assert.Equal("1F", b.LocationPath.Split(" · ").Last());
        await ScheduleAssert.AssertSchedulableAsync(_db, a.Id);

        using var s4 = _db.NewScope();
        var created = await s4.Db.Activities.AsNoTracking().Where(x => x.ProjectId == a.Id && x.Type == ActivityType.Created).SingleAsync();
        Assert.Equal("과제 등록 (Excel 가져오기: 계획.xlsx)", created.Text);
        Assert.Equal(2, await s4.Db.Activities.CountAsync(x => x.ProjectId == a.Id && x.Text.Contains("목표일 설정")));
    }

    [Fact]
    public async Task 담당_이름은_대소문자만_다르면_기존_사용자로_보고_미리보기와_저장이_같다()
    {
        using (var s = _db.NewScope())
        {
            s.Db.Users.Add(new AppUser { Name = "CaseOwner", CreatedAt = _db.Clock.UtcNow });
            await s.Db.SaveChangesAsync();
        }
        var book = await BookAsync(new[] { new[] { "대소문자 담당 과제", "물류", "한국", "", "caseowner", "2026-10-12" } },
            new[] { new[] { "대소문자 담당 과제", "검토", "조사", "CASEOWNER", "2026-10-12", "2026-10-13" } });
        int id;
        using (var s = _db.NewScope())
        {
            var pv = await s.Projects.PreviewImportAsync(book);
            Assert.True(pv.Ok, string.Join("\n", pv.Issues.Select(i => i.Message)));
            Assert.Empty(pv.NewUsers);
            Assert.Equal(("CaseOwner", false), (pv.Projects[0].Owner, pv.Projects[0].NewOwner));
            Assert.False(pv.Works[0].NewOwner);
            // 새 담당자가 없으니 확인 없이 가져온다 — 사용자도 새로 생기지 않는다
            id = (await s.Projects.ImportAsync(book, confirmNewUsers: false)).Ids[0];
        }
        using (var s = _db.NewScope())
            Assert.Equal(1, await s.Db.Users.CountAsync(u => u.Name.ToLower() == "caseowner"));
        var v = await ViewAsync(id);
        Assert.Equal("CaseOwner", v.Owner);
        Assert.Equal("CaseOwner", v.Stages[0].Works[0].Owner);
    }

    [Fact]
    public void 가져오기_파일은_먼_칸이_채워져_있어도_열과_줄을_상한까지만_읽는다()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("과제");
        for (var c = 0; c < ProjectService.ImportColumns.Project.Length; c++) ws.Cell(1, c + 1).Value = ProjectService.ImportColumns.Project[c];
        for (var r = 2; r <= ProjectService.MaxImportProjects + 6; r++) ws.Cell(r, 1).Value = $"과제 {r}";
        ws.Cell(1048576, 16384).Value = "x";        // 이 칸 하나로 범위가 시트 끝까지 넓어진다
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var book = new ProjectImporter().Read(ms);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"읽기가 너무 오래 걸림: {watch.Elapsed}");
        var sheet = book.Projects!;
        Assert.Equal(ProjectImporter.MaxColumns, sheet.Headers.Count);
        Assert.Equal(ProjectService.MaxImportProjects, sheet.Rows.Count);           // 상한까지만 칸을 읽는다
        Assert.Equal(ProjectService.MaxImportProjects + 6, sheet.TotalRows);        // 나머지는 센다(먼 칸 줄 포함)
    }

    [Fact]
    public async Task 가져오기는_줄_상한을_넘으면_읽은_줄_수와_함께_나눠_올리라고_알린다()
    {
        var rows = Enumerable.Range(2, 3).Select(r => new ImportRow(r, new() { $"과제 {r}", "물류", "한국", "", "", "2026-10-12" })).ToList();
        var sheet = new ImportSheet("과제", ProjectService.ImportColumns.Project.ToList(), rows, ProjectService.MaxImportProjects + 50);
        using var s = _db.NewScope();
        var pv = await s.Projects.PreviewImportAsync(new ImportBook(sheet, null));
        Assert.Contains(pv.Issues, i => i.Message.Contains($"지금 {ProjectService.MaxImportProjects + 50}줄"));
    }

    [Fact]
    public async Task 가져오기는_과제_시트와_필수_열이_없으면_알린다()
    {
        using var s = _db.NewScope();
        var none = await s.Projects.PreviewImportAsync(new ImportBook(null, null));
        Assert.Contains(none.Issues, i => i.Message.Contains("'과제' 시트가 없습니다"));
        var noCol = await s.Projects.PreviewImportAsync(new ImportBook(new ImportSheet("과제", new() { "과제명", "유형" }, new()), null));
        Assert.Contains(noCol.Issues, i => i.Column == "법인");
        var empty = await s.Projects.PreviewImportAsync(new ImportBook(new ImportSheet("과제", ProjectService.ImportColumns.Project.ToList(), new() { new(2, new() { "", "" }) }), null));
        Assert.Contains(empty.Issues, i => i.Message.Contains("가져올 과제가 없습니다"));
        Assert.Throws<RuleException>(() => new ProjectImporter().Read(new MemoryStream(new byte[] { 1, 2, 3 })));
    }
}
