using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GanttScheduler.Data;

// 빌드에 든 마이그레이션 이름. 배포의 SchemaId(release.yml)는 SQL Server 마이그레이션의 마지막 이름이라,
// SQLite 서버에서도 "이 빌드의 DB 구조"를 그 이름으로 견준다(서버 상태의 DB 구조 점검)
public static class MigrationInfo
{
    public static string? LastSqlServerId() =>
        typeof(AppDb).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(DbContextAttribute), false).OfType<DbContextAttribute>().Any(a => a.ContextType == typeof(AppDb)))
            .Select(t => t.GetCustomAttributes(typeof(MigrationAttribute), false).OfType<MigrationAttribute>().FirstOrDefault()?.Id)
            .Where(id => id != null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .LastOrDefault();
}
