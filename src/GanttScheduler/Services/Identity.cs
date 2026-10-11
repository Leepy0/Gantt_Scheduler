using System.Net;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 시각 공급자 — 지연 판정 등 '오늘'은 한국 시간 기준. 테스트에서는 고정 시각으로 바꿔 끼운다
public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}

public class KoreaClock : IClock
{
    private static readonly TimeZoneInfo Kst = FindKst();

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Kst));

    public static DateTime ToKst(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Kst);

    // 한국 날짜의 0시를 UTC 시각으로
    public static DateTime StartOfDayUtc(DateOnly kstDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(kstDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Kst);

    public static DateOnly KstDate(DateTime utc) => DateOnly.FromDateTime(ToKst(utc));

    private static TimeZoneInfo FindKst()
    {
        foreach (var id in new[] { "Asia/Seoul", "Korea Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { /* 다음 후보 시도 */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    }
}

// 요청 단위 현재 사용자 (UserGateMiddleware 가 채운다)
public class CurrentUser
{
    public AppUser? User { get; set; }
    public string Ip { get; set; } = "";
    // 서버 자신(루프백)에서 온 요청인지 — 관리자가 없을 때 첫 관리자를 정하는 데 쓴다
    public bool IsLocal { get; set; }
    // 오래 쓰지 않은 IP 연결이라 '이 사람이 맞는지' 다시 물어야 한다. 확인하기 전에는 등록 화면만 열린다
    public bool NeedsConfirm { get; set; }
    public bool IsKnown => User != null;
    public bool IsAdmin => User?.IsAdmin == true;
    public int Id => User?.Id ?? throw new InvalidOperationException("등록되지 않은 사용자입니다.");
    public string Name => User?.Name ?? "";
}

// 접속 IP → 사용자 매핑. 처음 보는 IP 는 이름 등록 화면으로 보낸다
public class UserGateMiddleware
{
    private readonly RequestDelegate _next;
    public UserGateMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, CurrentUser me, IClock clock, AccessOptions access)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        me.Ip = NormalizeIp(remote);
        me.IsLocal = AccessOptions.IsLocal(remote);

        // 허용 대역 밖에서 온 요청은 이름 등록 화면도 보여 주지 않는다 (사내망의 게스트·협력사 PC 등)
        if (!access.IsAllowed(remote))
        {
            await DenyAsync(ctx, me.Ip);
            return;
        }

        // 열려 있는 화면이 1분마다 부르는 /version 과 상태 확인(/health …)은 사용자를 찾지 않고 통과시킨다.
        // 요청마다 DB 를 읽지 않게 하고, DB 가 멈춰도 상태 확인은 답하게 하기 위해서다
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/version") || path.StartsWithSegments("/health"))
        {
            await _next(ctx);
            return;
        }

        var db = ctx.RequestServices.GetRequiredService<AppDb>();
        var addr = await db.UserAddresses.Include(a => a.User).ThenInclude(u => u!.OrgUnit).ThenInclude(o => o!.Parent)
            .FirstOrDefaultAsync(a => a.Ip == me.Ip);
        if (addr?.User != null && addr.User.IsActive)
        {
            me.User = addr.User;
            var idle = clock.UtcNow - addr.LastSeenAt;
            if (access.ReconfirmDays > 0 && idle > TimeSpan.FromDays(access.ReconfirmDays))
            {
                // DHCP 로 IP 가 다른 사람에게 넘어갔을 수 있다. 확인을 받기 전에는 마지막 접속 시각을 바꾸지 않는다
                me.NeedsConfirm = true;
            }
            else if (idle > TimeSpan.FromDays(1))
            {
                // 마지막 접속 시각은 하루에 한 번만 갱신 (요청마다 쓰지 않도록)
                addr.LastSeenAt = clock.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        var open = path.StartsWithSegments("/Welcome") || path.StartsWithSegments("/Error");
        if ((!me.IsKnown || me.NeedsConfirm) && !open)
        {
            var wantsJson = ctx.Request.Headers.Accept.ToString().Contains("application/json");
            if (HttpMethods.IsGet(ctx.Request.Method) && !wantsJson)
            {
                var returnUrl = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
                ctx.Response.Redirect(ctx.Request.PathBase + "/Welcome?returnUrl=" + Uri.EscapeDataString(returnUrl));
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = me.NeedsConfirm
                        ? "이 기기의 사용자를 다시 확인해야 합니다. 화면을 새로고침해 주세요."
                        : "사용자 등록이 필요합니다. 화면을 새로고침해 주세요.",
                });
            }
            return;
        }

        await _next(ctx);
    }

    private static async Task DenyAsync(HttpContext ctx, string ip)
    {
        var message = $"이 주소({ip})에서는 과제 스케줄러에 접속할 수 없습니다. 사내망에서 접속하거나 관리자에게 문의해 주세요.";
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (ctx.Request.Headers.Accept.ToString().Contains("application/json"))
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = message });
        else
        {
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(message);
        }
    }

    public static string NormalizeIp(IPAddress? ip)
    {
        if (ip == null) return "local";
        if (IPAddress.IsLoopback(ip)) return "127.0.0.1";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}

