using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 사내 SMTP 릴레이 (Notify:Smtp:*). 계정이 없으면 익명으로 보낸다(사내 릴레이는 대개 IP 로 허용한다)
public sealed record SmtpOptions(string? Host, int Port, string? From, bool EnableSsl, string? User, string? Password)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

// 메일 요약 설정 (Notify:*). Enabled 가 꺼져 있으면(기본) 알림은 화면의 종에서만 본다
public sealed record NotifyOptions(bool Enabled, int DailyHour, string? BaseUrl, SmtpOptions Smtp)
{
    // 정한 시각부터 이 시간 안에만 보낸다 — 오후에 다시 시작한 서버가 그날 요약을 늦게 보내지 않게(다음 근무일에 모아 보낸다)
    public const int CatchUpHours = 4;

    public static NotifyOptions From(IConfiguration cfg)
    {
        var s = cfg.GetSection("Notify:Smtp");
        return new NotifyOptions(
            cfg.GetValue("Notify:Enabled", false),
            Math.Clamp(cfg.GetValue("Notify:DailyHour", 8), 0, 23),
            string.IsNullOrWhiteSpace(cfg["Notify:BaseUrl"]) ? null : cfg["Notify:BaseUrl"]!.Trim().TrimEnd('/') + "/",
            new SmtpOptions(s["Host"]?.Trim(), s.GetValue("Port", 25), s["From"]?.Trim(), s.GetValue("EnableSsl", false), s["User"], s["Password"]));
    }

    // 메일 주소 확인 (관리 화면 입력). 올바르면 정리한 주소, 아니면 null
    public static string? NormalizeEmail(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > Limits.Email) return null;
        return MailAddress.TryCreate(text, out var addr) && addr.Address == text && text.Contains('.', StringComparison.Ordinal) ? addr.Address : null;
    }
}

// 보낼 메일 한 통 (본문은 글자만 — 사내 메일 프로그램마다 HTML 표시가 달라 글자로 둔다)
public sealed record MailItem(string To, string Subject, string Body);

// 알림을 내보내는 통로. 지금은 사내 SMTP 하나다(메신저 웹훅은 만들지 않는다). 시험에서는 가짜 통로로 바꿔 끼운다
public interface INotifyChannel
{
    bool Ready { get; }
    Task SendAsync(MailItem mail, CancellationToken ct);
}

// System.Net.Mail 로 보낸다 — 기본 라이브러리라 추가 패키지가 없고 섀도 빌드에서도 컴파일된다
public sealed class SmtpChannel : INotifyChannel
{
    private readonly NotifyOptions _o;

    public SmtpChannel(NotifyOptions o) => _o = o;

    public bool Ready => _o.Smtp.Configured;

    public async Task SendAsync(MailItem mail, CancellationToken ct)
    {
        if (!Ready) throw new RuleException("메일 서버(Notify:Smtp:Host·From)가 설정되지 않았습니다.");
        using var client = new SmtpClient(_o.Smtp.Host, _o.Smtp.Port)
        {
            EnableSsl = _o.Smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        if (!string.IsNullOrEmpty(_o.Smtp.User)) client.Credentials = new NetworkCredential(_o.Smtp.User, _o.Smtp.Password);
        using var msg = new MailMessage(_o.Smtp.From!, mail.To, mail.Subject, mail.Body)
        {
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        await client.SendMailAsync(msg, ct);
    }
}

// 마지막 요약 결과 (App_Data/notify.json). 하루 한 번만 보내기 위해 날짜를 남기고, 서버 상태 화면이 결과를 보인다. 항목은 추가만 한다
public sealed record DigestRun(DateOnly Date, DateTime At, int Users, int Sent, int Failed, int Notifications, string? Error);

public sealed class NotifyState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private DigestRun? _last;
    private bool _loaded;

    public NotifyState(string path) => Path = path;

    public string Path { get; }

    public DigestRun? Last
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _last = Load(Path);
                }
                return _last;
            }
        }
    }

    public static DigestRun? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DigestRun>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception)
        {
            return null;    // 깨진 파일은 없는 것으로 본다(다음 요약이 새로 쓴다)
        }
    }

    public void Save(DigestRun run)
    {
        lock (_gate)
        {
            _last = run;
            _loaded = true;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(run, Json));
            }
            catch (Exception)
            {
                // 기록을 못 남겨도 메모리의 날짜로 오늘은 다시 보내지 않는다(다시 시작하면 한 번 더 갈 수 있다)
            }
        }
    }
}

// 사용자별 하루 요약 메일: 읽지 않았고 아직 메일로 보내지 않은 알림 + 내 작업의 지연·임박(Reporting.My — 내 할 일 화면과 같은 판정).
// 행위자가 없는 배경 작업이다 — 활동을 남기지 않고, 지금 사용자(CurrentUser)를 쓰지 않는다
public class DigestService
{
    public const int MaxNotifications = 30;

