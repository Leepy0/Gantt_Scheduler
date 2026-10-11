using System.Diagnostics;
using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 업데이트와 백업: DB 구조가 바뀌는 버전은 적용 전에 백업하고, 실패하면 적용하지 않는다. 이력에 백업 이름을 남긴다.
// 업데이트 화면의 진단 정보(apply.log 끝부분, 시작 오류, 상태 요약)
public partial class UpdateTests
{
    private UpdateService ServiceWithBackup(BackupService backup) => new(
        Release, new UpdatePaths { AppDir = AppDir, DataDir = DataDir }, new UpdateSource(SourceDir, Release, null, null),
        _activity, new FakeLifetime(), NullLogger<UpdateService>.Instance, new ConfigurationBuilder().Build(), backup);

    // 받아 둔 0.3.1 (schema: 이 빌드는 S1)
    private void StageVersion(string schema)
    {
        var staged = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(staged, "0.3.1");
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), schema, null, DateTime.UtcNow, "download", "평영"),
        });
    }

    private static UpdateState ReadState(string dataDir) =>
        JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(Path.Combine(dataDir, "state.json")))!;

    [Fact]
    public async Task 구조가_바뀌는_업데이트는_먼저_백업하고_그_이름을_교체_기록과_이력에_남긴다()
    {
        using var db = new TestDb();
        var backup = BackupHelper.Create(db, Path.Combine(_root, "server"));
        StageVersion("S2");
        var svc = ServiceWithBackup(backup);
        ProcessStartInfo? helper = null;
        svc.StartProcess = psi => { helper = psi; return Process.GetCurrentProcess(); };

        svc.Apply("평영");
        Assert.Equal("backing-up", svc.Status().Phase);   // 백업은 요청과 떼어 돌린다
        await svc.BackupTask!;

        var made = Assert.Single(backup.List());
        Assert.True(made.Ok, made.Error);
        Assert.Equal(BackupKinds.Update, made.Kind);
        Assert.Contains("v0.3.0 → v0.3.1", made.Note);
        Assert.NotNull(helper);   // 백업이 끝나고 교체 도우미를 띄웠다
        Assert.Contains("--apply-update", helper!.ArgumentList);
        Assert.Equal("applying", svc.Status().Phase);
        var pending = ReadState(DataDir).Pending!;
        Assert.Equal(made.Name, pending.Backup);

        // 새 버전이 떠서 결과를 정리하면 이력에 백업 이름이 남고, 되돌리기 막힘 안내에 복원할 백업으로 보인다
        MakePrevious("0.3.0");
        var next = new UpdateService(new AppBuild(new Version(0, 3, 1), "def5678", "release", "S2"),
            new UpdatePaths { AppDir = AppDir, DataDir = DataDir }, new UpdateSource(SourceDir, Release, null, null),
            _activity, new FakeLifetime(), NullLogger<UpdateService>.Instance, new ConfigurationBuilder().Build());
        next.Startup();
        var st = next.Status();
        Assert.Equal(made.Name, Assert.Single(st.History).Backup);
        Assert.False(st.Previous!.CanRollback);
        Assert.Contains("복원할 백업: " + made.Name, st.Previous.BlockReason);
    }

    [Fact]
    public async Task 적용_전_백업이_실패하면_교체하지_않고_이유를_보인다()
    {
        // 백업 폴더 자리에 파일이 있어 백업할 수 없는 서버
        var server = Path.Combine(_root, "server");
        Directory.CreateDirectory(server);
        File.WriteAllText(Path.Combine(server, "blocked"), "파일");
        var backup = BackupHelper.Create(null, server, new() { ["Backup:FilesPath"] = Path.Combine(server, "blocked") });
        StageVersion("S2");
        var svc = ServiceWithBackup(backup);
        var launched = false;
        svc.StartProcess = _ => { launched = true; return Process.GetCurrentProcess(); };

        svc.Apply("평영");
        await svc.BackupTask!;

        Assert.False(launched);
        var st = svc.Status();
        Assert.Equal("idle", st.Phase);
        Assert.Contains("백업에 실패해 업데이트를 적용하지 않았습니다", st.Error);
        Assert.Contains("Backup:BeforeSchemaUpdate", st.ErrorFix);
        Assert.Equal("0.3.1", st.Staged!.Version);   // 받아 둔 것은 그대로 — 고친 뒤 다시 적용한다
        Assert.Null(ReadState(DataDir).Pending);
    }

    [Fact]
    public async Task 구조가_같은_업데이트나_적용_전_백업을_끈_서버는_백업하지_않고_바로_교체한다()
    {
        var server = Path.Combine(_root, "server");
        var backup = BackupHelper.Create(null, server);
        StageVersion("S1");
        var svc = ServiceWithBackup(backup);
        var launched = 0;
        svc.StartProcess = _ => { launched++; return Process.GetCurrentProcess(); };
        svc.Apply("평영");
        Assert.Null(svc.BackupTask);
        Assert.Equal(1, launched);
        Assert.Empty(backup.List());
        Assert.Null(ReadState(DataDir).Pending!.Backup);

        // 구조가 바뀌어도 Backup:BeforeSchemaUpdate=false 면 백업하지 않는다 (DBA 가 따로 백업한 경우)
        Directory.Delete(DataDir, true);
        var off = BackupHelper.Create(null, server, new() { ["Backup:BeforeSchemaUpdate"] = "false" });
        StageVersion("S2");
        var svc2 = ServiceWithBackup(off);
        svc2.StartProcess = _ => { launched++; return Process.GetCurrentProcess(); };
        svc2.Apply("평영");
        Assert.Null(svc2.BackupTask);
        Assert.Equal(2, launched);
        await Task.CompletedTask;
    }

    [Fact]
    public void 진단_정보는_교체_기록_끝부분과_시작_오류와_상태_요약을_주고_비밀번호는_가린다()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllLines(Path.Combine(DataDir, "apply.log"), Enumerable.Range(1, 100).Select(i => $"줄 {i}"));
        Put(AppDir, "App_Data/" + ServiceInstaller.StartupErrorFile,
            "2026-10-11 01:00:00\nSystem.InvalidOperationException: 연결 실패 Server=db1;User Id=gantt;Password=s3cret!");
        var svc = Service();

        var d = svc.Diagnostics(new[]
        {
            new LogEntry(DateTime.UtcNow, "warn", "GanttScheduler.Services.UpdateService", "업데이트 확인 실패: 시간 초과", null, null),
            new LogEntry(DateTime.UtcNow, "warn", "Microsoft.AspNetCore.Something", "관계없는 경고", null, null),
        });

        var lines = d.ApplyLog!.Split(Environment.NewLine);
        Assert.Equal(UpdateService.ApplyLogTail, lines.Length);
        Assert.Equal("줄 41", lines[0]);
        Assert.Equal("줄 100", lines[^1]);
        Assert.Contains("InvalidOperationException", d.StartupError);
        Assert.DoesNotContain("s3cret", d.StartupError);
        Assert.NotNull(d.StartupErrorAt);
        Assert.Contains("지금 버전: v0.3.0", d.State);
        Assert.Contains("받아 오는 곳", d.State);
        Assert.Equal("업데이트 확인 실패: 시간 초과", Assert.Single(d.Logs).Message);   // 업데이트·백업 관련 경고만

        var text = svc.DiagnosticsText();
        Assert.Contains("== 상태 요약 ==", text);
        Assert.Contains("줄 100", text);
        Assert.DoesNotContain("s3cret", text);
    }

    [Fact]
    public async Task 확인이_계속_실패하면_실패_시작_시각을_남기고_성공하면_지운다()
    {
        var svc = Service(source: new UpdateSource(Path.Combine(_root, "없는 폴더"), Release, null, null));
        await svc.CheckAsync(CancellationToken.None);
        var first = svc.CheckFailedSince;
        Assert.NotNull(first);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Equal(first, svc.CheckFailedSince);   // 처음 실패한 시각을 유지한다

        MakePackage("0.3.1");
        var ok = Service();
        await ok.CheckAsync(CancellationToken.None);
        Assert.Null(ok.CheckFailedSince);
        Assert.NotNull(ReadState(DataDir).LastCheckOkAt);
    }
}
