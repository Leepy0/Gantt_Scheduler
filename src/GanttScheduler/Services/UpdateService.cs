using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// 화면에 내려 주는 업데이트 상태
public record ReleaseView(string Version, long Size, DateTime? Published, string? Page, string? Notes, bool SchemaChange);
public record StagedView(string Version, DateTime At, string Origin, string By, string Sha256, string? Notes, bool SchemaChange);
public record PreviousView(string Version, DateTime At, bool CanRollback, string? BlockReason);
public record UpdateSettings(bool AutoCheck, bool AutoApply, int WindowFrom, int WindowTo, int IdleMinutes);

public record UpdateStatus(
    string Version, string Build, string Channel, string ChannelName, string? Commit, string Host, string Source, string? BrowserPage,
    bool CanApply, string? BlockReason, string? BlockFix,
    string Phase, int Progress, string? Error, string? ErrorFix,
    ReleaseView? Latest, DateTime? CheckedAt, string? CheckError, string? CheckFix,
    StagedView? Staged, PreviousView? Previous, UpdateSettings Settings, List<UpdateLog> History, bool SqliteData, string? SkipAuto);

// 마지막 사용자 요청 시각 — 아무도 쓰지 않을 때만 자동 적용하기 위해 기록한다
public class ActivityTracker
{
    private long _ticks = DateTime.UtcNow.Ticks;
    public DateTime LastUtc => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
    public void Touch() => Interlocked.Exchange(ref _ticks, DateTime.UtcNow.Ticks);
}

public class UpdatePaths
{
    public string AppDir { get; init; } = "";      // 실행 파일이 있는 폴더 (교체 대상)
    public string? ServiceName { get; init; }      // Windows 서비스로 돌고 있으면 그 이름
    public string DataDir { get; init; } = "";     // 받은 파일·이전 버전·상태 (App_Data/updates)
    public string StatePath => Path.Combine(DataDir, "state.json");
    public string ResultPath => Path.Combine(DataDir, "result.json");
    public string StagedRoot => Path.Combine(DataDir, "staged");
    public string PreviousDir => Path.Combine(DataDir, "previous");
    public string RolledBackDir => Path.Combine(DataDir, "rolledback");
}