    private readonly AppDb _db;
    private readonly ProjectService _projects;
    private readonly IClock _clock;
    private readonly INotifyChannel _channel;
    private readonly NotifyOptions _o;
    private readonly ILogger<DigestService> _log;

    public DigestService(AppDb db, ProjectService projects, IClock clock, INotifyChannel channel, NotifyOptions options, ILogger<DigestService> log)
    {
        _db = db;
        _projects = projects;
        _clock = clock;
        _channel = channel;
        _o = options;
        _log = log;
    }

    public async Task<DigestRun> RunAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var today = _clock.Today;
        var users = (await _db.Users.AsNoTracking().Where(u => u.IsActive && u.Email != null)
                .Select(u => new { u.Id, u.Name, u.Email }).ToListAsync(ct))
            .Where(u => NotifyOptions.NormalizeEmail(u.Email) != null).OrderBy(u => u.Id).ToList();
        if (users.Count == 0 || !_channel.Ready) return new DigestRun(today, now, users.Count, 0, 0, 0, users.Count == 0 ? null : "메일 서버가 설정되지 않았습니다.");

        var projects = await _projects.AllAsync(q => q.Where(p => p.State == ProjectState.Active));
        var titles = new Dictionary<int, string?>();
        int sent = 0, failed = 0, notes = 0;
        string? error = null;
        foreach (var u in users)
        {
            ct.ThrowIfCancellationRequested();
            var pending = await _db.Notifications.Include(n => n.Activity).ThenInclude(a => a!.User)
                .Where(n => n.UserId == u.Id && n.ReadAt == null && n.EmailedAt == null)
                // 휴지통에 있는 과제의 알림은 싣지 않는다(화면 알림 목록과 같은 기준 — NotificationService.Live)
                .Where(n => _db.Projects.Any(p => p.Id == n.ProjectId && p.DeletedAt == null))
                .OrderByDescending(n => n.Id).Take(MaxNotifications).ToListAsync(ct);
            var my = Reporting.My(projects, u.Id, today);
            var soon = my.Soon.Where(w => w.Soon).ToList();
            if (pending.Count == 0 && my.Late.Count == 0 && soon.Count == 0) continue;
            foreach (var pid in pending.Select(n => n.ProjectId).Distinct().Where(id => !titles.ContainsKey(id)))
                titles[pid] = await _db.LiveProjects.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Title).FirstOrDefaultAsync(ct);

            var mail = Compose(u.Name, u.Email!, today, pending, titles, my.Late, soon);
            try
            {
                await _channel.SendAsync(mail, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 한 사람이 실패해도 다른 사람은 보낸다. 보내지 못한 알림은 EmailedAt 이 비어 다음 요약에 다시 실린다
                failed++;
                error = ex.Message;
                _log.LogWarning(ex, "메일 요약을 보내지 못했습니다: {User}", u.Name);
                continue;
            }
            // 메일은 이미 나갔으므로 보낸 것으로 센다. 보낸 표시(EmailedAt)를 저장하지 못하면 다음 요약에 같은 알림이 한 번 더 실린다 —
            // 알림을 잃는 것보다 낫다. 실패한 변경이 다음 사람의 저장에 섞여 같이 실패하지 않게 추적을 비운다
            sent++;
            notes += pending.Count;
            foreach (var n in pending) n.EmailedAt = now;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                error = "메일은 보냈지만 보낸 표시를 남기지 못했습니다(다음 요약에 다시 실릴 수 있습니다): " + ex.Message;
                _log.LogWarning(ex, "메일 요약을 보낸 뒤 보낸 표시를 저장하지 못했습니다: {User}", u.Name);
            }
        }
        return new DigestRun(today, now, users.Count, sent, failed, notes, error);
    }

    public MailItem Compose(string name, string email, DateOnly today, IReadOnlyList<Notification> notes, IReadOnlyDictionary<int, string?> titles,
        IReadOnlyList<MyWork> late, IReadOnlyList<MyWork> soon)
    {
        var b = new StringBuilder();
        b.AppendLine($"{name} 님, {today:yyyy-MM-dd}(한국 시간) 과제 스케줄러 요약입니다.");
        if (notes.Count > 0)
        {
            b.AppendLine().AppendLine($"[읽지 않은 알림 {notes.Count}건]");
            foreach (var n in notes)
            {
                var title = titles.TryGetValue(n.ProjectId, out var t) && t != null ? t : "(삭제된 과제)";
                var who = n.Activity?.User?.Name;
                b.AppendLine($"- [{NotifyKinds.Label(n.Kind)}] {title} — {n.Text}" + (who == null ? "" : $" ({who}, {KoreaClock.ToKst(n.CreatedAt):MM-dd HH:mm})"));
            }
        }
        if (late.Count > 0)
        {
            b.AppendLine().AppendLine($"[지연된 내 작업 {late.Count}건]");
            foreach (var w in late) b.AppendLine($"- {w.Project} · {w.Stage} · {w.Title} (목표 종료 {w.PlanEnd:MM-dd}, {w.Chip.Text})");
        }
        if (soon.Count > 0)
        {
            b.AppendLine().AppendLine($"[{Rules.SoonDays}일 안에 끝나야 하는 내 작업 {soon.Count}건]");
            foreach (var w in soon) b.AppendLine($"- {w.Project} · {w.Stage} · {w.Title} (목표 종료 {w.PlanEnd:MM-dd}, {w.Chip.Text})");
        }
        b.AppendLine();
        if (_o.BaseUrl != null) b.AppendLine($"내 할 일: {_o.BaseUrl}My").AppendLine($"알림: {_o.BaseUrl}My/Notifications");
        b.AppendLine($"이 메일은 근무일 {_o.DailyHour}시(한국 시간)에 하루 한 번 보냅니다. 받는 주소는 관리자가 관리 > 사용자에서 바꿉니다.");
        var subject = $"[과제 스케줄러] {today:MM-dd} 요약 — 알림 {notes.Count}건" + (late.Count > 0 ? $", 지연 {late.Count}건" : "");
        return new MailItem(email, subject, b.ToString());
    }

    // 관리 화면의 '시험 메일 보내기'. 요약을 꺼 둔 서버에서도 메일 서버 설정을 확인할 수 있게 Enabled 와 관계없이 보낸다
    public async Task SendTestAsync(string? email, string by, CancellationToken ct)
    {
        var to = NotifyOptions.NormalizeEmail(email) ?? throw new RuleException("메일 주소를 확인해 주세요.", "email");
        if (!_channel.Ready) throw new RuleException("메일 서버(Notify:Smtp:Host·From)가 설정되지 않았습니다. appsettings 의 Notify:Smtp 를 채운 뒤 서버를 다시 시작하세요.", "email");
        var body = $"과제 스케줄러의 시험 메일입니다. {by} 님이 {KoreaClock.ToKst(_clock.UtcNow):yyyy-MM-dd HH:mm}(한국 시간)에 보냈습니다.\n"
                   + (_o.Enabled ? $"하루 요약은 근무일 {_o.DailyHour}시(한국 시간)에 보냅니다." : "하루 요약은 꺼져 있습니다(Notify:Enabled).");
        try
        {
            await _channel.SendAsync(new MailItem(to, "[과제 스케줄러] 시험 메일", body), ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RuleException))
        {
            _log.LogWarning(ex, "시험 메일을 보내지 못했습니다: {To}", to);
            throw new RuleException("보내지 못했습니다: " + ex.Message, "email");
        }
    }
}

