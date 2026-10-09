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

    public async Task InvokeAsync(HttpContext ctx, AppDb db, CurrentUser me, IClock clock)
    {
        me.Ip = NormalizeIp(ctx.Connection.RemoteIpAddress);

        var addr = await db.UserAddresses.Include(a => a.User).ThenInclude(u => u!.OrgUnit).ThenInclude(o => o!.Parent)
            .FirstOrDefaultAsync(a => a.Ip == me.Ip);
        if (addr?.User != null && addr.User.IsActive)
        {
            me.User = addr.User;
            // 마지막 접속 시각은 하루에 한 번만 갱신 (요청마다 쓰지 않도록)
            if (clock.UtcNow - addr.LastSeenAt > TimeSpan.FromDays(1))
            {
                addr.LastSeenAt = clock.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        var path = ctx.Request.Path;
        var open = path.StartsWithSegments("/Welcome") || path.StartsWithSegments("/health") || path.StartsWithSegments("/version")
            || path.StartsWithSegments("/Error");
        if (!me.IsKnown && !open)
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
                await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "사용자 등록이 필요합니다. 화면을 새로고침해 주세요." });
            }
            return;
        }

        await _next(ctx);
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
    private readonly AppDb _db;
    private readonly IClock _clock;

    public UserService(AppDb db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    // 이름으로 등록. 같은 이름의 사용자가 이미 있으면 이 IP 를 그 사용자에 연결한다(PC 교체·IP 변경 대응)
    public async Task<AppUser> RegisterAsync(string ip, string? name, int? orgUnitId)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new RuleException("이름을 입력해 주세요.", "name");
        if (name.Length > 50) throw new RuleException("이름은 50자 이내로 입력해 주세요.", "name");
        if (orgUnitId != null && !await _db.OrgUnits.AnyAsync(o => o.Id == orgUnitId.Value))
            throw new RuleException("소속을 다시 선택해 주세요.", "orgUnitId");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Name == name);
        if (user != null && !user.IsActive)
            throw new RuleException("사용이 중지된 이름입니다. 관리자에게 문의해 주세요.", "name");

        if (user == null)
        {
            // 관리자가 한 명도 없으면 처음 등록하는 사람이 관리자가 된다
            var hasAdmin = await _db.Users.AnyAsync(u => u.IsAdmin);
            user = new AppUser { Name = name, OrgUnitId = orgUnitId, IsAdmin = !hasAdmin, CreatedAt = _clock.UtcNow };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }
        else if (orgUnitId != null && user.OrgUnitId == null)
        {
            user.OrgUnitId = orgUnitId;
        }

        var addr = await _db.UserAddresses.FirstOrDefaultAsync(a => a.Ip == ip);
        if (addr == null)
            _db.UserAddresses.Add(new UserAddress { Ip = ip, UserId = user.Id, LastSeenAt = _clock.UtcNow });
        else
        {
            addr.UserId = user.Id;
            addr.LastSeenAt = _clock.UtcNow;
        }
        await _db.SaveChangesAsync();
        return user;
    }

    // 담당자 이름 → 사용자. 없으면 주소 없는 사용자로 만든다
    public async Task<AppUser?> ResolveAsync(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        if (name.Length > 50) throw new RuleException("담당자 이름은 50자 이내로 입력해 주세요.", "owner");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Name == name);
        if (user != null) return user;

        user = new AppUser { Name = name, CreatedAt = _clock.UtcNow };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }
}
