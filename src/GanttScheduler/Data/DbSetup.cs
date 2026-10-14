using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Data;

// DB 연결 구성 — 웹 서버와 콘솔 명령(admin)이 같은 설정으로 같은 DB 를 열도록 한곳에 둔다
public static class DbSetup
{
    // 운영·미리보기·개발·테스트 기본 DB 는 SQLite. SQL Server 는 Database:Provider=SqlServer 로 명시한 경우에만 사용한다.
    public static bool UseSqlServer(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);

    public static void Configure(DbContextOptionsBuilder options, IConfiguration config, string contentRoot)
    {
        if (UseSqlServer(config))
        {
            // 일시적인 연결 끊김(장애 조치, 네트워크 순단)은 몇 번 다시 시도한다. 코드에서 트랜잭션을 직접 열지 않으므로
            // (SaveChanges 한 번 = 트랜잭션 하나) 재시도 전략과 충돌하지 않는다. 직접 트랜잭션을 열려면 실행 전략 안에서 열어야 한다
            options.UseSqlServer(config.GetConnectionString("SqlServer"),
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDb.Schema).EnableRetryOnFailure());
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
