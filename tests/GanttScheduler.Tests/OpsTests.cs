using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 운영 안전장치: SQLite DB 를 함부로 지우지 않기, SQL Server 일시 장애 재시도
public class OpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gantt-ops-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "gantt.db");

    public OpsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* 정리 실패는 무시 */ }
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "GanttScheduler";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // 시작 작업(DbInitializer)이 쓰는 것만 갖춘 서비스 묶음
    private ServiceProvider Services(string environment)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:SqlitePath"] = DbPath,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<IHostEnvironment>(new Env { EnvironmentName = environment, ContentRootPath = _dir });
        services.AddSingleton<IClock>(new FixedClock());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddDbContext<AppDb>(o => DbSetup.Configure(o, cfg, _dir));
        return services.BuildServiceProvider();
    }

    private async Task InitAsync(string environment)
    {
        await using var sp = Services(environment);
        using var scope = sp.CreateScope();
        await DbInitializer.InitAsync(scope.ServiceProvider);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private async Task AddUserAsync(string name)
    {
        await using var sp = Services(Environments.Production);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Users.Add(new AppUser { Name = name, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task 운영에서_SQLite_구조가_바뀌면_기존_DB_를_지우지_않고_옆에_남긴다()
    {
        await InitAsync(Environments.Production);
        await AddUserAsync("실제 사용자");
        // 다른 구조로 만든 DB 였다고 표시해 둔다 (EF 패치·모델 변경)
        await File.WriteAllTextAsync(DbPath + ".schema", "옛 구조");

        await InitAsync(Environments.Production);

        var backup = Assert.Single(Directory.GetFiles(_dir, "gantt.db.*.bak"));
        Assert.True(new FileInfo(backup).Length > 0);
        // 옮겨 둔 DB 에 데이터가 그대로 있다
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={backup}").Options;
        await using (var old = new AppDb(options))
            Assert.True(await old.Users.AnyAsync(u => u.Name == "실제 사용자"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // 새 DB 는 비어 있고 구조 표시가 새로 기록된다
        await using var sp = Services(Environments.Production);
        using var scope = sp.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDb>().Users.AnyAsync());
        Assert.NotEqual("옛 구조", await File.ReadAllTextAsync(DbPath + ".schema"));
    }

    [Fact]
    public async Task 개발_환경에서는_구조가_바뀌면_미리보기_DB_를_새로_만든다()
    {
        await InitAsync(Environments.Development);
        await File.WriteAllTextAsync(DbPath + ".schema", "옛 구조");
        await InitAsync(Environments.Development);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void 옮길_때_WAL_보조_파일도_같이_옮긴다()
    {
        File.WriteAllText(DbPath, "db");
        File.WriteAllText(DbPath + "-wal", "wal");
        var backup = DbInitializer.SetAside(DbPath, new DateTime(2026, 10, 11, 1, 2, 3, DateTimeKind.Utc));
        Assert.Equal(DbPath + ".20261011010203.bak", backup);
        Assert.Equal("db", File.ReadAllText(backup));
        Assert.Equal("wal", File.ReadAllText(backup + "-wal"));
        Assert.False(File.Exists(DbPath));
        Assert.False(File.Exists(DbPath + "-wal"));
    }

    [Fact]
    public void 서비스가_키를_DPAPI_로_암호화해도_예전에_평문으로_저장한_키를_읽는다()
    {
        // 업데이트로 이 설정이 처음 켜진 서버: 열려 있던 화면의 위조 방지 토큰(옛 평문 키로 만든 것)이 그대로 통해야 한다
        var keys = new DirectoryInfo(Path.Combine(_dir, "keys"));
        IDataProtector Protector(bool dpapi)
        {
            var services = new ServiceCollection();
            var dp = services.AddDataProtection().SetApplicationName("GanttScheduler").PersistKeysToFileSystem(keys);
            if (dpapi && OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
            return services.BuildServiceProvider().GetDataProtectionProvider().CreateProtector("시험");
        }

        var token = Protector(false).Protect("토큰");
        Assert.Contains("<key ", File.ReadAllText(Assert.Single(keys.GetFiles("key-*.xml")).FullName));
        // DPAPI 는 Windows 에만 있다. 다른 운영체제에서는 평문 키 저장까지만 확인한다 (서버는 Windows 서비스에서만 켠다)
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("토큰", Protector(true).Unprotect(token));
    }

    [Fact]
    public void SQL_Server_는_일시적인_연결_오류를_다시_시도한다()
    {
        // 연결하지 않고 구성만 본다 (재시도 전략은 연결 전에 정해진다)
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "SqlServer",
            ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=gs_none;Trusted_Connection=True;TrustServerCertificate=True",
        }).Build();
        var builder = new DbContextOptionsBuilder<AppDb>();
        DbSetup.Configure(builder, cfg, _dir);
        using var db = new AppDb(builder.Options);
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
    }
}
