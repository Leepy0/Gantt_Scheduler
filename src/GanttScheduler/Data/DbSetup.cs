using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Data;

// DB 연결 구성 — 웹 서버와 콘솔 명령(admin)이 같은 설정으로 같은 DB 를 열도록 한곳에 둔다
public static class DbSetup
{
    // DB: 운영은 SQL Server, 미리보기·개발·테스트는 SQLite (Database:Provider 로 선택)
    public static bool UseSqlServer(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);

    public static void Configure(DbContextOptionsBuilder options, IConfiguration config, string contentRoot)
    {
        if (UseSqlServer(config))
        {
            options.UseSqlServer(config.GetConnectionString("SqlServer"),
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDb.Schema));
        }
        else
        {
            // 상대 경로는 앱 폴더 기준으로 풀고, 폴더가 없으면 만든다
            var path = AppPaths.Sqlite(config, contentRoot);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            options.UseSqlite($"Data Source={path}");
        }
    }
}
