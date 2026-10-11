using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 백업 한 건 (App_Data/backups/backups.json). 버전이 바뀌어도 읽을 수 있게 항목은 추가만 한다.
// Kind: manual(지금 백업) · nightly(야간 자동) · update(구조가 바뀌는 업데이트 적용 전)
// DbFile: DB 백업 파일의 전체 경로(SQL Server 는 그 컴퓨터 기준). DbReachable: 이 서버에서 그 파일을 열 수 있는지 — 크기 확인·정리 여부
public record BackupEntry(
    string Name, DateTime At, string Kind, string By, bool Ok, string? Error, string Provider,
    string? DbFile, bool DbReachable, long? DbSize, string? FilesFile, long? FilesSize, double Seconds, string? Note);

// 업데이트·서버 상태 화면에 보이는 백업 설정과 목록
public record BackupStatus(
    string Provider, string DbLocation, string FilesPath, bool DbReachable, string? Guide, int Keep,
    bool Nightly, string NightlyAt, bool BeforeSchemaUpdate, bool Running, DateTime? LastOkAt, List<BackupEntry> List);

// 연결 문자열에서 서버·DB 이름만 꺼낸다 — 화면에는 이것만 보이고 계정·비밀번호는 보이지 않는다
public record DbTarget(bool SqlServer, string? Server, string? Database, bool Local, string? SqlitePath)
{
    public static DbTarget From(IConfiguration cfg, string contentRoot)
    {
        if (!DbSetup.UseSqlServer(cfg)) return new DbTarget(false, null, null, true, AppPaths.Sqlite(cfg, contentRoot));
        string? server = null, database = null;
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = cfg.GetConnectionString("SqlServer") ?? "" };
            server = First(b, "server", "data source", "address", "addr", "network address");
            database = First(b, "database", "initial catalog");
        }
        catch (ArgumentException) { /* 형식이 틀린 연결 문자열 — 서버를 모르는 것으로 둔다 */ }
        return new DbTarget(true, server, database, IsLocalServer(server), null);
    }

    public string Describe => SqlServer
        ? $"SQL Server · 서버 {Server ?? "(알 수 없음)"} · DB {Database ?? "(기본)"}"
        : $"SQLite · {SqlitePath}";

    private static string? First(DbConnectionStringBuilder b, params string[] keys)
    {
        foreach (var k in keys)
            if (b.TryGetValue(k, out var v) && v?.ToString() is { Length: > 0 } s) return s;
        return null;
    }

    // 같은 컴퓨터의 SQL Server 인지 (tcp:이름,포트 / 이름\인스턴스 형태 포함). 모르면 원격으로 본다 — 원격이면 백업 파일을 건드리지 않는다
    public static bool IsLocalServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        var s = server.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        if (s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return true;
        var cut = s.IndexOfAny(new[] { ',', '\\' });
        if (cut >= 0) s = s[..cut];
        s = s.Trim();
        return s is "." or "(local)" or "127.0.0.1" or "::1"
            || s.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || s.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}

// 백업 설정 (Backup:*)
public class BackupOptions
{
    public string DbPath { get; init; } = "";          // Backup:Path — DB 백업 파일 폴더. SQL Server 는 그 컴퓨터 기준(원격이면 UNC)
    public string FilesPath { get; init; } = "";       // Backup:FilesPath — 파일 묶음(zip)과 목록. 앱 서버 기준 (기본 App_Data/backups)
    public int Keep { get; init; } = 7;                // Backup:Keep — 성공한 백업을 이만큼 남기고 오래된 것을 지운다
    public bool BeforeSchemaUpdate { get; init; } = true;   // Backup:BeforeSchemaUpdate — DB 구조가 바뀌는 업데이트 전에 백업
    public bool Nightly { get; init; }                 // Backup:Nightly — 야간 자동 백업 (DBA 백업과 겹칠 수 있어 기본 꺼짐)
    public TimeOnly NightlyAt { get; init; } = new(1, 0);   // Backup:NightlyAt — 한국 시간 "HH:mm"
    public int TimeoutMinutes { get; init; } = 60;     // Backup:TimeoutMinutes — 큰 DB 의 BACKUP 명령 시간 제한
    public string ContentRoot { get; init; } = "";     // appsettings.Production.json 을 찾을 곳

