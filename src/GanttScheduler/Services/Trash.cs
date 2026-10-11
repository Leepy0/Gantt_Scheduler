using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 휴지통 설정. KeepDays: 지운 과제를 보관하는 날 수(기본 30). 0 이하면 자동으로 영구 삭제하지 않는다(관리 화면에서 직접만)
public sealed record TrashOptions(int KeepDays)
{
    public const int DefaultKeepDays = 30;

    public static TrashOptions From(IConfiguration cfg) => new(cfg.GetValue("Trash:KeepDays", DefaultKeepDays));

    public bool AutoPurge => KeepDays > 0;

    // 이 시각 전에 지운 과제가 정리 대상이다
    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddDays(-KeepDays);

    // 화면에 보일 '자동 영구 삭제 예정 시각' (자동 정리를 끈 서버는 null)
    public DateTime? PurgeAt(DateTime deletedAtUtc) => AutoPurge ? deletedAtUtc.AddDays(KeepDays) : null;
}

// 휴지통 화면 한 줄 = 과제 + 자동 영구 삭제 예정 시각
public record TrashRow(TrashItem Item, DateTime? PurgeAt);

// 휴지통 작업 묶음: 영구 삭제는 DB 기록(ProjectService.PurgeAsync)과 첨부 폴더 정리, 파일 로그를 함께 한다.
// 관리 화면과 자동 정리(TrashWorker)가 같은 길을 쓴다
public class TrashService
{
    private readonly ProjectService _projects;
    private readonly AttachmentService _files;
    private readonly TrashOptions _options;
    private readonly ILogger _log;

    public TrashService(ProjectService projects, AttachmentService files, TrashOptions options, ILogger<TrashService>? log = null)
    {
        _projects = projects;
        _files = files;
        _options = options;
        _log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public TrashOptions Options => _options;

    public async Task<List<TrashRow>> ListAsync() =>
        (await _projects.TrashAsync()).Select(t => new TrashRow(t, _options.PurgeAt(t.DeletedAt))).ToList();

    public Task<List<PurgeLogView>> LogAsync() => _projects.PurgeLogAsync();

    public Task RestoreAsync(int id, int? version = null) => _projects.RestoreAsync(id, version);

    // 영구 삭제: DB 를 먼저 커밋하고 첨부 폴더를 지운다(폴더를 못 지워도 경고만 — 서버 상태의 고아 파일 정리가 나중에 치운다).
    // 활동까지 사라지므로 파일 로그에 한 줄을 남긴다(관리 변경 이력이 따로 없어서)
    public async Task<PurgeLogView> PurgeAsync(int id, bool auto = false, int? version = null)
    {
        var done = await _projects.PurgeAsync(id, auto, version);
        _files.RemoveProjectFiles(id);
        _log.LogWarning("과제 영구 삭제: #{Id} {Title} — 삭제 {DeletedBy} {DeletedAt:yyyy-MM-dd HH:mm}Z, 영구 삭제 {PurgedBy}",
            done.ProjectId, done.Title, done.DeletedBy ?? "-", done.DeletedAt, done.PurgedBy);
        return done;
    }
}

// 하루 한 번, 보관 기간이 지난 휴지통 과제를 영구 삭제한다. 요청이 없는 배경 작업이라 '현재 사용자'가 없다 —
// 과제마다 범위를 새로 만들고 그 과제를 지운 사람을 행위자로 둔다(상대 과제에 남는 '연결 해제' 기록의 주인).
// 정리 기록(DeletedProjectLog)의 영구 삭제자는 '자동 정리'로 남는다
public class TrashWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TrashOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<TrashWorker> _log;

    public TrashWorker(IServiceScopeFactory scopes, TrashOptions options, IClock clock, ILogger<TrashWorker> log)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.AutoPurge) return;
        try
        {
            // 시작 직후의 요청 처리·다른 시작 작업과 겹치지 않게 기다린 뒤, 하루마다
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "휴지통 자동 정리 실패");
                }
                await Task.Delay(TimeSpan.FromDays(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 서버 종료 */ }
    }

    // 한 번 정리: 영구 삭제한 과제 수. 한 과제가 실패해도 나머지는 계속한다(다음 날 다시 시도)
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.AutoPurge) return 0;
        List<(int Id, int? DeletedById)> expired;
        await using (var scope = _scopes.CreateAsyncScope())
            expired = await scope.ServiceProvider.GetRequiredService<ProjectService>().ExpiredAsync(_options.CutoffUtc(_clock.UtcNow));
        var purged = 0;
        foreach (var (id, deletedById) in expired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDb>();
                var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == deletedById, ct)
                    ?? await db.Projects.Where(p => p.Id == id).Select(p => p.Owner).FirstOrDefaultAsync(ct);
                if (actor == null) continue;
                sp.GetRequiredService<CurrentUser>().User = actor;
                await sp.GetRequiredService<TrashService>().PurgeAsync(id, auto: true);
                purged++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "휴지통 자동 정리: 과제 #{Id} 를 영구 삭제하지 못했습니다", id);
            }
        }
        if (purged > 0) _log.LogInformation("휴지통 자동 정리: {Count}건 영구 삭제 (보관 {Days}일)", purged, _options.KeepDays);
        return purged;
    }
}
