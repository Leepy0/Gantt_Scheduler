using System.Text;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 관리자 복구 명령:  GanttScheduler.exe admin <이름> [--ip <주소>] [--contentRoot <폴더>] [--environment <이름>]
// 화면에서는 관리자 이름에 새 PC(IP)를 연결할 수 없으므로(이름 사칭으로 관리자 권한을 얻지 못하게),
// 혼자인 관리자가 PC 를 바꿨거나 관리자가 아무도 없을 때 서버 콘솔에서 되살리는 길이다.
// 웹 서버와 같은 설정(appsettings + 환경 변수)으로 DB 를 연다. 마이그레이션은 하지 않는다 — 구조가 맞지 않으면 안내하고 끝낸다
public static class AdminCommand
{
    public const string Name = "admin";

    public const string Usage =
        "사용법: GanttScheduler.exe admin <이름> [--ip <주소>]\n" +
        "  <이름>        관리자로 지정할 사용자 이름 (없으면 새로 만든다)\n" +
        "  --ip <주소>   이 IP 를 그 사용자에 연결한다 (다른 사용자에 연결돼 있으면 옮긴다)\n" +
        "  --contentRoot <폴더>  설정 파일·App_Data 가 있는 폴더 (기본: 지금 폴더에 appsettings.json 이 있으면 그 폴더, 없으면 프로그램 폴더)\n" +
        "  --environment <이름>  설정 환경 (기본: ASPNETCORE_ENVIRONMENT, 없으면 Production)";

    public sealed record Options(string UserName, string? Ip, string? ContentRoot, string? Environment);

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == Name;

