using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GanttScheduler.Services;

// 파일 교체 도우미. 서버는 실행 중인 자기 파일을 덮어쓸 수 없으므로, 받아 둔 새 버전 폴더의 실행 파일을
// "--apply-update" 로 따로 띄워 교체를 맡긴다. 순서: 점검 중 안내(app_offline.htm) → 서버 종료 대기 →
// 지금 파일을 보관 폴더로 옮김 → 새 파일 복사 → 안내 제거 → (직접 실행이면) 서버 다시 시작.
// 되돌리기도 같은 절차로, 보관해 둔 이전 버전 폴더의 실행 파일이 맡는다.
// 적용(apply)일 때는 다시 띄운 새 버전이 정해진 시간 안에 요청을 받기 시작했는지(started.json) 확인하고,
// 시작되지 않으면 DB 구조가 같을 때만 이전 버전으로 되돌린다. 도우미는 새 버전의 실행 파일이므로 옛 서버가 띄워도 이 확인을 한다
public static class UpdateApplier
{
    public const string Flag = "--apply-update";

    // 배포 패키지에 들어 있는 파일 목록. 설치 폴더에서 "앱이 깔아 둔 파일"과 그 밖의 파일(데이터·직접 넣은 파일)을 가른다
    public const string FileList = "package.files";

    // IIS 는 이 파일이 있으면 앱을 내리고 모든 요청에 이 내용을 보여 준다 (파일 잠금도 풀린다)
    public const string OfflineFile = "app_offline.htm";

    // 서버마다 손본 설정 — 이미 있으면 덮어쓰지 않는다
    public static readonly string[] Keep = { "web.config", "appsettings.Production.json" };

    // 어떤 경우에도 건드리지 않는 데이터 폴더
    private const string DataDir = "App_Data";

    public record Result(bool Ok, string Kind, string From, string To, string? Error, DateTime At);

    // 새 버전이 시작되기를 기다리는 기본 시간. 서버가 --verify-seconds 로 바꿀 수 있다 (0 = 확인하지 않음)
    public const int DefaultVerifySeconds = 120;

    public static int Run(string[] args)
    {
        var opt = Parse(args);
        var app = Path.GetFullPath(opt.Get("--app"));
        var from = Path.GetFullPath(opt.Get("--from"));
        var backup = Path.GetFullPath(opt.Get("--backup"));
        var resultPath = opt.Get("--result");
        var logPath = Path.Combine(Path.GetDirectoryName(resultPath)!, "apply.log");
        void Log(string text)
        {
            try { File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {text}{Environment.NewLine}"); }
            catch (Exception) { /* 기록 실패가 교체를 막지 않게 */ }
        }

        var kind = opt.Get("--kind");
        var fromVersion = opt.Get("--from-version");
        var to = opt.Get("--to");
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(resultPath))!;
        var statePath = Path.Combine(dataDir, "state.json");
        var startedPath = Path.Combine(dataDir, StartedMark.FileName);
        // 교체 전 상태. 새 버전이 시작되지 않아 되돌릴 때 이 상태로 돌려놓는다 (새 버전이 잠깐 떠서 바꿔 놓은 것을 지우기 위해)
        var snapshot = ReadText(statePath);

        var offline = Path.Combine(app, OfflineFile);
        string? error = null;
        try
        {
            Log($"{kind} 시작: {fromVersion} → {to} (app={app}, from={from})");
            // 지금 서버가 남긴 시작 표시를 지워 둔다. 다시 띄운 뒤 새로 생기는 표시만 본다
            TryDelete(startedPath);
            File.WriteAllText(offline, OfflinePage);

            if (int.TryParse(opt.Get("--pid", ""), out var pid)) WaitExit(pid, TimeSpan.FromSeconds(90));
            if (!WaitUnlocked(app, TimeSpan.FromSeconds(90)))
                throw new IOException("서버가 종료되지 않아 파일을 바꿀 수 없습니다.");

            Swap(app, from, backup);
            Log("교체 완료");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("실패: " + ex);
        }

        WriteResult(resultPath, new Result(error == null, kind, fromVersion, to, error, DateTime.UtcNow), Log);

        try { File.Delete(offline); }
        catch (Exception ex) { Log("안내 파일 삭제 실패: " + ex.Message); }

        // 서버를 다시 띄운다. 교체에 실패했어도 원래 버전이 다시 떠야 한다
        var began = DateTime.UtcNow;
        var server = StartServer(opt, app, Log);

        // 새 버전이 실제로 요청을 받기 시작했는지 확인한다. IIS 는 다음 요청 때 앱을 띄우므로(도우미가 띄우지 않음) 확인하지 않는다
        var verify = int.TryParse(opt.Get("--verify-seconds", ""), out var v) ? Math.Clamp(v, 0, 1800) : DefaultVerifySeconds;
        if (error == null && kind == "apply" && verify > 0 && server.Restarted)
        {
            // 기다리는 시간은 다시 띄우기가 끝난 뒤부터 센다 (서비스 시작 재시도에 쓴 시간은 빼고)
            var crashed = false;
            var started = WaitStarted(startedPath, to, DateTime.UtcNow + TimeSpan.FromSeconds(verify),
                () => crashed = server.Failed || server.Process is { HasExited: true });
            if (started) Log($"새 버전 v{to} 시작 확인");
            else
            {
                // 시작하다 멈췄으면(프로세스 종료·서비스 시작 실패) 그 사실을, 아니면 기다린 시간을 이유로 남긴다
                var why = crashed ? "시작하다 멈춤" : $"{verify}초 안에 시작되지 않음";
                StartFailed(new FailedStart(opt, app, backup, Path.Combine(dataDir, "rolledback"), resultPath, statePath, snapshot,
                    fromVersion, to, why, began, server), Log);
                return 1;
            }
        }
        return error == null ? 0 : 1;
    }

