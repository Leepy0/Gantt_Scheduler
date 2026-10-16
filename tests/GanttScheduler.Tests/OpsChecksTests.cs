using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 서버 상태 점검(OpsChecks) 항목별, 첫 설정 점검표, 첨부 대조·고아 정리, 관리자 알림 칩(OpsAlerts)
public sealed class OpsChecksTests : IDisposable
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gantt-ops-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = new();
    private string Uploads => Path.Combine(_root, "App_Data", "uploads");

    public OpsChecksTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }

    private sealed record Kit(OpsChecks Ops, BackupService Backups, UpdateService Updates, FileLog Log, OpsAlerts Alerts, AppDb Db);

    private Kit Make(Dictionary<string, string?>? extra = null, string environment = "Production", AppBuild? build = null, string source = "")
    {
        var cfg = BackupHelper.Config(_db, _root, extra);
        var env = new TestHostEnv { ContentRootPath = _root, EnvironmentName = environment };
        build ??= BackupHelper.Build;
        var paths = new UpdatePaths { AppDir = Path.Combine(_root, "app"), DataDir = Path.Combine(_root, "App_Data", "updates") };
        var updates = new UpdateService(build, paths, new UpdateSource(source, build, null, null), new ActivityTracker(), new Lifetime(),
            NullLogger<UpdateService>.Instance, cfg);
        var backups = BackupHelper.Create(_db, _root, extra);
        var log = new FileLog(Path.Combine(_root, "App_Data", "logs"), 30);
        _owned.Add(log);
        var db = _db.Open();
        _owned.Add(db);
        var ops = new OpsChecks(db, cfg, env, _db.Clock, updates, backups, log, paths, NullLogger<OpsChecks>.Instance);
        return new Kit(ops, backups, updates, log, new OpsAlerts(updates, backups, _db.Clock, env, cfg), db);
    }

    [Fact]
    public async Task 보고서는_DB_왕복_구조_디스크_폴더_글꼴_공휴일_백업_기준_정보를_모두_점검한다()
    {
        var k = Make();
        var report = await k.Ops.ReportAsync(CancellationToken.None);
        var keys = report.Checks.Select(c => c.Key).ToList();
        foreach (var key in new[] { "db", "schema", "disk", "folders", "dbkind", "pdf", "update", "signature", "attachments", "holidays", "backup", "setup" })
            Assert.Contains(key, keys);
        Assert.Equal(CheckLevel.Ok, report.Checks.Single(c => c.Key == "db").Level);
        Assert.Equal(CheckLevel.Ok, report.Checks.Single(c => c.Key == "folders").Level);
        Assert.Equal("v0.3.0", "v" + report.Server.Version);
        // 연결 문자열 대신 DB 종류·위치만
        Assert.StartsWith(TestDb.UseSqlServer ? "SQL Server" : "SQLite", report.Server.Db);
        Assert.DoesNotContain("Password", report.Server.Db, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DB_구조는_마이그레이션_이력과_빌드_정보로_본다()
    {
        // 시험 DB 는 마이그레이션으로 만들었으므로 적용 안 된 것이 없다. 빌드 정보의 구조값(SchemaId = SQL Server 마지막 마이그레이션)이
        // 같으면 정상, 다르면(시험 기본값 'S1') 배포 패키지가 어긋났다는 경고 — SQLite 서버도 같은 이름으로 견준다
        var stale = Make();
        var last = MigrationInfo.LastSqlServerId()!;
        Assert.Equal(CheckLevel.Ok, (await Make(build: BackupHelper.Build with { Schema = last }).Ops.SchemaAsync(CancellationToken.None)).Level);
        var warn = await stale.Ops.SchemaAsync(CancellationToken.None);
        Assert.Equal(CheckLevel.Warn, warn.Level);
        Assert.Contains(last, warn.Message);
        if (TestDb.UseSqlServer) return;

        // 모르는 마이그레이션이 적용돼 있으면(프로그램보다 새 DB) 실패
        var k = Make(build: BackupHelper.Build with { Schema = last });
        await k.Db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99991231000000_Future', '10.0.0');");
        var r = await k.Ops.SchemaAsync(CancellationToken.None);
        Assert.Equal(CheckLevel.Fail, r.Level);
        Assert.Contains("Future", r.Message);
        Assert.NotNull(r.Fix);
        await k.Db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '99991231000000_Future';");
    }

    [Fact]
    public void DB_종류는_SQLite_도_운영에서_정상이다()
    {
        // SQLite 가 기본 운영 DB 다(마이그레이션으로 구조를 올린다) — 종류·위치만 보여 주고 경고하지 않는다
        Assert.Equal(CheckLevel.Ok, Make().Ops.DbKind().Level);
        Assert.Equal(CheckLevel.Ok, Make(environment: "Development").Ops.DbKind().Level);
        Assert.StartsWith(TestDb.UseSqlServer ? "SQL Server" : "SQLite", Make().Ops.DbKind().Message);
    }

    [Theory]
    [InlineData(100L * 1024 * 1024 * 1024, 2000L * 1024 * 1024 * 1024, "ok")]    // 2TB 의 5% — 남은 양이 넉넉하다
    [InlineData(20L * 1024 * 1024 * 1024, 250L * 1024 * 1024 * 1024, "ok")]
    [InlineData(8L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, "warn")]     // 8% · 10GB 미만
    [InlineData(1536L * 1024 * 1024, 40L * 1024 * 1024 * 1024, "warn")]         // 2GB 미만 (3.75%)
    [InlineData(1L * 1024 * 1024 * 1024, 2000L * 1024 * 1024 * 1024, "fail")]    // 큰 디스크라도 1GB 는 0.05% — 실패
    [InlineData(3L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024, "fail")]     // 1.5% · 5GB 미만
    [InlineData(300L * 1024 * 1024, 1000L * 1024 * 1024 * 1024, "fail")]         // 500MB 미만
    public void 디스크는_작으면_비율로_크면_남은_양으로_본다(long free, long total, string level) =>
        Assert.Equal(level, OpsChecks.DiskLevel(free, total));

    [Fact]
    public void 드라이브_여유는_데이터_첨부_백업_경로를_보고_같은_드라이브는_한_번만_센다()
    {
        // 가장 나쁜 드라이브가 판정을 정한다 (데이터 드라이브는 넉넉해도 백업 드라이브가 가득 차면 실패)
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal(CheckLevel.Fail, OpsChecks.WorstDiskLevel(new[] { ("데이터", "C:\\", 500 * gb, 1000 * gb), ("백업", "D:\\", gb / 10, 1000 * gb) }));
        Assert.Equal(CheckLevel.Ok, OpsChecks.WorstDiskLevel(Array.Empty<(string, string, long, long)>()));

        var cfg = BackupHelper.Config(_db, _root);
        var same = OpsChecks.Drives(cfg, _root, Path.Combine(_root, "App_Data", "backups"));
        Assert.Equal("데이터", Assert.Single(same).Label);

        // 백업 묶음을 다른 드라이브(마운트)에 두면 그 드라이브도 본다 — 서버 상태와 관리자 칩이 같은 목록을 쓴다
        var other = DriveInfo.GetDrives().Select(d =>
        {
            try { return d.IsReady && d.TotalSize > 0 ? d.RootDirectory.FullName : null; }
            catch (Exception) { return null; }
        }).FirstOrDefault(r => r != null && r != same[0].Root && OpsChecks.DriveOf(r)?.Root == r);
        if (other == null) return;   // 드라이브가 하나뿐인 시험 환경
        var two = OpsChecks.Drives(cfg, _root, other);
        Assert.Equal(new[] { "데이터", "백업" }, two.Select(d => d.Label));
    }

    [Fact]
    public void 쓸_수_없는_폴더가_있으면_실패와_권한_부여_방법을_알린다()
    {
        File.WriteAllText(Path.Combine(_root, "blocker"), "파일");
        var r = Make(new() { ["Storage:Path"] = Path.Combine(_root, "blocker", "uploads") }).Ops.Folders();
        Assert.Equal(CheckLevel.Fail, r.Level);
        Assert.Contains("첨부 저장소", r.Message);
        Assert.Contains("icacls", r.Fix);
    }

    [Fact]
    public void PDF_글꼴은_설정한_파일을_먼저_찾는다()
    {
        var font = Path.Combine(_root, "my.ttf");
        File.WriteAllText(font, "글꼴");
        var r = Make(new() { ["Pdf:Font"] = font }).Ops.PdfFont();
        Assert.Equal(CheckLevel.Ok, r.Level);
        Assert.StartsWith(font, r.Message);
    }

    [Fact]
    public async Task 공휴일은_올해와_10월부터는_내년까지_확인한다()
    {
        var k = Make();
        Assert.Equal(CheckLevel.Ok, (await k.Ops.HolidaysAsync(CancellationToken.None)).Level);   // 샘플: 2026·2027

        using (var s = _db.NewScope())
        {
            s.Db.Holidays.RemoveRange(s.Db.Holidays.Where(h => h.Date >= new DateOnly(2027, 1, 1)));
            await s.Db.SaveChangesAsync();
        }
        var r = await k.Ops.HolidaysAsync(CancellationToken.None);
        Assert.Equal(CheckLevel.Warn, r.Level);   // 시험 시계 2026-10-09 — 10월부터는 내년 공휴일이 필요하다
        Assert.Contains("2027년", r.Message);
        Assert.Contains(await k.Ops.SetupAsync(CancellationToken.None), s => s.Key == "holiday-next" && !s.Done);
        var alerts = await k.Alerts.ForAdminAsync(k.Db);
        Assert.Contains(alerts, a => a.Key == "holidays" && a.Href == "/Admin#holiday");

        // 9월에는 내년 공휴일을 보지 않는다
        _db.Clock.Today = new DateOnly(2026, 9, 1);
        Assert.Equal(CheckLevel.Ok, (await k.Ops.HolidaysAsync(CancellationToken.None)).Level);
        Assert.DoesNotContain(await k.Ops.SetupAsync(CancellationToken.None), s => s.Key == "holiday-next");
    }

    [Fact]
    public async Task 첫_설정_점검표는_법인_조직이_없으면_덜_된_것으로_보고_관리_화면으로_보낸다()
    {
        var k = Make();
        var setup = await k.Ops.SetupAsync(CancellationToken.None);
        Assert.True(setup.Single(s => s.Key == "corp").Done);
        Assert.True(setup.Single(s => s.Key == "org").Done);
        Assert.DoesNotContain(await k.Alerts.ForAdminAsync(k.Db), a => a.Key == "setup");

        using (var s = _db.NewScope())
        {
            await s.Db.Locations.ForEachAsync(l => l.IsActive = false);
            await s.Db.OrgUnits.ForEachAsync(o => o.IsActive = false);
            await s.Db.SaveChangesAsync();
        }
        setup = await k.Ops.SetupAsync(CancellationToken.None);
        var corp = setup.Single(s => s.Key == "corp");
        Assert.False(corp.Done);
        Assert.Equal("/Admin#location", corp.Link);
        Assert.False(setup.Single(s => s.Key == "org").Done);
        var check = k.Ops.SetupCheck(setup);
        Assert.Equal(CheckLevel.Warn, check.Level);
        Assert.Contains("법인·위치", check.Message);
        Assert.False((await k.Ops.ReportAsync(CancellationToken.None)).SetupDone);
        // 법인이 없으면 과제를 등록할 수 없으므로 관리자 칩이 점검표(서버 상태)로 보낸다
        k.Alerts.Invalidate();
        var alert = (await k.Alerts.ForAdminAsync(k.Db))[0];
        Assert.Equal(("setup", "/Admin/Status"), (alert.Key, alert.Href));
    }

    [Fact]
    public async Task 백업_경로는_SQL_Server_일_때만_따로_지정해야_한다()
    {
        async Task<SetupItem> BackupItem(Dictionary<string, string?> settings) =>
            (await Make(settings).Ops.SetupAsync(CancellationToken.None)).Single(s => s.Key == "backup");
        // SQLite 는 앱 폴더(App_Data/backups)에 둔다
        Assert.True((await BackupItem(new() { ["Database:Provider"] = "Sqlite" })).Done);
        // SQL Server 는 그 컴퓨터 기준의 경로를 정해야 한다 (원격이면 UNC)
        var sql = new Dictionary<string, string?> { ["Database:Provider"] = "SqlServer", ["ConnectionStrings:SqlServer"] = "Server=db1;Database=G" };
        var none = await BackupItem(sql);
        Assert.False(none.Done);
        Assert.Contains("Backup:Path", none.Hint);
        sql["Backup:Path"] = @"\\nas01\backup";
        Assert.True((await BackupItem(sql)).Done);
    }

    [Fact]
    public async Task 첨부는_DB_와_디스크를_대조하고_고아_파일만_지운다()
    {
        var k = Make();
        int projectId;
        using (var s = _db.NewScope())
        {
            projectId = (await s.Db.Projects.FirstAsync()).Id;
            s.Db.Attachments.Add(new Attachment { ProjectId = projectId, FileName = "있음.png", StoredName = "keep.png", ContentType = "image/png", Size = 4, UploadedById = s.Me.Id, UploadedAt = _db.Clock.UtcNow });
            s.Db.Attachments.Add(new Attachment { ProjectId = projectId, FileName = "없음.png", StoredName = "gone.png", ContentType = "image/png", Size = 9, UploadedById = s.Me.Id, UploadedAt = _db.Clock.UtcNow });
            await s.Db.SaveChangesAsync();
        }
        var dir = Path.Combine(Uploads, projectId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.png"), "그림");
        var orphan = Path.Combine(dir, "orphan.png");
        File.WriteAllText(orphan, "남은 파일");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        // 막 올라오는 중인 파일(기록 전)은 고아로 보지 않는다
        var uploading = Path.Combine(dir, "uploading.png");
        File.WriteAllText(uploading, "올리는 중");
        // 과제 번호 폴더가 아닌 곳의 파일은 건드리지 않는다
        Directory.CreateDirectory(Path.Combine(Uploads, "manual"));
        File.WriteAllText(Path.Combine(Uploads, "manual", "readme.txt"), "사람이 넣은 파일");

        var a = await k.Ops.AuditAttachmentsAsync(CancellationToken.None);
        Assert.Equal(2, a.Count);
        Assert.Equal(1, a.MissingCount);
        Assert.Equal($"{projectId}/gone.png", Assert.Single(a.Missing));
        Assert.Equal(1, a.OrphanCount);
        Assert.Equal($"{projectId}/orphan.png", Assert.Single(a.Orphans));
        Assert.Equal(3, a.DiskFiles);
        Assert.Equal(CheckLevel.Warn, k.Ops.Attachments(a).Level);

        var (removed, _) = await k.Ops.CleanOrphansAsync("테스터", CancellationToken.None);
        Assert.Equal(1, removed);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(dir, "keep.png")));
        Assert.True(File.Exists(uploading));
        Assert.True(File.Exists(Path.Combine(Uploads, "manual", "readme.txt")));
        Assert.Equal(0, (await k.Ops.AuditAttachmentsAsync(CancellationToken.None)).OrphanCount);
    }

    [Fact]
    public async Task 마지막_백업은_없거나_실패했거나_오래되면_알린다()
    {
        var k = Make();
        Assert.Equal(CheckLevel.Ok, k.Ops.LastBackup().Level);   // 야간 백업을 끈 서버(DBA 백업)는 없어도 괜찮다
        Assert.Equal(CheckLevel.Warn, Make(new() { ["Backup:Nightly"] = "true" }).Ops.LastBackup().Level);

        var e = await k.Backups.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);
        Assert.True(e.Ok, e.Error);
        var r = k.Ops.LastBackup();
        Assert.Equal(CheckLevel.Ok, r.Level);
        Assert.Contains(e.Name, r.Message);

        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(8);
        Assert.Equal(CheckLevel.Warn, k.Ops.LastBackup().Level);

        // 마지막 백업이 실패했으면 이유와 함께 (관리자 칩에도)
        File.WriteAllText(Path.Combine(_root, "blocker"), "파일");
        var failing = Make(new() { ["Backup:Path"] = TestDb.UseSqlServer ? "relative" : Path.Combine(_root, "blocker", "x") });
        Assert.False((await failing.Backups.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None)).Ok);
        r = failing.Ops.LastBackup();
        Assert.Equal(CheckLevel.Warn, r.Level);
        Assert.Contains("실패", r.Message);
        Assert.Contains(await failing.Alerts.ForAdminAsync(failing.Db), a => a.Key == "backup-failed");
    }

    [Fact]
    public async Task 관리자_알림은_시작_실패와_새_버전을_칩으로_알린다()
    {
        var k = Make();
        Assert.DoesNotContain(await k.Alerts.ForAdminAsync(k.Db), a => a.Key.StartsWith("update"));

        // 교체 도우미가 기록한 시작 실패
        var data = Path.Combine(_root, "App_Data", "updates");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(new UpdateState
        {
            StartFailure = new StartFailure("0.3.1", "0.3.0", "새 버전이 시작되지 않아 되돌렸습니다.", null, true, DateTime.UtcNow),
            Latest = new ReleaseInfo("0.3.2", "x.zip", null, 0, null, null, null, "S1", null),
        }));
        var alerts = await Make().Alerts.ForAdminAsync(k.Db);
        Assert.Equal("update-failed", alerts[0].Key);   // 문제가 먼저, 새 버전 알림은 마지막
        Assert.Equal("v0.3.1 시작 실패", alerts[0].Text);
        Assert.Equal("v0.3.2 업데이트", alerts[^1].Text);
        Assert.Equal("accent", alerts[^1].Level);
    }

    [Fact]
    public async Task 관리자_알림은_교체_실패_확인_연속_실패_백업_실패_오래된_백업을_알린다()
    {
        var now = _db.Clock.UtcNow;
        var data = Path.Combine(_root, "App_Data", "updates");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(new UpdateState
        {
            History = { new UpdateLog(now.AddDays(-1), "apply", "0.3.0", "0.3.1", "평영", false, "파일이 잠겨 있습니다.") },
            CheckFailedSince = now.AddDays(-4),
            CheckedAt = now.AddHours(-2),
            CheckError = "연결 시간 초과",
        }));
        var backups = Path.Combine(_root, "App_Data", "backups");
        Directory.CreateDirectory(backups);
        void Backups(params BackupEntry[] list) => File.WriteAllText(Path.Combine(backups, BackupService.ListFile), System.Text.Json.JsonSerializer.Serialize(list));
        BackupEntry Entry(bool ok, DateTime at) => new("gantt-x", at, BackupKinds.Nightly, "자동", ok, ok ? null : "쓰기 권한 없음", "Sqlite", null, true, null, null, null, 1, null);

        Backups(Entry(false, now.AddHours(-1)), Entry(true, now.AddDays(-1)));
        var k = Make(source: Path.Combine(_root, "releases"));
        var keys = (await k.Alerts.ForAdminAsync(k.Db)).Select(a => a.Key).ToList();
        Assert.Equal("update-failed", keys[0]);
        Assert.Contains("check-failing", keys);
        Assert.Contains("backup-failed", keys);

        // 야간 백업을 켠 서버는 성공한 백업이 이틀 넘게 없으면 알린다 (끈 서버는 DBA 백업을 쓰므로 알리지 않는다)
        Backups(Entry(true, now.AddDays(-3)));
        Assert.DoesNotContain("backup-old", (await Make().Alerts.ForAdminAsync(k.Db)).Select(a => a.Key));
        Assert.Contains("backup-old", (await Make(new() { ["Backup:Nightly"] = "true" }).Alerts.ForAdminAsync(k.Db)).Select(a => a.Key));
        Backups(Entry(true, now.AddHours(-3)));
        Assert.DoesNotContain("backup-old", (await Make(new() { ["Backup:Nightly"] = "true" }).Alerts.ForAdminAsync(k.Db)).Select(a => a.Key));
    }

    [Fact]
    public async Task 업데이트_확인_실패_칩은_자동_확인이_돌며_계속_실패할_때만_뜬다()
    {
        var now = _db.Clock.UtcNow;
        var data = Path.Combine(_root, "App_Data", "updates");
        Directory.CreateDirectory(data);
        var source = Path.Combine(_root, "releases");
        async Task<bool> Chip(UpdateState state, string src)
        {
            File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(state));
            var k = Make(source: src);
            return (await k.Alerts.ForAdminAsync(k.Db)).Any(a => a.Key == "check-failing");
        }
        UpdateState Failing() => new() { CheckFailedSince = now.AddDays(-4), CheckedAt = now.AddHours(-1), CheckError = "연결 시간 초과" };

        Assert.True(await Chip(Failing(), source));
        // 사흘이 안 됐으면 아직 알리지 않는다
        var fresh = Failing();
        fresh.CheckFailedSince = now.AddDays(-1);
        Assert.False(await Chip(fresh, source));
        // 자동 확인을 끈 서버(인터넷이 없는 사내 서버): 예전에 한 번 '지금 확인'이 실패한 기록이 영영 칩으로 남지 않는다
        var off = Failing();
        off.AutoCheck = false;
        Assert.False(await Chip(off, source));
        // 받아 오는 곳이 비어 확인 자체를 하지 않는 서버
        Assert.False(await Chip(Failing(), ""));
        // 마지막 확인이 오래전(주기 6시간의 두 배를 넘음) — 확인이 멈춘 뒤의 옛 실패
        var stale = Failing();
        stale.CheckedAt = now.AddDays(-2);
        Assert.False(await Chip(stale, source));
        // 마지막 확인은 성공 — 실패 기록이 남아 있어도 지금은 문제가 아니다
        var ok = Failing();
        ok.CheckError = null;
        Assert.False(await Chip(ok, source));

        // 자동 확인을 끄면 '계속 실패'를 새로 센다
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(Failing()));
        var svc = Make(source: source).Updates;
        svc.SaveSettings(new UpdateSettings(true, false, 2, 5, 30));    // 다른 설정만 바꾸면 그대로
        Assert.NotNull(svc.CheckFailedSince);
        svc.SaveSettings(new UpdateSettings(false, false, 2, 5, 30));
        Assert.Null(svc.CheckFailedSince);
    }

    [Fact]
    public async Task 시작_실패_안내를_닫으면_같은_실패를_다른_칩으로_다시_알리지_않는다()
    {
        var data = Path.Combine(_root, "App_Data", "updates");
        Directory.CreateDirectory(data);
        // 이전 버전이 다시 떠서 남긴 실패 이력 + 교체 도우미가 남긴 시작 실패 (시각은 실제 시계 — 닫은 시각과 비교한다)
        var at = DateTime.UtcNow.AddHours(-1);
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(new UpdateState
        {
            History = { new UpdateLog(at, "apply", "0.3.0", "0.3.1", "자동", false, "새 버전이 시작되지 않아 되돌렸습니다.") },
            StartFailure = new StartFailure("0.3.1", "0.3.0", "새 버전이 시작되지 않아 되돌렸습니다.", null, true, at),
        }));
        var k = Make();
        Assert.Equal("v0.3.1 시작 실패", Assert.Single(await k.Alerts.ForAdminAsync(k.Db), a => a.Key == "update-failed").Text);

        k.Updates.DismissFailure();
        Assert.DoesNotContain(await k.Alerts.ForAdminAsync(k.Db), a => a.Key == "update-failed");
        // 다시 띄워도(상태 파일에서 읽어도) 그대로
        Assert.DoesNotContain(await Make().Alerts.ForAdminAsync(k.Db), a => a.Key == "update-failed");

        // 닫은 뒤에 생긴 새 실패는 알린다
        var state = System.Text.Json.JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(Path.Combine(data, "state.json")))!;
        state.History.Add(new UpdateLog(DateTime.UtcNow.AddMinutes(1), "apply", "0.3.0", "0.3.2", "평영", false, "파일이 잠겨 있습니다."));
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(state));
        Assert.Equal("업데이트 실패", Assert.Single(await Make().Alerts.ForAdminAsync(k.Db), a => a.Key == "update-failed").Text);
    }

    [Fact]
    public async Task 준비_점검은_DB_와_디스크만_본다()
    {
        var ready = await Make().Ops.ReadyAsync(CancellationToken.None);
        Assert.Equal(new[] { "db", "disk" }, ready.Select(c => c.Key));
        Assert.Equal(CheckLevel.Ok, ready[0].Level);
    }
}
