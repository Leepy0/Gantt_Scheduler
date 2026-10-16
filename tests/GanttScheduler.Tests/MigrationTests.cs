using GanttScheduler.Data;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 모델(Entities·AppDb)을 바꾸면 SQL Server·SQLite 마이그레이션을 둘 다 추가해야 한다(CLAUDE.md "모델을 바꿀 때").
// 빠뜨리면 서버가 시작할 때 MigrateAsync 가 "pending model changes" 로 멈추므로 여기서 먼저 잡는다. 접속은 하지 않는다
public class MigrationTests
{
    [Fact]
    public void SQLite_마이그레이션은_모델과_같다()
    {
        using var db = new SqliteAppDb(new DbContextOptionsBuilder<SqliteAppDb>().UseSqlite("Data Source=:memory:").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void SQL_Server_마이그레이션은_모델과_같다()
    {
        using var db = new DesignTimeFactory().CreateDbContext(Array.Empty<string>());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void 빌드의_DB_구조_이름은_SQL_Server_마지막_마이그레이션이다()
    {
        using var db = new DesignTimeFactory().CreateDbContext(Array.Empty<string>());
        Assert.Equal(db.Database.GetMigrations().Last(), MigrationInfo.LastSqlServerId());
    }
}
