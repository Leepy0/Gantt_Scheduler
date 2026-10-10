using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

public class FixedClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 10, 9);
}

// 테스트용 DB. 기본은 임시 SQLite 파일, TEST_DB=SqlServer 이면 SQL Server 에 마이그레이션을 적용해 같은 테스트를 돌린다
public sealed class TestDb : IDisposable
{
    public static bool UseSqlServer => string.Equals(Environment.GetEnvironmentVariable("TEST_DB"), "SqlServer", StringComparison.OrdinalIgnoreCase);

    // 예: Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True (Database 는 테스트가 붙인다)
    public static string SqlServerBase => Environment.GetEnvironmentVariable("TEST_SQLSERVER")
        ?? throw new InvalidOperationException("TEST_SQLSERVER 환경 변수가 필요합니다.");

    private readonly string _name = "gs_test_" + Guid.NewGuid().ToString("N");
    private readonly string _sqlitePath;
    public FixedClock Clock { get; } = new();
    public AppUser Me { get; }

    public string SqlitePath => _sqlitePath;
    public string SqlServerConnection => SqlServerBase.TrimEnd(';') + ";Database=" + _name;

    public TestDb()
    {
        _sqlitePath = Path.Combine(Path.GetTempPath(), _name + ".db");
        using var db = Open();
        if (UseSqlServer) db.Database.Migrate();
        else db.Database.EnsureCreated();

        Seeder.SeedMastersAsync(db).GetAwaiter().GetResult();
        Seeder.SeedDemoAsync(db, Clock).GetAwaiter().GetResult();

        Me = new AppUser { Name = "테스터", IsAdmin = true, CreatedAt = Clock.UtcNow };
        db.Users.Add(Me);
        db.SaveChanges();
    }

    public AppDb Open()
    {
        var builder = new DbContextOptionsBuilder<AppDb>();
        if (UseSqlServer) builder.UseSqlServer(SqlServerConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDb.Schema));
        else builder.UseSqlite($"Data Source={_sqlitePath}");
        return new AppDb(builder.Options);
    }

    // 요청 하나에 해당하는 서비스 묶음 (새 DbContext).
    // userName 을 주면 그 사용자로 요청한다 — 없으면 만들고, 관리자 여부는 admin 으로 맞춘다 (권한 테스트용)
    public Scope NewScope(string? userName = null, bool admin = false, AccessOptions? access = null) => new(this, userName, admin, access);

    public void Dispose()
    {
        using (var db = Open()) db.Database.EnsureDeleted();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath);
    }

    public sealed class Scope : IDisposable
    {
        public AppDb Db { get; }
        public CurrentUser Me { get; }
        public ProjectService Projects { get; }
        public DashboardService Dashboard { get; }
        public MasterService Masters { get; }
        public UserService Users { get; }
        public InsightService Insights { get; }

        public Scope(TestDb owner, string? userName = null, bool admin = false, AccessOptions? access = null)
        {
            Db = owner.Open();
            Me = new CurrentUser { User = userName == null ? Db.Users.First(u => u.Id == owner.Me.Id) : Act(owner, userName, admin), Ip = "test" };
            Users = new UserService(Db, owner.Clock, access);
            Projects = new ProjectService(Db, owner.Clock, Me, Users);
            Dashboard = new DashboardService(Projects, owner.Clock, Me);
            Masters = new MasterService(Db, owner.Clock, Me);
            Insights = new InsightService(Db, Projects, owner.Clock, Me);
        }

        private AppUser Act(TestDb owner, string name, bool admin)
        {
            var user = Db.Users.FirstOrDefault(u => u.Name == name);
            if (user == null) Db.Users.Add(user = new AppUser { Name = name, CreatedAt = owner.Clock.UtcNow });
            user.IsAdmin = admin;
            Db.SaveChanges();
            return user;
        }

        public void Dispose() => Db.Dispose();
    }
}
