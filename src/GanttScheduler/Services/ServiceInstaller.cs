using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace GanttScheduler.Services;

// Windows 서비스 등록·제거:  GanttScheduler.exe install [--port 80] [--name GanttScheduler] [--system] [--remoteip 10.0.0.0/8,...]
//                           GanttScheduler.exe uninstall [--name GanttScheduler]
// 스크립트(PowerShell)는 사내 실행 정책에 막힐 수 있어 실행 파일이 직접 한다.
// 서비스는 전용 계정(NT SERVICE\<이름>)으로 돌린다. 사내망에 열려 있는 웹 서버를 SYSTEM 권한으로 돌리지 않기 위해서다.
// 그 계정에는 (1) 프로그램 폴더 수정 권한과 (2) 자기 서비스를 시작할 권한만 준다 — 둘 다 자동 업데이트가 쓴다.
public static class ServiceInstaller
{
    public const string DefaultName = "GanttScheduler";
    public const string DisplayName = "과제 스케줄러";
    public const int DefaultPort = 80;
    public const string StartupErrorFile = "startup-error.log";
    // 설치·제거 명령이 화면에 쓴 내용을 그대로 남기는 파일(App_Data 아래). 창이 닫혀도 무엇이 일어났는지 볼 수 있게
    public const string InstallLogFile = "install.log";
    public const string Usage = "사용법: install-service.cmd [--port 8080] [--remoteip 10.0.0.0/8,192.168.0.0/16] [--name GanttScheduler] [--system]";

    // 첫 인자가 install/uninstall 이면 명령이다. 옵션을 앞에 적은 경우(--port 8080 install)도 받는다 — 놓치면 웹 서버로 떠 버린다.
    // 서비스로 시작된 프로세스(--service)는 명령이 아니다
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && (args[0] is "install" or "uninstall"
            || args[0].StartsWith('-') && !args.Contains("--service") && args.Any(a => a is "install" or "uninstall"));

    public sealed record Options(string Verb, string Name, int Port, bool System, string? RemoteIp, bool Pause);

    // 명령 인자를 읽는다. 모르는 인자나 값이 빠진 옵션은 거절한다 — 예전에는 --port=8080 이나 -port 8080 을 못 읽고
    // 조용히 80 으로 등록해, 80 을 쓰는 다른 프로그램(IIS 등)과 부딪혀 엉뚱한 오류가 났다.
    // 문서·메신저에서 복사하면 '--' 가 긴 줄표(—, –)나 전각 문자로 바뀌어 오므로 그것은 고쳐 읽는다
    public static Options ParseOptions(string[] args)
    {
        var verb = args.First(a => a is "install" or "uninstall");
        var install = verb == "install";
        string name = DefaultName;
        var port = DefaultPort;
        bool system = false, pause = false, verbSeen = false;
        string? remote = null;
        for (var i = 0; i < args.Length; i++)
        {
            var raw = Normalize(args[i]);
            if (raw.Length == 0) continue;
            if (raw == verb && !verbSeen) { verbSeen = true; continue; }
            var key = raw;
            string? value = null;
            var eq = raw.IndexOf('=');
            if (raw.StartsWith("--", StringComparison.Ordinal) && eq > 2) { key = raw[..eq]; value = raw[(eq + 1)..]; }
            string Value() => value ?? (i + 1 < args.Length ? Normalize(args[++i]) : throw new ArgumentException($"{key} 다음에 값을 적어 주세요. {Usage}"));
            switch (key.ToLowerInvariant())
            {
                case "--pause":
                    pause = true;
                    break;
                case "--name":
                    name = CheckName(Value());
                    break;
                case "--system" when install:
                    system = true;
                    break;
                case "--port" when install:
                    var text = Value();
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                        throw new ArgumentException($"--port 에는 1~65535 사이의 숫자를 지정해 주세요: '{text}'" + Environment.NewLine + Usage);
                    break;
                case "--remoteip" when install:
                    remote = RemoteIp(Value());
                    break;
                default:
                    throw new ArgumentException(UnknownHint(raw) + Environment.NewLine + Usage);
            }
        }
        return new Options(verb, name, port, system, remote, pause);
    }