    // 인자 해석. 잘못됐으면 error 에 이유를 담고 false
    public static bool TryParse(string[] args, out Options? options, out string? error)
    {
        options = null;
        error = null;
        string? name = null, ip = null, root = null, env = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) { error = $"{a} 다음에 값을 적어 주세요."; return false; }
                var value = args[++i];
                switch (a.ToLowerInvariant())
                {
                    case "--ip": ip = value; break;
                    case "--contentroot": root = value; break;
                    case "--environment": env = value; break;
                    default: error = $"알 수 없는 옵션입니다: {a}"; return false;
                }
            }
            else if (name == null) name = a;
            else { error = $"이름은 하나만 적어 주세요. 띄어쓰기가 있는 이름은 따옴표로 감쌉니다: \"{name} {a}\""; return false; }
        }

        name = (name ?? "").Trim();
        if (name.Length == 0) { error = "관리자로 지정할 이름을 적어 주세요."; return false; }
        if (name.Length > 50) { error = "이름은 50자 이내로 적어 주세요."; return false; }

        if (ip != null)
        {
            if (!AccessOptions.TryParseIp(ip, out var parsed)) { error = $"IP 주소 형식이 아닙니다: {ip}"; return false; }
            // 접속할 때와 같은 모양으로 저장해야 찾을 수 있다 (루프백은 127.0.0.1, IPv4 매핑 주소는 IPv4)
            ip = UserGateMiddleware.NormalizeIp(parsed);
        }
        options = new Options(name, ip, root, env);
        return true;
    }

    public sealed record Result(bool Created, bool Reactivated, bool WasAdmin, string? MovedFrom, bool IpAdded);

    // DB 반영: 사용자를 관리자로(없으면 만들고, 사용 중지였으면 다시 사용), IP 를 주면 그 사용자에 연결한다
    public static async Task<Result> ApplyAsync(AppDb db, IClock clock, string userName, string? ip)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Name == userName);
        var created = user == null;
        var wasAdmin = user?.IsAdmin == true;
        var reactivated = user is { IsActive: false };
        if (user == null)
        {
            user = new AppUser { Name = userName, CreatedAt = clock.UtcNow };
            db.Users.Add(user);
        }
        user.IsAdmin = true;
        user.IsActive = true;

        string? movedFrom = null;
        var ipAdded = false;
        if (ip != null)
        {
            var addr = await db.UserAddresses.Include(a => a.User).FirstOrDefaultAsync(a => a.Ip == ip);
            if (addr == null)
            {
                db.UserAddresses.Add(new UserAddress { Ip = ip, User = user, LastSeenAt = clock.UtcNow });
                ipAdded = true;
            }
            else
            {
                if (addr.UserId != user.Id || created)
                {
                    movedFrom = addr.User?.Name;
                    ipAdded = true;
                }
                if (user.Id != 0) addr.UserId = user.Id;
                addr.User = user;
                addr.LastSeenAt = clock.UtcNow;
            }
        }
        // 서버 콘솔의 복구도 관리 변경 이력에 남긴다(행위자: 서버) — 관리자 권한이 어디서 생겼는지 화면에서 찾을 수 있게
        var log = new MasterLog(db, clock);
        var parts = new List<string>();
        if (created) parts.Add("새 사용자");
        if (!wasAdmin) parts.Add("관리자 지정");
        if (reactivated) parts.Add("다시 사용");
        if (ipAdded) parts.Add($"IP 연결 {ip}" + (movedFrom != null && movedFrom != userName ? $"({movedFrom} 님에게서 옮김)" : ""));
        if (parts.Count > 0)
            log.Add(ipAdded && wasAdmin && !created && !reactivated ? "ip" : "user", () => user.Id,
                $"admin 명령(서버 콘솔): {userName} — {string.Join(", ", parts)}", wasAdmin ? "관리자" : created ? null : "일반", "관리자");
        await db.SaveChangesAsync();
        return new Result(created, reactivated, wasAdmin, movedFrom, ipAdded);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { /* 콘솔이 없는 경우 */ }
        if (!TryParse(args, out var opt, out var error))
        {
            Console.WriteLine(error);
            Console.WriteLine();
            Console.WriteLine(Usage);
            return 2;
        }

        try
        {
            var root = ContentRoot(opt!.ContentRoot);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = opt.Environment,
            });
            var config = builder.Configuration;
            var sqlServer = DbSetup.UseSqlServer(config);

            Console.WriteLine("과제 스케줄러 관리자 지정");
            Console.WriteLine($"  폴더   {root}");
            Console.WriteLine($"  환경   {builder.Environment.EnvironmentName}");
            if (!sqlServer)
            {
                var path = AppPaths.Sqlite(config, root);
                Console.WriteLine($"  DB     SQLite {path}");
                // 없는 DB 를 여기서 새로 만들지 않는다 (만들어 봐야 서버가 쓰는 DB 가 아니다)
                if (!File.Exists(path))
                {
                    Console.WriteLine();
                    Console.WriteLine("DB 파일이 없습니다. 서버를 한 번 시작해 DB 를 만든 뒤 다시 실행하거나, --contentRoot 로 서버 폴더를 지정해 주세요.");
                    return 1;
                }
            }
            else Console.WriteLine("  DB     SQL Server");
            Console.WriteLine();

            var services = new ServiceCollection();
            services.AddAppDb(config, root);
            await using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            var problem = await SchemaProblemAsync(db);
            if (problem != null)
            {
                Console.WriteLine(problem);
                return 1;
            }

            var r = await ApplyAsync(db, new KoreaClock(), opt.UserName, opt.Ip);
            Console.WriteLine(r.Created ? $"'{opt.UserName}' 사용자를 새로 만들어 관리자로 지정했습니다."
                : r.WasAdmin ? $"'{opt.UserName}' 님은 이미 관리자입니다."
                : $"'{opt.UserName}' 님을 관리자로 지정했습니다.");
            if (r.Reactivated) Console.WriteLine("사용 중지 상태였던 사용자를 다시 사용하게 했습니다.");
            if (opt.Ip != null)
            {
                Console.WriteLine(!r.IpAdded ? $"IP {opt.Ip} 은(는) 이미 이 사용자에 연결돼 있습니다."
                    : r.MovedFrom != null ? $"IP {opt.Ip} 을(를) '{r.MovedFrom}' 님에게서 옮겨 연결했습니다."
                    : $"IP {opt.Ip} 을(를) 연결했습니다.");
            }
            else Console.WriteLine("그 PC 에서 접속하려면 --ip 로 IP 를 연결하거나, 관리 > 사용자에서 다른 관리자가 IP 를 추가합니다.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("실패: " + ex.Message);
            Console.WriteLine("DB 연결 문자열과, 이 명령을 실행한 Windows 계정의 DB 권한을 확인해 주세요(서비스 계정이 아니라 지금 로그인한 계정으로 접속합니다).");
            return 1;
        }
    }

    // 설정 파일이 있는 폴더. 서버 폴더에서 실행하면 그 폴더, 다른 곳에서 전체 경로로 실행하면 프로그램 폴더
    private static string ContentRoot(string? given)
    {
        if (!string.IsNullOrWhiteSpace(given)) return Path.GetFullPath(given);
        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "appsettings.json"))) return cwd;
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // DB 구조가 이 프로그램과 맞는지(SQL Server·SQLite 모두 마이그레이션 이력으로 본다). 맞지 않으면 안내 문구, 맞으면 null
    private static async Task<string?> SchemaProblemAsync(AppDb db)
    {
        if (!await db.Database.CanConnectAsync())
            return "DB 에 연결하지 못했습니다. 연결 문자열과, 이 명령을 실행한 Windows 계정의 DB 권한을 확인해 주세요.";

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
            return $"DB 구조가 이 프로그램 버전보다 오래되었습니다(적용되지 않은 변경 {pending.Count}개). 서비스를 한 번 시작해 DB 를 올린 뒤 다시 실행해 주세요.";
        var known = db.Database.GetMigrations().ToHashSet();
        if ((await db.Database.GetAppliedMigrationsAsync()).Any(m => !known.Contains(m)))
            return "DB 가 이 프로그램보다 새 버전입니다. 서버에 설치된 프로그램(서비스 폴더의 GanttScheduler.exe)으로 실행해 주세요.";
        return null;
    }
}
