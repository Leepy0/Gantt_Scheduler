using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 알림 종류. 한 저장에서 한 사람에게 여러 이유가 겹치면 앞의 것(더 직접적인 것)을 쓴다
public static class NotifyKinds
{
    public const string Mention = "mention";    // 댓글에서 알릴 사람으로 나를 골랐다
    public const string Work = "work";          // 내 작업(담당·부담당)이 바뀌었다 — 자동 밀기·연쇄 이동 포함
    public const string Project = "project";    // 내 과제(담당)에 다른 사람이 활동을 남겼다
    public const string Watch = "watch";        // 관심 과제에 다른 사람이 활동을 남겼다

    public static readonly string[] Order = { Mention, Work, Project, Watch };

    public static string Label(string kind) => kind switch
    {
        Mention => "멘션",
        Work => "내 작업",
        Project => "내 과제",
        Watch => "관심 과제",
        _ => "알림",
    };
}

// 댓글 설정 (Comments:EditHours — 작성자가 고치거나 지울 수 있는 시간)
public sealed record CommentOptions(int EditHours)
{
    public static readonly CommentOptions Default = new(24);

    public static CommentOptions From(IConfiguration cfg) => new(Math.Max(0, cfg.GetValue("Comments:EditHours", Default.EditHours)));
}

// 활동을 만드는 한 곳(누가·언제). ProjectService.NewActivity 와 첨부(AttachmentService)가 이것을 거친다.
// 만든 활동을 기억해 두었다가 저장 직전(AppDb.SaveChangesAsync → BeforeSaveAsync)에 받을 사람을 계산해 같은 SaveChanges 에 알림을 더한다 —
// 활동은 남았는데 알림은 빠지는(또는 그 반대) 일이 없게 하기 위해서다.
// 받을 사람: 내 작업(담당·부담당 — 자동 밀기 포함), 내 과제(담당), 관심 과제, 멘션. 활동을 남긴 본인과 사용 중지된 사람은 빼고,
// 한 저장에서 한 사람에게는 과제마다 한 건으로 묶는다
public sealed class ActivityLog : ISaveHook
{
    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;
    private readonly List<Activity> _pending = new();
    private readonly List<Activity> _deferred = new();
    private readonly List<Hint> _hints = new();
    // 받을 사람 바꿔 보내기(사용자 병합: 같은 저장에서 지워지는 사람 대신 합쳐질 사람에게) — 지워질 사용자를 가리키는 알림을 만들지 않게
    private readonly Dictionary<int, int> _redirect = new();

    // 활동만으로는 알 수 없는 받을 사람. Target: 알림이 가리킬 활동(없으면 Source) — 댓글을 고치며 새로 고른 멘션은 그 댓글을 가리킨다
    private sealed record Hint(Activity Source, int UserId, string Kind, Activity? Target);

    public ActivityLog(AppDb db, IClock clock, CurrentUser me)
    {
        _db = db;
        _clock = clock;
        _me = me;
        db.AddSaveHook(this);
    }

    public Activity New(ActivityType type, string text, string? reason = null, int? stageId = null, int? workId = null,
                        string? oldValue = null, string? newValue = null, Guid? batch = null)
    {
        var a = new Activity
        {
            Type = type,
            Text = text.Length > 1000 ? text[..1000] : text,
            Reason = reason,
            StageId = stageId,
            WorkItemId = workId,
            OldValue = oldValue,
            NewValue = newValue,
            BatchId = batch,
            UserId = _me.Id,
            At = _clock.UtcNow,
        };
        _pending.Add(a);
        return a;
    }

    // 활동에 받을 사람을 더한다(새 작업·지운 작업의 담당, 새로 바뀐 담당, 멘션). 저장 전 새 사용자(Id 0)는 아직 볼 사람이 없어 건너뛴다
    public void Involve(Activity source, int? userId, string kind, Activity? target = null)
    {
        if (userId is int id && id > 0) _hints.Add(new Hint(source, id, kind, target));
    }

    // 이 저장에서 from 에게 갈 알림을 to 에게 보낸다 (UserAdminService.MergeAsync)
    public void Redirect(int from, int to) => _redirect[from] = to;

