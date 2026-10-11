using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GanttScheduler.Tests;

// 데이터 일관성: 변경마다 활동·버전 규칙, 기준 정보 변경이 기존 과제를 깨뜨리지 않음, 입력 길이·소속 검사
public class IntegrityTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);

    public IntegrityTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string category = "물류", string? owner = null)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == category);
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, owner, Day(10, 1), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    private async Task<List<Activity>> ActivitiesAsync(int projectId)
    {
        using var s = _db.NewScope();
        return await s.Db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.Id).ToListAsync();
    }

    // ── 단계 순서 ──

    [Fact]
    public async Task 단계_순서를_바꾸면_활동을_남기고_두_단계의_버전을_확인하고_올린다()
    {
        var id = await NewProjectAsync("테스트 순서 활동");
        var st = (await ViewAsync(id)).Stages;      // 검토 · 보고 · 적용

        // 옛 버전·엉뚱한 이웃으로 보내면 409 (아무것도 바뀌지 않음)
        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.MoveStageAsync(st[1].Id, -1, st[1].Version + 1, st[0].Id, st[0].Version));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.MoveStageAsync(st[1].Id, -1, st[1].Version, st[2].Id, st[2].Version));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.MoveStageAsync(st[1].Id, -1, st[1].Version, st[0].Id, st[0].Version + 1));
        }
        Assert.Equal(new[] { "검토", "보고", "적용" }, (await ViewAsync(id)).Stages.Select(x => x.Name).ToArray());

        using (var s = _db.NewScope())
            await s.Projects.MoveStageAsync(st[1].Id, -1, st[1].Version, st[0].Id, st[0].Version);

        var after = await ViewAsync(id);
        Assert.Equal(new[] { "보고", "검토", "적용" }, after.Stages.Select(x => x.Name).ToArray());
        Assert.Equal(st[1].Version + 1, after.Stages[0].Version);
        Assert.Equal(st[0].Version + 1, after.Stages[1].Version);
        Assert.Equal(st[2].Version, after.Stages[2].Version);
        var act = (await ActivitiesAsync(id)).Last();
        Assert.Equal((ActivityType.Edited, "단계 순서: 검토 ↔ 보고", (int?)st[1].Id), (act.Type, act.Text, act.StageId));

        // 끝에서 더 밀면 아무 일도 없다 (활동도 남기지 않는다)
        var count = (await ActivitiesAsync(id)).Count;
        using (var s = _db.NewScope()) await s.Projects.MoveStageAsync(after.Stages[0].Id, -1);
        Assert.Equal(count, (await ActivitiesAsync(id)).Count);
    }

    // ── 체크 항목 ──

    [Fact]
    public async Task 체크_항목_문구를_고치면_옛_새_문구를_남기고_완료한_단계는_거절한다()
    {
        var id = await NewProjectAsync("테스트 체크 문구");
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope()) await s.Projects.SaveCheckAsync(new CheckInput(stage.Id, null, "도면 확인", stage.Version));
        var st = (await ViewAsync(id)).Stages[0];
        Assert.Equal(stage.Version + 1, st.Version);              // 체크리스트도 단계의 일부 — 단계 버전이 오른다
        var check = st.Checks.Single();

        // 옛 단계 버전으로 고치면 409
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.SaveCheckAsync(new CheckInput(st.Id, check.Id, "도면 승인", stage.Version)));

        using (var s = _db.NewScope()) await s.Projects.SaveCheckAsync(new CheckInput(st.Id, check.Id, "도면 승인", st.Version));
        var act = (await ActivitiesAsync(id)).Last();
        Assert.Equal((ActivityType.CheckChanged, "[검토] 체크 항목 수정: 도면 확인 → 도면 승인"), (act.Type, act.Text));
        st = (await ViewAsync(id)).Stages[0];
        Assert.Equal("도면 승인", st.Checks.Single().Text);

        // 체크(단계 버전 확인) 뒤 완료하면 문구 수정은 거절된다
        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.ToggleCheckAsync(check.Id, true, st.Version - 1));
            await s.Projects.ToggleCheckAsync(check.Id, true, st.Version);
        }
        st = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, st.Id, st.Name, null, Day(10, 8), null, false, st.Version));
        st = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveCheckAsync(new CheckInput(st.Id, check.Id, "다른 문구", st.Version)));
            Assert.Equal("check", ex.Field);
            // 지우기도 같은 이유로 거절한다 — 완료 조건이 줄면 무엇을 확인하고 완료했는지가 달라진다
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.DeleteCheckAsync(check.Id, st.Version));
            Assert.Equal("check", ex.Field);
        }
        Assert.Equal("도면 승인", (await ViewAsync(id)).Stages[0].Checks.Single().Text);
    }

    [Fact]
    public async Task 삭제_계열은_화면이_본_버전을_확인한다()
    {
        var id = await NewProjectAsync("테스트 삭제 버전");
        var st = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveWorkAsync(new WorkInput(null, st[0].Id, "조사", null, null, Day(10, 12), Day(10, 14), null, null, 0, null, null));
            await s.Projects.SaveCheckAsync(new CheckInput(st[1].Id, null, "보고서 승인"));
        }
        var v = await ViewAsync(id);
        var work = v.Stages[0].Works.Single();
        var report = v.Stages[1];

        using (var s = _db.NewScope())
        {
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.DeleteWorkAsync(work.Id, work.Version + 1));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.DeleteStageAsync(v.Stages[2].Id, v.Stages[2].Version + 1));
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.DeleteCheckAsync(report.Checks[0].Id, report.Version + 1));
        }
        v = await ViewAsync(id);
        Assert.Equal((3, 1, 1), (v.Stages.Count, v.Stages[0].Works.Count, v.Stages[1].Checks.Count));

        using (var s = _db.NewScope())
        {
            await s.Projects.DeleteWorkAsync(work.Id, work.Version);
            await s.Projects.DeleteCheckAsync(report.Checks[0].Id, report.Version);
            await s.Projects.DeleteStageAsync(v.Stages[2].Id, v.Stages[2].Version);
        }
        v = await ViewAsync(id);
        Assert.Equal((2, 0, 0), (v.Stages.Count, v.Stages[0].Works.Count, v.Stages[1].Checks.Count));
    }

    // ── 기준선 ──

    [Fact]
    public async Task 기준선_다시_확정은_담당자_관리자만_사유와_함께_하고_직전_새_종료일을_남긴다()
    {
        var id = await NewProjectAsync("테스트 기준선 재확정", owner: "기준선담당");
        var st = (await ViewAsync(id)).Stages;
        using (var s = _db.NewScope())
        {
            await s.Projects.SaveStageAsync(new StageInput(id, st[0].Id, st[0].Name, Day(10, 14), null, null, false));
            await s.Projects.SaveStageAsync(new StageInput(id, st[2].Id, st[2].Name, Day(11, 20), null, null, false));
        }
        var v = await ViewAsync(id);

        // 처음 확정은 누구나(사유 없이). 과제 버전을 확인하고 올린다
        using (var s = _db.NewScope("기준선동료"))
        {
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.SetBaselineAsync(id, null, v.Version + 1));
            await s.Projects.SetBaselineAsync(id, null, v.Version);
        }
        var first = (await ActivitiesAsync(id)).Last();
        Assert.Equal((ActivityType.BaselineSet, null, "2026-11-20"), (first.Type, first.OldValue, first.NewValue));
        v = await ViewAsync(id);

        // 목표를 미룬 뒤 다시 확정: 담당자·관리자가 아니면 거절, 사유가 없으면 거절
        using (var s = _db.NewScope())
            await s.Projects.SaveStageAsync(new StageInput(id, v.Stages[2].Id, v.Stages[2].Name, Day(11, 27), null, "범위 추가", false, v.Stages[2].Version));
        v = await ViewAsync(id);
        Assert.Equal(7, v.SlipDays);
        using (var s = _db.NewScope("기준선동료"))
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SetBaselineAsync(id, "계약 변경", v.Version));
        using (var s = _db.NewScope("기준선담당"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SetBaselineAsync(id, " ", v.Version));
            Assert.Equal("reason", ex.Field);
            Assert.True((await s.Projects.ViewAsync(id))!.CanRebaseline);
            await s.Projects.SetBaselineAsync(id, "계약 변경", v.Version);
        }
        using (var s = _db.NewScope("기준선동료"))
            Assert.False((await s.Projects.ViewAsync(id))!.CanRebaseline);

        var again = (await ActivitiesAsync(id)).Last();
        Assert.Equal((ActivityType.BaselineSet, "계약 변경", "2026-11-20", "2026-11-27"), (again.Type, again.Reason, again.OldValue, again.NewValue));
        Assert.Contains("2026-11-20 → 2026-11-27", again.Text);
        var after = await ViewAsync(id);
        Assert.Equal(0, after.SlipDays);
        Assert.Equal(v.Version + 1, after.Version);

        // 관리자(테스터)도 다시 확정할 수 있다
        using (var s = _db.NewScope()) await s.Projects.SetBaselineAsync(id, "재검토", after.Version);
    }
}