public class UserService
{
    public const string AdminLinkRefused = "관리자 이름에는 이 화면에서 PC 를 연결할 수 없습니다. 다른 관리자에게 IP 추가를 요청하세요.";
    public const string AdminConfirmRefused = "관리자 계정은 오래 쓰지 않은 기기를 이 화면에서 다시 확인할 수 없습니다. 다른 관리자에게 IP 연결을 요청하거나 서버에서 admin 명령을 실행하세요.";

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly AccessOptions _access;
    // 이 요청에서 새로 만든(아직 저장 전) 담당자. 같은 이름을 두 번 만들지 않게 한다
    private readonly Dictionary<string, AppUser> _pending = new(StringComparer.OrdinalIgnoreCase);

    public UserService(AppDb db, IClock clock, AccessOptions? access = null)
    {
        _db = db;
        _clock = clock;
        _access = access ?? new AccessOptions();
    }

    // 이름으로 등록. 같은 이름의 사용자가 이미 있으면 이 IP 를 그 사용자에 연결한다(PC 교체·IP 변경 대응).
    // local: 서버 자신(루프백)에서 온 요청 — 관리자가 없을 때 첫 관리자가 될 수 있다
    // leaveAdmin: 관리자에 연결된 이 기기를 다른 이름으로 옮기는 것을 확인했다는 표시 (LeaveAdminWarningAsync)
    public async Task<AppUser> RegisterAsync(string ip, string? name, int? orgUnitId, bool local = false, bool leaveAdmin = false)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new RuleException("이름을 입력해 주세요.", "name");
        if (name.Length > 50) throw new RuleException("이름은 50자 이내로 입력해 주세요.", "name");
        if (orgUnitId != null && !await _db.OrgUnits.AnyAsync(o => o.Id == orgUnitId.Value))
            throw new RuleException("소속을 다시 선택해 주세요.", "orgUnitId");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Name == name);
        if (user != null && !user.IsActive)
            throw new RuleException("사용이 중지된 이름입니다. 관리자에게 문의해 주세요.", "name");

        var addr = await _db.UserAddresses.FirstOrDefaultAsync(a => a.Ip == ip);
        // 관리자 이름은 이미 그 사람에 연결된 IP 에서만 받는다. 이름만 알면 관리자 권한(업데이트 파일 올리기 → 서버에서 실행)을
        // 얻는 길을 막기 위해서다. PC 를 바꾼 관리자는 다른 관리자가 관리 > 사용자에서 IP 를 추가하거나, 서버에서 admin 명령으로 연결한다
        if (user is { IsAdmin: true } && addr?.UserId != user.Id)
            throw new RuleException(AdminLinkRefused, "name");
        // 관리자에 연결된 기기를 다른 이름으로 옮기면 위 규칙 때문에 그 관리자는 이 기기에서 돌아오지 못한다. 실수로 잠기지 않게 확인을 받는다
        // (막지는 않는다 — IP 를 넘겨받은 사람은 자기 이름으로 바꿀 수 있어야 한다)
        if (addr != null && addr.UserId != user?.Id && !leaveAdmin && await LeaveAdminWarningAsync(ip) is { } warning)
            throw new RuleException(warning + " 그래도 바꾸려면 확인란을 선택해 주세요.", "leaveAdmin");

        // 관리자가 한 명도 없으면 첫 관리자를 정한다. 기본은 서버 자신에서 등록한 사람만(설치 직후 아무 PC 나 선점하지 못하게)
        var firstAdmin = !await _db.Users.AnyAsync(u => u.IsAdmin) && (local || !_access.FirstAdminLocalOnly);

        if (user == null)
        {
            user = new AppUser { Name = name, OrgUnitId = orgUnitId, IsAdmin = firstAdmin, CreatedAt = _clock.UtcNow };
            _db.Users.Add(user);
        }
        else
        {
            if (orgUnitId != null && user.OrgUnitId == null) user.OrgUnitId = orgUnitId;
            if (firstAdmin) user.IsAdmin = true;
        }

        // 새 사용자는 아직 Id 가 없으므로 내비게이션으로 연결해 한 번에 저장한다
        if (addr == null)
            _db.UserAddresses.Add(new UserAddress { Ip = ip, User = user, LastSeenAt = _clock.UtcNow });
        else
        {
            if (user.Id != 0) addr.UserId = user.Id;
            else addr.User = user;
            addr.LastSeenAt = _clock.UtcNow;
        }
        await _db.SaveChangesAsync();
        return user;
    }

    // 이 IP 가 사용 중인 관리자에 연결돼 있으면, 다른 이름으로 옮겼을 때 그 관리자가 되돌아오는 방법을 알리는 문구. 아니면 null
    public async Task<string?> LeaveAdminWarningAsync(string ip)
    {
        var addr = await _db.UserAddresses.Include(a => a.User).FirstOrDefaultAsync(a => a.Ip == ip);
        if (addr?.User is not { IsAdmin: true, IsActive: true } admin) return null;
        var otherAdmin = await _db.Users.AnyAsync(u => u.IsAdmin && u.IsActive && u.Id != admin.Id);
        var otherDevice = await _db.UserAddresses.AnyAsync(a => a.UserId == admin.Id && a.Ip != ip);
        var back = otherAdmin ? "되돌리려면 다른 관리자가 관리 > 사용자에서 이 IP 를 다시 연결해야 합니다."
            : otherDevice ? $"되돌리려면 {admin.Name} 님이 연결된 다른 기기에서 관리 > 사용자로 이 IP 를 다시 연결해야 합니다."
            : "관리자가 혼자이고 연결된 다른 기기도 없어 서버 콘솔의 admin 명령으로만 되돌릴 수 있습니다.";
        return $"이 기기는 관리자 {admin.Name} 님에 연결돼 있습니다. 다른 이름으로 바꾸면 {admin.Name} 님은 이 기기에서 다시 관리자로 돌아올 수 없습니다. {back}";
    }

    // 'OOO 님이 맞습니까?'에 맞다고 답한 경우 — 이 IP 연결을 다시 쓰는 것으로 기록한다.
    // 관리자 계정은 이 한 번의 확인으로 되살리지 않는다: DHCP 로 이 IP 를 넘겨받은 사람이 '맞습니다'만 누르면
    // 관리자 권한(업데이트 파일 올리기 → 서버에서 실행)을 얻게 되기 때문이다. 서버 자신(루프백)에서 온 확인만 받는다
    public async Task ConfirmAsync(string ip, bool local = false)
    {
        var addr = await _db.UserAddresses.Include(a => a.User).FirstOrDefaultAsync(a => a.Ip == ip);
        if (addr == null) return;
        if (addr.User is { IsAdmin: true } && !local) throw new RuleException(AdminConfirmRefused, "confirm");
        addr.LastSeenAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
    }

    // 관리자가 아직 없는지 (관리 화면 안내용)
    public Task<bool> HasAdminAsync() => _db.Users.AnyAsync(u => u.IsAdmin);

    // 담당자 이름 → 사용자. 없으면 주소 없는 사용자로 만든다.
    // 새 사용자는 Add 만 하고 저장하지 않는다 — 호출한 쪽이 내비게이션으로 연결해 마지막 SaveChanges 에서 함께 저장하므로,
    // 뒤의 검증(Version·사유·선행)에 실패하면 유령 사용자가 남지 않는다.
    // keepId: 지금 담당인 사용자. 사용이 중지됐어도 그대로 두는 것은 막지 않는다(다른 항목을 고칠 수 있게)
    public async Task<AppUser?> ResolveAsync(string? name, string field = "owner", int? keepId = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        if (name.Length > 50) throw new RuleException("담당자 이름은 50자 이내로 입력해 주세요.", field);
        if (_pending.TryGetValue(name, out var pending)) return pending;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Name == name);
        if (user != null)
        {
            if (!user.IsActive && user.Id != keepId)
                throw new RuleException($"'{name}' 은(는) 사용이 중지된 사용자입니다. 다른 담당자를 입력해 주세요.", field);
            return user;
        }

        user = new AppUser { Name = name, CreatedAt = _clock.UtcNow };
        _db.Users.Add(user);
        _pending[name] = user;
        return user;
    }
}