    public static BackupOptions From(IConfiguration cfg, string contentRoot)
    {
        string Resolve(string? path, string fallback)
        {
            var p = string.IsNullOrWhiteSpace(path) ? fallback : path.Trim();
            return Path.IsPathRooted(p) ? p : Path.Combine(contentRoot, p);
        }
        var at = TimeOnly.TryParseExact(cfg["Backup:NightlyAt"] ?? "", new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : new TimeOnly(1, 0);
        return new BackupOptions
        {
            // SQL Server 의 경로는 그 컴퓨터 기준이라 앱 폴더로 풀지 않는다 (상대 경로는 아래 검사에서 거절)
            DbPath = (cfg["Backup:Path"] ?? "").Trim(),
            FilesPath = Resolve(cfg["Backup:FilesPath"], "App_Data/backups"),
            Keep = Math.Clamp(cfg.GetValue("Backup:Keep", 7), 1, 365),
            BeforeSchemaUpdate = cfg.GetValue("Backup:BeforeSchemaUpdate", true),
            Nightly = cfg.GetValue("Backup:Nightly", false),
            NightlyAt = at,
            TimeoutMinutes = Math.Clamp(cfg.GetValue("Backup:TimeoutMinutes", 60), 1, 24 * 60),
            ContentRoot = contentRoot,
        };
    }
}

public static class BackupKinds
{
    public const string Manual = "manual";
    public const string Nightly = "nightly";
    public const string Update = "update";
}

// DB 백업과 파일 묶음. 구조가 바뀐 업데이트는 프로그램만 되돌릴 수 없고 과제 삭제도 영구라, 백업이 유일한 복구 수단이다.
//  - DB: SQL Server 는 BACKUP DATABASE … WITH COPY_ONLY(DBA 의 백업 체인을 깨지 않음), CHECKSUM, INIT. Express 판을 고려해 압축은 쓰지 않는다.
//        SQLite 는 VACUUM INTO 로 일관된 사본을 만든다.
//  - 파일 묶음: 첨부 저장소, 암호화 키, appsettings.Production.json 을 같은 이름의 zip 으로 — DB 만 복원하면 첨부가 404 가 된다.
//  - 목록은 backups.json 에 원자적으로 쓰고, Backup:Keep 을 넘는 오래된 백업은 이 서버에서 열 수 있는 파일만 지운다.
// 복원 절차는 docs/OPERATIONS.md
public class BackupService
{
    public const string ListFile = "backups.json";

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _cfg;
    private readonly IClock _clock;
    private readonly ILogger<BackupService> _log;
    private readonly AppBuild? _build;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _listGate = new();

    public BackupService(IServiceScopeFactory scopes, IConfiguration cfg, IHostEnvironment env, IClock clock, ILogger<BackupService> log, AppBuild? build = null)
    {
        _scopes = scopes;
        _cfg = cfg;
        _clock = clock;
        _log = log;
        _build = build;
        Options = BackupOptions.From(cfg, env.ContentRootPath);
        Target = DbTarget.From(cfg, env.ContentRootPath);
    }

    public BackupOptions Options { get; }
    public DbTarget Target { get; }
    public bool Running => _gate.CurrentCount == 0;
    private string ListPath => Path.Combine(Options.FilesPath, ListFile);

    // SQLite 는 DB 사본도 앱 서버에 둔다 (Backup:Path 가 없으면 파일 묶음과 같은 폴더)
    private string SqliteDir => string.IsNullOrEmpty(Options.DbPath) ? Options.FilesPath
        : Path.IsPathRooted(Options.DbPath) ? Options.DbPath : Path.Combine(Options.ContentRoot, Options.DbPath);

    // 이 서버에서 DB 백업 파일을 열 수 있는지: SQLite, 또는 같은 컴퓨터의 SQL Server, 또는 UNC 경로.
    // 원격 SQL Server 의 로컬 경로(D:\Backup)는 그 컴퓨터의 폴더라, 이 서버의 같은 이름 폴더를 건드리면 안 된다
    public bool DbReachable => !Target.SqlServer
        || (!string.IsNullOrEmpty(Options.DbPath) && (Target.Local || IsUnc(Options.DbPath)));

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    // DB 백업 파일이 저장되는 곳 (화면 표시용)
    public string DbLocation => !Target.SqlServer ? SqliteDir
        : string.IsNullOrEmpty(Options.DbPath) ? "SQL Server 의 기본 백업 폴더" : Options.DbPath;

