using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Data;

// SQLite 용 컨텍스트. 모델은 AppDb 와 같고, 마이그레이션만 다르다 — EF 는 마이그레이션을 컨텍스트 형으로 고르므로
// (SQL Server 는 Data/Migrations 의 [DbContext(typeof(AppDb))], SQLite 는 Data/Migrations/Sqlite 의 [DbContext(typeof(SqliteAppDb))])
// 같은 모델을 두 공급자에서 각자의 마이그레이션으로 올릴 수 있다. 서비스는 AppDb 로만 받고, DI 가 SQLite 일 때 이 형을 넣는다(DbSetup.AddAppDb)
public class SqliteAppDb : AppDb
{
    public SqliteAppDb(DbContextOptions<SqliteAppDb> options, GanttScheduler.Services.DataVersion? version = null) : base(options, version) { }

    // 마이그레이션을 도입(0.6.0)하기 전의 SQLite DB 는 EnsureCreated 로 만들고 옆의 gantt.db.schema 에 모델 해시를 남겼다.
    // 그 해시가 이 값(첫 SQLite 마이그레이션 'Initial' 과 같은 모델)이면 첫 마이그레이션을 적용된 것으로 표시하고 그대로 이어 쓴다.
    // 값은 그때 모델의 해시로 고정한다 — 모델이 바뀌어도 바꾸지 않는다(그때 만든 DB 를 알아보는 표식이다)
    public const string LegacySchemaHash = "FB7B28922AAA0ED203663730B4832C54541A8F584DDD5AEE25635CB0C6F4607F";
}