    // ── 다시 띄우기와 시작 확인 ────────────────────────────────────────────
    // Restarted: 도우미가 서버를 띄웠는지(서비스·직접 실행). Failed: 서비스를 시작하지 못함. Process: 직접 실행한 서버
    private sealed record Server(bool Restarted, bool Failed, Process? Process);

    private static Server StartServer(Options opt, string app, Action<string> log)
    {
        var service = opt.Get("--start-service", "");
        if (service.Length > 0)
        {
            var failed = ServiceInstaller.StartService(service, TimeSpan.FromSeconds(90));
            log(failed == null ? "서비스 다시 시작: " + service : "서비스 다시 시작 실패: " + failed);
            return new Server(true, failed != null, null);
        }

        // 직접 실행(서비스·IIS 아님)한 서버는 같은 명령으로 다시 띄운다
        var restart = opt.Get("--restart", "");
        if (restart.Length > 0)
        {
            try
            {
                var psi = new ProcessStartInfo(restart) { UseShellExecute = false, WorkingDirectory = opt.Get("--restart-dir", app) };
                foreach (var a in opt.Rest) psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                log("서버 다시 시작: " + restart);
                return new Server(true, p == null, p);
            }
            catch (Exception ex)
            {
                log("서버 다시 시작 실패: " + ex.Message);
                return new Server(true, true, null);
            }
        }
        return new Server(false, false, null);
    }

    // 새 버전이 시작 표시(started.json)를 남길 때까지 기다린다. 서버가 먼저 끝나 버리면(시작하자마자 죽음) 더 기다리지 않는다
    internal static bool WaitStarted(string startedPath, string version, DateTime until, Func<bool> gone)
    {
        while (true)
        {
            if (StartedVersion(startedPath) == version) return true;
            if (gone() || DateTime.UtcNow > until)
            {
                Thread.Sleep(500);   // 끝나기 직전에 쓴 표시를 놓치지 않게 한 번 더 본다
                return StartedVersion(startedPath) == version;
            }
            Thread.Sleep(1000);
        }
    }