    // 설정에 따른 안내 — 원격 DB·경로 미지정은 크기 확인·정리를 하지 못한다
    public string? Guide
    {
        get
        {
            if (!Target.SqlServer) return null;
            if (string.IsNullOrEmpty(Options.DbPath))
                return "Backup:Path 가 비어 있어 SQL Server 의 기본 백업 폴더에 저장합니다. 이 서버에서는 백업 파일의 크기를 확인하거나 오래된 백업을 지우지 못합니다. " +
                       "Backup:Path 를 지정하세요(원격 DB 면 UNC 경로 \\\\서버\\공유).";
            if (!Target.Local && !IsUnc(Options.DbPath))
                return $"DB 서버({Target.Server})가 다른 컴퓨터라 {Options.DbPath} 는 그 컴퓨터의 폴더입니다. 이 서버에서 크기 확인·정리를 하지 못하므로 UNC 경로(\\\\서버\\공유)로 지정하세요.";
            return "SQL Server 서비스 계정에 이 폴더의 쓰기 권한이 있어야 합니다(백업 파일은 SQL Server 가 씁니다).";
        }
    }

    public BackupStatus Status()
    {
        var list = List();
        return new BackupStatus(Target.SqlServer ? "SqlServer" : "Sqlite", DbLocation, Options.FilesPath, DbReachable, Guide, Options.Keep,
            Options.Nightly, Options.NightlyAt.ToString("HH:mm", CultureInfo.InvariantCulture), Options.BeforeSchemaUpdate, Running,
            list.FirstOrDefault(e => e.Ok)?.At, list);
    }

    public DateTime? LastOkAt => List().FirstOrDefault(e => e.Ok)?.At;

    // 목록 (새것부터). 파일이 깨졌으면 빈 목록 — 백업 자체는 계속할 수 있어야 한다
    public List<BackupEntry> List()
    {
        lock (_listGate)
        {
            try
            {
                if (File.Exists(ListPath))
                    return (JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(ListPath)) ?? new()).OrderByDescending(e => e.At).ToList();
            }
            catch (Exception ex) when (ex is JsonException or IOException) { _log.LogWarning("백업 목록을 읽지 못했습니다: {Message}", ex.Message); }
            return new();
        }
    }

    // 백업을 만든다. 실패해도 예외를 던지지 않고 실패 기록(Ok=false, Error)을 돌려준다 — 호출한 쪽(업데이트 적용 등)이 결과로 판단한다.
    // 이미 다른 백업이 돌고 있으면 RuleException
    public async Task<BackupEntry> RunAsync(string kind, string by, CancellationToken ct, string? note = null)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new RuleException("이미 백업하고 있습니다. 끝난 뒤 다시 시도해 주세요.");
        var started = DateTime.UtcNow;
        var at = _clock.UtcNow;
        var name = NewName(at);
        string? dbFile = null, filesFile = null, error = null;
        long? dbSize = null, filesSize = null;
        var notes = new List<string>();
        if (note != null) notes.Add(note);
        try
        {
            Directory.CreateDirectory(Options.FilesPath);
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                (dbFile, dbSize) = await BackupDbAsync(db, name, ct);
            }
            filesFile = Path.Combine(Options.FilesPath, name + ".files.zip");
            var skipped = await Task.Run(() => PackFiles(filesFile, name, at, dbFile), ct);
            filesSize = new FileInfo(filesFile).Length;
            if (skipped > 0) notes.Add($"읽지 못한 파일 {skipped}개는 묶음에서 뺐습니다(로그 참고).");
            if (!DbReachable) notes.Add("DB 백업 파일은 이 서버에서 열 수 없어 크기를 확인하지 않았습니다.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = Explain(ex);
            _log.LogError(ex, "백업 실패 ({Kind}, {By}): {Error}", kind, by, error);
        }
        finally
        {
            _gate.Release();
        }

