using System.IO.Compression;
using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

internal sealed class TestHostEnv : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "GanttScheduler";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

// 시험용 백업 서비스: 앱 폴더(root) 아래에 첨부·키·설정을 두고, DB 는 TestDb(없으면 DB 없이 — 실패 시험용)
internal static class BackupHelper
{
    public static readonly AppBuild Build = new(new Version(0, 3, 0), "abc1234", "release", "S1");

    public static IConfiguration Config(TestDb? db, string root, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = TestDb.UseSqlServer ? "SqlServer" : "Sqlite",
            ["ConnectionStrings:SqlServer"] = TestDb.UseSqlServer && db != null ? db.SqlServerConnection : null,
            ["Database:SqlitePath"] = db?.SqlitePath ?? Path.Combine(root, "none.db"),
            ["Storage:Path"] = Path.Combine(root, "App_Data", "uploads"),
            ["Storage:KeyPath"] = Path.Combine(root, "App_Data", "keys"),
        };
        foreach (var kv in extra ?? new()) settings[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public static BackupService Create(TestDb? db, string root, Dictionary<string, string?>? extra = null)
    {
        var services = new ServiceCollection();
        if (db != null) services.AddScoped(_ => db.Open());
        var sp = services.BuildServiceProvider();
        return new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), Config(db, root, extra), new TestHostEnv { ContentRootPath = root },
            db?.Clock ?? new FixedClock(), NullLogger<BackupService>.Instance, Build);
    }
}

