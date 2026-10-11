using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GanttScheduler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 서버 자동 업데이트: 확인·받기·검증, 파일 교체와 되돌리기, 자동 적용 판단.
// 실제로 서버를 내렸다 올리는 흐름은 tools/release/selftest.sh 가 따로 확인한다
public partial class UpdateTests : IDisposable
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gantt-update-" + Guid.NewGuid().ToString("N"));
    private string AppDir => Path.Combine(_root, "app");
    private string DataDir => Path.Combine(_root, "app", "App_Data", "updates");
    private string SourceDir => Path.Combine(_root, "source");
    private readonly ActivityTracker _activity = new();

    private static readonly AppBuild Release = new(new Version(0, 3, 0), "abc1234", "release", "S1");

    public UpdateTests()
    {
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(SourceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }

    // 공개키는 빌드에 넣은 것(update-key.pub)과 상관없이 시험마다 정한다 — 기본은 서명 확인을 하지 않는 빌드
    private UpdateService Service(AppBuild? build = null, UpdateKey? key = null, UpdateSource? source = null) => new(
        build ?? Release, new UpdatePaths { AppDir = AppDir, DataDir = DataDir }, source ?? new UpdateSource(SourceDir, build ?? Release, key, null),
        _activity, new FakeLifetime(), NullLogger<UpdateService>.Instance, new ConfigurationBuilder().Build());

    private static void Put(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // 버전 폴더(실행 파일 + 목록)를 만든다. files 는 그 버전에만 있는 추가 파일
    private static void MakeVersion(string dir, string version, params string[] files)
    {
        Put(dir, UpdateApplier.EntryDll, "dll " + version);
        Put(dir, "wwwroot/js/app.js", "js " + version);
        Put(dir, "web.config", "<config " + version + " />");
        Put(dir, "appsettings.Production.json", "{ \"v\": \"" + version + "\" }");
        foreach (var f in files) Put(dir, f, f + " " + version);
        var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Append(UpdateApplier.FileList).Distinct().OrderBy(x => x);
        File.WriteAllLines(Path.Combine(dir, UpdateApplier.FileList), all);
    }

    // 배포 패키지(zip)와 매니페스트를 업데이트 폴더에 만든다. 돌려주는 값은 zip 경로
    private string MakePackage(string version, string schema = "S1", string? sha = null, string rid = "")
    {
        var dir = Path.Combine(_root, "pkg-" + version);
        MakeVersion(dir, version);
        File.WriteAllText(Path.Combine(dir, "release.json"), JsonSerializer.Serialize(new { version, schema, rid, notes = "변경 " + version }));
        var zip = Path.Combine(SourceDir, $"pkg-{version}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip);
        using var fs = File.OpenRead(zip);
        var actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(SourceDir, UpdateSource.ManifestName), JsonSerializer.Serialize(new
        {
            version, file = Path.GetFileName(zip), sha256 = sha ?? actual, size = new FileInfo(zip).Length, schema, rid, notes = "변경 " + version,
        }));
        return zip;
    }

    private static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));

    // ── 버전 ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.3.1", 0, 3, 1)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.4", 1, 4, 0)]
    [InlineData("0.3.1-rc1", 0, 3, 1)]
    [InlineData("0.3.1+abc1234", 0, 3, 1)]
    public void 버전_문자열을_읽는다(string text, int major, int minor, int patch)
    {
        Assert.True(AppBuild.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("latest")]
    public void 버전이_아닌_문자열은_거절한다(string text) => Assert.False(AppBuild.TryParseVersion(text, out _));

    [Fact]
    public void 빌드_식별값은_버전과_커밋으로_만든다()
    {
        Assert.Equal("0.3.0+abc1234", Release.Id);
        Assert.Equal("0.3.0", new AppBuild(new Version(0, 3, 0), null, "local", "").Id);
        Assert.True(Release.SelfUpdating);
        Assert.False(new AppBuild(new Version(0, 3, 0), null, "local", "").SelfUpdating);
    }

    // ── 파일 교체 ───────────────────────────────────────────────────────
    [Fact]
    public void 교체는_패키지_파일만_바꾸고_데이터와_서버_설정은_그대로_둔다()
    {
        MakeVersion(AppDir, "1", "old-only.txt");
        Put(AppDir, "App_Data/gantt.db", "데이터");
        Put(AppDir, "uploads/a.png", "목록에 없는 직접 넣은 파일");
        var next = Path.Combine(_root, "next");
        MakeVersion(next, "2", "new-only.txt", "App_Data/should-not-copy.txt");
        var backup = Path.Combine(_root, "backup");

        UpdateApplier.Swap(AppDir, next, backup);

        Assert.Equal("dll 2", Read(AppDir, UpdateApplier.EntryDll));
        Assert.Equal("js 2", Read(AppDir, "wwwroot/js/app.js"));
        Assert.True(File.Exists(Path.Combine(AppDir, "new-only.txt")));
        Assert.False(File.Exists(Path.Combine(AppDir, "old-only.txt")));
        // 서버마다 고친 설정은 덮어쓰지 않는다
        Assert.Equal("<config 1 />", Read(AppDir, "web.config"));
        Assert.Contains("\"1\"", Read(AppDir, "appsettings.Production.json"));
        // 데이터 폴더는 건드리지도, 패키지에서 복사하지도 않는다
        Assert.Equal("데이터", Read(AppDir, "App_Data/gantt.db"));
        Assert.False(File.Exists(Path.Combine(AppDir, "App_Data/should-not-copy.txt")));
        Assert.Equal("목록에 없는 직접 넣은 파일", Read(AppDir, "uploads/a.png"));
        // 원래 파일은 보관 폴더에
        Assert.Equal("dll 1", Read(backup, UpdateApplier.EntryDll));
        Assert.True(File.Exists(Path.Combine(backup, "old-only.txt")));
        Assert.False(File.Exists(Path.Combine(backup, "uploads/a.png")));

        // 되돌리기: 보관 폴더를 새 버전처럼 다시 넣는다
        UpdateApplier.Swap(AppDir, backup, Path.Combine(_root, "rolledback"));
        Assert.Equal("dll 1", Read(AppDir, UpdateApplier.EntryDll));
        Assert.True(File.Exists(Path.Combine(AppDir, "old-only.txt")));
        Assert.False(File.Exists(Path.Combine(AppDir, "new-only.txt")));
        Assert.Equal("데이터", Read(AppDir, "App_Data/gantt.db"));
        Assert.Equal("목록에 없는 직접 넣은 파일", Read(AppDir, "uploads/a.png"));
    }

    [Fact]
    public void 교체_도중_실패하면_원래_파일로_되돌린다()
    {
        MakeVersion(AppDir, "1");
        // 목록에 없는 '파일' blocker 가 있는데 새 버전은 같은 이름의 '폴더'를 쓴다 → 복사가 실패한다
        Put(AppDir, "blocker", "직접 넣은 파일");
        var next = Path.Combine(_root, "next");
        MakeVersion(next, "2", "blocker/inner.txt");

        Assert.ThrowsAny<Exception>(() => UpdateApplier.Swap(AppDir, next, Path.Combine(_root, "backup")));

        Assert.Equal("dll 1", Read(AppDir, UpdateApplier.EntryDll));
        Assert.Equal("js 1", Read(AppDir, "wwwroot/js/app.js"));
        Assert.Equal("직접 넣은 파일", Read(AppDir, "blocker"));
        Assert.True(File.Exists(Path.Combine(AppDir, UpdateApplier.FileList)));
    }

    [Fact]
    public void 실행_파일이_없는_폴더로는_교체하지_않는다()
    {
        MakeVersion(AppDir, "1");
        var next = Path.Combine(_root, "next");
        Put(next, "readme.txt", "엉뚱한 폴더");

        Assert.Throws<IOException>(() => UpdateApplier.Swap(AppDir, next, Path.Combine(_root, "backup")));
        Assert.Equal("dll 1", Read(AppDir, UpdateApplier.EntryDll));
    }

    [Fact]
    public void 목록_없이_설치된_폴더는_새_버전과_겹치는_파일만_바꾼다()
    {
        MakeVersion(AppDir, "1");
        File.Delete(Path.Combine(AppDir, UpdateApplier.FileList));
        Put(AppDir, "notes.txt", "직접 넣은 파일");
        var next = Path.Combine(_root, "next");
        MakeVersion(next, "2");

        UpdateApplier.Swap(AppDir, next, Path.Combine(_root, "backup"));

        Assert.Equal("dll 2", Read(AppDir, UpdateApplier.EntryDll));
        Assert.Equal("직접 넣은 파일", Read(AppDir, "notes.txt"));
        Assert.Equal("dll 1", Read(Path.Combine(_root, "backup"), UpdateApplier.EntryDll));
    }

    // ── 확인·받기 ───────────────────────────────────────────────────────
    [Fact]
    public async Task 폴더에서_새_버전을_확인하고_받아_검증한_뒤_풀어_둔다()
    {
        MakePackage("0.3.1");
        var svc = Service();

        await svc.CheckAsync(CancellationToken.None);
        var st = svc.Status();
        Assert.Null(st.CheckError);
        Assert.Equal("0.3.1", st.Latest!.Version);
        Assert.False(st.Latest.SchemaChange);
        Assert.Equal("변경 0.3.1", st.Latest.Notes);

        await svc.DownloadAsync(UpdateSource.ParseManifest(Read(SourceDir, UpdateSource.ManifestName))!, "테스터", CancellationToken.None);
        st = svc.Status();
        Assert.Null(st.Error);
        Assert.Equal("0.3.1", st.Staged!.Version);
        Assert.Equal("download", st.Staged.Origin);
        Assert.Equal("idle", st.Phase);
        Assert.Equal("dll 0.3.1", Read(Path.Combine(DataDir, "staged", "0.3.1"), UpdateApplier.EntryDll));
        Assert.Empty(Directory.GetFiles(DataDir, "*.partial"));
        Assert.Equal(("v0.3.1 적용 대기", true), svc.Badge()!.Value);

        // 상태는 파일에 남아 서버를 다시 띄워도 이어진다
        Assert.Equal("0.3.1", Service().Status().Staged!.Version);
    }

    [Fact]
    public async Task 같은_버전이나_옛_버전은_새_버전으로_보지_않는다()
    {
        MakePackage("0.3.0");
        var svc = Service();
        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().Latest);
        Assert.Null(svc.Badge());
        Assert.Throws<UpdateException>(() => svc.StartDownload("테스터"));
    }

    [Fact]
    public async Task 해시가_다르면_받은_파일을_버린다()
    {
        MakePackage("0.3.1", sha: new string('0', 64));
        var svc = Service();
        await svc.CheckAsync(CancellationToken.None);
        await svc.DownloadAsync(UpdateSource.ParseManifest(Read(SourceDir, UpdateSource.ManifestName))!, "테스터", CancellationToken.None);

        var st = svc.Status();
        Assert.Null(st.Staged);
        Assert.Contains("SHA-256", st.Error);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "staged", "0.3.1")));
    }

    [Fact]
    public async Task 업데이트_위치에_정보가_없으면_원인과_해결_방법을_알린다()
    {
        var svc = Service();
        await svc.CheckAsync(CancellationToken.None);
        var st = svc.Status();
        Assert.NotNull(st.CheckError);
        Assert.NotNull(st.CheckFix);
        Assert.NotNull(st.CheckedAt);
        Assert.Null(st.Latest);
    }

    [Fact]
    public async Task DB_구조가_다른_새_버전은_표시해_둔다()
    {
        MakePackage("0.4.0", schema: "S2");
        var svc = Service();
        await svc.CheckAsync(CancellationToken.None);
        Assert.True(svc.Status().Latest!.SchemaChange);
    }

    // ── 파일로 올리기 ───────────────────────────────────────────────────
    [Fact]
    public async Task 올린_배포_파일을_확인해_받아_둔다()
    {
        var zip = MakePackage("0.3.2");
        var svc = Service();
        await using (var fs = File.OpenRead(zip)) await svc.UploadAsync(fs, "테스터", CancellationToken.None);

        var st = svc.Status();
        Assert.Equal("0.3.2", st.Staged!.Version);
        Assert.Equal("upload", st.Staged.Origin);
        Assert.Equal("테스터", st.Staged.By);
        Assert.Equal(64, st.Staged.Sha256.Length);

        svc.Discard();
        Assert.Null(svc.Status().Staged);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "staged", "0.3.2")));
    }

    [Fact]
    public async Task 올린_파일이_새_버전이_아니거나_배포_파일이_아니면_거절한다()
    {
        var svc = Service();

        var same = MakePackage("0.3.0");
        await using (var fs = File.OpenRead(same))
            await Assert.ThrowsAsync<UpdateException>(() => svc.UploadAsync(fs, "테스터", CancellationToken.None));

        using (var junk = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
            await Assert.ThrowsAsync<UpdateException>(() => svc.UploadAsync(junk, "테스터", CancellationToken.None));

        // zip 이지만 과제 스케줄러 배포 파일이 아닌 경우
        var other = Path.Combine(_root, "other");
        Put(other, "readme.txt", "다른 프로그램");
        var otherZip = Path.Combine(_root, "other.zip");
        ZipFile.CreateFromDirectory(other, otherZip);
        await using (var fs = File.OpenRead(otherZip))
            await Assert.ThrowsAsync<UpdateException>(() => svc.UploadAsync(fs, "테스터", CancellationToken.None));

        Assert.Null(svc.Status().Staged);
        Assert.Equal("idle", svc.Status().Phase);
        // 풀다 만 폴더가 남지 않는다
        var staged = Path.Combine(DataDir, "staged");
        Assert.True(!Directory.Exists(staged) || Directory.GetDirectories(staged).Length == 0);
    }

    // ── 적용·되돌리기 조건 ──────────────────────────────────────────────
    [Fact]
    public async Task 로컬_빌드는_스스로_교체하지_않는다()
    {
        var local = new AppBuild(new Version(0, 3, 0), null, "local", "");
        var zip = MakePackage("0.3.1");
        var svc = Service(local);
        await using (var fs = File.OpenRead(zip)) await svc.UploadAsync(fs, "테스터", CancellationToken.None);

        var st = svc.Status();
        Assert.False(st.CanApply);
        Assert.NotNull(st.BlockReason);
        var ex = Assert.Throws<UpdateException>(() => svc.Apply("테스터"));
        Assert.NotEqual("", ex.Fix);
        Assert.False(svc.Applying);
    }

    [Fact]
    public async Task 관리자가_아니면_업데이트를_다룰_수_없다()
    {
        var page = new GanttScheduler.Pages.Admin.UpdateModel(Service(), new CurrentUser { User = new GanttScheduler.Domain.AppUser { Name = "일반", IsAdmin = false } });
        foreach (var call in new Func<Task<Microsoft.AspNetCore.Mvc.IActionResult>>[]
                 {
                     page.OnPostCheckAsync, page.OnPostDownloadAsync, page.OnPostApplyAsync, page.OnPostRollbackAsync, page.OnPostDiscardAsync,
                     () => page.OnPostSettingsAsync(new GanttScheduler.Pages.Admin.UpdateModel.SettingsInput(true, false, 0, 0, 5)),
                 })
        {
            var result = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(await call());
            Assert.Equal(403, result.StatusCode);
        }
        Assert.True(Service().Status().Settings.AutoApply);   // 바뀌지 않았다
    }

    [Fact]
    public void 받아_둔_것이_없으면_적용할_수_없다() => Assert.Throws<UpdateException>(() => Service().Apply("테스터"));

    private void WriteState(UpdateState state)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "state.json"), JsonSerializer.Serialize(state));
    }

    private string MakePrevious(string version)
    {
        var dir = Path.Combine(DataDir, "previous");
        MakeVersion(dir, version);
        return dir;
    }

    [Fact]
    public void 교체가_끝나고_다시_뜨면_결과를_기록하고_이전_버전을_되돌리기용으로_남긴다()
    {
        // 0.2.0 에서 0.3.0 으로 바뀐 직후 0.3.0 이 처음 뜨는 상황
        var staged = Path.Combine(DataDir, "staged", "0.3.0");
        MakeVersion(staged, "0.3.0");
        MakePrevious("0.2.0");
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.0", staged, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "평영"),
            Pending = new PendingSwap("apply", "0.2.0", "0.3.0", "S1", "평영", DateTime.UtcNow),
        });
        File.WriteAllText(Path.Combine(DataDir, "result.json"),
            JsonSerializer.Serialize(new UpdateApplier.Result(true, "apply", "0.2.0", "0.3.0", null, DateTime.UtcNow)));

        var svc = Service();
        svc.Startup();

        var st = svc.Status();
        Assert.Null(st.Staged);
        Assert.Equal("0.2.0", st.Previous!.Version);
        Assert.True(st.Previous.CanRollback);
        var log = Assert.Single(st.History);
        Assert.True(log.Ok);
        Assert.Equal(("apply", "0.2.0", "0.3.0", "평영"), (log.Kind, log.From, log.To, log.By));
        Assert.False(File.Exists(Path.Combine(DataDir, "result.json")));
        // 방금 적용한 받아 둔 폴더는 이 버전이 요청을 받기 시작할 때까지 남긴다 — 그 전에 죽으면 도우미가 되돌리고
        // 상태를 교체 전으로 돌려 '다시 시도'가 이 폴더를 쓴다
        Assert.True(Directory.Exists(staged));
        svc.Cleanup();
        Assert.True(Directory.Exists(staged));
        svc.MarkStarted();
        svc.Cleanup();
        Assert.False(Directory.Exists(staged));   // 시작한 뒤에는 쓰고 남은 폴더로 정리
    }

    [Fact]
    public void 교체가_실패했으면_원인을_이력에_남기고_받아_둔_파일은_그대로_둔다()
    {
        // 0.3.0 → 0.3.1 교체가 실패해 0.3.0 이 다시 뜬 상황
        var staged = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(staged, "0.3.1");
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "평영"),
            Pending = new PendingSwap("apply", "0.3.0", "0.3.1", "S1", "평영", DateTime.UtcNow),
        });
        File.WriteAllText(Path.Combine(DataDir, "result.json"),
            JsonSerializer.Serialize(new UpdateApplier.Result(false, "apply", "0.3.0", "0.3.1", "서버가 종료되지 않아 파일을 바꿀 수 없습니다.", DateTime.UtcNow)));

        var svc = Service();
        svc.Startup();

        var st = svc.Status();
        var log = Assert.Single(st.History);
        Assert.False(log.Ok);
        Assert.Contains("종료되지", log.Error);
        Assert.Equal("0.3.1", st.Staged!.Version);
        Assert.Null(st.Previous);
        Assert.True(Directory.Exists(staged));
    }

    [Fact]
    public void 되돌린_뒤에는_그_버전을_자동으로_다시_적용하지_않는다()
    {
        // 0.3.1 에서 0.3.0 으로 되돌린 직후 0.3.0 이 뜨는 상황 (결과 파일이 없어도 버전으로 성공을 안다)
        MakePrevious("0.3.0");
        WriteState(new UpdateState
        {
            Previous = new PreviousVersion("0.3.0", "S1", Path.Combine(DataDir, "previous"), DateTime.UtcNow),
            Pending = new PendingSwap("rollback", "0.3.1", "0.3.0", "S1", "평영", DateTime.UtcNow),
        });

        var svc = Service();
        svc.Startup();

        var st = svc.Status();
        Assert.Null(st.Previous);
        Assert.Equal("0.3.1", st.SkipAuto);
        Assert.True(Assert.Single(st.History).Ok);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "previous")));
    }

    [Fact]
    public void DB_구조가_바뀐_업데이트는_프로그램만_되돌릴_수_없다()
    {
        MakePrevious("0.2.0");
        WriteState(new UpdateState { Previous = new PreviousVersion("0.2.0", "S0", Path.Combine(DataDir, "previous"), DateTime.UtcNow) });

        var svc = Service();
        svc.Startup();

        var prev = svc.Status().Previous!;
        Assert.False(prev.CanRollback);
        Assert.Contains("DB", prev.BlockReason);
        Assert.Throws<UpdateException>(() => svc.Rollback("테스터"));
    }

    // ── 자동 적용 판단 ──────────────────────────────────────────────────
    // 2026-10-09 18:00 UTC = 한국 시간 10일 03:00
    private static readonly DateTime Night = new(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Noon = new(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc);    // 한국 시간 12:00

    private static UpdateState Ready(string version = "0.3.1", string schema = "S1") => new()
    {
        Staged = new StagedUpdate(version, "dir", new string('a', 64), schema, null, Night, "download", "자동"),
    };

    [Fact]
    public void 자동_적용은_설정한_시간대에_한동안_사용이_없을_때만_한다()
    {
        var idle = TimeSpan.FromMinutes(31);
        Assert.True(UpdateService.ShouldAutoApply(Ready(), Release, Night, Night - idle));
        // 방금까지 누가 쓰고 있었다
        Assert.False(UpdateService.ShouldAutoApply(Ready(), Release, Night, Night - TimeSpan.FromMinutes(5)));
        // 시간대 밖
        Assert.False(UpdateService.ShouldAutoApply(Ready(), Release, Noon, Noon - idle));
        // 꺼 둠
        var off = Ready();
        off.AutoApply = false;
        Assert.False(UpdateService.ShouldAutoApply(off, Release, Night, Night - idle));
        // 받아 둔 것이 없음
        Assert.False(UpdateService.ShouldAutoApply(new UpdateState(), Release, Night, Night - idle));
    }

    [Fact]
    public void 자동_적용_시간대는_자정을_넘길_수_있고_시작과_끝이_같으면_종일이다()
    {
        var idle = TimeSpan.FromMinutes(31);
        var late = Ready();
        late.WindowFrom = 22;
        late.WindowTo = 4;
        Assert.True(UpdateService.ShouldAutoApply(late, Release, Night, Night - idle));                       // 03시
        Assert.True(UpdateService.ShouldAutoApply(late, Release, Night.AddHours(20), Night.AddHours(19)));   // 23시
        Assert.False(UpdateService.ShouldAutoApply(late, Release, Noon, Noon - idle));                        // 12시

        var always = Ready();
        always.WindowFrom = always.WindowTo = 0;
        Assert.True(UpdateService.ShouldAutoApply(always, Release, Noon, Noon - idle));
    }

    [Fact]
    public void DB_구조가_바뀌거나_되돌렸던_버전은_자동으로_적용하지_않는다()
    {
        var idle = TimeSpan.FromMinutes(31);
        Assert.False(UpdateService.ShouldAutoApply(Ready(schema: "S2"), Release, Night, Night - idle));
        Assert.False(UpdateService.ShouldAutoApply(Ready(schema: ""), Release, Night, Night - idle));

        var skipped = Ready();
        skipped.SkipAuto = "0.3.1";
        Assert.False(UpdateService.ShouldAutoApply(skipped, Release, Night, Night - idle));

        // 이미 지난 버전
        Assert.False(UpdateService.ShouldAutoApply(Ready(version: "0.3.0"), Release, Night, Night - idle));
    }

    [Fact]
    public void 설정은_범위를_확인하고_저장한다()
    {
        var svc = Service();
        svc.SaveSettings(new UpdateSettings(true, false, 22, 4, 60));
        var s = Service().Status().Settings;
        Assert.Equal((true, false, 22, 4, 60), (s.AutoCheck, s.AutoApply, s.WindowFrom, s.WindowTo, s.IdleMinutes));

        Assert.Throws<GanttScheduler.Domain.RuleException>(() => svc.SaveSettings(new UpdateSettings(true, true, 25, 4, 60)));
        Assert.Throws<GanttScheduler.Domain.RuleException>(() => svc.SaveSettings(new UpdateSettings(true, true, 2, 5, 1)));
    }

    [Fact]
    public void 매니페스트를_읽는다()
    {
        var r = UpdateSource.ParseManifest("""
            { "version": "0.7.8", "file": "GanttScheduler-win-x64.zip", "sha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
              "size": 61882790, "published": "2026-10-08T09:00:00Z", "page": "https://example.com/v0.7.8", "notes": "변경 내용",
              "schema": "20261008175313_AdvancedFeatures", "rid": "win-x64" }
            """)!;
        Assert.Equal("0.7.8", r.Version);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.Sha256);
        Assert.Equal(61882790, r.Size);
        Assert.Equal(new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc), r.Published);
        Assert.Equal("20261008175313_AdvancedFeatures", r.Schema);
        Assert.Equal("win-x64", r.Rid);

        Assert.Null(UpdateSource.ParseManifest("""{ "version": "dev" }"""));
        Assert.Null(UpdateSource.ParseManifest("""{ "version": "1.0.0", "sha256": "짧음" }""")!.Sha256);
    }
}
