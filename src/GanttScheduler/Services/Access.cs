using System.Net;
using System.Text.RegularExpressions;

namespace GanttScheduler.Services;

// 접속 제어 설정 (appsettings 의 "Access" 절).
// 로그인이 없는 설계(IP 로 식별)를 유지하면서, 이름 입력만으로 서버 권한을 얻는 길과 사내망 밖 접속만 막는다
public sealed class AccessOptions
{
    // 관리자가 한 명도 없을 때, 서버 자신(루프백)에서 등록한 사람만 관리자가 된다.
    // 설치 직후 사내망의 아무 PC 나 먼저 등록해 관리자를 선점하지 못하게 한다
    public bool FirstAdminLocalOnly { get; init; } = true;

    // IP 연결을 이 일수보다 오래 쓰지 않았으면 'OOO 님이 맞습니까?'를 다시 묻는다(DHCP 로 IP 가 다른 사람에게 넘어간 경우 대비). 0 이면 묻지 않는다
    public int ReconfirmDays { get; init; } = 30;

    // IP 연결 보존 기간(일). 이 기간 넘게 접속이 없던 연결은 하루 한 번 지운다(AddressRetentionWorker) — 퇴사자·반납 PC 의 IP 가
    // 다음 사람에게 넘어가 엉뚱한 이름으로 기록되는 것을 줄인다. 0 이하면 정리하지 않는다
    public int AddressKeepDays { get; init; } = 365;

    // 접속을 받을 대역. 비어 있으면 모두 받는다. 루프백(서버 자신)은 항상 받는다
    public IReadOnlyList<IPNetwork> AllowedNetworks { get; init; } = Array.Empty<IPNetwork>();

    public static AccessOptions From(IConfiguration cfg)
    {
        var section = cfg.GetSection("Access");
        // 목록은 JSON 배열로도, 환경 변수 한 줄(쉼표 구분)로도 받는다. 두 곳에 다 있으면 합친다
        // (한쪽의 빈 값이 다른 쪽 목록을 가려 '모두 허용'이 되지 않게)
        var raw = section.GetSection("AllowedNetworks");
        var items = raw.GetChildren().Select(c => c.Value ?? "").Append(raw.Value ?? "");
        return new AccessOptions
        {
            FirstAdminLocalOnly = section.GetValue("FirstAdminLocalOnly", true),
            ReconfirmDays = Math.Max(0, section.GetValue("ReconfirmDays", 30)),
            AddressKeepDays = section.GetValue("AddressKeepDays", 365),
            AllowedNetworks = ParseNetworks(items),
        };
    }

    // "10.0.0.0/8, 192.168.1.10" 같은 목록을 대역으로 바꾼다. 주소 하나는 그 주소만 허용한다.
    // 잘못 쓴 값을 건너뛰면 목록이 비어 '모두 허용'이 될 수 있으므로 시작 단계에서 오류로 알린다
    public static List<IPNetwork> ParseNetworks(IEnumerable<string> items)
    {
        var list = new List<IPNetwork>();
        foreach (var token in items.SelectMany(s => s.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)))
        {
            if (TryParseNetwork(token, out var net)) list.Add(net);
            else if (token.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase))
                // 설치 명령의 --remoteip 는 방화벽 이름 LocalSubnet 을 받지만 앱은 서버의 서브넷을 알지 못한다
                throw new InvalidOperationException("Access:AllowedNetworks 에는 LocalSubnet 을 적을 수 없습니다(방화벽 --remoteip 전용). 서버가 속한 대역을 10.1.0.0/16 처럼 CIDR 로 적어 주세요.");
            else throw new InvalidOperationException($"Access:AllowedNetworks 의 '{token}' 을(를) 해석할 수 없습니다. 10.0.0.0/8 처럼 CIDR 이나 주소 하나로 적어 주세요(앞자리 0 없는 십진수).");
        }
        return list;
    }

    // IPv4 는 십진수 네 마디만 받는다. IPAddress.TryParse 는 "10" 이나 "10.1" 도 받아 엉뚱한 주소(0.0.0.10)가 되고,
    // 앞자리 0 은 8진수(192.168.001.010 → 192.168.1.8), 0x 는 16진수로 읽는다. 자산 대장에 적힌 모양 그대로 넣었다가
    // 다른 사람의 PC 가 관리자로 연결되거나 방화벽에 엉뚱한 대역이 열리지 않게 한다
    private static readonly Regex DecimalV4 = new(@"^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$", RegexOptions.CultureInvariant);

    // IP 주소 하나 (IPv4 는 위 형식, IPv6 는 표준 표기. IPv6 안에 적은 IPv4 부분도 같은 규칙)
    public static bool TryParseIp(string? text, out IPAddress ip)
    {
        ip = IPAddress.None;
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 45) return false;
        if (text.Contains(':'))
        {
            var tail = text[(text.LastIndexOf(':') + 1)..];
            if (tail.Contains('.') && !DecimalV4.IsMatch(tail)) return false;
        }
        else if (!DecimalV4.IsMatch(text)) return false;
        if (!IPAddress.TryParse(text, out var parsed)) return false;
        ip = parsed;
        return true;
    }

    // CIDR(10.0.0.0/8) 또는 주소 하나. CIDR 의 주소 부분도 TryParseIp 와 같은 형식만 받는다
    public static bool TryParseNetwork(string token, out IPNetwork network)
    {
        network = default;
        token = (token ?? "").Trim();
        var slash = token.IndexOf('/');
        if (slash >= 0)
        {
            var prefix = token[(slash + 1)..];
            if (!TryParseIp(token[..slash], out _) || prefix.Length is 0 or > 3 || !prefix.All(char.IsAsciiDigit)) return false;
            return IPNetwork.TryParse(token, out network);
        }
        if (TryParseIp(token, out var ip))
        {
            ip = Unmap(ip);
            network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        network = default;
        return false;
    }

    // 접속 주소가 허용 대역 안인지. 주소를 모르면(테스트 서버·유닉스 소켓) 같은 기기로 본다
    public bool IsAllowed(IPAddress? ip)
    {
        if (AllowedNetworks.Count == 0 || IsLocal(ip)) return true;
        var addr = Unmap(ip!);
        return AllowedNetworks.Any(n => n.Contains(addr));
    }

    // 서버 자신에서 온 요청인지. 같은 서버의 리버스 프록시를 TrustForwardedHeaders 없이 쓰면 모든 요청이 여기에 해당하므로 문서에 적어 둔다
    public static bool IsLocal(IPAddress? ip) => ip == null || IPAddress.IsLoopback(ip);

    // 이중 스택(IPv6) 소켓으로 들어온 IPv4 주소(::ffff:10.1.2.3)를 IPv4 로 바꾼다
    public static IPAddress Unmap(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

// 모든 응답에 붙이는 보안 헤더. 첨부 미리보기는 같은 출처의 iframe 이므로 SAMEORIGIN 이면 계속 된다
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        // 오류 처리기가 응답을 지우고(헤더 포함) 다시 그려도 남도록, 보내기 직전에 붙인다
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.TryAdd("X-Content-Type-Options", "nosniff");
            h.TryAdd("X-Frame-Options", "SAMEORIGIN");
            h.TryAdd("Referrer-Policy", "same-origin");
            return Task.CompletedTask;
        });
        await next();
    });
}
