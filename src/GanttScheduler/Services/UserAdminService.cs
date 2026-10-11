using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 사용자 병합: from 의 모든 기록을 into 로 옮기고 from 을 지운다
public record MergeInput(int FromId, int IntoId, string? Reason);

// 담당 이관 후 사용 중지. Projects: 진행 과제의 담당, Works: 미완료 작업의 담당·부담당을 To 에게 넘긴다
public record ReassignInput(int FromId, int? ToId, bool Projects, bool Works, string? Reason);

// 사용자가 지금 맡고 있는 것(진행 과제 담당 수, 미완료 작업 담당·부담당 수) — 관리 > 사용자 목록과 이관 시트
public record UserLoad(int Projects, int Works);

// 병합·이관 결과(옮긴 행 수) — 화면 안내와 시험
public record UserMoveResult(int Projects, int Works, int Rows, string Text);

// 사용 현황: 조직 한 줄. 조용한 과제 = 진행 과제인데 기간 동안 변경이 없음(Rules.IsQuiet)
public record OrgUsage(string Name, int Users, int ActiveUsers, int Updates, int OpenProjects, int QuietProjects)
{
    public int QuietPercent => Rules.Percent(QuietProjects, OpenProjects);
    public int ActivePercent => Rules.Percent(ActiveUsers, Users);
}

// 사용 현황: 사람 한 줄(관리자만 본다)
public record PersonUsage(int Id, string Name, string? Org, DateOnly? LastSeen, int Updates, int OpenProjects, int OpenWorks, bool Active);

public record UsageReport(int Days, DateOnly Since, List<OrgUsage> Orgs, OrgUsage Total, List<PersonUsage>? People);

// 사용자 정리(병합·담당 이관 후 사용 중지)·사용 현황·IP 연결 보존 정리.
// 사용자 하나를 여러 과제에 걸쳐 바꾸는 일이라 ProjectService 의 과제 단위 변경과 따로 둔다. 활동은 ActivityLog 로만 남기고
// 바꾼 작업·과제의 Version 을 올린다(열려 있던 화면이 옛 담당으로 덮어쓰지 않게)
public class UserAdminService
{
    public const string NoOrg = "소속 없음";

    // AppUser 를 가리키는 모든 참조(엔티티.속성). 병합이 이 목록을 모두 옮긴다 — 새 참조를 더하면 여기와 MergeAsync 에 함께 더한다.
    // 시험(UserAdminTests)이 EF 모델의 FK 와 FK 없는 참조(OwnerId 등)를 기계적으로 찾아 이 목록과 맞는지 본다
    public static readonly string[] References =
    {
        "UserAddress.UserId", "Project.OwnerId", "Project.CreatedById", "Project.DeletedById", "CheckItem.DoneById",
        "WorkItem.OwnerId", "WorkItem.CoOwnerId", "Attachment.UploadedById", "Activity.UserId", "ProjectVisit.UserId",
        "ProjectWatch.UserId", "Notification.UserId", "ActivityMention.UserId", "Baseline.UserId", "MasterChange.UserId",
        "ProjectSnapshot.OwnerId",
    };

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;
    private readonly ActivityLog _log;
    private readonly MasterLog _changes;

    public UserAdminService(AppDb db, IClock clock, CurrentUser me, ActivityLog? log = null, MasterLog? changes = null)
    {
        _db = db;
        _clock = clock;
        _me = me;
        _log = log ?? new ActivityLog(db, clock, me);
        _changes = changes ?? new MasterLog(db, clock, me);
    }

    // ── 병합 ──────────────────────────────────────────────────────────

