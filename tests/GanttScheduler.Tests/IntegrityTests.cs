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
