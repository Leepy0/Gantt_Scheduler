using System.Diagnostics;
using System.Text.RegularExpressions;
using GanttScheduler.Data;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 점검 결과 한 줄 — 업데이트 오류(UpdateException)처럼 원인(Message)과 해결 방법(Fix)을 같이 준다. Level: ok · warn · fail
public record CheckResult(string Key, string Name, string Level, string Message, string? Fix = null, string? Link = null);

public static class CheckLevel
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

// 첫 설정 점검표 한 항목 (관리 > 서버 상태 맨 위). Link: 그 설정을 하는 화면
public record SetupItem(string Key, string Name, bool Done, string Detail, string Hint, string? Link);

// 첨부 DB ↔ 디스크 대조. Missing: DB 에는 있는데 파일이 없는 것(내려받기 404), Orphans: 파일만 남은 것(용량만 차지)
public record AttachmentAudit(int Count, long DbBytes, long DiskBytes, int DiskFiles, int MissingCount, int OrphanCount, long OrphanBytes,
    List<string> Missing, List<string> Orphans);

public record ServerInfo(string Version, string Channel, string Host, string Environment, DateTime StartedAt, string Db, string AppDir,
    string DataDir, string LogDir, string StorageDir, string BackupDir);

public record OpsReport(ServerInfo Server, bool SetupDone, List<SetupItem> Setup, List<CheckResult> Checks, AttachmentAudit Attachments,
    List<LogEntry> Logs, DateTime CheckedAt);

// 로그·진단 파일을 화면에 보이거나 내려받게 할 때 연결 문자열의 계정·비밀번호를 가린다
public static class Redact
{
    private static readonly Regex Secret = new(@"(?i)\b(password|pwd|user id|uid|user)\s*=\s*([^;""'\r\n]*)", RegexOptions.Compiled);

    public static string Secrets(string text) => Secret.Replace(text, m => m.Groups[1].Value + "=***");
}

// 서버 상태 점검 — 관리 > 서버 상태, /health/ready, 관리자 상단 칩(OpsAlerts)이 같은 판정을 쓴다.
// 읽기 전용이다(폴더 쓰기 시험 파일만 만들고 바로 지운다). 고아 파일 정리만 파일을 지운다
public class OpsChecks
{
    // 디스크 여유: 이보다 적으면 경고·실패(비율은 남은 양이 10GB·5GB 아래일 때만 본다).
    // 백업·업데이트(런타임 포함 zip 수십 MB × 몇 벌)와 첨부가 같은 드라이브를 쓴다
    public const long WarnFreeBytes = 2L * 1024 * 1024 * 1024;
    public const long FailFreeBytes = 500L * 1024 * 1024;
    public const double WarnFreeRatio = 0.10;
    public const double FailFreeRatio = 0.03;
    public const long WarnRatioBelowBytes = 10L * 1024 * 1024 * 1024;   // 비율 경고는 남은 양이 이보다 적을 때만
    public const long FailRatioBelowBytes = 5L * 1024 * 1024 * 1024;
    // 막 올리는 중인 첨부는 파일을 먼저 쓰고 DB 에 기록하므로, 최근 파일은 고아로 보지 않는다
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(10);
    private static readonly DateTime ProcessStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly AppDb _db;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly IClock _clock;
    private readonly UpdateService _updates;
    private readonly BackupService _backups;
    private readonly FileLog _log;
    private readonly UpdatePaths _paths;
    private readonly ILogger<OpsChecks> _logger;

    public OpsChecks(AppDb db, IConfiguration cfg, IHostEnvironment env, IClock clock, UpdateService updates, BackupService backups,
        FileLog log, UpdatePaths paths, ILogger<OpsChecks> logger)
    {
        _db = db;
        _cfg = cfg;
        _env = env;
        _clock = clock;
        _updates = updates;
        _backups = backups;
        _log = log;
        _paths = paths;
        _logger = logger;
    }

    private string Root => _env.ContentRootPath;
    // 업데이트 상태는 한 번만 읽는다 — Status() 는 프로그램 폴더 쓰기 시험을 한다
    private UpdateStatus? _status;
    private UpdateStatus UpdateStatus => _status ??= _updates.Status();
    public string DataDir => Path.Combine(Root, "App_Data");
    public string StorageDir => AppPaths.Uploads(_cfg, Root);

