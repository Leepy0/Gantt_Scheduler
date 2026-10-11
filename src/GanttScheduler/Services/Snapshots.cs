using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 일별 진척 스냅샷 설정. Hour: 한국 시각 이 시 이후에 그날 것을 한 번 더 남긴다(그날의 마지막 모습에 가깝게 — 같은 날은 덮어쓴다)
public record SnapshotOptions(bool Enabled, int Hour)
{
    public static SnapshotOptions From(IConfiguration cfg) =>
        new(cfg.GetValue("Snapshot:Enabled", true), Math.Clamp(cfg.GetValue("Snapshot:Hour", 18), 0, 23));
}

// 스냅샷 쓰기. 과제·단계·작업은 읽기만 하고 스냅샷 표만 쓴다 — 활동을 남기지 않는다(사람의 변경이 아니라 기록이다)
public class SnapshotService
{
    private readonly AppDb _db;
    private readonly ProjectService _projects;
    private readonly IClock _clock;

    public SnapshotService(AppDb db, ProjectService projects, IClock clock)
    {
        _db = db;
        _projects = projects;
        _clock = clock;
    }

    public Task<bool> HasTodayAsync()
    {
        var today = _clock.Today;
        return _db.ProjectSnapshots.AnyAsync(s => s.Date == today);
    }

    // 오늘(한국 날짜) 것을 남긴다. 같은 날 다시 돌면 덮어쓰고, 그사이 대상에서 빠진 과제(중단·휴지통)의 오늘 행은 지운다.
    // 대상: 휴지통이 아닌 진행·보류 과제와 오늘 완료된 과제(Rules.InSnapshot). 값은 대시보드와 같은 계산(Dashboard.ViewOf, 위험 포함)이다
    public async Task<int> TakeAsync()
    {
        var today = _clock.Today;
        var all = await _projects.AllAsync(q => q.Where(p => p.State == ProjectState.Active || p.State == ProjectState.Hold
            || (p.State == ProjectState.Done && p.Stages.Any(s => s.CompletedDate == today))), withLinks: true);
        var risk = new RiskContext(await _projects.CalendarAsync(), await _projects.AllCrossLinksAsync(), _clock.UtcNow);
        var locations = await _projects.LocationsAsync();
        var rows = all.Where(p => Rules.InSnapshot(p, today))
            .Select(p => Reporting.Snapshot(p, Dashboard.ViewOf(p, locations, today, risk), today))
            .ToDictionary(s => s.ProjectId);

        var existing = await _db.ProjectSnapshots.Where(s => s.Date == today).ToListAsync();
        foreach (var e in existing)
        {
            if (!rows.Remove(e.ProjectId, out var fresh)) { _db.ProjectSnapshots.Remove(e); continue; }
            Reporting.CopySnapshot(fresh, e);
        }
        foreach (var s in rows.Values) _db.ProjectSnapshots.Add(s);
        await _db.SaveChangesAsync();
        return await _db.ProjectSnapshots.CountAsync(s => s.Date == today);
    }
}

// 하루 한 번 스냅샷을 남긴다. 시작 직후 오늘 것이 없으면 바로 한 번, 그 뒤로는 정한 시각(Snapshot:Hour) 이후 한 번 더(덮어쓰기).
// 서버가 꺼져 있던 날은 건너뛴다 — 지난 날짜를 지금 값으로 채우면 그날의 모습이 아니다
public class SnapshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SnapshotOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<SnapshotWorker> _log;
    private DateTime? _lastRunKst;

    public SnapshotWorker(IServiceScopeFactory scopes, SnapshotOptions options, IClock clock, ILogger<SnapshotWorker> log)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        try
        {
            // 시작 작업(DB 준비)과 첫 요청들이 지나간 뒤
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "진척 스냅샷 기록 실패");
                }
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }

    // 한 번 확인: 때가 됐으면 남기고 남긴 과제 수를, 아니면 null
    public async Task<int?> TickAsync()
    {
        await using var scope = _scopes.CreateAsyncScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<SnapshotService>();
        var now = KoreaClock.ToKst(_clock.UtcNow);
        if (!Due(_options, now, _lastRunKst, await snapshots.HasTodayAsync())) return null;
        var count = await snapshots.TakeAsync();
        _lastRunKst = now;
        _log.LogInformation("진척 스냅샷: {Date} 과제 {Count}건", DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"), count);
        return count;
    }

    // 오늘 것이 없으면 바로. 있으면 정한 시각이 지났고 그 뒤로 아직 남기지 않았을 때 한 번 더(그날 마지막 모습으로 덮어쓰기)
    public static bool Due(SnapshotOptions o, DateTime kstNow, DateTime? lastRunKst, bool hasToday)
    {
        if (!o.Enabled) return false;
        if (!hasToday) return true;
        var slot = kstNow.Date.AddHours(o.Hour);
        return kstNow >= slot && (lastRunKst == null || lastRunKst.Value < slot);
    }
}