// 서버 자동 업데이트: 새 버전 확인 → 받기(SHA-256 검증) → 풀어 두기 → 도우미를 띄워 교체.
// 데스크톱 프로그램은 닫을 때 바꾸지만 서버는 닫히는 때가 없으므로, 관리자가 누르거나 아무도 쓰지 않는 시간에 바꾼다
public class UpdateService
{
    private readonly AppBuild _build;
    private readonly UpdatePaths _paths;
    private readonly UpdateSource _source;
    private readonly ActivityTracker _activity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _log;
    private readonly bool _sqlite;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);   // 확인·받기는 한 번에 하나만
    private UpdateState _state;
    private string _phase = "idle";                      // idle | checking | downloading | applying
    private int _progress;
    private string? _error;
    private string? _errorFix;

    public UpdateService(AppBuild build, UpdatePaths paths, UpdateSource source, ActivityTracker activity,
        IHostApplicationLifetime lifetime, ILogger<UpdateService> log, IConfiguration cfg)
    {
        _build = build;
        _paths = paths;
        _source = source;
        _activity = activity;
        _lifetime = lifetime;
        _log = log;
        _sqlite = !string.Equals(cfg["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);
        _state = Load();
    }

    public AppBuild Build => _build;
    public bool Applying { get { lock (_gate) return _phase == "applying"; } }
    public bool SourceConfigured => _source.Configured;

    // IIS 아래에서 돌고 있는지 (ASP.NET Core 모듈이 넣어 주는 환경 변수로 판단)
    public static bool UnderIis =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_TOKEN"));

    // ── 상태 ─────────────────────────────────────────────────────────────
    public UpdateStatus Status()
    {
        lock (_gate)
        {
            var (ok, reason, fix) = ApplyBlock();
            var s = _state;
            var newer = s.Latest != null && IsNewer(s.Latest.Version);
            PreviousView? prev = null;
            if (s.Previous != null && Directory.Exists(s.Previous.Dir))
            {
                var same = s.Previous.Schema == _build.Schema;
                prev = new PreviousView(s.Previous.Version, s.Previous.At, same && ok,
                    !same ? "이 버전에서 DB 구조가 바뀌어 프로그램만 되돌릴 수 없습니다. 되돌리려면 적용 전 DB 백업을 복원해야 합니다." : reason);
            }
            return new UpdateStatus(
                _build.VersionText, _build.Id, _build.Channel, _build.ChannelName, _build.Commit,
                _paths.ServiceName != null ? $"Windows 서비스 ({_paths.ServiceName})" : UnderIis ? "IIS" : "직접 실행",
                _source.Describe, _source.BrowserPage,
                ok, reason, fix, _phase, _progress, _error, _errorFix,
                newer ? new ReleaseView(s.Latest!.Version, s.Latest.Size, s.Latest.Published, s.Latest.Page, s.Latest.Notes, SchemaChanged(s.Latest.Schema)) : null,
                s.CheckedAt, s.CheckError, s.CheckFix,
                s.Staged == null ? null : new StagedView(s.Staged.Version, s.Staged.At, s.Staged.Origin, s.Staged.By, s.Staged.Sha256, s.Staged.Notes, SchemaChanged(s.Staged.Schema)),
                prev, new UpdateSettings(s.AutoCheck, s.AutoApply, s.WindowFrom, s.WindowTo, s.IdleMinutes),
                s.History.OrderByDescending(h => h.At).Take(10).ToList(), _sqlite, s.SkipAuto);
        }
    }

    // 관리자에게 보여 줄 한 줄 요약 (상단 칩): 받아 둔 버전 또는 새 버전
    public (string Text, bool Ready)? Badge()
    {
        lock (_gate)
        {
            if (_state.Staged != null) return ($"v{_state.Staged.Version} 적용 대기", true);
            if (_state.Latest != null && IsNewer(_state.Latest.Version)) return ($"v{_state.Latest.Version} 업데이트", false);
            return null;
        }
    }

    private bool IsNewer(string version) => AppBuild.TryParseVersion(version, out var v) && v > _build.Version;

    // DB 구조(마지막 마이그레이션)가 지금과 다른지. 알 수 없으면 바뀐 것으로 본다 (조심하는 쪽)
    private bool SchemaChanged(string? schema) =>
        string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(_build.Schema) || schema != _build.Schema;

    private (bool Ok, string? Reason, string? Fix) ApplyBlock()
    {
        if (!_build.SelfUpdating)
            return (false, "로컬 빌드는 스스로 교체하지 않습니다.", "배포 패키지(zip)로 설치한 서버에서만 자동으로 적용됩니다.");
        if (!CanWrite(_paths.AppDir))
        {
            var pool = Environment.GetEnvironmentVariable("APP_POOL_ID");
            var account = _paths.ServiceName != null ? $"NT SERVICE\\{_paths.ServiceName}"
                : string.IsNullOrEmpty(pool) ? "서버를 실행하는 계정" : $"IIS AppPool\\{pool}";
            return (false, "서버 계정이 프로그램 폴더에 쓸 수 없어 파일을 바꿀 수 없습니다.",
                $"{account} 에 폴더 수정 권한을 주세요: icacls \"{_paths.AppDir}\" /grant \"{account}:(OI)(CI)M\"");
        }
        return (true, null, null);
    }

    private static bool CanWrite(string dir)
    {
        var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ── 확인 ─────────────────────────────────────────────────────────────
    public async Task CheckAsync(CancellationToken ct)
    {
        if (!await _work.WaitAsync(0, ct)) return;   // 이미 확인·받는 중
        try
        {
            SetPhase("checking");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var latest = await _source.GetLatestAsync(limit.Token);
                Change(s =>
                {
                    s.Latest = latest;
                    s.CheckedAt = DateTime.UtcNow;
                    s.CheckError = latest == null ? "배포된 버전 정보를 찾지 못했습니다." : null;
                    s.CheckFix = latest == null
                        ? "받아 오는 곳에 정식 릴리스가 없거나, 로그인해야 볼 수 있는 곳(비공개 레포)입니다. 배포 파일(zip)을 직접 올려 적용하거나, 설정 Update:Source 를 확인하세요."
                        : null;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var (message, fix) = Explain(ex);
                Change(s => { s.CheckedAt = DateTime.UtcNow; s.CheckError = message; s.CheckFix = fix; });
                _log.LogWarning("업데이트 확인 실패: {Message}", ex.Message);
            }
        }
        finally
        {
            SetPhase("idle");
            _work.Release();
        }
    }

    private static (string Message, string Fix) Explain(Exception ex) => ex switch
    {
        UpdateException u => (u.Message, u.Fix),
        OperationCanceledException => ("업데이트 서버가 응답하지 않습니다.", "서버에서 인터넷(또는 지정한 주소)에 접속할 수 있는지 확인하세요. 안 되면 배포 파일(zip)을 직접 올려 적용할 수 있습니다."),
        HttpRequestException h => ($"업데이트 서버에 연결하지 못했습니다 ({(h.StatusCode == null ? "연결 실패" : ((int)h.StatusCode).ToString())}).",
            "서버의 프록시·방화벽 설정을 확인하세요. 안 되면 배포 파일(zip)을 직접 올려 적용할 수 있습니다."),
        _ => ("업데이트를 처리하지 못했습니다: " + ex.Message, "잠시 후 다시 시도하세요."),
    };

    // ── 받기 ─────────────────────────────────────────────────────────────
    // 받기는 오래 걸리므로 요청과 분리해 돌린다. 진행 상황은 Status() 로 본다
    public void StartDownload(string by)
    {
        ReleaseInfo latest;
        lock (_gate)
        {
            latest = _state.Latest is { } l && IsNewer(l.Version) ? l
                : throw new UpdateException("받을 새 버전이 없습니다.", "먼저 '지금 확인'을 눌러 주세요.");
            if (_phase != "idle") throw new UpdateException("이미 다른 작업을 하고 있습니다.", "끝난 뒤 다시 시도하세요.");
        }
        _ = Task.Run(() => DownloadAsync(latest, by, _lifetime.ApplicationStopping));
    }

    public async Task DownloadAsync(ReleaseInfo r, string by, CancellationToken ct)
    {
        if (!await _work.WaitAsync(0, ct)) return;
        var zip = Path.Combine(_paths.DataDir, $"GanttScheduler_{r.Version}.zip.partial");
        try
        {
            SetPhase("downloading");
            Directory.CreateDirectory(_paths.DataDir);
            var expected = r.Sha256 ?? await _source.FetchShaAsync(r, ct)
                ?? throw new UpdateException("배포 정보에 검증용 SHA-256 이 없습니다.", "배포 파일(zip)을 직접 올려 적용하세요.");

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(30));
            await _source.DownloadAsync(r, zip, p => _progress = p, limit.Token);

            var actual = await Task.Run(() => HashFile(zip), ct);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("받은 파일이 손상됐습니다 (SHA-256 불일치).", "네트워크 상태를 확인하고 다시 받으세요.");

            Stage(zip, actual, "download", by, r);
        }
        catch (Exception ex)
        {
            (_error, _errorFix) = Explain(ex);
            _log.LogWarning(ex, "업데이트 받기 실패");
        }
        finally
        {
            TryDelete(zip);
            SetPhase("idle", keepError: true);
            _work.Release();
        }
    }

    // 브라우저로 올린 배포 파일(zip)을 검증해 받아 둔다. 서버가 인터넷에 못 나가는 사내망용
    public async Task UploadAsync(Stream content, string by, CancellationToken ct)
    {
        if (!await _work.WaitAsync(0, ct)) throw new UpdateException("이미 다른 작업을 하고 있습니다.", "끝난 뒤 다시 시도하세요.");
        var zip = Path.Combine(_paths.DataDir, "upload.zip.partial");
        try
        {
            SetPhase("downloading");
            Directory.CreateDirectory(_paths.DataDir);
            await using (var dst = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(dst, ct);
            var sha = await Task.Run(() => HashFile(zip), ct);
            Stage(zip, sha, "upload", by, null);
        }
        finally
        {
            TryDelete(zip);
            SetPhase("idle");
            _work.Release();
        }
    }

    // zip 을 풀어 내용을 확인하고 "받아 둔 업데이트"로 기록한다
    private void Stage(string zip, string sha, string origin, string by, ReleaseInfo? expected)
    {
        var temp = Path.Combine(_paths.StagedRoot, "_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            try { ZipFile.ExtractToDirectory(zip, temp, overwriteFiles: true); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                throw new UpdateException("zip 파일을 열 수 없습니다.", "릴리스의 GanttScheduler-win-x64.zip 을 다시 받아 올려 주세요.");
            }

            var inner = Path.Combine(temp, "release.json");
            var info = File.Exists(inner) ? UpdateSource.ParseManifest(File.ReadAllText(inner)) : null;
            if (info == null || !File.Exists(Path.Combine(temp, UpdateApplier.EntryDll)))
                throw new UpdateException("과제 스케줄러 배포 파일이 아닙니다.", "릴리스의 GanttScheduler-win-x64.zip 을 그대로 올려 주세요 (압축을 풀거나 다시 묶지 않은 파일).");
            if (expected != null && info.Version != expected.Version)
                throw new UpdateException($"받은 파일의 버전(v{info.Version})이 배포 정보(v{expected.Version})와 다릅니다.", "잠시 후 다시 확인해 주세요.");
            if (!IsNewer(info.Version))
                throw new UpdateException($"v{info.Version} 은 지금 버전(v{_build.VersionText})보다 새 버전이 아닙니다.", "더 새로운 릴리스의 파일을 올려 주세요.");
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!string.IsNullOrEmpty(info.Rid) && info.Rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) != windows)
                throw new UpdateException($"이 서버의 운영체제용 파일이 아닙니다 ({info.Rid}).", "서버 운영체제에 맞는 배포 파일을 올려 주세요.");

            var dir = Path.Combine(_paths.StagedRoot, info.Version);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(temp, dir);
            MakeExecutable(dir);

            var schema = string.IsNullOrEmpty(info.Schema) ? expected?.Schema : info.Schema;
            var notes = string.IsNullOrEmpty(info.Notes) ? expected?.Notes : info.Notes;
            Change(s => s.Staged = new StagedUpdate(info.Version, dir, sha, schema, notes, DateTime.UtcNow, origin, by));
            _log.LogInformation("업데이트 v{Version} 받아 둠 ({Origin})", info.Version, origin);
        }
        finally
        {
            if (Directory.Exists(temp)) TryDeleteDir(temp);
        }
    }

    public void Discard()
    {
        lock (_gate)
        {
            if (_phase != "idle") throw new UpdateException("작업 중에는 지울 수 없습니다.", "끝난 뒤 다시 시도하세요.");
            var staged = _state.Staged;
            if (staged == null) return;
            TryDeleteDir(staged.Dir);
            _state.Staged = null;
            Save();
        }
    }

    public void SaveSettings(UpdateSettings input)
    {
        if (input.WindowFrom is < 0 or > 23 || input.WindowTo is < 0 or > 24)
            throw new RuleException("자동 적용 시간은 0~24시 사이로 입력해 주세요.", "windowFrom");
        if (input.IdleMinutes is < 5 or > 720)
            throw new RuleException("대기 시간은 5~720분 사이로 입력해 주세요.", "idleMinutes");
        Change(s =>
        {
            s.AutoCheck = input.AutoCheck;
            s.AutoApply = input.AutoApply;
            s.WindowFrom = input.WindowFrom;
            s.WindowTo = input.WindowTo;
            s.IdleMinutes = input.IdleMinutes;
        });
    }

    // ── 적용·되돌리기 ────────────────────────────────────────────────────
    public void Apply(string by)
    {
        lock (_gate)
        {
            var staged = _state.Staged ?? throw new UpdateException("받아 둔 업데이트가 없습니다.", "먼저 새 버전을 받거나 배포 파일을 올려 주세요.");
            if (!File.Exists(Path.Combine(staged.Dir, UpdateApplier.EntryDll)))
            {
                _state.Staged = null;
                Save();
                throw new UpdateException("받아 둔 업데이트 파일이 없어졌습니다.", "새 버전을 다시 받아 주세요.");
            }
            Launch("apply", staged.Dir, _paths.PreviousDir, staged.Version, by);
        }
    }

    public void Rollback(string by)
    {
        lock (_gate)
        {
            var prev = _state.Previous;
            if (prev == null || !File.Exists(Path.Combine(prev.Dir, UpdateApplier.EntryDll)))
                throw new UpdateException("되돌릴 이전 버전이 없습니다.", "이전 버전 배포 파일로 다시 설치해야 합니다.");
            if (prev.Schema != _build.Schema)
                throw new UpdateException("이 버전에서 DB 구조가 바뀌어 프로그램만 되돌릴 수 없습니다.", "적용 전 DB 백업을 복원한 뒤 이전 버전 배포 파일로 설치해 주세요.");
            Launch("rollback", prev.Dir, _paths.RolledBackDir, prev.Version, by);
        }
    }

    // 도우미(새 버전·이전 버전 폴더의 실행 파일)를 띄우고 서버를 내린다. _gate 안에서 부른다
    private void Launch(string kind, string fromDir, string backupDir, string toVersion, string by)
    {
        if (_phase != "idle") throw new UpdateException("이미 다른 작업을 하고 있습니다.", "끝난 뒤 다시 시도하세요.");
        var (ok, reason, fix) = ApplyBlock();
        if (!ok) throw new UpdateException(reason!, fix!);

        TryDelete(_paths.ResultPath);
        _state.Pending = new PendingSwap(kind, _build.VersionText, toVersion, _build.Schema, by, DateTime.UtcNow);
        Save();

        var (file, prefix) = Runner(fromDir);
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _paths.DataDir };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in new[]
                 {
                     UpdateApplier.Flag, "--kind", kind, "--app", _paths.AppDir, "--from", fromDir, "--backup", backupDir,
                     "--pid", Environment.ProcessId.ToString(), "--result", _paths.ResultPath,
                     "--from-version", _build.VersionText, "--to", toVersion,
                 })
            psi.ArgumentList.Add(a);

        // 다시 띄우는 방법: 서비스는 도우미가 서비스를 시작시키고, IIS 는 다음 요청 때 알아서 띄우며,
        // 직접 실행한 서버는 도우미가 같은 명령으로 다시 띄운다
        if (_paths.ServiceName != null)
        {
            psi.ArgumentList.Add("--start-service");
            psi.ArgumentList.Add(_paths.ServiceName);
        }
        else if (!UnderIis)
        {
            var (exe, args) = RestartCommand();
            psi.ArgumentList.Add("--restart");
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add("--restart-dir");
            psi.ArgumentList.Add(Environment.CurrentDirectory);
            psi.ArgumentList.Add("--");
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        try
        {
            using var helper = Process.Start(psi) ?? throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");
        }
        catch (Exception ex)
        {
            _state.Pending = null;
            Save();
            throw new UpdateException("교체 도우미를 실행하지 못했습니다: " + ex.Message, "서버 계정의 실행 권한을 확인하세요.");
        }

        _phase = "applying";
        _log.LogWarning("{Kind}: v{From} → v{To} ({By}). 서버를 다시 시작합니다.", kind, _build.VersionText, toVersion, by);
        // 이 요청의 응답이 나간 뒤에 내린다
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _lifetime.StopApplication();
        });
    }

    // 도우미를 실행할 방법: 폴더에 실행 파일이 있으면 그것, 없으면(런타임 미포함 배포) dotnet 으로 DLL 실행
    private static (string File, string[] Prefix) Runner(string dir)
    {
        var exe = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "GanttScheduler.exe" : "GanttScheduler");
        var host = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (File.Exists(exe) && !host.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return (exe, Array.Empty<string>());
        return (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : "dotnet",
            new[] { Path.Combine(dir, UpdateApplier.EntryDll) });
    }

    // 지금 서버를 띄운 명령 그대로 (dotnet GanttScheduler.dll … 또는 GanttScheduler.exe …)
    private (string Exe, string[] Args) RestartCommand()
    {
        var exe = Environment.ProcessPath ?? throw new UpdateException("실행 파일 경로를 알 수 없습니다.", "서버를 수동으로 다시 시작해 주세요.");
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args = new[] { Path.Combine(_paths.AppDir, UpdateApplier.EntryDll) }.Concat(args).ToArray();
        return (exe, args);
    }

    // ── 자동 적용 ────────────────────────────────────────────────────────
    // 받아 둔 업데이트가 있고, 설정한 시간대이고, 한동안 아무도 쓰지 않았을 때만. DB 구조가 바뀌는 업데이트는 사람이 누른다
    public static bool ShouldAutoApply(UpdateState s, AppBuild build, DateTime utcNow, DateTime lastActivityUtc)
    {
        if (!s.AutoApply || s.Staged == null) return false;
        if (!AppBuild.TryParseVersion(s.Staged.Version, out var v) || v <= build.Version) return false;
        if (s.Staged.Version == s.SkipAuto) return false;   // 문제가 있어 되돌린 버전
        if (string.IsNullOrEmpty(s.Staged.Schema) || s.Staged.Schema != build.Schema) return false;
        if (utcNow - lastActivityUtc < TimeSpan.FromMinutes(s.IdleMinutes)) return false;
        var hour = KoreaClock.ToKst(utcNow).Hour;
        if (s.WindowFrom == s.WindowTo) return true;
        return s.WindowFrom < s.WindowTo
            ? hour >= s.WindowFrom && hour < s.WindowTo
            : hour >= s.WindowFrom || hour < s.WindowTo;   // 22~4시처럼 자정을 넘는 시간대
    }

    public void TryAutoApply()
    {
        lock (_gate)
        {
            if (_phase != "idle" || !ApplyBlock().Ok) return;
            if (!ShouldAutoApply(_state, _build, DateTime.UtcNow, _activity.LastUtc)) return;
        }
        try { Apply("자동"); }
        catch (Exception ex) { _log.LogWarning("자동 적용 실패: {Message}", ex.Message); }
    }

    // 주기 확인: 새 버전이 있고 자동 적용이 켜져 있으면 미리 받아 둔다
    public async Task AutoCheckAsync(CancellationToken ct)
    {
        bool auto;
        lock (_gate) auto = _state.AutoCheck;
        if (!auto || !_source.Configured || !_build.SelfUpdating) return;
        await CheckAsync(ct);
        ReleaseInfo? latest;
        lock (_gate)
        {
            latest = _state.Latest;
            if (latest == null || !IsNewer(latest.Version) || !_state.AutoApply || _state.Staged?.Version == latest.Version) return;
            if (latest.Version == _state.SkipAuto) return;
        }
        await DownloadAsync(latest, "자동", ct);
    }

    // ── 시작할 때: 직전 교체의 결과를 정리한다 ───────────────────────────
    public void Startup()
    {
        lock (_gate)
        {
            var pending = _state.Pending;
            if (pending != null)
            {
                UpdateApplier.Result? result = null;
                try
                {
                    if (File.Exists(_paths.ResultPath))
                        result = JsonSerializer.Deserialize<UpdateApplier.Result>(File.ReadAllText(_paths.ResultPath));
                }
                catch (Exception) { /* 결과 파일이 깨졌으면 버전으로 판단 */ }

                // 결과 파일이 없어도 지금 버전이 목표 버전이면 성공한 것이다
                var ok = result?.Ok ?? (_build.VersionText == pending.To);
                var error = ok ? null : result?.Error ?? "교체가 끝나지 않았습니다. App_Data/updates/apply.log 를 확인하세요.";
                _state.History.Add(new UpdateLog(DateTime.UtcNow, pending.Kind, pending.From, pending.To, pending.By, ok, error));
                if (_state.History.Count > 30) _state.History.RemoveRange(0, _state.History.Count - 30);

                if (ok && pending.Kind == "apply")
                {
                    _state.Previous = new PreviousVersion(pending.From, pending.FromSchema, _paths.PreviousDir, DateTime.UtcNow);
                    _state.Staged = null;
                }
                else if (ok)
                {
                    _state.Previous = null;   // 되돌린 뒤에는 더 되돌릴 버전이 없다
                    _state.SkipAuto = pending.From;
                }
                _state.Pending = null;
                TryDelete(_paths.ResultPath);
                if (ok) _log.LogWarning("{Kind} 완료: v{From} → v{To}", pending.Kind, pending.From, pending.To);
                else _log.LogError("{Kind} 실패: v{From} → v{To}: {Error}", pending.Kind, pending.From, pending.To, error);
            }

            // 이미 적용됐거나 없어진 받아 둔 파일 정리
            if (_state.Staged != null && (!IsNewer(_state.Staged.Version) || !Directory.Exists(_state.Staged.Dir))) _state.Staged = null;
            if (_state.Previous != null && !Directory.Exists(_state.Previous.Dir)) _state.Previous = null;
            Save();
        }
        Cleanup();
    }

    // 쓰고 남은 폴더 정리 (도우미가 아직 그 폴더에서 돌고 있으면 다음 기회에 지운다)
    public void Cleanup()
    {
        if (!_work.Wait(0)) return;   // 받거나 푸는 중인 파일을 지우지 않도록
        try
        {
            string? keep;
            bool hasPrevious;
            lock (_gate)
            {
                keep = _state.Staged == null ? null : Path.GetFullPath(_state.Staged.Dir);
                hasPrevious = _state.Previous != null;
            }
            if (Directory.Exists(_paths.StagedRoot))
            {
                foreach (var dir in Directory.GetDirectories(_paths.StagedRoot))
                    if (!string.Equals(Path.GetFullPath(dir), keep, StringComparison.OrdinalIgnoreCase)) TryDeleteDir(dir);
            }
            TryDeleteDir(_paths.RolledBackDir);
            if (!hasPrevious) TryDeleteDir(_paths.PreviousDir);
            if (Directory.Exists(_paths.DataDir))
                foreach (var f in Directory.GetFiles(_paths.DataDir, "*.partial")) TryDelete(f);
        }
        finally
        {
            _work.Release();
        }
    }

    // ── 내부 ─────────────────────────────────────────────────────────────
    private void SetPhase(string phase, bool keepError = false)
    {
        lock (_gate)
        {
            if (_phase == "applying") return;   // 서버가 내려가는 중에는 바꾸지 않는다
            _phase = phase;
            _progress = 0;
            if (phase != "idle") { _error = null; _errorFix = null; }
            else if (!keepError) { _error = null; _errorFix = null; }
        }
    }

    private void Change(Action<UpdateState> change)
    {
        lock (_gate)
        {
            change(_state);
            Save();
        }
    }

    private UpdateState Load()
    {
        try
        {
            if (File.Exists(_paths.StatePath))
                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_paths.StatePath)) ?? new UpdateState();
        }
        catch (Exception ex) { _log.LogWarning("업데이트 상태 파일을 읽지 못해 새로 만듭니다: {Message}", ex.Message); }
        return new UpdateState();
    }

    // 쓰다가 끊겨도 파일이 깨지지 않게 임시 파일에 쓰고 바꿔치기한다
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDir);
            var temp = _paths.StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _paths.StatePath, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("업데이트 상태를 저장하지 못했습니다: {Message}", ex.Message); }
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    // zip 에서 풀린 실행 파일에 실행 권한을 준다 (Linux)
    private static void MakeExecutable(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var exe = Path.Combine(dir, "GanttScheduler");
        try
        {
            if (File.Exists(exe))
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception) { /* 권한을 못 바꾸면 dotnet 으로 실행한다 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* 무시 */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception) { /* 다음 정리 때 다시 */ }
    }
}

