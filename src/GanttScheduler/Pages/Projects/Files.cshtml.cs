using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Projects;

// 첨부 파일 올리기·내려받기 (화면 없음)
[RequestSizeLimit(AttachmentService.MaxBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = AttachmentService.MaxBytes + 1024 * 1024)]
public class FilesModel : PageModel
{
    private readonly AttachmentService _files;
    private readonly ProjectService _projects;
    private readonly IClock _clock;

    public FilesModel(AttachmentService files, ProjectService projects, IClock clock)
    {
        _files = files;
        _projects = projects;
        _clock = clock;
    }

    public IActionResult OnGet() => NotFound();

    // 내려받기. inline=1 이고 미리보기 가능한 형식(이미지·PDF)이면 브라우저에서 바로 연다
    public async Task<IActionResult> OnGetDownloadAsync(int id, bool inline = false)
    {
        var found = await _files.FindAsync(id);
        if (found == null) return NotFound();
        var (path, file) = found.Value;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var stream = System.IO.File.OpenRead(path);
        if (inline && Views.PreviewTypes.Contains(file.ContentType))
            return File(stream, file.ContentType);
        return File(stream, file.ContentType, file.FileName);
    }

    public async Task<IActionResult> OnPostUploadAsync(int projectId, int? stageId, IFormFile? file)
    {
        try
        {
            if (file == null) throw new RuleException("파일을 선택해 주세요.", "file");
            await using var stream = file.OpenReadStream();
            await _files.SaveAsync(projectId, stageId, file.FileName, file.Length, stream);
            // today: 상세 화면의 다른 변경 응답(Detail 의 Run)과 같게 한국 날짜를 실어 오래 열어 둔 화면의 '오늘'을 맞춘다
            return new JsonResult(new { ok = true, project = await _projects.ViewAsync(projectId), today = _clock.Today });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
