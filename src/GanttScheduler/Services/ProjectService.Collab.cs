using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 활동 탭의 조건: 종류(Reporting.Kinds 의 키), 단계, 글자 검색
public record ActivityFilter(string? Kind, int? StageId, string? Q);

// 댓글 고치기. Mentions: 고친 뒤의 알릴 사람 전체(새로 더한 사람에게만 알린다)
public record CommentEditInput(int Id, string? Text, List<int>? Mentions = null);

// 내 할 일의 '새 소식' 한 줄. Why: 왜 나에게 보이는지(NotifyKinds — 멘션·내 작업·내 과제·관심 과제)
public record NewsItem(int ActivityId, int ProjectId, string Project, string Type, string Text, string? Reason, string User, DateTime At,
    string Why, string WhyLabel);

// 관심 과제 한 줄 (내 할 일)
public record WatchedProject(int Id, string Title, ChipView Chip, int NewCount);

// 협업: 상세 방문(새 활동)·관심 과제·댓글 수정·삭제·멘션·새 소식.
// 방문·관심은 개인 설정이라 활동을 남기지 않는다(과제 내용이 바뀌지 않는다). 댓글 수정·삭제는 과제 기록이므로 활동을 남긴다
public partial class ProjectService
{
    public const int MentionLimit = 20;
    public const int NewsLimit = 30;
    public const string DeletedCommentText = "삭제된 댓글";

    // 활동만으로는 알 수 없는 작업 담당자(새 작업·지운 작업·새로 맡은 사람)를 알림 대상에 더한다
    private void InvolveOwners(Activity a, int? ownerId, int? coOwnerId)
    {
        _log.Involve(a, ownerId, NotifyKinds.Work);
        _log.Involve(a, coOwnerId, NotifyKinds.Work);
    }

    // ── 상세 방문·새 활동 ────────────────────────────────────────────────