// 하루 한 번 메일 요약 (Notify:Enabled 일 때만). 1분마다 때가 됐는지 본다(Due) — 요청 범위가 없으므로 IServiceScopeFactory 로 범위를 만든다
public class NotifyWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NotifyOptions _o;
    private readonly NotifyState _state;
    private readonly IClock _clock;
    private readonly ILogger<NotifyWorker> _log;

    public NotifyWorker(IServiceScopeFactory scopes, NotifyOptions options, NotifyState state, IClock clock, ILogger<NotifyWorker> log)
    {
        _scopes = scopes;
        _o = options;
        _state = state;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "메일 요약 확인 실패");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }

    // 한 번 확인: 때가 됐으면 요약을 보내고 결과를 남긴다. 보내지 않았으면 null
    public async Task<DigestRun?> TickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (!InWindow(_o, now, _state.Last?.Date)) return null;
        await using var scope = _scopes.CreateAsyncScope();
        var cal = await scope.ServiceProvider.GetRequiredService<ProjectService>().CalendarAsync();
        if (!Due(_o, now, _state.Last?.Date, cal)) return null;
        var run = await scope.ServiceProvider.GetRequiredService<DigestService>().RunAsync(ct);
        _state.Save(run);
        if (run.Failed > 0) _log.LogWarning("메일 요약: {Failed}명에게 보내지 못했습니다 — {Error}", run.Failed, run.Error);
        else _log.LogInformation("메일 요약: {Sent}명에게 보냈습니다(알림 {Notes}건)", run.Sent, run.Notifications);
        return run;
    }

    // 시각만 보는 판정(DB 를 읽지 않는다): 켜져 있고, 오늘(한국 날짜) 아직 보내지 않았고, 정한 시각부터 CatchUpHours 안
    public static bool InWindow(NotifyOptions o, DateTime utcNow, DateOnly? lastRun)
    {
        if (!o.Enabled) return false;
        var kst = KoreaClock.ToKst(utcNow);
        if (lastRun == DateOnly.FromDateTime(kst)) return false;
        return kst.Hour >= o.DailyHour && kst.Hour < o.DailyHour + NotifyOptions.CatchUpHours;
    }

    // 근무일에만 보낸다 — 주말·공휴일의 알림은 다음 근무일 요약에 모인다
    public static bool Due(NotifyOptions o, DateTime utcNow, DateOnly? lastRun, WorkCalendar cal) =>
        InWindow(o, utcNow, lastRun) && cal.IsWorkingDay(KoreaClock.KstDate(utcNow));
}