    internal static string? StartedVersion(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<StartedMark>(File.ReadAllText(path))?.Version : null; }
        catch (Exception) { return null; }   // 쓰는 중이거나 깨졌으면 아직 없는 것으로 본다
    }

    // Why: 시작되지 않은 모습 ("시작하다 멈춤" / "120초 안에 시작되지 않음") — 안내 문장에 그대로 넣는다
    private sealed record FailedStart(Options Opt, string App, string Backup, string RolledBack, string ResultPath, string StatePath, string? Snapshot,
        string From, string To, string Why, DateTime Began, Server Server);

    // 새 버전이 시작되지 않았다. DB 구조가 같으면 이전 버전으로 되돌려 다시 띄우고, 다르면(마이그레이션이 이미 적용됐을 수 있음)
    // 되돌리지 않고 복구 방법을 남긴다. 결과·상태는 다시 뜬 이전 버전이 이력과 업데이트 화면에 보여 준다
    private static void StartFailed(FailedStart f, Action<string> log)
    {
        var detail = StartupError(f.App, f.Began);
        // 새 버전의 DB 구조: 도우미 자신(새 버전의 실행 파일)의 값, 없으면 서버가 넘긴 배포 정보의 값
        var toSchema = AppBuild.FromAssembly().Schema;
        if (toSchema.Length == 0) toSchema = f.Opt.Get("--to-schema", "");
        var fromSchema = f.Opt.Get("--from-schema", "");
        if (fromSchema.Length == 0) fromSchema = PendingSchema(f.Snapshot) ?? "";
        var sameSchema = fromSchema.Length > 0 && fromSchema == toSchema;
        log($"새 버전(v{f.To})이 시작되지 않음: {f.Why} (DB 구조 {(sameSchema ? "같음" : $"다르거나 알 수 없음: {fromSchema} → {toSchema}")})"
            + (detail != null ? Environment.NewLine + detail : ""));

        if (!sameSchema)
        {
            var message = $"새 버전(v{f.To})이 시작되지 않았습니다({f.Why}). DB 구조가 바뀌는 업데이트라 자동으로 되돌리지 않았습니다. " +
                          $"DB 백업을 복원한 뒤 이전 버전(v{f.From}) 배포 파일로 설치하세요.";
            WriteState(f.StatePath, ReadText(f.StatePath), s => s["StartFailure"] = Failure(f, message, detail, false), log);
            WriteResult(f.ResultPath, new Result(false, "apply", f.From, f.To, message, DateTime.UtcNow), log);
            log(message);
            return;
        }

        string? rollbackError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            StopServer(f.Opt, f.App, f.Server.Process, log);
            try
            {
                if (!WaitUnlocked(f.App, TimeSpan.FromSeconds(60))) throw new IOException("새 버전이 종료되지 않아 파일을 바꿀 수 없습니다.");
                Swap(f.App, f.Backup, f.RolledBack);
                rollbackError = null;
                break;
            }
            catch (Exception ex)
            {
                rollbackError = ex.Message;
                log($"되돌리기 실패 ({attempt}/3): {ex.Message}");
            }
        }

        string error;
        if (rollbackError == null)
        {
            error = $"새 버전(v{f.To})이 시작되지 않아({f.Why}) 이전 버전(v{f.From})으로 자동으로 되돌렸습니다.";
            // 교체 전 상태로 돌려놓고(되돌린 버전이 남긴 이력·이전 버전 기록 제거), 이 버전은 자동으로 다시 적용하지 않게 한다.
            // SkipAuto 는 원래 뜻(되돌린 버전) 그대로 쓴다 — 이 기능이 없는 옛 버전으로 돌아가도 밤마다 다시 적용하지 않도록
            WriteState(f.StatePath, f.Snapshot ?? ReadText(f.StatePath), s =>
            {
                s["SkipAuto"] = f.To;
                s["StartFailure"] = Failure(f, error, detail, true);
                s["Previous"] = null;   // 보관 폴더는 이번 교체에서 지금 버전 파일로 바뀌었다
            }, log);
        }
        else
        {
            error = $"새 버전(v{f.To})이 시작되지 않았고({f.Why}) 이전 버전으로 되돌리지도 못했습니다: {rollbackError}";
            WriteState(f.StatePath, ReadText(f.StatePath), s => s["StartFailure"] = Failure(f, error, detail, false), log);
        }
        WriteResult(f.ResultPath, new Result(false, "apply", f.From, f.To,
            error + (detail != null ? " 시작 오류: " + FirstLine(detail) : ""), DateTime.UtcNow), log);
        log(error);
        StartServer(f.Opt, f.App, log);
    }

    private static JsonNode? Failure(FailedStart f, string error, string? detail, bool rolledBack) =>
        JsonSerializer.SerializeToNode(new StartFailure(f.To, f.From, error, detail, rolledBack, DateTime.UtcNow));

    // 시작하다 죽은 서버가 남긴 오류(App_Data/startup-error.log)의 앞부분. 이번에 다시 띄운 뒤에 쓴 것만
    internal static string? StartupError(string app, DateTime since)
    {
        try
        {
            var path = Path.Combine(app, DataDir, ServiceInstaller.StartupErrorFile);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < since.AddSeconds(-2)) return null;
            var text = string.Join(Environment.NewLine, File.ReadLines(path).Take(15)).Trim();
            return text.Length == 0 ? null : text.Length > 2000 ? text[..2000] : text;
        }
        catch (Exception) { return null; }
    }

    // 교체 직전에 서버가 기록한 DB 구조 (옛 서버는 --from-schema 를 넘기지 않으므로 상태 파일에서 읽는다)
    private static string? PendingSchema(string? state)
    {
        try { return state == null ? null : JsonNode.Parse(state)?["Pending"]?["FromSchema"]?.GetValue<string>(); }
        catch (Exception) { return null; }
    }

    private static string FirstLine(string text)
    {
        // 첫 줄은 기록 시각이라 그다음 줄(예외 내용)을 쓴다
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.Count > 1 && DateTime.TryParse(lines[0], out _) ? lines[1] : lines.FirstOrDefault() ?? "";
        return line.Length > 300 ? line[..300] : line;
    }

    // 되돌리기 전에 새 버전을 확실히 내린다. 서비스는 복구 동작(실패 시 다시 시작)으로 다시 뜰 수 있어 남은 프로세스까지 끝낸다
    private static void StopServer(Options opt, string app, Process? process, Action<string> log)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
        }
        catch (Exception ex) { log("서버 종료 실패: " + ex.Message); }

        var service = opt.Get("--start-service", "");
        if (service.Length == 0) return;
        var stopped = ServiceInstaller.StopService(service, TimeSpan.FromSeconds(60));
        if (stopped != null) log("서비스 중지: " + stopped);
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(app, "GanttScheduler.exe");
        foreach (var p in Process.GetProcessesByName("GanttScheduler"))
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || !string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10000);
                    log("남은 서버 프로세스 종료: " + p.Id);
                }
                catch (Exception ex) { log("서버 프로세스 종료 실패: " + ex.Message); }
            }
        }
    }

    // 상태 파일은 서버의 UpdateState 다. 이 도우미보다 옛/새 버전이 쓴 항목도 그대로 남도록 JSON 으로 고친다
    private static void WriteState(string path, string? text, Action<JsonObject> change, Action<string> log)
    {
        try
        {
            JsonObject state;
            try { state = (text == null ? null : JsonNode.Parse(text) as JsonObject) ?? new JsonObject(); }
            catch (JsonException) { state = new JsonObject(); }
            change(state);
            var temp = path + ".tmp";
            File.WriteAllText(temp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) { log("상태 기록 실패: " + ex.Message); }
    }

    private static void WriteResult(string path, Result result, Action<string> log)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(result)); }
        catch (Exception ex) { log("결과 기록 실패: " + ex.Message); }
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception) { return null; }
    }

    // 앱 폴더의 파일을 source 의 내용으로 바꾼다. 원래 파일은 backup 으로 옮겨 둔다(되돌리기용).
    // 패키지 목록에 없는 파일(데이터, 직접 넣은 파일)과 서버별 설정은 그대로 둔다. 도중에 실패하면 원래대로 되돌린다
    public static void Swap(string appDir, string sourceDir, string backupDir)
    {
        appDir = Path.GetFullPath(appDir);
        sourceDir = Path.GetFullPath(sourceDir);
        backupDir = Path.GetFullPath(backupDir);

        var incoming = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(f => Rel(sourceDir, f)).Where(r => !Protected(r)).ToList();
        if (!incoming.Any(r => r.Equals(EntryDll, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("새 버전 폴더에 실행 파일이 없습니다.");

        // 지금 깔려 있는 파일: 설치 때의 목록이 있으면 그것, 없으면(목록 도입 전 설치) 새 버전과 이름이 겹치는 파일만
        var installed = (ReadList(appDir) ?? incoming).Where(r => !Protected(r)).ToList();

        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
        Directory.CreateDirectory(backupDir);

        var moved = new List<string>();
        var copied = new List<string>();
        try
        {
            foreach (var rel in installed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var src = Inside(appDir, rel);
                if (!File.Exists(src) || Kept(appDir, rel)) continue;
                var dst = Inside(backupDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                Retry(() => File.Move(src, dst));
                moved.Add(rel);
            }
            foreach (var rel in incoming)
            {
                if (Kept(appDir, rel)) continue;
                var dst = Inside(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                // 목록에 없던 파일이 같은 이름으로 있으면 덮어쓰기 전에 보관한다 (실패하면 되돌릴 수 있게)
                if (File.Exists(dst))
                {
                    var keep = Inside(backupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    Retry(() => File.Move(dst, keep, overwrite: true));
                    moved.Add(rel);
                }
                Retry(() => File.Copy(Inside(sourceDir, rel), dst));
                copied.Add(rel);
            }
        }
        catch (Exception)
        {
            foreach (var rel in copied) TryDelete(Inside(appDir, rel));
            foreach (var rel in moved)
            {
                try { File.Move(Inside(backupDir, rel), Inside(appDir, rel), overwrite: true); }
                catch (Exception) { /* 하나가 안 돌아와도 나머지는 계속 되돌린다 */ }
            }
            throw;
        }

        // 파일이 빠져 비게 된 폴더 정리 (깊은 폴더부터)
        foreach (var dir in moved.Select(r => Path.GetDirectoryName(Inside(appDir, r))!).Distinct().OrderByDescending(d => d.Length))
        {
            try
            {
                if (dir.Length > appDir.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception) { /* 정리 실패는 무시 */ }
        }
    }

    public const string EntryDll = "GanttScheduler.dll";

    public static List<string>? ReadList(string dir)
    {
        var path = Path.Combine(dir, FileList);
        if (!File.Exists(path)) return null;
        var list = File.ReadAllLines(path).Select(l => l.Trim().Replace('\\', '/')).Where(l => l.Length > 0).ToList();
        if (!list.Contains(FileList)) list.Add(FileList);
        return list;
    }

    // 데이터 폴더와 점검 안내 파일은 교체 대상에서 뺀다
    private static bool Protected(string rel) =>
        rel.Equals(OfflineFile, StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith(DataDir + "/", StringComparison.OrdinalIgnoreCase)
        || rel.Split('/').Contains("..");

    private static bool Kept(string appDir, string rel) =>
        Keep.Contains(rel, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(appDir, rel));

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Inside(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("폴더 밖을 가리키는 경로입니다: " + rel);
        return full;
    }

    private static void WaitExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception) { /* 이미 끝났으면 예외 — 그대로 진행 */ }
    }

    // Windows 는 실행 중인 DLL 을 쓰기용으로 열 수 없다. 열리면 서버가 완전히 내려간 것이다
    public static bool WaitUnlocked(string appDir, TimeSpan timeout)
    {
        var dll = Path.Combine(appDir, EntryDll);
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (!File.Exists(dll)) return true;
                using var fs = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(500);
        }
    }

    // 백신·색인 프로그램이 파일을 잠깐 잡고 있는 경우가 있어 몇 번 다시 해 본다
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(300 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* 무시 */ }
    }

    private sealed class Options
    {
        public Dictionary<string, string> Values { get; } = new();
        public List<string> Rest { get; } = new();
        public string Get(string key) => Values.TryGetValue(key, out var v) ? v : throw new ArgumentException(key + " 값이 없습니다.");
        public string Get(string key, string fallback) => Values.TryGetValue(key, out var v) ? v : fallback;
    }

    // "--이름 값" 쌍. "--" 뒤는 서버를 다시 띄울 때 넘길 인자
    private static Options Parse(string[] args)
    {
        var opt = new Options();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--") { opt.Rest.AddRange(args.Skip(i + 1)); break; }
            if (i + 1 < args.Length) opt.Values[args[i]] = args[++i];
        }
        return opt;
    }

    // 교체하는 동안 IIS 가 대신 보여 주는 화면. 이 파일 하나만 응답되므로 글꼴·스타일을 모두 안에 넣는다
    private const string OfflinePage = """
        <!DOCTYPE html>
        <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>업데이트 중 · 과제 스케줄러</title>
        <style>
        body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#F4F5F7;color:#16181D;
        font-family:"Malgun Gothic","Apple SD Gothic Neo","Noto Sans KR",system-ui,sans-serif;font-size:14px;line-height:1.5}
        .box{width:min(420px,calc(100vw - 32px));padding:32px;border-radius:12px;background:#fff;box-shadow:0 1px 2px rgba(16,24,40,.06)}
        h1{margin:0 0 8px;font-size:20px;font-weight:600}p{margin:0;color:#454B57}.b{color:#1428A0;font-weight:600;margin-bottom:16px}
        </style></head>
        <body><div class="box"><p class="b">과제 스케줄러</p><h1>새 버전을 적용하고 있습니다</h1>
        <p>보통 1분 안에 끝납니다. 끝나면 이 화면이 자동으로 다시 열립니다.</p></div>
        <script>
        setInterval(function(){fetch(location.href,{cache:'no-store'}).then(function(r){if(r.ok)location.reload();}).catch(function(){});},3000);
        </script></body></html>
        """;
}