    // 이름 오타로 같은 사람이 둘이 된 경우 from 을 into 로 합친다. 한 번의 SaveChanges 로 from 을 가리키는 모든 행을 옮기고 from 을 지운다
    // (이름이 유일하므로 지워야 오타 이름이 자동완성·담당 입력에 다시 나오지 않는다. 기록은 into 에 모두 남는다).
    // 유일 색인이 걸린 개인 행(방문·관심·멘션)이 겹치면 into 쪽 하나로 합친다. 관리자 권한은 합집합, 메일·소속은 into 에 없을 때만 가져온다
    public async Task<UserMoveResult> MergeAsync(MergeInput input)
    {
        RequireAdmin();
        if (input.FromId == input.IntoId) throw new RuleException("같은 사용자끼리는 합칠 수 없습니다.", "intoId");
        if (input.FromId == _me.Id) throw new RuleException("지금 이 화면을 쓰는 사용자는 다른 사용자로 합칠 수 없습니다. 다른 관리자가 합쳐 주세요.", "fromId");
        var reason = Reason(input.Reason);
        var from = await _db.Users.FirstOrDefaultAsync(u => u.Id == input.FromId) ?? throw NotFound();
        var into = await _db.Users.FirstOrDefaultAsync(u => u.Id == input.IntoId) ?? throw new RuleException("합칠 사용자를 다시 골라 주세요.", "intoId");
        var f = from.Id;
        var t = into.Id;
        var touched = new Dictionary<int, int>();       // 과제 Id → 옮긴 행 수 (과제마다 활동 한 건)
        void Touch(int projectId) => touched[projectId] = touched.GetValueOrDefault(projectId) + 1;
        var rows = 0;
        var projectCount = 0;
        var workCount = 0;

        // 과제: 담당·등록자·지운 사람 (휴지통 과제 포함 — 복원하면 그대로 보여야 한다). 담당이 바뀐 과제는 Version 을 올린다
        foreach (var p in await _db.Projects.Where(p => p.OwnerId == f || p.CreatedById == f || p.DeletedById == f).ToListAsync())
        {
            if (p.OwnerId == f) { p.OwnerId = t; p.Version++; projectCount++; }
            if (p.CreatedById == f) p.CreatedById = t;
            if (p.DeletedById == f) p.DeletedById = t;
            Touch(p.Id);
            rows++;
        }
        // 작업: 담당·부담당. 담당과 부담당이 같아지면 부담당을 비운다(작업 저장과 같은 규칙)
        foreach (var w in await _db.WorkItems.Include(w => w.Stage).Where(w => w.OwnerId == f || w.CoOwnerId == f).ToListAsync())
        {
            if (w.OwnerId == f) w.OwnerId = t;
            if (w.CoOwnerId == f) w.CoOwnerId = t;
            if (w.CoOwnerId == w.OwnerId) w.CoOwnerId = null;
            w.Version++;
            workCount++;
            Touch(w.Stage!.ProjectId);
            rows++;
        }
        foreach (var c in await _db.CheckItems.Include(c => c.Stage).Where(c => c.DoneById == f).ToListAsync())
        {
            c.DoneById = t;
            Touch(c.Stage!.ProjectId);
            rows++;
        }
        foreach (var a in await _db.Attachments.Where(a => a.UploadedById == f).ToListAsync())
        {
            a.UploadedById = t;
            Touch(a.ProjectId);
            rows++;
        }
        foreach (var a in await _db.Activities.Where(a => a.UserId == f).ToListAsync())
        {
            a.UserId = t;
            Touch(a.ProjectId);
            rows++;
        }
        foreach (var b in await _db.Baselines.Where(b => b.UserId == f).ToListAsync())
        {
            b.UserId = t;
            Touch(b.ProjectId);
            rows++;
        }

        // 개인 행: IP 연결(지우면 cascade 로 사라지므로 꼭 옮긴다)·알림·관리 변경 이력·스냅샷 담당(FK 없는 참조)
        foreach (var a in await _db.UserAddresses.Where(a => a.UserId == f).ToListAsync()) { a.UserId = t; rows++; }
        foreach (var n in await _db.Notifications.Where(n => n.UserId == f).ToListAsync()) { n.UserId = t; rows++; }
        foreach (var c in await _db.MasterChanges.Where(c => c.UserId == f).ToListAsync()) { c.UserId = t; rows++; }
        foreach (var s in await _db.ProjectSnapshots.Where(s => s.OwnerId == f).ToListAsync()) { s.OwnerId = t; rows++; }

        // 유일 색인이 있는 개인 행: 겹치면 into 쪽에 합치고 from 쪽을 지운다
        var visits = await _db.ProjectVisits.Where(v => v.UserId == f || v.UserId == t).ToListAsync();
        foreach (var v in visits.Where(v => v.UserId == f).ToList())
        {
            var mine = visits.FirstOrDefault(x => x.UserId == t && x.ProjectId == v.ProjectId);
            if (mine == null) v.UserId = t;
            else
            {
                if (v.LastSeenAt > mine.LastSeenAt) mine.LastSeenAt = v.LastSeenAt;
                _db.ProjectVisits.Remove(v);
            }
            rows++;
        }
        var watches = await _db.ProjectWatches.Where(w => w.UserId == f || w.UserId == t).ToListAsync();
        foreach (var w in watches.Where(w => w.UserId == f).ToList())
        {
            var mine = watches.FirstOrDefault(x => x.UserId == t && x.ProjectId == w.ProjectId);
            if (mine == null) w.UserId = t;
            else
            {
                if (w.CreatedAt < mine.CreatedAt) mine.CreatedAt = w.CreatedAt;
                _db.ProjectWatches.Remove(w);
            }
            rows++;
        }
        var mentions = await _db.ActivityMentions.Where(m => m.UserId == f || m.UserId == t).ToListAsync();
        foreach (var m in mentions.Where(m => m.UserId == f).ToList())
        {
            if (mentions.Any(x => x.UserId == t && x.ActivityId == m.ActivityId)) _db.ActivityMentions.Remove(m);
            else m.UserId = t;
            rows++;
        }

        // 사용자 속성: 관리자 권한은 합집합(관리자를 잃지 않게), 메일·소속은 into 에 없을 때만
        if (from.IsAdmin) into.IsAdmin = true;
        into.Email ??= from.Email;
        into.OrgUnitId ??= from.OrgUnitId;

        var text = $"사용자 병합: {from.Name} → {into.Name}";
        var batch = Guid.NewGuid();
        // 지워질 from 에게 갈 알림은 into 에게 보낸다(같은 저장에서 지워지는 사용자를 가리키는 알림이 생기지 않게)
        _log.Redirect(f, t);
        foreach (var projectId in touched.Keys)
        {
            var a = _log.New(ActivityType.Edited, text, reason, oldValue: from.Name, newValue: into.Name, batch: batch);
            a.ProjectId = projectId;
            _db.Activities.Add(a);
        }
        var summary = $"과제 {touched.Count} · 담당 과제 {projectCount} · 작업 {workCount} · 옮긴 기록 {rows}";
        _changes.Add("user", t, $"{text} ({summary})" + (reason == null ? "" : $" — {reason}"), from.Name, into.Name);
        _db.Users.Remove(from);
        await SaveAsync();
        return new UserMoveResult(projectCount, workCount, rows, $"{from.Name} 님을 {into.Name} 님으로 합쳤습니다. ({summary})");
    }