        var entry = new BackupEntry(name, at, kind, by, error == null, error, Target.SqlServer ? "SqlServer" : "Sqlite",
            dbFile, DbReachable, dbSize, error == null ? filesFile : null, filesSize, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            notes.Count == 0 ? null : string.Join(" ", notes));
        if (error != null && filesFile != null) TryDelete(filesFile);   // 반쪽 묶음은 남기지 않는다
        lock (_listGate)
        {
            var list = List();
            list.Insert(0, entry);
            Save(Prune(list));
        }
        if (entry.Ok) _log.LogInformation("백업 완료 {Name} ({Kind}, {By}, {Seconds}초)", name, kind, by, entry.Seconds);
        return entry;
    }

    // 같은 초에 두 번 눌러도 이름이 겹치지 않게 한다 (VACUUM INTO 는 이미 있는 파일에 쓰지 않는다)
    private string NewName(DateTime at)
    {
        var baseName = "gantt-" + KoreaClock.ToKst(at).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = baseName;
        for (var i = 2; File.Exists(Path.Combine(Options.FilesPath, name + ".files.zip")) || List().Any(e => e.Name == name); i++) name = baseName + "-" + i;
        return name;
    }

    private async Task<(string File, long? Size)> BackupDbAsync(AppDb db, string name, CancellationToken ct)
    {
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(Options.TimeoutMinutes));
        if (db.Database.IsSqlServer())
        {
            var dir = Options.DbPath;
            if (dir.Length > 0 && !Path.IsPathRooted(dir) && !IsUnc(dir))
                throw new RuleException("Backup:Path 는 SQL Server 컴퓨터 기준의 절대 경로(예: D:\\Backup)나 UNC 경로(\\\\서버\\공유)로 적어 주세요.");
            // 경로가 없으면 파일 이름만 넘긴다 → SQL Server 의 기본 백업 폴더. 구분자는 SQL Server 쪽 운영체제에 맞춘다(Linux 용 SQL Server 는 '/')
            var sep = dir.Contains('/') && !dir.Contains('\\') ? "/" : "\\";
            var file = dir.Length == 0 ? name + ".bak" : dir.TrimEnd('\\', '/') + sep + name + ".bak";
            // SQL Server 는 폴더를 만들어 주지 않는다. 이 서버에서 닿는 경로면 만들어 둔다 (권한이 없으면 SQL Server 의 오류로 안내된다)
            if (DbReachable)
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 아래 BACKUP 의 오류로 알린다 */ }
            }
            // DB 이름을 문자열로 이어 붙이지 않으려고 DB_NAME() 을 변수로 넘긴다 (BACKUP 은 이름 자리에 변수를 받는다)
            await db.Database.ExecuteSqlRawAsync(
                "DECLARE @db sysname = DB_NAME(); BACKUP DATABASE @db TO DISK = {0} WITH COPY_ONLY, CHECKSUM, INIT, NAME = {1};",
                new object[] { file, name }, ct);
            long? size = DbReachable && File.Exists(file) ? new FileInfo(file).Length : null;
            return (file, size);
        }
        else
        {
            Directory.CreateDirectory(SqliteDir);
            var file = Path.GetFullPath(Path.Combine(SqliteDir, name + ".db"));
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0};", new object[] { file }, ct);
            return (file, File.Exists(file) ? new FileInfo(file).Length : null);
        }
    }

    // 첨부 저장소·암호화 키·운영 설정을 zip 하나로. 안에 backup.json(이름·시각·버전·DB 파일)을 넣어 복원할 때 짝을 맞출 수 있게 한다.
    // 돌려주는 값은 읽지 못해 뺀 파일 수
    private int PackFiles(string target, string name, DateTime at, string? dbFile)
    {
        var skipped = 0;
        var temp = target + ".partial";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var info = new
                {
                    name, at, version = _build?.VersionText, schema = _build?.Schema, provider = Target.SqlServer ? "SqlServer" : "Sqlite",
                    database = Target.Database, dbFile,
                };
                var entry = zip.CreateEntry("backup.json");
                using (var w = new StreamWriter(entry.Open()))
                    w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

                skipped += AddFolder(zip, AppPaths.Uploads(_cfg, Options.ContentRoot), "uploads");
                skipped += AddFolder(zip, AppPaths.Keys(_cfg, Options.ContentRoot), "keys");
                var settings = Path.Combine(Options.ContentRoot, "appsettings.Production.json");
                if (File.Exists(settings)) skipped += AddFile(zip, settings, "appsettings.Production.json") ? 0 : 1;
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
        return skipped;
    }

    private int AddFolder(ZipArchive zip, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return 0;
        var skipped = 0;
        var backups = Path.GetFullPath(Options.FilesPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            // 백업 폴더가 저장소 안에 있으면 백업이 백업을 묶지 않게 한다
            if (Path.GetFullPath(file).StartsWith(backups, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (!AddFile(zip, file, prefix + "/" + rel)) skipped++;
        }
        return skipped;
    }

    private bool AddFile(ZipArchive zip, string file, string entryName)
    {
        try
        {
            // 이미지·PDF·Office 파일은 이미 압축돼 있어 빠른 압축만 한다
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("백업 묶음에서 뺀 파일 {File}: {Message}", file, ex.Message);
            return false;
        }
    }

    // 성공한 백업은 Keep 개만 남긴다. 실패 기록은 남은 가장 오래된 성공보다 새것만 (최대 10건).
    // 지울 백업의 파일은 이 서버에서 열 수 있는 것만 지운다 — 원격 SQL Server 의 파일은 그쪽에서 정리해야 한다
    private List<BackupEntry> Prune(List<BackupEntry> list)
    {
        list = list.OrderByDescending(e => e.At).ToList();
        var okKept = list.Where(e => e.Ok).Take(Options.Keep).ToList();
        var oldest = okKept.Count == Options.Keep ? okKept[^1].At : DateTime.MinValue;
        var failKept = list.Where(e => !e.Ok && e.At >= oldest).Take(10).ToList();
        var keep = okKept.Concat(failKept).ToHashSet();
        foreach (var e in list.Where(e => !keep.Contains(e)))
        {
            if (e.DbReachable && e.DbFile != null) TryDelete(e.DbFile);
            if (e.FilesFile != null) TryDelete(e.FilesFile);
            _log.LogInformation("오래된 백업 정리: {Name}", e.Name);
        }
        return list.Where(keep.Contains).OrderByDescending(e => e.At).ToList();
    }

    // 쓰다가 끊겨도 목록이 깨지지 않게 임시 파일에 쓰고 바꿔치기한다
    private void Save(List<BackupEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Options.FilesPath);
            var temp = ListPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ListPath, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("백업 목록을 저장하지 못했습니다: {Message}", ex.Message); }
    }

    // 실패 이유를 관리자가 알아들을 말로 (권한·경로가 대부분이다)
    private string Explain(Exception ex)
    {
        var msg = ex.Message.Split('\n')[0].Trim();
        if (ex is RuleException) return msg;
        if (Target.SqlServer && (msg.Contains("Operating system error", StringComparison.OrdinalIgnoreCase) || msg.Contains("Cannot open backup device", StringComparison.OrdinalIgnoreCase)))
            return $"SQL Server 가 백업 파일을 쓰지 못했습니다. Backup:Path({DbLocation})가 SQL Server 컴퓨터에 있는지, SQL Server 서비스 계정에 쓰기 권한이 있는지 확인하세요. ({msg})";
        if (Target.SqlServer && msg.Contains("permission", StringComparison.OrdinalIgnoreCase))
            return $"DB 계정에 백업 권한이 없습니다(db_owner 또는 db_backupoperator 필요). ({msg})";
        if (ex is UnauthorizedAccessException or IOException)
            return $"백업 폴더({Options.FilesPath})에 쓰지 못했습니다. 서버 계정의 쓰기 권한과 디스크 여유를 확인하세요. ({msg})";
        return msg;
    }

    // 야간 자동 백업을 할 때인지: 켜져 있고, 한국 시간으로 정한 시각이 지났고, 오늘(한국 날짜) 야간 백업을 아직 하지 않았을 때
    public static bool DueNightly(BackupOptions o, DateTime utcNow, IEnumerable<BackupEntry> list)
    {
        if (!o.Nightly) return false;
        var kst = KoreaClock.ToKst(utcNow);
        if (TimeOnly.FromDateTime(kst) < o.NightlyAt) return false;
        var today = DateOnly.FromDateTime(kst);
        return !list.Any(e => e.Kind == BackupKinds.Nightly && KoreaClock.KstDate(e.At) == today);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* 다음 정리 때 */ }
    }
}

// 야간 자동 백업 (Backup:Nightly). 1분마다 때가 됐는지 본다 — 실패한 날은 그날 다시 하지 않는다(실패 기록이 남고 서버 상태 화면이 알린다)
public class BackupWorker : BackgroundService
{
    private readonly BackupService _backups;
    private readonly UpdateService _updates;
    private readonly ILogger<BackupWorker> _log;

    public BackupWorker(BackupService backups, UpdateService updates, ILogger<BackupWorker> log)
    {
        _backups = backups;
        _updates = updates;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_backups.Options.Nightly) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_updates.Applying && !_backups.Running && BackupService.DueNightly(_backups.Options, DateTime.UtcNow, _backups.List()))
                        await _backups.RunAsync(BackupKinds.Nightly, "자동", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "야간 백업 확인 실패");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }
}