// 백업: SQLite 는 VACUUM INTO 사본, SQL Server 는 BACKUP DATABASE(COPY_ONLY). 첨부·키·운영 설정은 같은 이름의 zip.
// 목록(backups.json)과 Keep 정리, 실패 기록, 야간 백업 판단, 연결 문자열에서 서버만 보이기
public sealed class BackupTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gantt-backup-" + Guid.NewGuid().ToString("N"));
    private string Backups => Path.Combine(_root, "App_Data", "backups");

    public BackupTests()
    {
        Directory.CreateDirectory(_root);
        // 첨부 하나, 암호화 키 하나, 운영 설정
        Put("App_Data/uploads/1/a1b2.png", "그림");
        Put("App_Data/keys/key-1.xml", "<key />");
        Put("appsettings.Production.json", "{ \"Database\": { \"Provider\": \"SqlServer\" } }");
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }

    private void Put(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task SQLite_백업은_DB_사본과_첨부_키_설정_묶음을_같은_이름으로_남긴다()
    {
        if (TestDb.UseSqlServer) return;   // SQL Server 는 아래 시험
        var svc = BackupHelper.Create(_db, _root);
        int projects;
        using (var s = _db.NewScope()) projects = await s.Db.Projects.CountAsync();

        var e = await svc.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);

        Assert.True(e.Ok, e.Error);
        Assert.Equal("gantt-20261009-100000", e.Name);   // 한국 시간 (시험 시계 2026-10-09 01:00 UTC)
        Assert.Equal("Sqlite", e.Provider);
        Assert.Equal(Path.Combine(Backups, e.Name + ".db"), e.DbFile);
        Assert.True(e.DbReachable);
        Assert.Equal(new FileInfo(e.DbFile!).Length, e.DbSize);
        // 사본은 그대로 열리는 SQLite DB 다
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={e.DbFile}").Options;
        await using (var copy = new AppDb(options))
            Assert.Equal(projects, await copy.Projects.CountAsync());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.Equal(Path.Combine(Backups, e.Name + ".files.zip"), e.FilesFile);
        using (var zip = ZipFile.OpenRead(e.FilesFile!))
        {
            var names = zip.Entries.Select(x => x.FullName).ToList();
            Assert.Contains("uploads/1/a1b2.png", names);
            Assert.Contains("keys/key-1.xml", names);
            Assert.Contains("appsettings.Production.json", names);
            using var info = new StreamReader(zip.GetEntry("backup.json")!.Open());
            var text = await info.ReadToEndAsync();
            Assert.Contains(e.Name, text);
            Assert.Contains("0.3.0", text);
        }

        // 목록에 남고, 다시 만든 서비스(서버 재시작)도 읽는다
        var list = BackupHelper.Create(_db, _root).List();
        Assert.Equal(e.Name, Assert.Single(list).Name);
        Assert.True(File.Exists(Path.Combine(Backups, BackupService.ListFile)));
        Assert.Empty(Directory.GetFiles(Backups, "*.tmp"));
        Assert.Equal(e.At, svc.LastOkAt);
    }

    [Fact]
    public async Task 같은_초에_다시_백업해도_이름이_겹치지_않는다()
    {
        var svc = BackupHelper.Create(_db, _root);
        var a = await svc.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);
        var b = await svc.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);
        Assert.True(a.Ok && b.Ok, a.Error ?? b.Error);
        Assert.NotEqual(a.Name, b.Name);
        Assert.Equal(b.Name, svc.List()[0].Name);   // 새것부터
    }

    [Fact]
    public async Task 성공한_백업은_Keep_개만_남기고_오래된_파일을_지운다()
    {
        var svc = BackupHelper.Create(_db, _root, new() { ["Backup:Keep"] = "2" });
        var made = new List<BackupEntry>();
        for (var i = 0; i < 3; i++)
        {
            _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(1);
            made.Add(await svc.RunAsync(i == 0 ? BackupKinds.Nightly : BackupKinds.Manual, "테스터", CancellationToken.None));
        }
        Assert.All(made, e => Assert.True(e.Ok, e.Error));

        var list = svc.List();
        Assert.Equal(new[] { made[2].Name, made[1].Name }, list.Select(e => e.Name));
        // 가장 오래된 백업의 묶음(과 이 서버에서 열 수 있는 DB 사본)은 지웠다
        Assert.False(File.Exists(made[0].FilesFile));
        if (made[0].DbReachable) Assert.False(File.Exists(made[0].DbFile));
        Assert.True(File.Exists(made[2].FilesFile));
        Assert.Equal(2, Directory.GetFiles(Backups, "*.files.zip").Length);
    }

    [Fact]
    public async Task 백업에_실패하면_예외_대신_실패_기록을_남기고_반쪽_파일을_지운다()
    {
        // SQLite: DB 사본 폴더 자리에 파일이 있어 만들 수 없다 / SQL Server: 상대 경로는 SQL Server 기준이 모호해 거절
        File.WriteAllText(Path.Combine(_root, "blocker"), "파일");
        var path = TestDb.UseSqlServer ? "relative/dir" : Path.Combine(_root, "blocker", "sub");
        var svc = BackupHelper.Create(_db, _root, new() { ["Backup:Path"] = path });

        var e = await svc.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);

        Assert.False(e.Ok);
        Assert.False(string.IsNullOrWhiteSpace(e.Error));
        Assert.Null(e.FilesFile);
        Assert.Empty(Directory.GetFiles(Backups, "*.zip*"));
        var saved = Assert.Single(svc.List());
        Assert.False(saved.Ok);
        Assert.Null(svc.LastOkAt);
    }

    [Fact]
    public async Task SQL_Server_는_COPY_ONLY_로_백업하고_경로가_없으면_서버의_기본_폴더에_둔다()
    {
        if (!TestDb.UseSqlServer) return;   // CI 의 SQL Server 단계에서만 돈다
        var svc = BackupHelper.Create(_db, _root);
        Assert.Contains("기본 백업 폴더", svc.Guide);
        Assert.False(svc.DbReachable);

        var e = await svc.RunAsync(BackupKinds.Manual, "테스터", CancellationToken.None);

        Assert.True(e.Ok, e.Error);
        Assert.Equal("SqlServer", e.Provider);
        Assert.Equal(e.Name + ".bak", e.DbFile);   // 파일 이름만 넘겨 SQL Server 의 기본 백업 폴더에 쓴다
        Assert.False(e.DbReachable);
        Assert.Null(e.DbSize);                     // 이 서버에서 볼 수 없는 파일은 크기를 모른다
        Assert.True(File.Exists(e.FilesFile));
        Assert.Contains("크기를 확인하지", e.Note);
    }

    [Fact]
    public void 야간_백업은_켜져_있고_정한_시각이_지났고_오늘_하지_않았을_때만()
    {
        var on = new BackupOptions { Nightly = true, NightlyAt = new TimeOnly(1, 30) };
        var before = new DateTime(2026, 10, 10, 16, 20, 0, DateTimeKind.Utc);   // 한국 10-11 01:20
        var after = new DateTime(2026, 10, 10, 16, 40, 0, DateTimeKind.Utc);    // 한국 10-11 01:40
        var none = new List<BackupEntry>();
        BackupEntry Entry(string kind, DateTime at) => new("x", at, kind, "자동", true, null, "Sqlite", null, true, null, null, null, 1, null);

        Assert.False(BackupService.DueNightly(new BackupOptions { Nightly = false }, after, none));
        Assert.False(BackupService.DueNightly(on, before, none));
        Assert.True(BackupService.DueNightly(on, after, none));
        // 오늘(한국 날짜) 이미 야간 백업을 했으면 다시 하지 않는다 — 실패했어도 같은 날 되풀이하지 않는다
        Assert.False(BackupService.DueNightly(on, after, new List<BackupEntry> { Entry(BackupKinds.Nightly, after.AddMinutes(-5)) }));
        // 어제 한 야간 백업이나 오늘의 '지금 백업'은 상관없다
        Assert.True(BackupService.DueNightly(on, after, new List<BackupEntry> { Entry(BackupKinds.Nightly, after.AddDays(-1)), Entry(BackupKinds.Manual, after.AddMinutes(-5)) }));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData(".", true)]
    [InlineData("(local)", true)]
    [InlineData(@".\SQLEXPRESS", true)]
    [InlineData("tcp:localhost,1433", true)]
    [InlineData(@"(localdb)\MSSQLLocalDB", true)]
    [InlineData("127.0.0.1,1433", true)]
    [InlineData("db-server01", false)]
    [InlineData(@"10.1.2.3\SQL2019", false)]
    [InlineData("", false)]
    public void 같은_컴퓨터의_SQL_Server_인지_연결_문자열의_서버로_판단한다(string server, bool local) =>
        Assert.Equal(local, DbTarget.IsLocalServer(server));

    [Fact]
    public void 원격_DB_의_로컬_경로는_이_서버에서_건드리지_않고_UNC_를_안내한다()
    {
        BackupService Remote(string path) => BackupHelper.Create(null, _root, new()
        {
            ["Database:Provider"] = "SqlServer",
            ["ConnectionStrings:SqlServer"] = "Server=db-server01;Database=Gantt;User Id=gantt;Password=s3cret!;TrustServerCertificate=True",
            ["Backup:Path"] = path,
        });
        var local = Remote(@"D:\Backup");
        Assert.False(local.DbReachable);
        Assert.Contains("UNC", local.Guide);
        // 화면에는 서버·DB 이름만 보이고 계정·비밀번호는 보이지 않는다
        Assert.Equal("SQL Server · 서버 db-server01 · DB Gantt", local.Target.Describe);
        Assert.DoesNotContain("s3cret", local.Status().Guide ?? "");

        var unc = Remote(@"\\nas01\backup\gantt");
        Assert.True(unc.DbReachable);
        Assert.Contains("쓰기 권한", unc.Guide);
    }

    [Fact]
    public void 진단_로그에서_연결_문자열의_계정과_비밀번호를_가린다()
    {
        var text = Redact.Secrets("오류: Server=db1;Database=G;User Id=gantt;Password=s3cret!;Trust=True / pwd = abc");
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("gantt;", text);
        Assert.DoesNotContain("abc", text);
        Assert.Contains("Server=db1", text);
    }
}
