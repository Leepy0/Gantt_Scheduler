using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
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

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "install" or "uninstall";

    // 서비스로 시작될 때 SCM 이 넘기는 인자(--service <이름>)에서 서비스 이름을 읽는다. 없으면 서비스가 아니다
    public static string? ServiceNameFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--service");
        return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
    }

    // 서비스가 실행할 명령. 포트를 여기에 두므로 설정 파일을 고치지 않는다 (바꾸려면 install 을 다시 실행)
    public static string BinPath(string exe, string name, int port) => $"\"{exe}\" --service {name} --urls http://*:{port}";

    // 서비스 보안 설명자(SDDL)에 "이 계정은 서비스를 시작·조회할 수 있다"를 더한다.
    // 교체 도우미는 서비스 계정으로 돌기 때문에, 이 권한이 없으면 파일을 바꾼 뒤 서비스를 다시 띄우지 못한다
    public static string AddStartRight(string sddl, string sid)
    {
        sddl = sddl.Trim();
        var ace = $"(A;;RPLC;;;{sid})";
        if (sddl.Contains(ace, StringComparison.OrdinalIgnoreCase)) return sddl;
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

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { /* 콘솔이 없는 경우 */ }
        var pause = args.Contains("--pause");
        int code;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Windows 에서만 서비스로 등록할 수 있습니다.");
                return 1;
            }
            if (!IsAdmin()) return Elevate(args);
            code = args[0] == "install" ? Install(args) : Uninstall(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("실패: " + ex.Message);
            code = 1;
        }
        if (pause)
        {
            Console.WriteLine();
            Console.WriteLine("Enter 를 누르면 닫습니다.");
            Console.ReadLine();
        }
        return code;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // 관리자 권한이 아니면 권한 상승 창(UAC)을 띄워 같은 명령을 다시 실행한다. 새 창이 바로 닫히지 않게 --pause 를 붙인다
    private static int Elevate(string[] args)
    {
        Console.WriteLine("관리자 권한이 필요합니다. 권한 확인 창에서 '예'를 눌러 주세요.");
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!args.Contains("--pause")) psi.ArgumentList.Add("--pause");
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception)
        {
            Console.WriteLine("권한 확인이 취소되어 아무것도 바꾸지 않았습니다.");
            return 1;
        }
    }

    private static string Opt(string[] args, string key, string fallback)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static string CheckName(string name) =>
        Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_.-]{0,60}$") ? name
            : throw new ArgumentException("서비스 이름은 영문자로 시작하는 영문·숫자여야 합니다: " + name);

    private static int Install(string[] args)
    {
        var name = CheckName(Opt(args, "--name", DefaultName));
        if (!int.TryParse(Opt(args, "--port", DefaultPort.ToString()), out var port) || port is < 1 or > 65535)
            throw new ArgumentException("--port 에는 1~65535 사이의 숫자를 지정해 주세요.");
        var system = args.Contains("--system");
        var remote = RemoteIp(Opt(args, "--remoteip", ""));
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
        if (remote != null)
            Console.WriteLine("앱에서도 같은 대역만 받으려면 appsettings.Production.json 의 Access:AllowedNetworks 에 같은 값을 적습니다.");
        return 0;
    }

    private static int Uninstall(string[] args)
    {
        var name = CheckName(Opt(args, "--name", DefaultName));
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
        while (StateOf(Sc("query", name).Output) is { } state && state != "STOPPED")
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("서비스가 멈추지 않습니다. 작업 관리자에서 GanttScheduler.exe 를 끝낸 뒤 다시 실행해 주세요.");
            Thread.Sleep(500);
        }
    }

    // 서비스가 떠서 요청에 답할 때까지 기다린다. 도중에 멈추면(시작 실패) 바로 그만둔다
    private static bool WaitHealthy(string name, int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var began = DateTime.UtcNow;
        while (DateTime.UtcNow - began < timeout)
        {
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
