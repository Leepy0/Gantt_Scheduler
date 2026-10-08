using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GanttScheduler.Data;

// dotnet ef 전용 — 마이그레이션은 항상 SQL Server 기준으로 만든다 (실제 접속은 하지 않음)
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
