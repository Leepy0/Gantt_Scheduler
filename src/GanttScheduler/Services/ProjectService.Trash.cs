using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 휴지통 목록 한 줄 (관리 > 휴지통)
// Version 은 복원·영구 삭제 때 화면이 다시 보낸다 — 낡은 휴지통 화면이 그 사이 복원·수정 후 다시 지워진 다른 삭제 건을 확인 없이 지우지 않게
public record TrashItem(int Id, string Title, string Owner, string? DeletedBy, DateTime DeletedAt, string? Reason, int Stages, int Attachments, int Version = 0);

// 영구 삭제 기록 한 줄
public record PurgeLogView(int ProjectId, string Title, string? DeletedBy, DateTime? DeletedAt, string? Reason, DateTime PurgedAt, string PurgedBy);

// 휴지통: 지운 과제의 목록·복원·영구 삭제. 휴지통으로 보내기는 ProjectService.DeleteAsync.
// 여기만 지운 과제를 읽는다(_db.Projects 를 그대로 쓴다) — 다른 조회는 모두 _db.LiveProjects 를 거친다
public partial class ProjectService
{
    // 자동 정리 기록의 '영구 삭제한 사람' 자리
    public const string AutoPurgeBy = "자동 정리(보관 기간 경과)";

    public async Task<List<TrashItem>> TrashAsync()
    {
        var rows = await _db.Projects.AsNoTracking().Where(p => p.DeletedAt != null)
            .Select(p => new
            {
                p.Id, p.Title, Owner = p.Owner!.Name, DeletedBy = p.DeletedBy!.Name, p.DeletedAt, p.DeleteReason,
                Stages = p.Stages.Count, Attachments = p.Attachments.Count, p.Version,
            })
            .ToListAsync();
        return rows.OrderByDescending(r => r.DeletedAt).ThenByDescending(r => r.Id)
            .Select(r => new TrashItem(r.Id, r.Title, r.Owner ?? "", r.DeletedBy, r.DeletedAt!.Value, r.DeleteReason, r.Stages, r.Attachments, r.Version)).ToList();
    }

    // 최근 영구 삭제 기록 (최신 순)
    public async Task<List<PurgeLogView>> PurgeLogAsync(int take = 50) =>
        (await _db.DeletedProjectLogs.AsNoTracking().OrderByDescending(l => l.PurgedAt).ThenByDescending(l => l.Id).Take(take).ToListAsync())
            .Select(l => new PurgeLogView(l.ProjectId, l.Title, l.DeletedBy, l.DeletedAt, l.Reason, l.PurgedAt, l.PurgedBy)).ToList();

    // 보관 기간이 지난 과제 (Id 와 지운 사람 — 자동 정리가 그 사람 이름으로 상대 과제에 기록을 남긴다)
    public async Task<List<(int Id, int? DeletedById)>> ExpiredAsync(DateTime cutoffUtc) =>
        (await _db.Projects.AsNoTracking().Where(p => p.DeletedAt != null && p.DeletedAt < cutoffUtc)
            .Select(p => new { p.Id, p.DeletedById }).ToListAsync())
            .Select(p => (p.Id, p.DeletedById)).ToList();

    // 휴지통에서 되살린다. 지운 권한이 있는 사람(담당자·등록자·관리자)만. 단계·작업·첨부·활동·과제 간 연결은 그대로 남아 있었으므로 표시만 지운다
    // version 은 화면이 본 과제 Version (없으면 확인하지 않는다)
    public async Task RestoreAsync(int id, int? version = null)
    {
        var p = await _db.Projects.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null)
            ?? throw new RuleException("휴지통에서 과제를 찾을 수 없습니다. 이미 복원했거나 영구 삭제했을 수 있습니다.");
        CheckVersion(version, p.Version, p.Id);
        if (!Rules.CanDeleteProject(p, _me.User?.Id, _me.IsAdmin))
            throw new RuleException("과제 복원은 담당자·등록자 또는 관리자만 할 수 있습니다.");
        var when = ProjectService.Fmt(KoreaClock.KstDate(p.DeletedAt!.Value));
        p.DeletedAt = null;
        p.DeletedById = null;
        p.DeleteReason = null;
        p.Activities.Add(NewActivity(ActivityType.ProjectRestored, $"과제 복원 (휴지통에서 되살림 — {when} 삭제)"));
        p.Version++;
        Touch(p);
        await _db.SaveChangesAsync();
    }

    // 휴지통의 과제를 영구 삭제한다(되돌릴 수 없다). 관리자만 — auto 면 보관 기간이 지난 자동 정리(권한 검사 없음, 행위자는 지운 사람).
    // 과제 행과 의존 관계만 읽고 단계·작업·체크·첨부·활동·기준선 차수·방문·관심은 DB cascade 가 지운다(그래프를 읽으면 EF 가 행마다 DELETE 를 보낸다).
    // 과제·활동이 사라지므로 '누가 지웠고 언제 영구 삭제됐는지'를 DeletedProjectLog 에 남긴다. 첨부 파일 폴더는 부른 쪽이 지운다(TrashService)
    // version 은 화면이 본 과제 Version — 수동 영구 삭제는 관리자가 본 그 삭제 건일 때만 지운다(자동 정리는 넘기지 않는다)
    public async Task<PurgeLogView> PurgeAsync(int id, bool auto = false, int? version = null)
    {
        if (!auto && !_me.IsAdmin) throw new RuleException("영구 삭제는 관리자만 할 수 있습니다.");
        var p = await _db.Projects.Include(x => x.Links).Include(x => x.DeletedBy).FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null)
            ?? throw new RuleException("휴지통에서 과제를 찾을 수 없습니다. 이미 복원했거나 영구 삭제했을 수 있습니다.");
        CheckVersion(version, p.Version, p.Id);
        var log = new DeletedProjectLog
        {
            ProjectId = p.Id, Title = p.Title, DeletedBy = p.DeletedBy?.Name, DeletedAt = p.DeletedAt, Reason = p.DeleteReason,
            PurgedAt = _clock.UtcNow, PurgedBy = auto ? AutoPurgeBy : _me.Name,
        };
        // 의존 관계는 작업보다 먼저 지워야 한다 (작업 쪽 FK 가 Restrict). SQL Server 는 한 문장 안의 cascade 와 NO ACTION 검사 순서가
        // 얽힐 수 있어, cascade 로도 지워질 의존 관계를 먼저 따로 지운다(같은 SaveChanges 에서 과제 행보다 앞서 나간다).
        // 과제 간 단계 연결도 단계 쪽 FK 가 Restrict 라 먼저 지운다(상대 과제에는 기록). 단계는 읽지 않았으므로 Id 만 따로 읽는다
        _db.WorkLinks.RemoveRange(p.Links);
        p.Links.Clear();
        await RemoveStageLinksAsync(p, await _db.Stages.Where(s => s.ProjectId == p.Id).Select(s => s.Id).ToListAsync(), removedProject: true);
        _db.Projects.Remove(p);
        _db.DeletedProjectLogs.Add(log);
        await _db.SaveChangesAsync();
        return new PurgeLogView(log.ProjectId, log.Title, log.DeletedBy, log.DeletedAt, log.Reason, log.PurgedAt, log.PurgedBy);
    }
}