    // 상세 화면을 연다: 지난번에 본 뒤의 활동을 '새 활동'으로 표시할 기준(Since)을 정하고, 본 시각을 지금으로 바꾼다.
    // 저장은 요청 하나에 한 번이다(목록·대시보드·1분 확인은 방문으로 치지 않는다). 과제가 없으면 null
    public async Task<(ProjectView View, DateTime Since, bool Watched)?> OpenAsync(int id)
    {
        var me = _me.Id;
        var now = _clock.UtcNow;
        var visit = await _db.ProjectVisits.FirstOrDefaultAsync(v => v.UserId == me && v.ProjectId == id);
        var since = Rules.NewSince(visit?.LastSeenAt, now);
        var view = await ViewAsync(id, since: since);
        if (view == null) return null;
        if (visit == null) _db.ProjectVisits.Add(new ProjectVisit { UserId = me, ProjectId = id, LastSeenAt = now });
        else visit.LastSeenAt = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 같은 사람이 두 탭에서 동시에 처음 열면 (사용자, 과제) 유니크 색인에 걸린다 — 다른 탭이 이미 남겼으므로 화면은 그대로 연다
        }
        var watched = await _db.ProjectWatches.AsNoTracking().AnyAsync(w => w.UserId == me && w.ProjectId == id);
        return (view, since, watched);
    }

    // 과제별 '새 활동' 수(목록·대시보드): 다른 사람이 마지막 방문 뒤(최근 Rules.NewActivityDays 일 안)에 남긴 활동.
    // 과제마다 세지 않고 한 번의 그룹 쿼리로 센다 — Activity.At 색인으로 최근 활동만 읽고 내 방문 기록을 왼쪽 조인한다
    public async Task<Dictionary<int, int>> NewCountsAsync()
    {
        var me = _me.Id;
        var floor = _clock.UtcNow.AddDays(-Rules.NewActivityDays);
        var rows = await (
                from a in _db.LiveActivities.AsNoTracking()
                where a.At > floor && a.UserId != me && a.Type != ActivityType.CommentChanged && a.DeletedAt == null
                join v in _db.ProjectVisits.AsNoTracking().Where(v => v.UserId == me) on a.ProjectId equals v.ProjectId into visits
                from v in visits.DefaultIfEmpty()
                where v == null || a.At > v.LastSeenAt
                group a by a.ProjectId into g
                select new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.ProjectId, r => r.Count);
    }

    // 활동 행에 지금 사용자 기준의 값을 채운다: 내 것·새 활동·고칠 수 있는지·멘션(댓글이 있을 때만 한 번 더 읽는다)
    private async Task<List<ActivityView>> DecorateAsync(int projectId, List<ActivityView> rows, DateTime? since)
    {
        if (rows.Count == 0) return rows;
        var me = _me.User?.Id ?? 0;
        var now = _clock.UtcNow;
        var commentKey = Views.Key(ActivityType.Comment);
        var comments = rows.Where(r => r.Type == commentKey).Select(r => r.Id).ToHashSet();
        var mentions = new Dictionary<int, List<MentionView>>();
        if (comments.Count > 0)
        {
            // Id 범위로 읽고 메모리에서 거른다 — 목록 매개 변수(IN)를 쓰지 않는다(DESIGN 6장)
            var lo = comments.Min();
            var hi = comments.Max();
            mentions = (await _db.ActivityMentions.AsNoTracking().Where(m => m.ActivityId >= lo && m.ActivityId <= hi)
                    .Select(m => new { m.ActivityId, m.UserId, m.User!.Name }).ToListAsync())
                .Where(m => comments.Contains(m.ActivityId))
                .GroupBy(m => m.ActivityId)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new MentionView(m.UserId, m.Name)).ToList());
        }
        return rows.Select(r =>
        {
            var type = Enum.TryParse<ActivityType>(r.Type, ignoreCase: true, out var t) ? t : ActivityType.Edited;
            return r with
            {
                Mine = r.UserId == me,
                New = since != null && Rules.IsNewActivity(type, r.UserId, r.At, me, since.Value, r.Deleted),
                CanEdit = type == ActivityType.Comment && Rules.CanEditComment(r.UserId, r.At, r.Deleted ? now : null, me, now, _comments.EditHours),
                Mentions = mentions.GetValueOrDefault(r.Id),
            };
        }).ToList();
    }

    // 종류·단계 조건은 DB 에서 (두 DB 에서 같게 번역되는 같음·OR 식만 — Reporting.KindIs)
    private static IQueryable<Activity> ApplyFilter(IQueryable<Activity> q, ActivityFilter? f)
    {
        if (f == null) return q;
        if (f.StageId is int stageId) q = q.Where(a => a.StageId == stageId);
        if (!string.IsNullOrWhiteSpace(f.Kind) && Reporting.Kinds.Any(k => k.Key == f.Kind)) q = q.Where(Reporting.KindIs(f.Kind));
        return q;
    }

    // 글자 검색은 투영한 뒤 메모리에서 한다 — SQLite(대소문자 구분)와 SQL Server(정렬 규칙)의 결과가 다르기 때문이다(변경 이력과 같은 방식).
    // 과제 하나의 활동으로 묶이므로 읽는 양이 정해진다
    private static async Task<List<ActivityView>> FilteredRowsAsync(IQueryable<Activity> q, ActivityFilter? f, int take)
    {
        var text = f?.Q?.Trim() ?? "";
        if (text.Length == 0) return await ActivityRowsAsync(q, take);
        return (await ActivityRowsAsync(q, null))
            .Where(a => a.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (a.Reason ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)
                        || a.User.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(take).ToList();
    }

    // ── 관심 과제 ───────────────────────────────────────────────────────

    // 관심 과제로 두거나 뺀다. 지금 상태를 돌려준다
    public async Task<bool> WatchAsync(int projectId, bool on)
    {
        if (!await _db.LiveProjects.AnyAsync(p => p.Id == projectId))
            throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");
        var me = _me.Id;
        var row = await _db.ProjectWatches.FirstOrDefaultAsync(w => w.UserId == me && w.ProjectId == projectId);
        if (on && row == null) _db.ProjectWatches.Add(new ProjectWatch { UserId = me, ProjectId = projectId, CreatedAt = _clock.UtcNow });
        else if (!on && row != null) _db.ProjectWatches.Remove(row);
        else return on;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (on)
        {
            // 두 탭에서 동시에 누르면 유니크 색인에 걸린다 — 이미 관심 과제다
        }
        return on;
    }

    public async Task<HashSet<int>> WatchedIdsAsync()
    {
        var me = _me.Id;
        return (await _db.ProjectWatches.AsNoTracking().Where(w => w.UserId == me && w.Project!.DeletedAt == null).Select(w => w.ProjectId).ToListAsync()).ToHashSet();
    }

    // 내 관심 과제 (진행 → 보류 → 완료 → 중단, 그 안에서는 이름순)와 새 활동 수
    public async Task<List<WatchedProject>> WatchedProjectsAsync(Dictionary<int, int>? newCounts = null)
    {
        var me = _me.Id;
        var rows = await _db.ProjectWatches.AsNoTracking().Where(w => w.UserId == me && w.Project!.DeletedAt == null)
            .Select(w => new { w.ProjectId, w.Project!.Title, w.Project.State }).ToListAsync();
        newCounts ??= await NewCountsAsync();
        return rows
            .OrderBy(r => r.State switch { ProjectState.Active => 0, ProjectState.Hold => 1, ProjectState.Done => 2, _ => 3 })
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .Select(r => new WatchedProject(r.ProjectId, r.Title, Views.ProjectChip(r.State, 0), newCounts.GetValueOrDefault(r.ProjectId)))
            .ToList();
    }

    // ── 멘션 ───────────────────────────────────────────────────────────

    // 알릴 사람으로 고를 수 있는 사람 (사용 중지된 사람 제외, 이름순)
    public async Task<List<MentionView>> PeopleAsync() =>
        (await _db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Name }).ToListAsync())
            .OrderBy(u => u.Name).Select(u => new MentionView(u.Id, u.Name)).ToList();

    // 알릴 사람 확인: 있는 사용자이고 사용 중이어야 한다(담당자 지정과 달리 새 사용자를 만들지 않는다). 중복은 하나로.
    // kept: 이미 그 댓글에 있던 멘션 — 그 뒤 사용 중지된 사람이 있어도 글만 고치려는 작성자를 막지 않는다(새로 알리지는 않는다)
    private async Task<List<int>> ValidateMentionsAsync(List<int>? ids, IReadOnlyCollection<int>? kept = null)
    {
        var list = (ids ?? new()).Distinct().ToList();
        if (list.Count == 0) return list;
        if (list.Count > MentionLimit) throw new RuleException($"알릴 사람은 {MentionLimit}명까지 고를 수 있습니다.", "mentions");
        var active = (await _db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync()).ToHashSet();
        if (list.Any(id => !active.Contains(id) && kept?.Contains(id) != true))
            throw new RuleException("알릴 사람을 다시 골라 주세요. 없는 사용자이거나 사용 중지된 사용자입니다.", "mentions");
        return list;
    }

    // ── 댓글 수정·삭제 ──────────────────────────────────────────────────
    // 작성자만, 남긴 뒤 Comments:EditHours 시간 안에(Rules.CanEditComment). 행은 지우지 않고(이력 보존) 누가·언제 고쳤는지 활동으로 남긴다

    public async Task<int> EditCommentAsync(CommentEditInput input)
    {
        var text = Text(input.Text, Limits.ActivityText, "내용", "text", required: true)!;
        var (p, c) = await RequireOwnCommentAsync(input.Id, "고칠");
        var current = await _db.ActivityMentions.Where(m => m.ActivityId == c.Id).ToListAsync();
        var mentions = await ValidateMentionsAsync(input.Mentions, current.Select(m => m.UserId).ToList());
        var added = mentions.Where(id => current.All(m => m.UserId != id)).ToList();
        var removed = current.Where(m => !mentions.Contains(m.UserId)).ToList();
        if (c.Text == text && added.Count == 0 && removed.Count == 0) return p.Id;

        var change = NewActivity(ActivityType.CommentChanged, "댓글 수정", stageId: c.StageId, workId: c.WorkItemId, newValue: c.Id.ToString());
        if (c.Text != text)
        {
            c.OldValue ??= c.Text;      // 처음 남긴 글을 한 번만 남긴다 — 여러 번 고쳐도 원문은 그대로
            c.Text = text;
            c.EditedAt = _clock.UtcNow;
            await RewriteNotificationsAsync(c);
        }
        foreach (var userId in added)
        {
            _db.ActivityMentions.Add(new ActivityMention { ActivityId = c.Id, UserId = userId });
            // 알림은 고친 댓글을 가리킨다(수정 기록이 아니라)
            _log.Involve(change, userId, NotifyKinds.Mention, c);
        }
        _db.ActivityMentions.RemoveRange(removed);
        p.Activities.Add(change);
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // 지운 댓글은 본문을 자리 표시로 바꾸고 원문·멘션도 지운다(내용을 지우려는 것이므로). 그 댓글을 가리키는 알림의 글도 바꾼다
    public async Task<int> DeleteCommentAsync(int id)
    {
        var (p, c) = await RequireOwnCommentAsync(id, "지울");
        var now = _clock.UtcNow;
        c.Text = DeletedCommentText;
        c.OldValue = null;
        c.DeletedAt = now;
        _db.ActivityMentions.RemoveRange(await _db.ActivityMentions.Where(m => m.ActivityId == c.Id).ToListAsync());
        await RewriteNotificationsAsync(c);
        p.Activities.Add(NewActivity(ActivityType.CommentChanged, "댓글 삭제", stageId: c.StageId, workId: c.WorkItemId, newValue: c.Id.ToString()));
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // 그 댓글을 가리키는 알림의 글을 지금 댓글 글로 맞춘다(고칠 때·지울 때 같은 방식) — 메일 요약도 이 글을 싣는다
    private async Task RewriteNotificationsAsync(Activity c)
    {
        foreach (var n in await _db.Notifications.Where(n => n.ActivityId == c.Id).ToListAsync())
            n.Text = ActivityLog.Resummarize(n.Text, c);
    }

    private async Task<(Project, Activity)> RequireOwnCommentAsync(int id, string verb)
    {
        var c = await _db.Activities.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new RuleException("댓글을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        if (c.Type != ActivityType.Comment) throw new RuleException($"댓글만 {verb} 수 있습니다.");
        if (c.UserId != _me.Id) throw new RuleException($"댓글은 남긴 사람만 {verb} 수 있습니다.");
        if (c.DeletedAt != null) throw new RuleException("이미 지운 댓글입니다.");
        if (!Rules.CanEditComment(c.UserId, c.At, c.DeletedAt, _me.Id, _clock.UtcNow, _comments.EditHours))
            throw new RuleException(_comments.EditHours > 0
                ? $"댓글은 남긴 뒤 {_comments.EditHours}시간 안에만 고치거나 지울 수 있습니다."
                : "댓글을 고치거나 지울 수 없게 설정되어 있습니다.");
        var p = await _db.LiveProjects.FirstOrDefaultAsync(x => x.Id == c.ProjectId)
            ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");
        return (p, c);
    }

    // ── 새 소식 (내 할 일) ──────────────────────────────────────────────

    // 내 과제(담당)·관심 과제·내 작업(담당·부담당)·나를 멘션한 댓글에 다른 사람이 남긴 활동 중, 그 과제를 마지막으로 본 뒤
    // (최근 Rules.NewActivityDays 일 안)의 것. 자동 밀기·연쇄 이동으로 내 작업 일정이 바뀐 것도 그 작업의 활동이라 여기 나온다.
    // 조건은 EXISTS(Any)로, 방문 시각 비교는 내 방문 기록을 왼쪽 조인해 모두 DB 에서 거른 뒤 자른다 — 메모리에서 거르면
    // 이미 본 바쁜 과제의 활동이 잘린 몫을 채워 다른 과제의 새 소식이 빠진다. 기준은 Rules.IsNewActivity·NewCountsAsync 와 같다
    public async Task<List<NewsItem>> NewsAsync()
    {
        var me = _me.Id;
        var floor = _clock.UtcNow.AddDays(-Rules.NewActivityDays);
        var rows = await (
                from a in _db.LiveActivities.AsNoTracking()
                where a.At > floor && a.UserId != me && a.Type != ActivityType.CommentChanged && a.DeletedAt == null
                where a.Project!.OwnerId == me
                      || _db.ProjectWatches.Any(w => w.ProjectId == a.ProjectId && w.UserId == me)
                      || _db.WorkItems.Any(w => w.Id == a.WorkItemId && (w.OwnerId == me || w.CoOwnerId == me))
                      || a.Mentions.Any(m => m.UserId == me)
                join v in _db.ProjectVisits.AsNoTracking().Where(v => v.UserId == me) on a.ProjectId equals v.ProjectId into visits
                from v in visits.DefaultIfEmpty()
                where v == null || a.At > v.LastSeenAt
                orderby a.At descending, a.Id descending
                select a)
            .Take(NewsLimit)
            .Select(a => new
            {
                a.Id, a.ProjectId, Project = a.Project!.Title, a.Type, a.Text, a.Reason, a.UserId, User = a.User!.Name, a.At,
                Mentioned = a.Mentions.Any(m => m.UserId == me),
                MyWork = _db.WorkItems.Any(w => w.Id == a.WorkItemId && (w.OwnerId == me || w.CoOwnerId == me)),
                MyProject = a.Project.OwnerId == me,
            })
            .ToListAsync();
        return rows
            .Select(a =>
            {
                var why = a.Mentioned ? NotifyKinds.Mention : a.MyWork ? NotifyKinds.Work : a.MyProject ? NotifyKinds.Project : NotifyKinds.Watch;
                return new NewsItem(a.Id, a.ProjectId, a.Project ?? "", Views.Key(a.Type), a.Text, a.Reason, a.User ?? "", a.At, why, NotifyKinds.Label(why));
            })
            .ToList();
    }
}