    public async Task BeforeSaveAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        // 과제 컬렉션에 더한 활동의 과제 Id(FK)는 EF 가 맞춰 둔다. 새 과제(아직 Id 없음)의 활동은 저장 뒤로 미룬다
        _db.ChangeTracker.DetectChanges();
        var ready = _pending.Where(a => a.ProjectId > 0).ToList();
        _deferred.AddRange(_pending.Where(a => a.ProjectId <= 0));
        _pending.Clear();
        await FanOutAsync(ready, ct);
    }

    public async Task<bool> AfterSaveAsync(CancellationToken ct)
    {
        if (_deferred.Count == 0) return false;
        // 저장되지 않은 활동(Id 가 없는 것)은 버려진 것이다
        var ready = _deferred.Where(a => a.ProjectId > 0 && a.Id > 0).ToList();
        _deferred.Clear();
        return await FanOutAsync(ready, ct) > 0;
    }

    private async Task<int> FanOutAsync(List<Activity> acts, CancellationToken ct)
    {
        if (acts.Count == 0) return 0;
        var made = 0;
        var save = Guid.NewGuid().ToString("N");
        foreach (var group in acts.GroupBy(a => a.ProjectId))
        {
            var projectId = group.Key;
            var list = group.ToList();
            var hints = _hints.Where(h => list.Any(a => ReferenceEquals(a, h.Source))).ToList();
            // 댓글 수정·삭제 기록은 새로 고른 멘션에게만 알린다(고칠 때마다 담당·관심자에게 알리면 소음이다)
            var broad = list.Where(a => a.Type != ActivityType.CommentChanged).ToList();

            // 과제 담당과 관심 등록자 (사용 중지된 사람 제외)
            var project = broad.Count == 0 ? null : await _db.Projects.AsNoTracking().Where(p => p.Id == projectId)
                .Select(p => new
                {
                    p.OwnerId,
                    OwnerActive = p.Owner!.IsActive,
                    Watchers = _db.ProjectWatches.Where(w => w.ProjectId == p.Id && w.User!.IsActive).Select(w => w.UserId).ToList(),
                })
                .FirstOrDefaultAsync(ct);
            // 활동이 가리키는 작업의 담당·부담당(저장 전 값 — 이번에 바뀐 담당은 Involve 로 따로 받는다)
            var workIds = broad.Where(a => a.WorkItemId != null).Select(a => a.WorkItemId!.Value).ToHashSet();
            var works = workIds.Count == 0 ? new() : (await _db.WorkItems.AsNoTracking().Where(w => w.Stage!.ProjectId == projectId)
                    .Select(w => new
                    {
                        w.Id,
                        Owner = w.Owner != null && w.Owner.IsActive ? w.OwnerId : null,
                        CoOwner = w.CoOwner != null && w.CoOwner.IsActive ? w.CoOwnerId : null,
                    })
                    .ToListAsync(ct))
                .Where(w => workIds.Contains(w.Id)).ToDictionary(w => w.Id);

            var per = new Dictionary<int, List<(Activity Lead, string Kind)>>();
            void Add(Activity source, int? userId, string kind, Activity? target = null)
            {
                if (userId is int moved && _redirect.TryGetValue(moved, out var to)) userId = to;
                if (userId is not int id || id <= 0 || id == source.UserId) return;
                if (!per.TryGetValue(id, out var items)) per[id] = items = new();
                items.Add((target ?? source, kind));
            }
            foreach (var a in list)
            {
                foreach (var h in hints.Where(h => ReferenceEquals(h.Source, a))) Add(a, h.UserId, h.Kind, h.Target);
                if (a.Type == ActivityType.CommentChanged) continue;
                if (a.WorkItemId is int wid && works.TryGetValue(wid, out var w))
                {
                    Add(a, w.Owner, NotifyKinds.Work);
                    Add(a, w.CoOwner, NotifyKinds.Work);
                }
                if (project is { OwnerActive: true }) Add(a, project.OwnerId, NotifyKinds.Project);
                foreach (var u in project?.Watchers ?? new List<int>()) Add(a, u, NotifyKinds.Watch);
            }

            foreach (var (userId, items) in per)
            {
                var kind = NotifyKinds.Order.First(k => items.Any(i => i.Kind == k));
                var lead = items.First(i => i.Kind == kind).Lead;
                var count = items.Select(i => i.Lead).Distinct(ReferenceEqualityComparer.Instance).Count();
                // 이미 저장된 활동을 가리키는 알림(고친 댓글의 멘션)은 활동·사람마다 한 번만 — 같은 사람을 뺐다 다시 골라도 또 알리지 않는다.
                // 새 활동은 Id 가 없으니 이번 저장·과제·사람으로 묶는다
                var key = lead.Id > 0 ? $"a{lead.Id}:u{userId}" : $"s{save}:p{projectId}:u{userId}";
                if (lead.Id > 0)
                {
                    var leadId = lead.Id;
                    // 댓글을 남길 때 이미 같은 이유로 알렸으면(그때의 열쇠는 저장 단위라 다르다) 다시 알리지 않는다
                    if (await _db.Notifications.AnyAsync(n => n.Key == key || (n.ActivityId == leadId && n.UserId == userId && n.Kind == kind), ct)) continue;
                }
                var n = new Notification
                {
                    UserId = userId, ProjectId = projectId, Kind = kind, Text = Summary(lead, count), Key = key, CreatedAt = _clock.UtcNow,
                };
                if (lead.Id > 0) n.ActivityId = lead.Id;
                else n.Activity = lead;
                _db.Notifications.Add(n);
                made++;
            }
        }
        _hints.RemoveAll(h => acts.Any(a => ReferenceEquals(a, h.Source)));
        return made;
    }

    // 알림 한 줄: 대표 활동의 글(댓글은 '댓글: …'). 같은 저장에서 함께 남은 활동은 '외 n건'으로 센다
    public static string Summary(Activity lead, int count)
    {
        var text = lead.Type == ActivityType.Comment ? "댓글: " + lead.Text : lead.Text;
        if (text.Length > 300) text = text[..300] + "…";
        return count > 1 ? $"{text} 외 {count - 1}건" : text;
    }

    // 댓글을 고치거나 지운 뒤 그 댓글을 가리키는 알림 글을 다시 쓴다. 알림 글은 만들 때 복사해 둔 값이라 그대로 두면
    // 고치기 전 글이 알림 목록·메일 요약에 남는다. 함께 묶였던 '외 n건'은 저장 당시 수라 다시 셀 수 없으므로 옛 글에서 읽어 둔다
    public static string Resummarize(string oldText, Activity lead)
    {
        var m = System.Text.RegularExpressions.Regex.Match(oldText, @" 외 (\d+)건$");
        var count = m.Success && int.TryParse(m.Groups[1].Value, out var more) ? more + 1 : 1;
        return Summary(lead, count);
    }
}

