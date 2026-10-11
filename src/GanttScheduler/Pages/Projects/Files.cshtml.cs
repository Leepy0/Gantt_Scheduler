using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;

namespace GanttScheduler.Pages.Projects;

// 첨부 파일 올리기·내려받기 (화면 없음).
// 특성의 한도는 상수라 설정할 수 있는 끝(MaxMBLimit)으로 넉넉히 두고, 실제 한도는 요청마다 설정값(Storage:MaxMB)으로 좁힌다
[RequestSizeLimit(AttachmentService.RequestCeiling)]
[RequestFormLimits(MultipartBodyLengthLimit = AttachmentService.RequestCeiling)]
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

    // 본문을 읽기 전에(모델 바인딩 전) 요청 크기 한도를 첨부 상한 + 여유로 좁힌다 — 상한을 넘는 파일을 끝까지 받은 뒤 거절하지 않게
    public override Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        var limit = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = _files.MaxBytes + 1024 * 1024;
        return Task.CompletedTask;
    }

    // 내려받기. inline=1 이고 미리보기 가능한 형식(이미지·PDF)이면 브라우저에서 바로 연다.
    // 저장 이름이 GUID 라 내용이 바뀌지 않으므로 저장 이름을 ETag 로 주고 범위 요청을 받는다 — 큰 PDF 를 다시 미리 볼 때 전부 다시 받지 않는다.
    // 사내 사용자별 내용이라 공유 캐시(프록시)에는 두지 않는다(private)
    public async Task<IActionResult> OnGetDownloadAsync(int id, bool inline = false)
    {
        var found = await _files.FindAsync(id);
        if (found == null) return NotFound();
        var (path, file) = found.Value;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private";
        FileStream stream;
        try
        {
            stream = AttachmentService.OpenRead(path);
        }
        catch (FileNotFoundException)
        {
            return NotFound();      // 찾은 뒤 여는 사이에 지워졌다
        }
        return new FileStreamResult(stream, file.ContentType)
        {
            FileDownloadName = inline && Views.PreviewTypes.Contains(file.ContentType) ? null : file.FileName,
            LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.UploadedAt, DateTimeKind.Utc)),
            EntityTag = new EntityTagHeaderValue("\"" + file.StoredName + "\""),
            EnableRangeProcessing = true,
        };
    }

    // compare: 상세 화면이 고른 기준선 비교 차수 — 돌려주는 과제도 같은 기준으로 센다
    public async Task<IActionResult> OnPostUploadAsync(int projectId, int? stageId, IFormFile? file, int? compare = null)
    {
        try
        {
            if (file == null) throw new RuleException("파일을 선택해 주세요.", "file");
            await using var stream = file.OpenReadStream();
            await _files.SaveAsync(projectId, stageId, file.FileName, file.Length, stream);
            // today: 상세 화면의 다른 변경 응답(Detail 의 Run)과 같게 한국 날짜를 실어 오래 열어 둔 화면의 '오늘'을 맞춘다
            return new JsonResult(new { ok = true, project = await _projects.ViewAsync(projectId, compare: compare), today = _clock.Today });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