// 첨부: 커밋 뒤 파일 삭제 실패 내성, 상한 설정(Storage:MaxMB)
public class AttachmentIntegrityTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    public AttachmentIntegrityTests(TestDb db) => _db = db;

    private AttachmentService Files(TestDb.Scope s, string root, int? maxMB = null)
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = root,
            ["Storage:MaxMB"] = maxMB?.ToString(),
        }).Build();
        return new AttachmentService(s.Db, _db.Clock, s.Me, cfg, new Env());
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GanttScheduler";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private async Task<int> NewProjectAsync(string title)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, new DateOnly(2026, 10, 1), null, null, null, null));
    }

    [Fact]
    public async Task 기록을_지운_뒤_파일을_지우지_못해도_성공으로_답하고_파일은_남긴다()
    {
        var root = Path.Combine(Path.GetTempPath(), "gantt-att-" + Guid.NewGuid().ToString("N"));
        var id = await NewProjectAsync("테스트 첨부 삭제 내성");
        int fileId;
        using (var s = _db.NewScope())
        {
            await Files(s, root).SaveAsync(id, null, "도면.pdf", 4, new MemoryStream(new byte[] { 1, 2, 3, 4 }));
            fileId = await s.Db.Attachments.Where(a => a.ProjectId == id).Select(a => a.Id).SingleAsync();
        }
        var stored = Directory.GetFiles(Path.Combine(root, id.ToString())).Single();

        using (var s = _db.NewScope())
        {
            var files = Files(s, root);
            files.DeleteFile = _ => throw new IOException("다른 프로세스가 사용 중");
            Assert.Equal(id, await files.DeleteAsync(fileId));
        }
        using (var s = _db.NewScope())
        {
            Assert.False(await s.Db.Attachments.AnyAsync(a => a.Id == fileId));
            Assert.Contains(await s.Db.Activities.Where(a => a.ProjectId == id).Select(a => a.Text).ToListAsync(), t => t == "첨부 삭제: 도면.pdf");
        }
        Assert.True(File.Exists(stored));      // 남은 파일은 고아 파일 정리가 치운다

        // 과제 폴더 정리도 실패를 올리지 않는다
        using (var s = _db.NewScope())
        {
            var files = Files(s, root);
            files.DeleteFolder = _ => throw new UnauthorizedAccessException("거부됨");
            files.RemoveProjectFiles(id);
            Assert.True(Directory.Exists(Path.Combine(root, id.ToString())));
            Files(s, root).RemoveProjectFiles(id);
        }
        Assert.False(Directory.Exists(Path.Combine(root, id.ToString())));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task 첨부_상한은_설정_하나로_정하고_문구도_그_값을_쓴다()
    {
        var root = Path.Combine(Path.GetTempPath(), "gantt-att-" + Guid.NewGuid().ToString("N"));
        var id = await NewProjectAsync("테스트 첨부 상한");
        using var s = _db.NewScope();
        Assert.Equal(AttachmentService.DefaultMaxMB, Files(s, root).MaxMB);
        Assert.Equal(AttachmentService.MaxMBLimit, Files(s, root, 99999).MaxMB);       // 설정할 수 있는 끝으로 자른다

        var small = Files(s, root, 1);
        Assert.Equal(1024L * 1024, small.MaxBytes);
        var ex = await Assert.ThrowsAsync<RuleException>(() => small.SaveAsync(id, null, "큰파일.zip", 2 * 1024 * 1024, new MemoryStream(new byte[10])));
        Assert.Equal("파일은 1MB 이하만 올릴 수 있습니다.", ex.Message);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

// 공휴일: 해마다 채우기, 변경 영향 안내, 여러 줄 붙여넣기 (공휴일을 바꾸므로 다른 시험과 DB 를 나눈다)
public class HolidayTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);
    public HolidayTests(TestDb db) => _db = db;

    [Fact]
    public async Task 기본_공휴일은_그_해가_비어_있을_때만_채운다()
    {
        var builtIn2027 = Seeder.KoreanHolidays().Count(h => h.Date.Year == 2027);
        using (var s = _db.NewScope())
        {
            s.Db.Holidays.RemoveRange(await s.Db.Holidays.Where(h => h.Date.Year == 2027).ToListAsync());
            s.Db.Holidays.Remove(await s.Db.Holidays.SingleAsync(h => h.Date == new DateOnly(2026, 12, 25)));   // 관리자가 일부를 지운 해
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope()) await Seeder.SeedMastersAsync(s.Db);
        using (var s = _db.NewScope())
        {
            var dates = await s.Db.Holidays.Select(h => h.Date).ToListAsync();
            Assert.Equal(builtIn2027, dates.Count(d => d.Year == 2027));        // 빈 해는 내장 목록으로 다시 채운다
            Assert.DoesNotContain(new DateOnly(2026, 12, 25), dates);           // 일부를 지운 해는 건드리지 않는다
            Assert.Equal(dates.Count, dates.Distinct().Count());
        }
        // 2028 년 이후는 확정 발표 전이라 내장하지 않는다(관리자 알림이 10월부터 알린다)
        Assert.DoesNotContain(Seeder.KoreanHolidays(), h => h.Date.Year >= 2028);
        // 되돌림
        using (var s = _db.NewScope())
        {
            s.Db.Holidays.Add(new Holiday { Date = new DateOnly(2026, 12, 25), Name = "성탄절" });
            await s.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public void 공휴일_영향은_일정에_걸린_과제와_새로_생기는_선행_위반을_센다()
    {
        // A(10-12~10-14) → B(10-16, 끝나면 시작 + 1근무일). 10-15 가 공휴일이 되면 B 는 10-19 이후여야 한다
        var a = new WorkItem { Id = 1, Title = "A", PlanStart = Day(10, 12), PlanEnd = Day(10, 14) };
        var b = new WorkItem { Id = 2, Title = "B", PlanStart = Day(10, 16), PlanEnd = Day(10, 16) };
        var stage = new Stage { Id = 1, Name = "제작", SortOrder = 1, TargetDate = Day(10, 30), Works = { a, b } };
        var p = new Project { State = ProjectState.Active, Stages = { stage }, Links = { new WorkLink { PredecessorId = 1, SuccessorId = 2, LagDays = 1 } } };
        var before = WorkCalendar.WeekendsOnly;
        var after = new WorkCalendar(new[] { Day(10, 15) });

        // 10-15 는 두 작업의 기간 밖이지만 B 의 선행 관계가 새로 어긋난다
        Assert.Equal((false, 1), Rules.HolidayImpact(p, new[] { Day(10, 15) }, before, after));
        // 작업 기간 안의 날은 일정에 걸린다(근무일 기간이 바뀐다)
        Assert.Equal((true, 0), Rules.HolidayImpact(p, new[] { Day(10, 13) }, before, new WorkCalendar(new[] { Day(10, 13) })));
        // 단계 목표일이 공휴일이 되는 것도 걸린다
        Assert.Equal((true, 0), Rules.HolidayImpact(p, new[] { Day(10, 30) }, before, new WorkCalendar(new[] { Day(10, 30) })));
        // 일정에 걸리지 않는 날
        Assert.Equal((false, 0), Rules.HolidayImpact(p, new[] { Day(11, 20) }, before, new WorkCalendar(new[] { Day(11, 20) })));
        // 빼면(after → before) 새 위반은 없다
        Assert.Equal((false, 0), Rules.HolidayImpact(p, new[] { Day(10, 15) }, after, before));
        // 이미 시작한 후행은 밀기 대상이 아니라 세지 않는다
        b.ActualStart = Day(10, 16);
        Assert.Equal((false, 0), Rules.HolidayImpact(p, new[] { Day(10, 15) }, before, after));
    }

    [Fact]
    public async Task 공휴일을_저장_삭제하면_영향을_안내하고_붙여넣기는_날짜_이름을_줄마다_읽는다()
    {
        int id;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            id = await s.Projects.CreateAsync(new ProjectInput("테스트 공휴일 영향", cat.Id, corp.Id, null, Day(10, 1), null, null, null, null));
            var stage = (await s.Projects.ViewAsync(id))!.Stages[0];
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "A", null, null, Day(11, 9), Day(11, 11), null, null, 0, null, null));
            var aId = (await s.Projects.ViewAsync(id))!.Stages[0].Works.Single().Id;
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "B", null, null, Day(11, 13), Day(11, 13), null, null, 0, null, null,
                Links: new List<LinkInput> { new(aId, 1, "fs") }));
        }

        int holidayId;
        using (var s = _db.NewScope())
        {
            await s.Masters.SaveAsync(new MasterSave("holiday", 1, null, null, "임시공휴일", null, null, null, null, null, Day(11, 12)));
            Assert.StartsWith("공휴일을 저장했습니다. 영향받는 진행 과제", s.Masters.Notice);
            Assert.Contains("선행 관계를 어기게 된 작업", s.Masters.Notice);
            Assert.True(s.Masters.LastImpact!.Value.Violations >= 1);
            holidayId = await s.Db.Holidays.Where(h => h.Date == Day(11, 12)).Select(h => h.Id).SingleAsync();
        }
        // 이름만 고치면 일정 영향이 없다
        using (var s = _db.NewScope())
        {
            await s.Masters.SaveAsync(new MasterSave("holiday", 1, holidayId, null, "임시 공휴일", null, null, null, null, null, Day(11, 12)));
            Assert.Null(s.Masters.Notice);
        }
        using (var s = _db.NewScope())
        {
            await s.Masters.ToggleAsync(new MasterRef("holiday", 1, holidayId, 0));
            Assert.StartsWith("공휴일을 삭제했습니다.", s.Masters.Notice);
            Assert.Equal(0, s.Masters.LastImpact!.Value.Violations);
        }

        // 붙여넣기: TAB·공백, 여러 날짜 형식, 괄호 요일, 이미 있는 날짜는 건너뜀
        using (var s = _db.NewScope())
        {
            await s.Masters.PasteHolidaysAsync("2028-01-01\t신정\n2028.1.26 설날\r\n2028년 3월 1일 (수) 삼일절\n\n2026-10-09\t한글날");
            Assert.StartsWith("공휴일 3건을 추가했습니다(이미 있는 날짜 1건은 건너뜀).", s.Masters.Notice);
            var y2028 = (await s.Masters.ListAsync("holiday")).Where(n => n.Date!.Value.Year == 2028).Select(n => (n.Date!.Value, n.Name)).ToList();
            Assert.Equal(new[] { (new DateOnly(2028, 1, 1), "신정"), (new DateOnly(2028, 1, 26), "설날"), (new DateOnly(2028, 3, 1), "삼일절") }, y2028);

            // 한 줄이라도 읽지 못하면 아무것도 넣지 않고 그 줄을 알린다
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.PasteHolidaysAsync("2028-05-05 어린이날\n5월 2일 부처님오신날"));
            Assert.Equal("text", ex.Field);
            Assert.StartsWith("2번째 줄", ex.Message);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.PasteHolidaysAsync("2028-05-05 어린이날\n2028-05-05 어린이날"));
            Assert.Contains("두 번", ex.Message);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.PasteHolidaysAsync("2028-02-30 없는 날"));
            Assert.StartsWith("1번째 줄", ex.Message);
            Assert.False(await s.Db.Holidays.AnyAsync(h => h.Date == new DateOnly(2028, 5, 5)));
        }
    }
}

