using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GanttScheduler.Data;

// dotnet ef 전용 — 실제 접속은 하지 않는다. 마이그레이션은 공급자마다 한 벌씩 만든다(CLAUDE.md "모델을 바꿀 때"):
//   SQL Server: dotnet ef migrations add <이름> --context AppDb --output-dir Data/Migrations
//   SQLite:     dotnet ef migrations add <이름> --context SqliteAppDb --output-dir Data/Migrations/Sqlite
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDb>
{
    public AppDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlServer("Server=localhost;Database=GanttScheduler;Trusted_Connection=True;TrustServerCertificate=True",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDb.Schema))
            .Options;
        return new AppDb(options);
    }
}

public class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDb>
{
    public SqliteAppDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDb>().UseSqlite("Data Source=design-time.db").Options);
}
