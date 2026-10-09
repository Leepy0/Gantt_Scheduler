using System.Diagnostics;
using System.Text.Json;

namespace GanttScheduler.Services;

// 파일 교체 도우미. 서버는 실행 중인 자기 파일을 덮어쓸 수 없으므로, 받아 둔 새 버전 폴더의 실행 파일을
// "--apply-update" 로 따로 띄워 교체를 맡긴다. 순서: 점검 중 안내(app_offline.htm) → 서버 종료 대기 →
// 지금 파일을 보관 폴더로 옮김 → 새 파일 복사 → 안내 제거 → (직접 실행이면) 서버 다시 시작.
// 되돌리기도 같은 절차로, 보관해 둔 이전 버전 폴더의 실행 파일이 맡는다.
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

        var offline = Path.Combine(app, OfflineFile);
        string? error = null;
        try
        {
            Log($"{opt.Get("--kind")} 시작: {opt.Get("--from-version")} → {opt.Get("--to")} (app={app}, from={from})");
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

        try
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(
                new Result(error == null, opt.Get("--kind"), opt.Get("--from-version"), opt.Get("--to"), error, DateTime.UtcNow)));
        }
        catch (Exception ex) { Log("결과 기록 실패: " + ex.Message); }

        try { File.Delete(offline); }
        catch (Exception ex) { Log("안내 파일 삭제 실패: " + ex.Message); }

        // 직접 실행(IIS 아님)이면 서버를 다시 띄운다. 실패했어도 원래 버전이 다시 떠야 한다
        var restart = opt.Get("--restart", "");
        if (restart.Length > 0)
        {
            try
            {
                var psi = new ProcessStartInfo(restart) { UseShellExecute = false, WorkingDirectory = opt.Get("--restart-dir", app) };
                foreach (var a in opt.Rest) psi.ArgumentList.Add(a);
                Process.Start(psi);
                Log("서버 다시 시작: " + restart);
            }
            catch (Exception ex) { Log("서버 다시 시작 실패: " + ex.Message); }
        }
        return error == null ? 0 : 1;
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