    // ── 담당 이관 후 사용 중지 ──────────────────────────────────────

    // 퇴사·이동한 사람의 진행 과제 담당과 미완료 작업 담당·부담당을 다른 사람에게 넘기고 사용 중지한다.
    // 한 묶음(BatchId)으로 활동을 남기고 받는 사람에게 알린다. 맡은 것이 없으면 바로 사용 중지한다
    public async Task<UserMoveResult> ReassignAsync(ReassignInput input)
    {
        RequireAdmin();
        if (input.FromId == _me.Id) throw new RuleException("자기 자신은 사용 중지할 수 없습니다.");
        var reason = Reason(input.Reason);
        var from = await _db.Users.FirstOrDefaultAsync(u => u.Id == input.FromId) ?? throw NotFound();
        AppUser? to = null;
        if (input.ToId is int toId)
        {
            if (toId == from.Id) throw new RuleException("다른 사람을 골라 주세요.", "toId");
            to = await _db.Users.FirstOrDefaultAsync(u => u.Id == toId) ?? throw new RuleException("넘겨받을 사람을 다시 골라 주세요.", "toId");
            if (!to.IsActive) throw new RuleException($"'{to.Name}' 은(는) 사용이 중지된 사용자입니다. 다른 사람을 골라 주세요.", "toId");
        }

        var projects = !input.Projects ? new List<Project>()
            : (await _db.LiveProjects.Where(p => p.OwnerId == from.Id).ToListAsync()).Where(p => Rules.IsOpenProject(p.State)).ToList();
        var works = !input.Works ? new List<WorkItem>() : await OpenWorks(_db.WorkItems).Include(w => w.Stage)
            .Where(w => w.OwnerId == from.Id || w.CoOwnerId == from.Id).ToListAsync();
        if ((projects.Count > 0 || works.Count > 0) && to == null) throw new RuleException("넘겨받을 사람을 골라 주세요.", "toId");

        var batch = Guid.NewGuid();
        foreach (var p in projects)
        {
            p.OwnerId = to!.Id;
            p.Version++;
            var a = _log.New(ActivityType.Edited, $"담당 이관: {from.Name} → {to.Name} (사용 중지)", reason, oldValue: from.Name, newValue: to.Name, batch: batch);
            a.ProjectId = p.Id;
            _db.Activities.Add(a);
            _log.Involve(a, to.Id, NotifyKinds.Project);
        }
        foreach (var w in works)
        {
            var what = w.OwnerId == from.Id ? "담당" : "부담당";
            if (w.OwnerId == from.Id) w.OwnerId = to!.Id;
            if (w.CoOwnerId == from.Id) w.CoOwnerId = to!.Id;
            // 넘겨받는 사람이 이미 부담당(또는 담당)이면 같은 사람이 둘이 된다 — 부담당을 비운다(작업 저장과 같은 규칙)
            if (w.CoOwnerId == w.OwnerId) w.CoOwnerId = null;
            w.Version++;
            var a = _log.New(ActivityType.WorkEdited, $"[{w.Title}] {what} 이관: {from.Name} → {to!.Name} (사용 중지)", reason,
                stageId: w.StageId, workId: w.Id, oldValue: from.Name, newValue: to.Name, batch: batch);
            a.ProjectId = w.Stage!.ProjectId;
            _db.Activities.Add(a);
            _log.Involve(a, to.Id, NotifyKinds.Work);
        }

        var wasActive = from.IsActive;
        from.IsActive = false;
        var summary = $"과제 {projects.Count} · 작업 {works.Count}";
        var text = to == null || projects.Count + works.Count == 0
            ? $"사용 중지: {from.Name} (맡은 진행 과제·작업 없음)"
            : $"담당 이관 후 사용 중지: {from.Name} → {to.Name} ({summary})";
        if (wasActive || projects.Count + works.Count > 0)
            _changes.Add("user", from.Id, text + (reason == null ? "" : $" — {reason}"), wasActive ? "사용" : "사용 중지", to?.Name);
        await SaveAsync();
        return new UserMoveResult(projects.Count, works.Count, projects.Count + works.Count,
            projects.Count + works.Count == 0 ? $"{from.Name} 님을 사용 중지했습니다." : $"{summary}을(를) {to!.Name} 님에게 넘기고 {from.Name} 님을 사용 중지했습니다.");
    }