    // ── 전체 보고서 (서버 상태 화면) ─────────────────────────────────────
    public async Task<OpsReport> ReportAsync(CancellationToken ct)
    {
        var audit = await AuditAttachmentsAsync(ct);
        var setup = await SetupAsync(ct);
        var checks = new List<CheckResult>
        {
            await DbRoundTripAsync(ct),
            await SchemaAsync(ct),
            Disk(),
            Folders(),
            DbKind(),
            PdfFont(),
            UpdateSource(),
            Signature(),
            Attachments(audit),
            await HolidaysAsync(ct),
            LastBackup(),
            SetupCheck(setup),
            Mail(),
        };
        var st = UpdateStatus;
        var target = _backups.Target;
        var server = new ServerInfo(st.Version, st.ChannelName, st.Host, _env.EnvironmentName, ProcessStarted, target.Describe, _paths.AppDir,
            DataDir, _log.Dir, StorageDir, _backups.Options.FilesPath);
        return new OpsReport(server, setup.All(s => s.Done), setup, checks, audit,
            _log.Recent().ToList(), _clock.UtcNow);
    }

    // /health/ready: DB 와 디스크만 본다 (서버를 계속 쓸 수 있는지). 감시 도구가 자주 부르므로 가볍게
    public async Task<List<CheckResult>> ReadyAsync(CancellationToken ct) => new() { await DbRoundTripAsync(ct), Disk() };

    // ── 개별 점검 ─────────────────────────────────────────────────────