// 알림 목록 한 줄. Actor: 활동을 남긴 사람, Project: 과제명(지운 과제면 null)
public record NotificationView(
    int Id, string Kind, string KindLabel, int ProjectId, string? Project, int? ActivityId, string Text, string? Actor,
    DateTime CreatedAt, bool Read);

public record NotificationPage(List<NotificationView> Items, bool More, int Unread);

// 화면 내 알림 읽기·읽음 처리 (지금 사용자 것만)
public class NotificationService
{
    public const int PageSize = 50;

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;

    public NotificationService(AppDb db, IClock clock, CurrentUser me)
    {
        _db = db;
        _clock = clock;
        _me = me;
    }

    // 읽지 않은 알림 수 — 헤더의 종이 1분마다 묻는다(UserId·ReadAt 색인 하나로 센다)
    public Task<int> UnreadAsync() => Live(_db.Notifications.AsNoTracking()).CountAsync(n => n.UserId == _me.Id && n.ReadAt == null);

    // 휴지통에 있는 과제의 알림은 세지도 보이지도 않는다(지우지 않으므로 복원하면 돌아온다). 알림의 ProjectId 는 FK 없는 참조라 EXISTS 로 고른다
    private IQueryable<Notification> Live(IQueryable<Notification> q) =>
        q.Where(n => _db.Projects.Any(p => p.Id == n.ProjectId && p.DeletedAt == null));

    // 최근 순 한 쪽. beforeId: 화면의 마지막 알림 다음부터. unreadOnly: 읽지 않은 것만
    public async Task<NotificationPage> ListAsync(int? beforeId = null, bool unreadOnly = false, int take = PageSize)
    {
        take = Math.Clamp(take, 1, 200);
        var q = Live(_db.Notifications.AsNoTracking()).Where(n => n.UserId == _me.Id);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        if (beforeId != null) q = q.Where(n => n.Id < beforeId.Value);
        var rows = await q.OrderByDescending(n => n.Id).Take(take + 1)
            .Select(n => new
            {
                n.Id, n.Kind, n.ProjectId, n.ActivityId, n.Text, n.CreatedAt, n.ReadAt,
                Actor = n.Activity == null ? null : n.Activity.User!.Name,
                Project = _db.Projects.Where(p => p.Id == n.ProjectId).Select(p => p.Title).FirstOrDefault(),
            })
            .ToListAsync();
        var more = rows.Count > take;
        var items = rows.Take(take).Select(n => new NotificationView(n.Id, n.Kind, NotifyKinds.Label(n.Kind), n.ProjectId, n.Project,
            n.ActivityId, n.Text, n.Actor, n.CreatedAt, n.ReadAt != null)).ToList();
        return new NotificationPage(items, more, await UnreadAsync());
    }

    // 하나를 읽음으로. 남의 알림·없는 알림은 null
    public async Task<Notification?> ReadAsync(int id)
    {
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _me.Id);
        if (n == null) return null;
        if (n.ReadAt == null)
        {
            n.ReadAt = _clock.UtcNow;
            await _db.SaveChangesAsync();
        }
        return n;
    }

    // 모두 읽음. 읽음 처리한 수
    public async Task<int> ReadAllAsync()
    {
        var list = await Live(_db.Notifications).Where(n => n.UserId == _me.Id && n.ReadAt == null).ToListAsync();
        foreach (var n in list) n.ReadAt = _clock.UtcNow;
        if (list.Count > 0) await _db.SaveChangesAsync();
        return list.Count;
    }
}

public static class NotificationApi
{
    // 헤더 종의 읽지 않은 수. 화면이 1분마다(보이는 탭만) 묻는 가벼운 요청이다 — _Layout 이 모든 화면에서 세지 않게 따로 둔다.
    // /version 과 달리 사용자를 알아야 하므로 UserGate 를 거친다(모르는 기기는 401)
    public static void MapNotificationApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/unread", async (HttpContext ctx, NotificationService notes) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ok = true, unread = await notes.UnreadAsync() });
        });
}