    // 미완료 작업: 실적 종료가 없고 단계가 끝나지 않았으며(Rules.IsOpen) 과제가 휴지통이 아닌 진행·보류 과제
    private static IQueryable<WorkItem> OpenWorks(IQueryable<WorkItem> q) =>
        q.Where(w => w.ActualEnd == null && w.Stage!.CompletedDate == null && w.Stage.Project!.DeletedAt == null
                     && (w.Stage.Project.State == ProjectState.Active || w.Stage.Project.State == ProjectState.Hold));

    // 사용자별 진행 과제 담당 수·미완료 작업 담당/부담당 수 (관리 > 사용자 목록). 두 쿼리를 메모리에서 센다
    public static async Task<Dictionary<int, UserLoad>> LoadsAsync(AppDb db)
    {
        var owners = (await db.LiveProjects.AsNoTracking().Select(p => new { p.OwnerId, p.State }).ToListAsync())
            .Where(p => Rules.IsOpenProject(p.State)).GroupBy(p => p.OwnerId).ToDictionary(g => g.Key, g => g.Count());
        var works = await OpenWorks(db.WorkItems.AsNoTracking()).Select(w => new { w.OwnerId, w.CoOwnerId }).ToListAsync();
        var workCounts = new Dictionary<int, int>();
        foreach (var w in works)
        {
            if (w.OwnerId is int o) workCounts[o] = workCounts.GetValueOrDefault(o) + 1;
            if (w.CoOwnerId is int c && c != w.OwnerId) workCounts[c] = workCounts.GetValueOrDefault(c) + 1;
        }
        return owners.Keys.Concat(workCounts.Keys).Distinct()
            .ToDictionary(id => id, id => new UserLoad(owners.GetValueOrDefault(id), workCounts.GetValueOrDefault(id)));
    }

