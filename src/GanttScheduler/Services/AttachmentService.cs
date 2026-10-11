using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 과제 첨부 파일 — 파일은 디스크(Storage:Path/과제번호/)에 임의 이름으로 저장하고, 원래 이름은 DB 에 둔다
public class AttachmentService
{
    public const long MaxBytes = 30L * 1024 * 1024;

    // 받을 수 있는 확장자와 내려 줄 때의 형식. 브라우저에서 실행될 수 있는 형식(html, svg 등)은 받지 않는다
    public static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".pdf"] = "application/pdf",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xls"] = "application/vnd.ms-excel",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation", [".ppt"] = "application/vnd.ms-powerpoint",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [".doc"] = "application/msword",
        [".hwp"] = "application/x-hwp", [".hwpx"] = "application/hwp+zip",
        [".txt"] = "text/plain", [".csv"] = "text/csv", [".zip"] = "application/zip", [".dwg"] = "application/acad",
    };

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;
    private readonly string _root;
    private readonly ActivityLog _log;

    // log: 활동은 과제 서비스와 같은 기록(ActivityLog)을 거친다 — 첨부 활동에도 알림이 빠지지 않게
    public AttachmentService(AppDb db, IClock clock, CurrentUser me, IConfiguration cfg, IHostEnvironment env, ActivityLog? log = null)
    {
        _db = db;
        _clock = clock;
        _me = me;
        _root = AppPaths.Uploads(cfg, env.ContentRootPath);
        _log = log ?? new ActivityLog(db, clock, me);
    }

    private string FolderOf(int projectId) => Path.Combine(_root, projectId.ToString());

    public async Task<int> SaveAsync(int projectId, int? stageId, string? fileName, long length, Stream content)
    {
        fileName = Path.GetFileName((fileName ?? "").Trim());
        if (fileName.Length == 0) throw new RuleException("파일을 선택해 주세요.", "file");
        if (fileName.Length > 200) throw new RuleException("파일 이름은 200자 이내여야 합니다.", "file");
        var ext = Path.GetExtension(fileName);
        if (!Allowed.TryGetValue(ext, out var contentType))
            throw new RuleException($"올릴 수 없는 형식입니다({ext}). 이미지·PDF·Office·한글·zip 파일만 가능합니다.", "file");
        if (length <= 0) throw new RuleException("빈 파일은 올릴 수 없습니다.", "file");
        if (length > MaxBytes) throw new RuleException("파일은 30MB 이하만 올릴 수 있습니다.", "file");

        var project = await _db.Projects.Include(p => p.Stages).FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");
        var stage = stageId == null ? null : project.Stages.FirstOrDefault(s => s.Id == stageId.Value);

        var stored = Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
        Directory.CreateDirectory(FolderOf(projectId));
        var path = Path.Combine(FolderOf(projectId), stored);
        await using (var file = File.Create(path))
            await content.CopyToAsync(file);

        var now = _clock.UtcNow;
        _db.Attachments.Add(new Attachment
        {
            ProjectId = projectId, StageId = stage?.Id, FileName = fileName, StoredName = stored, ContentType = contentType,
            Size = length, UploadedById = _me.Id, UploadedAt = now,
        });
        var added = _log.New(ActivityType.AttachmentAdded, (stage == null ? "" : $"[{stage.Name}] ") + $"첨부 추가: {fileName}", stageId: stage?.Id);
        added.ProjectId = projectId;
        _db.Activities.Add(added);
        project.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            File.Delete(path);   // 기록에 실패하면 파일도 남기지 않는다
            throw;
        }
        return projectId;
    }

    // 내려받기·미리보기용: 실제 파일 경로와 정보
    public async Task<(string Path, Attachment File)?> FindAsync(int id)
    {
        var a = await _db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return null;
        var path = Path.Combine(FolderOf(a.ProjectId), a.StoredName);
        return File.Exists(path) ? (path, a) : null;
    }

    public async Task<int> DeleteAsync(int id)
    {
        var a = await _db.Attachments.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new RuleException("첨부 파일을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        if (!Rules.CanDeleteAttachment(a, a.Project?.OwnerId, _me.User?.Id, _me.IsAdmin))
            throw new RuleException("첨부 삭제는 올린 사람·과제 담당자 또는 관리자만 할 수 있습니다.");

        var now = _clock.UtcNow;
        _db.Attachments.Remove(a);
        var removed = _log.New(ActivityType.AttachmentRemoved, $"첨부 삭제: {a.FileName}", stageId: a.StageId);
        removed.ProjectId = a.ProjectId;
        _db.Activities.Add(removed);
        if (a.Project != null) a.Project.UpdatedAt = now;
        await _db.SaveChangesAsync();

        var path = Path.Combine(FolderOf(a.ProjectId), a.StoredName);
        if (File.Exists(path)) File.Delete(path);
        return a.ProjectId;
    }

    // 과제를 지운 뒤 남은 파일 정리
    public void RemoveProjectFiles(int projectId)
    {
        var dir = FolderOf(projectId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