// 기준 정보 변경이 기존 과제를 깨뜨리지 않음 · 입력 길이·소속 검사
public class MasterIntegrityTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private static DateOnly Day(int month, int day) => new(2026, month, day);
    public MasterIntegrityTests(TestDb db) => _db = db;

    private static MasterSave Save(string kind, int level, int? id, int? parentId, string name, string? subKind = null) =>
        new(kind, level, id, parentId, name, null, null, subKind, null, null, null);

    [Fact]
    public async Task 쓰는_유형의_성격은_바꿀_수_없고_성격_값이_없으면_그대로_둔다()
    {
        using var s = _db.NewScope();
        var cats = await s.Masters.ListAsync("category");
        var auto = cats.Single(n => n.Level == 1 && n.Name == "물류 자동화");     // 샘플 과제가 쓴다
        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("category", 1, auto.Id, null, "물류 자동화", "container")));
        Assert.Equal("subKind", ex.Field);

        // 이름만 고치는 요청(성격 없음)은 성격을 일반으로 되돌리지 않는다
        await s.Masters.SaveAsync(Save("category", 1, auto.Id, null, "물류 자동화", null));
        Assert.Equal("automation", (await s.Masters.ListAsync("category")).Single(n => n.Id == auto.Id && n.Level == 1).Kind);

        // 아무 과제도 쓰지 않는 유형은 바꿀 수 있다
        await s.Masters.SaveAsync(Save("category", 1, null, null, "테스트 새 유형"));
        var fresh = (await s.Masters.ListAsync("category")).Single(n => n.Level == 1 && n.Name == "테스트 새 유형");
        await s.Masters.SaveAsync(Save("category", 1, fresh.Id, null, "테스트 새 유형", "container"));
        Assert.Equal("container", (await s.Masters.ListAsync("category")).Single(n => n.Id == fresh.Id && n.Level == 1).Kind);

        // 단계 템플릿도 성격 값이 없으면 그대로 (검수 단계가 일반으로 돌아가 추천 입고일이 사라지지 않게)
        var inspect = cats.Single(n => n.Level == 2 && n.ParentId == auto.Id && n.Name == "검수");
        await s.Masters.SaveAsync(Save("category", 2, inspect.Id, auto.Id, "검수", null));
        Assert.Equal("inspection", (await s.Masters.ListAsync("category")).Single(n => n.Id == inspect.Id && n.Level == 2).Kind);
    }

    [Fact]
    public async Task 과제_수정은_지금_성격_쪽_내역만_바꾸고_반대쪽은_건드리지_않는다()
    {
        int id;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류 자동화");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            var agv = await s.Db.EquipmentTypes.FirstAsync(e => e.Name == "AGV");
            id = await s.Projects.CreateAsync(new ProjectInput("테스트 내역 보존", cat.Id, corp.Id, "테스터", Day(10, 1), null, null,
                new List<ItemInput> { new(agv.Id, 2, null) }, null));
            // 성격을 바꾸기 전(잠금 이전)에 들어간 반대쪽 내역이 남아 있는 과제
            var pallet = await s.Db.ContainerTypes.FirstAsync();
            s.Db.ProjectContainers.Add(new ProjectContainer { ProjectId = id, ContainerTypeId = pallet.Id, Quantity = 5 });
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope())
        {
            var v = (await s.Projects.ViewAsync(id))!;
            var amr = await s.Db.EquipmentTypes.FirstAsync(e => e.Name == "AMR");
            await s.Projects.UpdateAsync(id, new ProjectInput(v.Title, v.CategoryId, v.LocationId, "테스터", v.StartDate, null, null,
                new List<ItemInput> { new(amr.Id, 3, null) }, new List<ItemInput>(), v.Version));
        }
        using (var s = _db.NewScope())
        {
            var p = await s.Db.Projects.Include(x => x.Equipments).Include(x => x.Containers).SingleAsync(x => x.Id == id);
            Assert.Equal(3, p.Equipments.Single().Quantity);
            Assert.Equal(5, p.Containers.Single().Quantity);       // 화면이 보내지 않은 쪽은 지우지 않는다
        }
    }

    [Fact]
    public async Task 사용_중지는_요청한_상태로_맞추고_같으면_아무_일도_없다()
    {
        using var s = _db.NewScope();
        await s.Masters.SaveAsync(Save("container", 1, null, null, "테스트 상자"));
        var box = (await s.Masters.ListAsync("container")).Single(n => n.Name == "테스트 상자");
        async Task<bool> Active() => (await s.Masters.ListAsync("container")).Single(n => n.Id == box.Id).Active;

        await s.Masters.ToggleAsync(new MasterRef("container", 1, box.Id, 0, Active: false));
        await s.Masters.ToggleAsync(new MasterRef("container", 1, box.Id, 0, Active: false));     // 두 번 눌러도 중지 그대로
        Assert.False(await Active());
        await s.Masters.ToggleAsync(new MasterRef("container", 1, box.Id, 0, Active: true));
        Assert.True(await Active());
        // 원하는 상태가 없는 옛 요청은 뒤집지 않고 거절한다
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.ToggleAsync(new MasterRef("container", 1, box.Id, 0)));
        Assert.True(await Active());
        // 자기 자신을 다시 사용으로 두는 것은 막지 않는다
        await s.Masters.ToggleAsync(new MasterRef("user", 1, s.Me.Id, 0, Active: true));
    }

    [Fact]
    public async Task 이름_길이는_DB_열_길이로_검사한다()
    {
        using var s = _db.NewScope();
        var long51 = new string('가', Limits.CategoryName + 1);
        // SQL Server 에서만 DB 가 거절해 500 이 나던 경우 — 두 DB 모두 저장 전에 RuleException
        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("category", 1, null, null, long51)));
        Assert.Equal(("name", $"이름은 {Limits.CategoryName}자 이내로 입력해 주세요."), (ex.Field, ex.Message));
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("container", 1, null, null, long51)));
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(new MasterSave("holiday", 1, null, null, long51, null, null, null, null, null, Day(12, 31))));
        // 위치·협력사는 100자까지
        await s.Masters.SaveAsync(Save("vendor", 1, null, null, new string('나', Limits.VendorName)));
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("vendor", 1, null, null, new string('나', Limits.VendorName + 1))));

        // 체크 항목 200자 (템플릿·과제)
        var cats = await s.Masters.ListAsync("category");
        var template = cats.First(n => n.Level == 2);
        ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("category", 3, null, template.Id, new string('다', Limits.CheckText + 1))));
        Assert.Contains("체크 항목은 200자", ex.Message);
        var stage = await s.Db.Stages.FirstAsync();
        ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveCheckAsync(new CheckInput(stage.Id, null, new string('다', Limits.CheckText + 1))));
        Assert.Equal("check", ex.Field);
    }

    [Fact]
    public async Task 지운_상위에는_하위를_더할_수_없고_500_대신_안내한다()
    {
        using var s = _db.NewScope();
        await s.Masters.SaveAsync(Save("category", 1, null, null, "테스트 지울 유형"));
        var cat = (await s.Masters.ListAsync("category")).Single(n => n.Level == 1 && n.Name == "테스트 지울 유형");
        await s.Masters.SaveAsync(Save("category", 2, null, cat.Id, "임시 단계"));
        var stage = (await s.Masters.ListAsync("category")).Single(n => n.Level == 2 && n.ParentId == cat.Id);
        await s.Masters.ToggleAsync(new MasterRef("category", 2, stage.Id, 0));      // 단계 템플릿 삭제

        var ex = await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("category", 3, null, stage.Id, "체크")));
        Assert.Contains("단계을(를) 찾을 수 없습니다", ex.Message);
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("category", 2, null, 999999, "단계")));
        // 기기 하위 타입은 대분류 아래에만
        var sub = (await s.Masters.ListAsync("equipment")).First(n => n.Level == 2);
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.SaveAsync(Save("equipment", 2, null, sub.Id, "손자")));
    }

    [Fact]
    public async Task 수정_화면에서_비운_담당자_착수일과_다른_과제의_댓글_대상을_거절한다()
    {
        int id, other;
        using (var s = _db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            id = await s.Projects.CreateAsync(new ProjectInput("테스트 비운 칸", cat.Id, corp.Id, "테스터", Day(10, 1), null, null, null, null));
            other = await s.Projects.CreateAsync(new ProjectInput("테스트 다른 과제", cat.Id, corp.Id, "테스터", Day(10, 1), null, null, null, null));
        }
        using (var s = _db.NewScope())
        {
            var v = (await s.Projects.ViewAsync(id))!;
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateAsync(id, new ProjectInput(v.Title, v.CategoryId, v.LocationId, " ", v.StartDate, null, null, null, null, v.Version)));
            Assert.Equal("owner", ex.Field);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateAsync(id, new ProjectInput(v.Title, v.CategoryId, v.LocationId, "테스터", null, null, null, null, null, v.Version)));
            Assert.Equal("startDate", ex.Field);

            var mine = v.Stages[0];
            var theirs = (await s.Projects.ViewAsync(other))!.Stages[0];
            await s.Projects.SaveWorkAsync(new WorkInput(null, theirs.Id, "남의 작업", null, null, Day(10, 12), Day(10, 13), null, null, 0, null, null));
            var theirWork = (await s.Projects.ViewAsync(other))!.Stages[0].Works.Single();
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, theirs.Id, null, "엉뚱한 단계")));
            Assert.Equal("stageId", ex.Field);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(id, null, theirWork.Id, "엉뚱한 작업")));
            Assert.Equal("workId", ex.Field);
            ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.CommentAsync(new CommentInput(other, mine.Id, theirWork.Id, "단계와 작업이 어긋남")));
            Assert.Equal("stageId", ex.Field);
            await s.Projects.CommentAsync(new CommentInput(other, theirs.Id, theirWork.Id, "제대로 된 댓글"));
        }
    }
}