    // 메일 요약(Notify:*): 꺼져 있으면 그렇다고만 알리고, 켜져 있으면 메일 서버 설정과 마지막 요약 결과(App_Data/notify.json)를 본다
    public CheckResult Mail()
    {
        const string name = "메일 요약";
        var o = NotifyOptions.From(_cfg);
        if (!o.Enabled)
            return new CheckResult("mail", name, CheckLevel.Ok, "꺼짐 — 알림은 화면의 종에서만 봅니다." + (o.Smtp.Configured ? " (메일 서버는 설정됨)" : ""));
        if (!o.Smtp.Configured)
            return new CheckResult("mail", name, CheckLevel.Warn, "Notify:Enabled 가 켜져 있는데 메일 서버(Notify:Smtp:Host·From)가 비어 있습니다.",
                "appsettings 의 Notify:Smtp 를 채우고 서버를 다시 시작하세요.");
        var last = NotifyState.Load(AppPaths.Notify(_cfg, Root));
        var where = $"{o.Smtp.Host}:{o.Smtp.Port} · 근무일 {o.DailyHour}시(한국 시간)";
        if (last == null) return new CheckResult("mail", name, CheckLevel.Ok, where + " — 아직 보낸 적 없음", null, "/Admin");
        if (last.Failed > 0)
            return new CheckResult("mail", name, CheckLevel.Warn,
                $"{last.Date:MM-dd} 요약에서 {last.Failed}명에게 보내지 못했습니다: " + Redact.Secrets(last.Error ?? ""),
                "관리 > 사용자에서 시험 메일로 메일 서버 설정을 확인하세요.", "/Admin");
        return new CheckResult("mail", name, CheckLevel.Ok, $"{where} — 마지막 {last.Date:MM-dd} {last.Sent}명에게 보냄");
    }
    public async Task<CheckResult> DbRoundTripAsync(CancellationToken ct)
    {
        const string name = "DB 연결";
        var sw = Stopwatch.StartNew();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await _db.Database.CanConnectAsync(limit.Token))
                return new CheckResult("db", name, CheckLevel.Fail, "DB 에 연결하지 못했습니다.", "연결 문자열(ConnectionStrings:SqlServer)과 DB 서버 상태, 서비스 계정의 DB 권한을 확인하세요.");
            await _db.Users.AnyAsync(limit.Token);   // 연결만이 아니라 실제 조회가 되는지
            var ms = sw.ElapsedMilliseconds;
            return ms > 1000
                ? new CheckResult("db", name, CheckLevel.Warn, $"응답이 느립니다 ({ms}ms).", "DB 서버의 부하와 네트워크를 확인하세요.")
                : new CheckResult("db", name, CheckLevel.Ok, $"왕복 {ms}ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "DB 점검 실패");
            return new CheckResult("db", name, CheckLevel.Fail, "DB 조회에 실패했습니다: " + Redact.Secrets(ex.Message.Split('\n')[0]),
                "연결 문자열과 DB 서버 상태, 서비스 계정의 DB 권한을 확인하세요.");
        }
    }

    // 적용된 마이그레이션과 이 빌드의 마이그레이션 비교 (SQL Server·SQLite 모두 — SQLite 는 SqliteAppDb 의 마이그레이션)
    public async Task<CheckResult> SchemaAsync(CancellationToken ct)
    {
        const string name = "DB 구조";
        try
        {
            var sqlServer = _db.Database.IsSqlServer();
            var applied = (await _db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var known = _db.Database.GetMigrations().ToList();
            var unknown = applied.Except(known).ToList();
            var pending = known.Except(applied).ToList();
            var build = _updates.Build.Schema;
            if (unknown.Count > 0)
                return new CheckResult("schema", name, CheckLevel.Fail, $"DB 에 이 프로그램이 모르는 마이그레이션이 적용돼 있습니다({unknown[^1]}). DB 가 프로그램보다 새 구조입니다.",
                    "새 버전 프로그램을 설치하거나, 업데이트 전 백업을 복원하세요(docs/OPERATIONS.md).");
            if (pending.Count > 0)
                return new CheckResult("schema", name, CheckLevel.Fail, $"적용되지 않은 마이그레이션이 {pending.Count}개 있습니다({pending[^1]}).",
                    sqlServer ? "서버(서비스)를 다시 시작하면 시작할 때 적용됩니다. 계속되면 서비스 계정의 DB 권한(db_owner)을 확인하세요."
                              : "서버(서비스)를 다시 시작하면 시작할 때 적용됩니다. 계속되면 App_Data 폴더의 쓰기 권한과 시작 오류(startup-error.log)를 확인하세요.");
            // 빌드 정보의 DB 구조(SchemaId)는 SQL Server 마이그레이션 이름이다 — SQLite 서버에서도 그 이름으로 견준다
            var last = MigrationInfo.LastSqlServerId();
            if (build.Length > 0 && last != null && build != last)
                return new CheckResult("schema", name, CheckLevel.Warn, $"빌드 정보의 DB 구조({build})와 프로그램의 마지막 마이그레이션({last})이 다릅니다.",
                    "배포 패키지를 다시 받아 설치하세요. 업데이트의 구조 변경 판단이 틀릴 수 있습니다.");
            return new CheckResult("schema", name, CheckLevel.Ok, applied.Count == 0 ? "마이그레이션 없음" : $"마지막 마이그레이션 {applied[^1]}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new CheckResult("schema", name, CheckLevel.Fail, "DB 구조를 확인하지 못했습니다: " + Redact.Secrets(ex.Message.Split('\n')[0]), "DB 연결을 먼저 확인하세요.");
        }
    }

    // 데이터 폴더·첨부·백업이 있는 드라이브의 여유 공간 (가장 부족한 곳 기준)
    public CheckResult Disk()
    {
        const string name = "디스크 여유";
        var drives = Drives(_cfg, Root, _backups.Options.FilesPath);
        var level = WorstDiskLevel(drives);
        var parts = drives.Select(d => $"{d.Label} {d.Root} {Gb(d.Free)} 남음 ({(d.Total > 0 ? d.Free * 100 / d.Total : 0)}%)").ToList();
        if (parts.Count == 0) return new CheckResult("disk", name, CheckLevel.Ok, "드라이브 정보를 읽을 수 없는 경로입니다(네트워크 공유 등).");
        return new CheckResult("disk", name, level, string.Join(" · ", parts),
            level == CheckLevel.Ok ? null : "오래된 백업(Backup:Keep)·로그(Logs:KeepDays)를 줄이거나, 첨부·백업 폴더를 여유 있는 드라이브로 옮기세요.");
    }

    // 여유 공간을 볼 드라이브: 데이터(App_Data)·첨부 저장소·백업 묶음 폴더가 있는 곳, 같은 드라이브는 한 번만.
    // 서버 상태(Disk)·/health/ready·관리자 칩(OpsAlerts)이 이 목록과 아래 판정을 함께 쓴다 — 백업만 다른 드라이브에 두어도 칩이 놓치지 않게
    public static List<(string Label, string Root, long Free, long Total)> Drives(IConfiguration cfg, string contentRoot, string backupFiles)
    {
        var seen = new HashSet<string>();
        var list = new List<(string Label, string Root, long Free, long Total)>();
        foreach (var (label, path) in new[] { ("데이터", Path.Combine(contentRoot, "App_Data")), ("첨부", AppPaths.Uploads(cfg, contentRoot)), ("백업", backupFiles) })
        {
            var d = DriveOf(path);
            if (d == null || !seen.Add(d.Value.Root)) continue;
            list.Add((label, d.Value.Root, d.Value.Free, d.Value.Total));
        }
        return list;
    }

    // 여러 드라이브 중 가장 나쁜 수준 (읽을 수 있는 드라이브가 없으면 정상 — 네트워크 공유 등)
    public static string WorstDiskLevel(IEnumerable<(string Label, string Root, long Free, long Total)> drives)
    {
        var level = CheckLevel.Ok;
        foreach (var d in drives)
        {
            var l = DiskLevel(d.Free, d.Total);
            if (l == CheckLevel.Fail || (l == CheckLevel.Warn && level == CheckLevel.Ok)) level = l;
        }
        return level;
    }

    // 작은 디스크는 비율로, 큰 디스크는 남은 양으로 본다 — 2TB 의 5%(100GB)는 부족한 것이 아니다
    public static string DiskLevel(long free, long total)
    {
        var ratio = total > 0 ? (double)free / total : 1;
        if (free < FailFreeBytes || (ratio < FailFreeRatio && free < FailRatioBelowBytes)) return CheckLevel.Fail;
        if (free < WarnFreeBytes || (ratio < WarnFreeRatio && free < WarnRatioBelowBytes)) return CheckLevel.Warn;
        return CheckLevel.Ok;
    }

    public static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB" : $"{bytes / (1024.0 * 1024):0} MB";

    // 경로가 속한 드라이브(마운트). 가장 긴 루트가 맞는 것을 고른다 — Linux 는 / 아래에 여러 마운트가 있다. 네트워크 공유(UNC)는 모른다
    public static (string Root, long Free, long Total)? DriveOf(string path)
    {
        if (BackupService.IsUnc(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return null; }
        (string Root, long Free, long Total)? best = null;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                var root = d.RootDirectory.FullName;
                if (!full.StartsWith(root, cmp) || (best != null && root.Length <= best.Value.Root.Length)) continue;
                if (!d.IsReady || d.TotalSize <= 0) continue;
                best = (root, d.AvailableFreeSpace, d.TotalSize);
            }
            catch (Exception) { /* 읽을 수 없는 드라이브(가상 파일 시스템 등)는 건너뛴다 */ }
        }
        return best;
    }

    // 서버 계정이 써야 하는 폴더에 실제로 쓸 수 있는지 (시험 파일을 만들고 지운다)
    public CheckResult Folders()
    {
        const string name = "폴더 쓰기 권한";
        var folders = new[]
        {
            ("App_Data", DataDir), ("첨부 저장소", StorageDir), ("암호화 키", AppPaths.Keys(_cfg, Root)), ("업데이트", _paths.DataDir),
            ("로그", _log.Dir), ("백업 묶음", _backups.Options.FilesPath),
        };
        var bad = folders.Where(f => !CanWrite(f.Item2)).Select(f => $"{f.Item1}({f.Item2})").ToList();
        if (bad.Count > 0)
        {
            var account = _paths.ServiceName != null ? $"NT SERVICE\\{_paths.ServiceName}" : "서버를 실행하는 계정";
            return new CheckResult("folders", name, CheckLevel.Fail, "쓸 수 없는 폴더: " + string.Join(", ", bad),
                $"{account} 에 그 폴더의 수정 권한을 주세요: icacls \"<폴더>\" /grant \"{account}:(OI)(CI)M\"");
        }
        if (_log.LastError != null)
            return new CheckResult("folders", name, CheckLevel.Warn, "로그 파일을 쓰지 못했습니다: " + _log.LastError, "로그 폴더(Logs:Path)의 권한과 디스크 여유를 확인하세요.");
        return new CheckResult("folders", name, CheckLevel.Ok, $"{folders.Length}개 폴더 모두 쓸 수 있음");
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    // DB 종류와 위치. SQLite(기본)·SQL Server 모두 마이그레이션으로 구조를 올리므로 어느 쪽이든 정상이다
    public CheckResult DbKind()
    {
        const string name = "DB 종류";
        var target = _backups.Target;
        if (target.SqlServer) return new CheckResult("dbkind", name, CheckLevel.Ok, target.Describe);
        return new CheckResult("dbkind", name, CheckLevel.Ok, _env.IsDevelopment() ? "SQLite (개발·미리보기)" : target.Describe);
    }

    public CheckResult PdfFont()
    {
        var (regular, bold) = PdfFonts.Find(_cfg);
        return regular == null
            ? new CheckResult("pdf", "PDF 한글 글꼴", CheckLevel.Warn, "PDF 에 쓸 한글 글꼴(TTF)을 찾지 못했습니다. PDF 내보내기가 실패합니다.",
                "설정 Pdf:Font(굵게: Pdf:FontBold)에 글꼴 파일 경로를 지정하세요. Windows 는 맑은 고딕(malgun.ttf), Linux 는 나눔고딕(fonts-nanum)을 자동으로 찾습니다.")
            : new CheckResult("pdf", "PDF 한글 글꼴", CheckLevel.Ok, regular + (bold == null ? " (굵은 글꼴 없음 — 보통 글꼴을 굵게 흉내 냄)" : ""));
    }

    public CheckResult UpdateSource()
    {
        const string name = "업데이트 확인";
        var st = UpdateStatus;
        if (st.Channel is not ("release" or "dev")) return new CheckResult("update", name, CheckLevel.Ok, $"{st.ChannelName} — 스스로 교체하지 않습니다. 받아 오는 곳: {st.Source}");
        if (!_updates.SourceConfigured)
            return new CheckResult("update", name, CheckLevel.Warn, "Update:Source 가 비어 있어 새 버전을 확인하지 않습니다.", "배포 파일을 직접 올려 적용하거나 Update:Source 를 지정하세요.", "/Admin/Update");
        if (st.CheckError != null)
        {
            var since = _updates.CheckFailedSince;
            return new CheckResult("update", name, CheckLevel.Warn,
                $"{st.Source}: {st.CheckError}" + (since == null ? "" : $" ({KoreaClock.ToKst(since.Value):MM-dd HH:mm} 부터 실패)"), st.CheckFix, "/Admin/Update");
        }
        return st.CheckedAt == null
            ? new CheckResult("update", name, CheckLevel.Ok, $"{st.Source} — 아직 확인하지 않음", null, "/Admin/Update")
            : new CheckResult("update", name, CheckLevel.Ok, $"{st.Source} — 마지막 확인 {KoreaClock.ToKst(st.CheckedAt.Value):MM-dd HH:mm} (한국 시간) 성공", null, "/Admin/Update");
    }

    public CheckResult Signature()
    {
        var st = UpdateStatus;
        return st.SignatureCheck
            ? new CheckResult("signature", "배포 파일 서명 확인", CheckLevel.Ok, "켜짐 · 공개키 " + st.KeyId)
            : new CheckResult("signature", "배포 파일 서명 확인", CheckLevel.Warn, "꺼짐 — 이 빌드에는 공개키가 없어 배포 파일의 출처를 확인하지 않고 SHA-256 으로 손상만 확인합니다.",
                "README 의 '서명 키' 절차로 키를 만들어 정식 배포하면 켜집니다.");
    }

    public CheckResult Attachments(AttachmentAudit a)
    {
        const string name = "첨부 파일";
        var summary = $"{a.Count}개 · {Size(a.DbBytes)} (디스크 {a.DiskFiles}개 · {Size(a.DiskBytes)})";
        if (a.MissingCount > 0)
            return new CheckResult("attachments", name, CheckLevel.Warn, $"{summary} — 파일이 없는 첨부 {a.MissingCount}개(내려받기가 안 됩니다)" + (a.OrphanCount > 0 ? $", 기록 없는 파일 {a.OrphanCount}개" : ""),
                "첨부 저장소(Storage:Path)를 옮겼거나 DB 만 복원했다면, 백업의 파일 묶음(uploads)을 함께 복원하세요(docs/OPERATIONS.md).");
        if (a.OrphanCount > 0)
            return new CheckResult("attachments", name, CheckLevel.Warn, $"{summary} — 기록 없는 파일 {a.OrphanCount}개({Size(a.OrphanBytes)})",
                "아래 '고아 파일 정리'로 지울 수 있습니다.");
        return new CheckResult("attachments", name, CheckLevel.Ok, summary);
    }

    public static string Size(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    // 올해 공휴일, 그리고 10월부터는 내년 공휴일 — 없으면 근무일 계산(일정 이동·지연 일수)이 주말만 빼고 센다
    public async Task<CheckResult> HolidaysAsync(CancellationToken ct)
    {
        const string name = "공휴일";
        var (thisYear, nextYear) = await HolidayCountsAsync(ct);
        var y = _clock.Today.Year;
        if (thisYear == 0)
            return new CheckResult("holidays", name, CheckLevel.Warn, $"{y}년 공휴일이 없습니다.", "관리 > 공휴일에서 올해 공휴일을 등록하세요.", "/Admin#holiday");
        if (_clock.Today.Month >= 10 && nextYear == 0)
            return new CheckResult("holidays", name, CheckLevel.Warn, $"{y + 1}년 공휴일이 없습니다. 내년 일정의 근무일 계산이 틀립니다.", "관리 > 공휴일에서 내년 공휴일을 등록하세요.", "/Admin#holiday");
        return new CheckResult("holidays", name, CheckLevel.Ok, $"{y}년 {thisYear}일 · {y + 1}년 {nextYear}일");
    }

    // 공휴일은 해마다 수십 건이라 메모리에서 센다 (두 DB 의 날짜 비교 번역 차이를 피한다)
    public async Task<(int ThisYear, int NextYear)> HolidayCountsAsync(CancellationToken ct)
    {
        var y = _clock.Today.Year;
        var dates = await _db.Holidays.AsNoTracking().Select(h => h.Date).ToListAsync(ct);
        return (dates.Count(d => d.Year == y), dates.Count(d => d.Year == y + 1));
    }

    public CheckResult LastBackup()
    {
        const string name = "마지막 백업";
        var list = _backups.List();
        var last = list.FirstOrDefault(e => e.Ok);
        var o = _backups.Options;
        if (list.Count > 0 && !list[0].Ok)
            return new CheckResult("backup", name, CheckLevel.Warn, $"마지막 백업이 실패했습니다({KoreaClock.ToKst(list[0].At):MM-dd HH:mm}): {list[0].Error}",
                "업데이트 화면의 백업 목록에서 이유를 확인하고 '지금 백업'으로 다시 시도하세요.", "/Admin/Update#backup");
        if (last == null)
            return new CheckResult("backup", name, o.Nightly ? CheckLevel.Warn : CheckLevel.Ok,
                o.Nightly ? "야간 백업이 켜져 있지만 아직 백업이 없습니다." : "앱으로 만든 백업이 없습니다(야간 백업 꺼짐 — DBA 가 따로 백업하면 괜찮습니다).",
                "업데이트 화면의 '지금 백업'으로 만들 수 있습니다. DB 구조가 바뀌는 업데이트는 적용 전에 자동으로 백업합니다.", "/Admin/Update#backup");
        var age = _clock.UtcNow - last.At;
        var limit = TimeSpan.FromDays(o.Nightly ? 2 : 7);
        var text = $"{last.Name} · {KoreaClock.ToKst(last.At):yyyy-MM-dd HH:mm} (한국 시간, {(int)age.TotalDays}일 전)";
        return age > limit
            ? new CheckResult("backup", name, CheckLevel.Warn, text + " — 오래되었습니다.", o.Nightly ? "야간 백업(Backup:Nightly)이 돌고 있는지 서버 로그를 확인하세요." : "'지금 백업'으로 새로 만들 수 있습니다.", "/Admin/Update#backup")
            : new CheckResult("backup", name, CheckLevel.Ok, text, null, "/Admin/Update#backup");
    }

    public CheckResult SetupCheck(List<SetupItem> setup)
    {
        var missing = setup.Where(s => !s.Done && s.Key is "corp" or "org" or "category").Select(s => s.Name).ToList();
        return missing.Count == 0
            ? new CheckResult("setup", "기준 정보", CheckLevel.Ok, "법인·조직·유형이 등록돼 있습니다.")
            : new CheckResult("setup", "기준 정보", CheckLevel.Warn, "등록되지 않음: " + string.Join(", ", missing),
                "관리 화면에서 등록하세요. 법인이 없으면 과제를 등록할 수 없습니다.", "/Admin");
    }

    // ── 첫 설정 점검표 ──────────────────────────────────────────────────
    // 처음 설치하면 법인·조직이 없어 과제 등록이 막다른 길이 된다. 관리자가 해야 할 일을 한곳에 모은다
    public async Task<List<SetupItem>> SetupAsync(CancellationToken ct)
    {
        var locations = await _db.Locations.AsNoTracking().Where(l => l.IsActive).Select(l => l.Level).ToListAsync(ct);
        var orgs = await _db.OrgUnits.AsNoTracking().CountAsync(o => o.IsActive, ct);
        var categories = await _db.Categories.AsNoTracking().CountAsync(ct);
        var (thisYear, nextYear) = await HolidayCountsAsync(ct);
        var y = _clock.Today.Year;
        var font = PdfFonts.Find(_cfg).Regular;
        var corp = locations.Count(l => l == 1);
        var items = new List<SetupItem>
        {
            new("corp", "법인·위치", corp > 0, $"법인 {corp}개 · 하위 위치 {locations.Count - corp}개",
                "관리 > 계층에서 법인(해상 운송 소요일 포함)과 제품군·라인·층을 등록합니다. 법인이 없으면 과제를 등록할 수 없습니다.", "/Admin#location"),
            new("org", "조직", orgs > 0, $"{orgs}개", "관리 > 조직에서 그룹과 파트를 등록합니다. 이름 등록 화면의 소속 선택에 쓰입니다.", "/Admin#org"),
            new("category", "유형·단계", categories > 0, $"{categories}개", "관리 > 유형·단계에서 과제 유형과 기본 단계를 확인합니다.", "/Admin#category"),
            new("holiday-this", $"{y}년 공휴일", thisYear > 0, $"{thisYear}일", "관리 > 공휴일에서 올해 공휴일(대체·임시 공휴일 포함)을 등록합니다.", "/Admin#holiday"),
        };
        // 내년 공휴일은 정부 발표 뒤(대개 하반기)에 넣으므로 10월부터 점검한다
        if (_clock.Today.Month >= 10)
            items.Add(new("holiday-next", $"{y + 1}년 공휴일", nextYear > 0, $"{nextYear}일", "관리 > 공휴일에서 내년 공휴일을 미리 등록합니다. 내년 일정의 근무일 계산에 쓰입니다.", "/Admin#holiday"));
        items.Add(new("pdf", "PDF 한글 글꼴", font != null, font ?? "찾지 못함",
            "서버에 맑은 고딕(Windows) 또는 나눔고딕(Linux)이 있어야 합니다. 다른 글꼴은 설정 Pdf:Font 에 파일 경로를 적습니다.", null));
        var backupDone = !_backups.Target.SqlServer || _backups.Options.DbPath.Length > 0;
        items.Add(new("backup", "백업 경로", backupDone, _backups.DbLocation,
            "appsettings.Production.json 의 Backup:Path 에 SQL Server 가 쓸 수 있는 폴더를 지정합니다(DB 가 다른 컴퓨터면 UNC 경로 \\\\서버\\공유).", "/Admin/Update#backup"));
        return items;
    }

    // ── 첨부 대조 ─────────────────────────────────────────────────────
    public async Task<AttachmentAudit> AuditAttachmentsAsync(CancellationToken ct)
    {
        var rows = await _db.Attachments.AsNoTracking().Select(a => new { a.ProjectId, a.StoredName, a.Size }).ToListAsync(ct);
        var expected = rows.Select(r => Key(r.ProjectId, r.StoredName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = rows.Where(r => !File.Exists(Path.Combine(StorageDir, r.ProjectId.ToString(), r.StoredName)))
            .Select(r => Key(r.ProjectId, r.StoredName)).ToList();
        var (files, bytes, orphans, orphanBytes) = ScanDisk(expected);
        return new AttachmentAudit(rows.Count, rows.Sum(r => r.Size), bytes, files, missing.Count, orphans.Count, orphanBytes,
            missing.Take(20).ToList(), orphans.Take(20).Select(o => o.Key).ToList());
    }

    private static string Key(int projectId, string stored) => projectId + "/" + stored;

    // 저장소의 과제 번호 폴더만 본다 (다른 파일은 사람이 넣은 것일 수 있어 건드리지 않는다)
    private (int Files, long Bytes, List<(string Key, string Path, long Size)> Orphans, long OrphanBytes) ScanDisk(HashSet<string> expected)
    {
        var orphans = new List<(string, string, long)>();
        int files = 0;
        long bytes = 0, orphanBytes = 0;
        if (!Directory.Exists(StorageDir)) return (0, 0, orphans, 0);
        var recent = DateTime.UtcNow - OrphanGrace;
        foreach (var dir in Directory.GetDirectories(StorageDir))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var projectId)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                var info = new FileInfo(file);
                files++;
                bytes += info.Length;
                var key = Key(projectId, info.Name);
                if (expected.Contains(key) || info.LastWriteTimeUtc > recent) continue;
                orphans.Add((key, file, info.Length));
                orphanBytes += info.Length;
            }
        }
        return (files, bytes, orphans, orphanBytes);
    }

    // 기록 없는 파일을 지운다 (관리자). 지우기 직전에 다시 대조하고, 막 올라온 파일은 남긴다. 빈 과제 폴더도 정리한다
    public async Task<(int Files, long Bytes)> CleanOrphansAsync(string by, CancellationToken ct)
    {
        var rows = await _db.Attachments.AsNoTracking().Select(a => new { a.ProjectId, a.StoredName }).ToListAsync(ct);
        var expected = rows.Select(r => Key(r.ProjectId, r.StoredName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (_, _, orphans, _) = ScanDisk(expected);
        int removed = 0;
        long freed = 0;
        foreach (var (_, path, size) in orphans)
        {
            try
            {
                File.Delete(path);
                removed++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning("고아 파일을 지우지 못했습니다 {File}: {Message}", path, ex.Message); }
        }
        if (Directory.Exists(StorageDir))
        {
            foreach (var dir in Directory.GetDirectories(StorageDir))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception) { /* 다음에 */ }
            }
        }
        // 과제와 무관한 정리라 활동 이력 대신 로그로 남긴다 (누가·몇 개)
        _logger.LogWarning("고아 첨부 파일 정리: {Count}개, {Bytes} — {By}", removed, Size(freed), by);
        return (removed, freed);
    }
}

// 관리자 상단 칩에 보일 서버 문제 (업데이트 알림을 일반화). Level: warn(문제) · accent(새 버전)
public record OpsAlert(string Key, string Level, string Text, string Href);

// 화면마다 레이아웃이 부르므로 DB·디스크를 읽는 항목은 몇 분 동안 기억해 둔다
public class OpsAlerts
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly UpdateService _updates;
    private readonly BackupService _backups;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _cfg;
    private readonly object _gate = new();
    private DateTime _cachedAt = DateTime.MinValue;
    private string _diskLevel = CheckLevel.Ok;
    private bool _nextYearMissing;
    private bool _setupMissing;
    private static readonly DateTime Started = DateTime.UtcNow;

    public OpsAlerts(UpdateService updates, BackupService backups, IClock clock, IHostEnvironment env, IConfiguration cfg)
    {
        _updates = updates;
        _backups = backups;
        _clock = clock;
        _env = env;
        _cfg = cfg;
    }

    // 관리자에게 보일 알림, 중요한 것부터. 마지막은 새 버전 알림(있으면)
    public async Task<List<OpsAlert>> ForAdminAsync(AppDb db)
    {
        await RefreshAsync(db);
        var list = new List<OpsAlert>();
        var now = _clock.UtcNow;
        var (failure, last) = _updates.Problems();

        // 처음 설치한 서버는 법인이 없어 과제를 등록할 수 없다(등록 화면의 위치 선택이 막힌다) — 첫 설정 점검표로 보낸다
        bool setup;
        lock (_gate) setup = _setupMissing;
        if (setup) list.Add(new("setup", "warn", "첫 설정 필요", "/Admin/Status"));

        if (failure != null)
            list.Add(new("update-failed", "warn", $"v{failure.Version} 시작 실패", "/Admin/Update"));
        else if (last is { Ok: false } && now - last.At < TimeSpan.FromDays(7))
            list.Add(new("update-failed", "warn", "업데이트 실패", "/Admin/Update"));

        if (_updates.CheckFailing(now, TimeSpan.FromDays(3)))
            list.Add(new("check-failing", "warn", "업데이트 확인 실패", "/Admin/Update"));

        bool diskLow;
        lock (_gate) diskLow = _diskLevel != CheckLevel.Ok;
        if (diskLow) list.Add(new("disk", "warn", "디스크 부족", "/Admin/Status"));

        var backups = _backups.List();
        if (backups.Count > 0 && !backups[0].Ok && now - backups[0].At < TimeSpan.FromDays(2))
            list.Add(new("backup-failed", "warn", "백업 실패", "/Admin/Update#backup"));
        else if (_backups.Options.Nightly)
        {
            // 야간 백업을 켜 둔 서버만 '오래됨'을 알린다 (DBA 가 백업하는 서버에서 늘 떠 있으면 아무도 보지 않는다)
            var lastOk = backups.FirstOrDefault(e => e.Ok)?.At;
            if (lastOk == null ? now - Started > TimeSpan.FromDays(1) : now - lastOk.Value > TimeSpan.FromDays(2))
                list.Add(new("backup-old", "warn", "백업 오래됨", "/Admin/Update#backup"));
        }

        bool holidays;
        lock (_gate) holidays = _nextYearMissing;
        if (holidays) list.Add(new("holidays", "warn", $"{_clock.Today.Year + 1}년 공휴일 없음", "/Admin#holiday"));

        if (_updates.Badge() is { } badge) list.Add(new("update", "accent", badge.Text, "/Admin/Update"));
        return list;
    }

    private async Task RefreshAsync(AppDb db)
    {
        lock (_gate) if (DateTime.UtcNow - _cachedAt < CacheFor) return;
        var disk = OpsChecks.WorstDiskLevel(OpsChecks.Drives(_cfg, _env.ContentRootPath, _backups.Options.FilesPath));
        var missing = false;
        var setupMissing = false;
        try { setupMissing = !await db.Locations.AsNoTracking().AnyAsync(l => l.IsActive && l.Level == 1); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* 아래 공휴일과 같은 이유로 칩은 조용히 넘어간다 */ }
        if (_clock.Today.Month >= 10)
        {
            var next = _clock.Today.Year + 1;
            try
            {
                var dates = await db.Holidays.AsNoTracking().Select(h => h.Date).ToListAsync();
                missing = !dates.Any(d => d.Year == next);
            }
            // 칩 때문에 화면(오류 화면 포함)이 깨지면 안 된다 — DB 문제는 서버 상태 화면과 /health/ready 가 알린다
            catch (Exception ex) when (ex is not OperationCanceledException) { missing = false; }
        }
        lock (_gate)
        {
            _diskLevel = disk;
            _nextYearMissing = missing;
            _setupMissing = setupMissing;
            _cachedAt = DateTime.UtcNow;
        }
    }

    // 시험·관리 화면에서 바로 반영되게 기억한 값을 버린다 (공휴일을 등록한 직후 등)
    public void Invalidate()
    {
        lock (_gate) _cachedAt = DateTime.MinValue;
    }
}