    public async Task<UserLoad> LoadAsync(int userId) =>
        (await LoadsAsync(_db)).GetValueOrDefault(userId) ?? new UserLoad(0, 0);

    // ── 사용 현황 ────────────────────────────────────────────────────

    // 조직별 최근 접속자 수·갱신 건수·조용한 진행 과제 비율. 개인 감시가 아니라 조직 집계를 먼저 보인다 — 사람별 마지막 접속은 관리자에게만 준다
    public async Task<UsageReport> UsageAsync(int days = Rules.UsageDays)
    {
        var now = _clock.UtcNow;
        var since = now.AddDays(-days);
        var users = await _db.Users.AsNoTracking().Include(u => u.OrgUnit).ThenInclude(o => o!.Parent).ToListAsync();
        var lastSeen = (await _db.UserAddresses.AsNoTracking().Select(a => new { a.UserId, a.LastSeenAt }).ToListAsync())
            .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Max(a => a.LastSeenAt));
        var updates = (await _db.LiveActivities.AsNoTracking().Where(a => a.At >= since)
                .GroupBy(a => a.UserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.Count);
        var projects = (await _db.LiveProjects.AsNoTracking().Select(p => new { p.OwnerId, p.State, p.UpdatedAt }).ToListAsync())
            .Where(p => p.State == ProjectState.Active).ToList();
        var loads = await LoadsAsync(_db);

        var orgOf = users.ToDictionary(u => u.Id, u => Views.OrgName(u.OrgUnit) ?? NoOrg);
        // 조직 순서: 관리 화면의 조직 순서(그룹 → 파트), 소속 없음은 끝
        var orgOrder = users.Where(u => u.OrgUnit != null)
            .Select(u => (Name: orgOf[u.Id], Key: (u.OrgUnit!.Parent?.SortOrder ?? u.OrgUnit.SortOrder, u.OrgUnit.Parent == null ? 0 : u.OrgUnit.SortOrder)))
            .GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Key);

        bool Recent(int userId) => lastSeen.TryGetValue(userId, out var at) && at >= since;
        OrgUsage Row(string name, IEnumerable<AppUser> members)
        {
            var list = members.ToList();
            var active = list.Where(u => u.IsActive).ToList();
            var ids = list.Select(u => u.Id).ToHashSet();
            var owned = projects.Where(p => ids.Contains(p.OwnerId)).ToList();
            return new OrgUsage(name, active.Count, active.Count(u => Recent(u.Id)), list.Sum(u => updates.GetValueOrDefault(u.Id)),
                owned.Count, owned.Count(p => Rules.IsQuiet(p.UpdatedAt, now, days)));
        }

        var orgs = users.GroupBy(u => orgOf[u.Id])
            .OrderBy(g => g.Key == NoOrg ? 1 : 0).ThenBy(g => orgOrder.TryGetValue(g.Key, out var k) ? k : (int.MaxValue, 0)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Row(g.Key, g))
            // 사람도 과제도 기록도 없는 줄은 뺀다(사용 중지된 사람만 남은 조직 등)
            .Where(r => r.Users > 0 || r.OpenProjects > 0 || r.Updates > 0)
            .ToList();
        var total = Row("전체", users);