// 서버가 요청을 받기 전에 끝내야 하는 일: DB 준비(마이그레이션·기준 정보), 직전 파일 교체의 결과 정리.
// 호스트 시작 단계에서 돌리는 이유 — Windows 서비스는 프로세스가 뜬 뒤 30초 안에 서비스 관리자에 연결해야 하는데,
// 그 연결이 호스트 시작 때 이뤄진다. 그 전에 DB 작업을 하면 마이그레이션이 길어질 때 서비스 시작이 실패한다
public class StartupWork : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly UpdateService _updates;

    public StartupWork(IServiceProvider services, UpdateService updates)
    {
        _services = services;
        _updates = updates;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using (var scope = _services.CreateScope())
        {
            await GanttScheduler.Data.DbInitializer.InitAsync(scope.ServiceProvider);
        }
        _updates.Startup();
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

// 주기 확인(기본 6시간)과 자동 적용 판단을 돌리는 백그라운드 작업
public class UpdateWorker : BackgroundService
{
    private readonly UpdateService _updates;
    private readonly ILogger<UpdateWorker> _log;
    private readonly TimeSpan _every;

    public UpdateWorker(UpdateService updates, IConfiguration cfg, ILogger<UpdateWorker> log)
    {
        _updates = updates;
        _log = log;
        _every = TimeSpan.FromHours(Math.Clamp(cfg.GetValue("Update:CheckHours", 6.0), 0.01, 168));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // 시작 직후의 요청 처리와 겹치지 않게 조금 기다린다
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            var nextCheck = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= nextCheck)
                    {
                        nextCheck = DateTime.UtcNow + _every;
                        await _updates.AutoCheckAsync(ct);
                        _updates.Cleanup();
                    }
                    _updates.TryAutoApply();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "업데이트 주기 작업 실패");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }
}