    private static string Normalize(string arg)
    {
        // 전각 숫자·하이픈(８０８０, －－port)은 반각으로, 줄표로 시작하면 '--' 로 바꾼다
        var s = arg.Trim().Normalize(NormalizationForm.FormKC);
        foreach (var dash in new[] { "\u2014", "\u2013", "\u2012", "\u2212" })
            if (s.StartsWith(dash, StringComparison.Ordinal)) return "--" + s[dash.Length..].TrimStart('-');
        return s;
    }

    private static string UnknownHint(string arg)
    {
        if (arg.All(char.IsAsciiDigit)) return $"'{arg}' 을(를) 알 수 없습니다. 포트는 --port {arg} 처럼 앞에 --port 를 붙여 적어 주세요.";
        var bare = arg.TrimStart('-', '/');
        if (bare.Length > 0 && !arg.StartsWith("--", StringComparison.Ordinal) && new[] { "port", "name", "remoteip", "system", "pause" }.Contains(bare.ToLowerInvariant()))
            return $"'{arg}' 을(를) 알 수 없습니다. --{bare.ToLowerInvariant()} 처럼 하이픈 두 개로 적어 주세요.";
        return $"'{arg}' 을(를) 알 수 없는 인자입니다.";
    }

    // 서비스로 시작될 때 SCM 이 넘기는 인자(--service <이름>)에서 서비스 이름을 읽는다. 없으면 서비스가 아니다
    public static string? ServiceNameFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--service");
        return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
    }

    // 서비스가 실행할 명령. 포트를 여기에 두므로 설정 파일을 고치지 않는다 (바꾸려면 install 을 다시 실행)
    public static string BinPath(string exe, string name, int port) => $"\"{exe}\" --service {name} --urls http://*:{port}";

    // 서비스 보안 설명자(SDDL)에 "이 계정은 서비스를 시작·중지·조회할 수 있다"를 더한다.
    // 교체 도우미는 서비스 계정으로 돌기 때문에, 이 권한이 없으면 파일을 바꾼 뒤 서비스를 다시 띄우지 못한다.
    // 중지(WP)는 새 버전이 시작되지 않아 이전 버전으로 되돌릴 때, 복구 동작으로 다시 뜨는 서비스를 멈추는 데 쓴다
    public static string AddStartRight(string sddl, string sid)
    {
        sddl = sddl.Trim();
        var ace = $"(A;;RPWPLC;;;{sid})";
        if (sddl.Contains(ace, StringComparison.OrdinalIgnoreCase)) return sddl;
        // 예전 설치가 넣은 시작·조회 권한(RPLC)은 새 권한으로 바꾼다
        var old = $"(A;;RPLC;;;{sid})";
        var at = sddl.IndexOf(old, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return sddl[..at] + ace + sddl[(at + old.Length)..];
        if (!sddl.StartsWith("D:", StringComparison.Ordinal)) throw new FormatException("서비스 보안 설명자 형식을 알 수 없습니다: " + sddl);
        var sacl = sddl.IndexOf("S:(", StringComparison.Ordinal);   // 감사 설정(SACL)이 있으면 그 앞에 넣는다
        return sacl < 0 ? sddl + ace : sddl[..sacl] + ace + sddl[sacl..];
    }

    // 방화벽 규칙의 원격 주소 제한(--remoteip). 쉼표로 구분한 CIDR·주소와 netsh 의 LocalSubnet 만 받는다.
    // 잘못 쓴 값을 그대로 넘기면 규칙 추가가 실패해 방화벽이 열리지 않으므로 등록 전에 거른다
    public static string? RemoteIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)) list.Add("LocalSubnet");
            else if (AccessOptions.TryParseNetwork(part, out var net)) list.Add(part.Contains('/') ? net.ToString() : net.BaseAddress.ToString());
            else throw new ArgumentException($"--remoteip 의 '{part}' 을(를) 해석할 수 없습니다. 10.0.0.0/8,192.168.1.0/24 처럼 쉼표로 구분한 CIDR 이나 주소로 적어 주세요.");
        }
        return list.Count == 0 ? null : string.Join(",", list);
    }

    // 설치를 마친 뒤 앱 설정(Access:AllowedNetworks) 안내. LocalSubnet 은 방화벽만 아는 이름이라 앱 설정에 그대로 적으면
    // 서버가 시작하지 않는다 — 그때는 실제 대역을 적으라고 따로 알린다
    public static string AllowedNetworksHint(string remoteIp)
    {
        var parts = remoteIp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cidrs = parts.Where(x => !x.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)).ToList();
        const string where = "appsettings.Production.json 의 Access:AllowedNetworks";
        if (cidrs.Count == parts.Length)
            return $"앱에서도 같은 대역만 받으려면 {where} 에 같은 값을 적습니다.";
        return $"앱에서도 같은 대역만 받으려면 {where} 에 서버가 속한 대역을 10.1.0.0/16 처럼 CIDR 로 적습니다" +
               (cidrs.Count > 0 ? $"(나머지 {string.Join(",", cidrs)} 와 함께)" : "") +
               ". LocalSubnet 은 방화벽 전용이라 앱 설정에 적으면 서버가 시작하지 않습니다.";
    }

    // 방화벽 인바운드 규칙 추가 명령(netsh 인자). remoteIp 가 없으면 모든 주소에서 받는다
    public static string[] FirewallRule(string name, int port, string? remoteIp)
    {
        var args = new List<string> { "advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}" };
        if (remoteIp != null) args.Add($"remoteip={remoteIp}");
        return args.ToArray();
    }

    // "sc query" 출력에서 상태를 읽는다 (상태 이름은 Windows 언어와 상관없이 영문 상수다)
    public static string? StateOf(string scQueryOutput)
    {
        var m = Regex.Match(scQueryOutput, @":\s*\d+\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    // 끝날 때 Enter 를 기다릴지. --pause 를 받았거나, 이 창이 이 프로세스만의 것이면(탐색기·실행 창·바로 가기로 띄웠거나
    // 권한 상승으로 새로 연 창) 끝나는 순간 창이 닫혀 결과를 못 보므로 기다린다. 입출력이 파일·파이프로 돌려져 있으면(CI 등) 기다리지 않는다
    public static bool ShouldPause(bool flag, bool ownsConsole, bool redirected) => !redirected && (flag || ownsConsole);

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { /* 콘솔이 없는 경우 */ }
        var pause = ShouldPause(args.Any(a => Normalize(a) == "--pause"), OwnsConsole(), Redirected());
        using var log = InstallLog.Start(args);
        // 아래 try 밖에서(다른 스레드 등) 죽어도 이유를 남기고, 창이 닫히기 전에 읽을 수 있게 한다
        UnhandledExceptionEventHandler crash = (_, e) =>
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("예기치 않은 오류로 끝납니다: " + e.ExceptionObject);
                log?.Flush();
                if (pause) WaitEnter();
            }
            catch (Exception) { /* 마지막 시도 */ }
        };
        AppDomain.CurrentDomain.UnhandledException += crash;
        int code;
        var shownElsewhere = false;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Windows 에서만 서비스로 등록할 수 있습니다.");
                return 1;
            }
            // 인자는 권한 확인 창을 띄우기 전에 읽는다 — 잘못 적었으면 이 창에서 바로 알린다
            var options = ParseOptions(args);
            if (!IsAdmin()) (code, shownElsewhere) = Elevate(args, InstallLog.DefaultPath);
            else code = options.Verb == "install" ? Install(options) : Uninstall(options);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("실패: " + ex.Message);
            code = 1;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= crash;
        }
        if (log != null && !shownElsewhere) Console.WriteLine($"기록: {log.Path}");
        // 권한 상승한 새 창이 결과를 보여 주고 기다렸으면 이 창은 기다리지 않는다
        if (pause && !shownElsewhere) WaitEnter();
        return code;
    }

    private static void WaitEnter()
    {
        // 오래 기다리는 동안 눌러 둔 Enter·붙여넣기가 이 질문에 바로 답해 결과가 뜨자마자 창이 닫히지 않게 먼저 비운다
        try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); }
        catch (Exception) { /* 입력이 콘솔이 아니면 비울 것이 없다 */ }
        Console.WriteLine();
        Console.WriteLine("Enter 를 누르면 닫습니다.");
        Console.ReadLine();
    }

    private static bool Redirected()
    {
        try { return Console.IsInputRedirected || Console.IsOutputRedirected; }
        catch (Exception) { return true; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    // 이 콘솔 창에 붙어 있는 프로세스가 자기 하나뿐인가 (명령 프롬프트에서 실행했으면 cmd.exe 가 함께 붙어 있다)
    private static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return GetConsoleProcessList(new uint[4], 4) == 1; }
        catch (Exception) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // 관리자 권한이 아니면 권한 상승 창(UAC)을 띄워 같은 명령을 다시 실행한다. 새 창이 바로 닫히지 않게 --pause 를 붙인다.
    // 돌려주는 ShownElsewhere: 새 창이 결과를 보여 주고 Enter 를 기다렸는가(그러면 이 창은 기다리지 않는다)
    [SupportedOSPlatform("windows")]
    private static (int Code, bool ShownElsewhere) Elevate(string[] args, string? logPath)
    {
        Console.WriteLine("관리자 권한이 필요합니다. 권한 확인 창에서 '예'를 눌러 주세요. 결과는 새로 열리는 관리자 창에 나옵니다.");
        var exe = Environment.ProcessPath!;
        // 권한 상승한 프로세스는 작업 폴더가 System32 로 바뀔 수 있어 프로그램 폴더로 정해 준다
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!args.Any(a => Normalize(a) == "--pause")) psi.ArgumentList.Add("--pause");
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("관리자 창을 열지 못했습니다.");
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine();
            if (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: 권한 확인 창에서 '아니요'
                Console.WriteLine("권한 확인이 취소되어 아무것도 바꾸지 않았습니다.");
            else
            {
                Console.WriteLine($"관리자 권한으로 다시 실행하지 못했습니다: {ex.Message} (코드 {ex.NativeErrorCode})");
                if (IsNetworkPath(exe))
                    Console.WriteLine("  - 프로그램이 네트워크 드라이브에 있습니다. 관리자 권한으로는 연결된 드라이브가 보이지 않을 수 있으니 서버의 로컬 디스크(예: D:\\GanttScheduler)에 풀어 주세요.");
            }
            Console.WriteLine("  - 시작 메뉴에서 '명령 프롬프트'를 마우스 오른쪽 단추 > '관리자 권한으로 실행'으로 연 뒤, 이 폴더로 이동해 같은 명령을 다시 실행해 보세요.");
            return (1, false);
        }
        using (p)
        {
            p.WaitForExit();
            if (p.ExitCode == 0) return (0, true);
            // 실패했으면 관리자 창이 이유를 보여 주고 기다렸다. 그 창이 그보다 먼저 닫혔을 수도 있으니(예기치 않은 종료) 기록 끝부분을 여기에도 보인다
            Console.WriteLine();
            Console.WriteLine($"관리자 창의 작업이 실패했습니다 (코드 {p.ExitCode}).");
            var tail = logPath == null ? null : InstallLog.Tail(logPath, 15);
            if (tail != null)
            {
                Console.WriteLine($"기록({logPath}) 끝부분:");
                Console.WriteLine(tail);
            }
            // 코드는 1 로 돌려준다. install-service.cmd 는 0·1 밖의 코드를 '실행 파일이 결과를 보이지 못했다'로 보고 한 번 더 기다린다
            return (1, false);
        }
    }

    private static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(path);
            return root != null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception) { return false; }
    }

    private static string CheckName(string name) =>
        Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_.-]{0,60}$") ? name
            : throw new ArgumentException("서비스 이름은 영문자로 시작하는 영문·숫자여야 합니다: " + name);

    private static int Install(Options o)
    {
        var (name, port, system, remote) = (o.Name, o.Port, o.System, o.RemoteIp);
        var account = system ? "LocalSystem" : $"NT SERVICE\\{name}";
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exe = Path.Combine(dir, "GanttScheduler.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("배포 패키지(zip)를 푼 폴더의 GanttScheduler.exe 로 실행해 주세요.");

        Console.WriteLine($"{DisplayName} 서비스 등록");
        Console.WriteLine($"  폴더   {dir}");
        Console.WriteLine($"  이름   {name}");
        Console.WriteLine($"  포트   {port}");
        Console.WriteLine($"  계정   {account}");
        Console.WriteLine($"  허용   {remote ?? "모든 주소 (--remoteip 로 제한)"}");
        Console.WriteLine();

        var exists = Sc("query", name).Code == 0;
        if (exists)
        {
            Step("기존 서비스 중지", () => Stop(name));
        }

        // 다른 프로그램(IIS 등)이 포트를 쓰고 있으면 서비스가 뜨자마자 죽으므로 먼저 확인한다
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
            throw new InvalidOperationException($"포트 {port} 을 다른 프로그램이 쓰고 있습니다. 다른 포트로 등록해 주세요 (예: install --port 8080).");

        var bin = BinPath(exe, name, port);
        Step(exists ? "서비스 설정 변경" : "서비스 등록", () =>
        {
            if (exists)
            {
                Must(Sc("config", name, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", DisplayName));
                SetAccount(name, account);
            }
            else if (system)
            {
                Must(Sc("create", name, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", DisplayName));
            }
            else if (Sc("create", name, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", DisplayName, "obj=", account).Code != 0)
            {
                // 만들면서 계정을 지정하지 못하는 Windows 가 있으면, 만든 뒤에 계정을 바꾼다
                if (Sc("query", name).Code != 0) Must(Sc("create", name, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", DisplayName));
                SetAccount(name, account);
            }
            Sc("description", name, "과제 진척 대시보드·간트 일정 관리 웹 서버");
            // 비정상 종료하면 다시 띄운다 (5초, 5초, 그 뒤로는 1분 간격)
            Must(Sc("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"));
        });

        if (!system)
        {
            Step("서비스 계정 권한 (폴더 수정, 서비스 시작)", () =>
            {
                Must(Exec("icacls.exe", dir, "/grant", $"{account}:(OI)(CI)M", "/Q"));
                var sid = Regex.Match(Must(Sc("showsid", name)), @"S-1-5-80(-\d+)+").Value;
                if (sid.Length == 0) throw new InvalidOperationException("서비스 계정의 SID 를 알 수 없습니다.");
                var sddl = Must(Sc("sdshow", name)).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("D:", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("서비스 보안 설명자를 읽지 못했습니다.");
                Must(Sc("sdset", name, AddStartRight(sddl, sid)));
            });
        }

        Step($"방화벽 인바운드 허용 (TCP {port}{(remote != null ? ", " + remote : "")})", () =>
        {
            Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={name}");
            Must(Exec("netsh.exe", FirewallRule(name, port, remote)));
        }, required: false);

        var errorFile = Path.Combine(dir, "App_Data", StartupErrorFile);
        try { File.Delete(errorFile); } catch (Exception) { /* 없으면 그만 */ }

        var started = false;
        Step("서비스 시작", () =>
        {
            Must(Sc("start", name));
            started = WaitHealthy(name, port, TimeSpan.FromSeconds(120));
            if (!started) throw new InvalidOperationException("서비스가 시작되지 않았습니다.");
        }, required: false);

        Console.WriteLine();
        if (!started)
        {
            // 가장 흔한 원인은 DB 연결이다. 서비스가 남긴 오류를 그대로 보여 준다
            if (File.Exists(errorFile))
            {
                Console.WriteLine("서비스가 남긴 오류:");
                Console.WriteLine(string.Join(Environment.NewLine, File.ReadLines(errorFile).Take(12)));
                Console.WriteLine();
            }
            Console.WriteLine("서비스는 등록됐지만 시작하지 못했습니다.");
            Console.WriteLine("  - appsettings.Production.json 의 DB 연결 문자열을 확인하세요.");
            Console.WriteLine($"  - Windows 인증(Trusted_Connection)을 쓰면 SQL Server 에 '{(system ? "NT AUTHORITY\\SYSTEM" : account)}' 로그인이 있어야 합니다.");
            Console.WriteLine("  - 고친 뒤 이 명령을 다시 실행하면 됩니다.");
            return 1;
        }
        Console.WriteLine("완료. 이 서버의 브라우저에서 확인: http://localhost" + (port == 80 ? "" : ":" + port) + "/");
        Console.WriteLine("다른 PC 에서는 http://<서버 이름 또는 IP>" + (port == 80 ? "" : ":" + port) + "/ 로 접속합니다.");
        if (remote != null) Console.WriteLine(AllowedNetworksHint(remote));
        return 0;
    }

    private static int Uninstall(Options o)
    {
        var name = o.Name;
        if (Sc("query", name).Code != 0)
        {
            Console.WriteLine($"'{name}' 서비스가 등록되어 있지 않습니다.");
            return 0;
        }
        Step("서비스 중지", () => Stop(name));
        Step("서비스 제거", () => Must(Sc("delete", name)));
        Step("방화벽 규칙 제거", () => Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={name}"), required: false);
        Console.WriteLine();
        Console.WriteLine("완료. 프로그램 폴더와 데이터(App_Data)는 그대로 두었습니다.");
        return 0;
    }

    // 서비스 실행 계정 지정. 전용 계정은 암호가 없는데, sc 가 암호 인자를 받는 방식이 Windows 판마다 달라 두 가지로 해 본다
    private static void SetAccount(string name, string account)
    {
        var first = Sc("config", name, "obj=", account);
        if (first.Code == 0) return;
        if (account == "LocalSystem") Must(first);
        var second = Sc("config", name, "obj=", account, "password=", "");
        if (second.Code != 0) Must(first);
    }

    private static void Stop(string name)
    {
        Sc("stop", name);
        var until = DateTime.UtcNow.AddSeconds(60);
        var dots = new Progress();
        while (StateOf(Sc("query", name).Output) is { } state && state != "STOPPED")
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("서비스가 멈추지 않습니다. 작업 관리자에서 GanttScheduler.exe 를 끝낸 뒤 다시 실행해 주세요.");
            Thread.Sleep(500);
            dots.Tick();
        }
    }

    // 서비스가 떠서 요청에 답할 때까지 기다린다. 도중에 멈추면(시작 실패) 바로 그만둔다
    private static bool WaitHealthy(string name, int port, TimeSpan timeout)
    {
        // 이 서버 자신에게 묻는 것이므로 시스템 프록시를 거치지 않는다 — 프록시가 localhost 요청을 삼키면 서비스가 떠 있어도 2분을 기다리다 실패로 본다
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        var began = DateTime.UtcNow;
        var dots = new Progress();
        while (DateTime.UtcNow - began < timeout)
        {
            dots.Tick();
            try
            {
                if (http.GetAsync($"http://localhost:{port}/health").GetAwaiter().GetResult().IsSuccessStatusCode) return true;
            }
            catch (Exception) { /* 아직 뜨는 중 */ }
            // 시작 직후에는 상태가 아직 안 바뀌었을 수 있어 몇 초는 지켜본다
            if (StateOf(Sc("query", name).Output) == "STOPPED" && DateTime.UtcNow - began > TimeSpan.FromSeconds(5)) return false;
            Thread.Sleep(1000);
        }
        return false;
    }

    // 오래 걸리는 단계에서 몇 초마다 점을 찍는다. 아무것도 안 바뀌는 창은 멈춘 것처럼 보여 Enter 를 누르게 된다
    private sealed class Progress
    {
        private readonly DateTime _began = DateTime.UtcNow;
        private int _shown;

        public void Tick()
        {
            var due = (int)((DateTime.UtcNow - _began).TotalSeconds / 5);
            for (; _shown < due; _shown++) Console.Write(".");
        }
    }

    private static void Step(string title, Action action, bool required = true)
    {
        Console.Write("  " + title + " … ");
        try
        {
            action();
            Console.WriteLine("완료");
        }
        catch (Exception ex) when (!required)
        {
            Console.WriteLine("건너뜀");
            Console.WriteLine("    " + ex.Message.Replace("\n", "\n    "));
        }
        catch (Exception)
        {
            Console.WriteLine("실패");
            throw;
        }
    }

    private static (int Code, string Output) Sc(params string[] args) => Exec("sc.exe", args);

    private static string Must((int Code, string Output) result) =>
        result.Code == 0 ? result.Output : throw new InvalidOperationException(result.Output.Trim().Length > 0 ? result.Output.Trim() : $"명령이 실패했습니다 (코드 {result.Code}).");

    private static (int Code, string Output) Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // sc·netsh 는 콘솔(OEM) 코드 페이지로 출력한다. 한글 Windows 의 오류 문구가 깨지지 않게 맞춘다
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = oem;
            psi.StandardErrorEncoding = oem;
        }
        catch (Exception) { /* 기본 인코딩으로 읽는다 */ }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(file + " 을 실행하지 못했습니다.");
        var error = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + error.GetAwaiter().GetResult());
    }

    // 교체 도우미가 시작되지 않은 새 버전을 되돌리기 전에 서비스를 멈춘다. 멈췄거나 이미 멈춰 있으면 null, 아니면 이유.
    // 예전에 설치한 서비스는 서비스 계정에 중지 권한이 없어 거절될 수 있다 — 그때는 도우미가 프로세스를 직접 끝낸다
    public static string? StopService(string name, TimeSpan timeout)
    {
        try
        {
            var (code, output) = Sc("stop", name);
            if (code != 0 && code != 1062) return $"sc stop 코드 {code}: {output.Trim()}";   // 1062: 시작되지 않음
            var until = DateTime.UtcNow + timeout;
            while (StateOf(Sc("query", name).Output) is { } state && state != "STOPPED")
            {
                if (DateTime.UtcNow > until) return "서비스가 멈추지 않습니다 (" + state + ")";
                Thread.Sleep(500);
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // 교체 도우미가 파일을 바꾼 뒤 서비스를 다시 띄운다. 직전 프로세스가 완전히 정리될 때까지 잠깐 거절될 수 있어 다시 시도한다
    public static string? StartService(string name, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        string last = "";
        while (true)
        {
            try
            {
                var (code, output) = Sc("start", name);
                if (code == 0 || code == 1056) return null;   // 1056: 이미 실행 중
                last = $"sc start 코드 {code}: {output.Trim()}";
            }
            catch (Exception ex) { last = ex.Message; }
            if (DateTime.UtcNow > until) return last;
            Thread.Sleep(2000);
        }
    }
}

// 설치·제거 명령이 화면에 쓰는 내용을 App_Data/install.log 에도 덧붙여 남긴다. 창이 닫혀 버려도 무엇이 일어났는지 볼 수 있고,
// 관리자 창이 먼저 닫히면 원래 창이 그 끝부분을 보여 줄 수 있다. 파일을 열지 못하면(쓰기 권한 없음 등) 화면에만 쓴다
public sealed class InstallLog : IDisposable
{
    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "App_Data", ServiceInstaller.InstallLogFile);

    public string Path { get; }
    private readonly StreamWriter _file;
    private readonly TextWriter _console;

    private InstallLog(string path, StreamWriter file, TextWriter console)
    {
        Path = path;
        _file = file;
        _console = console;
    }

    public static InstallLog? Start(string[] args)
    {
        try
        {
            var path = DefaultPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // 관리자 창과 원래 창이 같은 파일에 쓰고 읽으므로 공유로 연다
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            file.WriteLine();
            file.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {string.Join(' ', args)} (PID {Environment.ProcessId})");
            var console = Console.Out;
            Console.SetOut(new Tee(console, file));
            return new InstallLog(path, file, console);
        }
        catch (Exception) { return null; }
    }

    public void Flush()
    {
        try { _file.Flush(); } catch (Exception) { /* 기록 실패는 무시 */ }
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        try { _file.Dispose(); } catch (Exception) { /* 기록 실패는 무시 */ }
    }

    // 파일 끝 몇 줄. 읽지 못하거나 비어 있으면 null
    public static string? Tail(string path, int lines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var all = reader.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
            return all.Length == 0 || all.All(string.IsNullOrWhiteSpace) ? null : string.Join(Environment.NewLine, all.TakeLast(lines));
        }
        catch (Exception) { return null; }
    }

    // 화면과 파일에 함께 쓴다. 파일 쓰기가 실패해도 화면 출력은 막지 않는다
    private sealed class Tee(TextWriter screen, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => screen.Encoding;

        public override void Write(char value)
        {
            screen.Write(value);
            try { file.Write(value); } catch (Exception) { /* 기록 실패는 무시 */ }
        }

        public override void Write(string? value)
        {
            screen.Write(value);
            try { file.Write(value); } catch (Exception) { /* 기록 실패는 무시 */ }
        }

        public override void WriteLine(string? value)
        {
            screen.WriteLine(value);
            try { file.WriteLine(value); } catch (Exception) { /* 기록 실패는 무시 */ }
        }

        public override void Flush()
        {
            screen.Flush();
            try { file.Flush(); } catch (Exception) { /* 기록 실패는 무시 */ }
        }
    }
}