        List<PersonUsage>? people = null;
        if (_me.IsAdmin)
        {
            people = users
                .Select(u => new PersonUsage(u.Id, u.Name, Views.OrgName(u.OrgUnit), lastSeen.TryGetValue(u.Id, out var at) ? KoreaClock.KstDate(at) : null,
                    updates.GetValueOrDefault(u.Id), loads.GetValueOrDefault(u.Id)?.Projects ?? 0, loads.GetValueOrDefault(u.Id)?.Works ?? 0, u.IsActive))
                .OrderByDescending(p => p.Active).ThenByDescending(p => p.LastSeen ?? DateOnly.MinValue).ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }
        return new UsageReport(days, KoreaClock.KstDate(since), orgs, total, people);
    }

    // ── IP 연결 보존 정리 ───────────────────────────────────────────

    // keepDays 넘게 접속이 없던 IP 연결을 지운다(배경 작업 — 현재 사용자 없음). 지운 것마다 관리 변경 이력에 남긴다(행위자: 서버)
    public async Task<int> PruneAddressesAsync(int keepDays)
    {
        if (keepDays <= 0) return 0;
        var cutoff = _clock.UtcNow.AddDays(-keepDays);
        var old = await _db.UserAddresses.Include(a => a.User).Where(a => a.LastSeenAt < cutoff).ToListAsync();
        foreach (var a in old)
        {
            _db.UserAddresses.Remove(a);
            _changes.Add("ip", a.UserId, $"IP 연결 정리: {a.User?.Name} — {a.Ip} ({keepDays}일 넘게 접속 없음, 마지막 {KoreaClock.KstDate(a.LastSeenAt):yyyy-MM-dd})",
                a.Ip, null);
        }
        if (old.Count > 0) await _db.SaveChangesAsync();
        return old.Count;
    }

    // ── 공용 ─────────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // 그 사이 누가 과제·작업을 고쳤다 — 아무것도 바뀌지 않았으니 다시 하면 된다
            throw new RuleException("그 사이 다른 사람이 과제나 작업을 고쳤습니다. 다시 시도해 주세요.");
        }
    }

    private static string? Reason(string? reason)
    {
        var r = (reason ?? "").Trim();
        if (r.Length > Limits.Reason) throw new RuleException($"사유는 {Limits.Reason}자 이내로 입력해 주세요.", "reason");
        return r.Length == 0 ? null : r;
    }

    private void RequireAdmin()
    {
        if (!_me.IsAdmin) throw new RuleException("관리자만 사용자를 정리할 수 있습니다.");
    }

    private static RuleException NotFound() => new("사용자를 찾을 수 없습니다. 화면을 새로고침해 주세요.");
}

// 하루 한 번 보존 기간(Access:AddressKeepDays)이 지난 IP 연결을 지운다. 0 이하면 끈다
public class AddressRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AccessOptions _access;
    private readonly ILogger<AddressRetentionWorker> _log;

    public AddressRetentionWorker(IServiceScopeFactory scopes, AccessOptions access, ILogger<AddressRetentionWorker> log)
    {
        _scopes = scopes;
        _access = access;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_access.AddressKeepDays <= 0) return;
        try
        {
            // 시작 직후의 요청·다른 시작 작업과 겹치지 않게 기다린 뒤, 하루마다
            await Task.Delay(TimeSpan.FromMinutes(7), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "IP 연결 보존 정리 실패");
                }
                await Task.Delay(TimeSpan.FromDays(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }

    public async Task<int> RunOnceAsync()
    {
        await using var scope = _scopes.CreateAsyncScope();
        var n = await scope.ServiceProvider.GetRequiredService<UserAdminService>().PruneAddressesAsync(_access.AddressKeepDays);
        if (n > 0) _log.LogInformation("IP 연결 보존 정리: {Count}건 ({Days}일 넘게 접속 없음)", n, _access.AddressKeepDays);
        return n;
    }
}
