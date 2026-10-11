using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Projects;

// Excel 가져오기: 양식 내려받기 → 올리기(미리보기) → 가져오기. 서버는 올린 파일을 보관하지 않는다 —
// 화면이 고른 파일을 미리보기와 가져오기에 두 번 보내고, 가져오기는 처음부터 다시 검사한다(그 사이 기준 정보가 바뀌어도 안전하게)
[RequestSizeLimit(ProjectImporter.MaxBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = ProjectImporter.MaxBytes + 1024 * 1024)]
public class ImportModel : PageModel
{
    private readonly ProjectService _projects;
    private readonly ProjectImporter _importer;
    private readonly ExcelExporter _excel;

    public ImportModel(ProjectService projects, ProjectImporter importer, ExcelExporter excel)
    {
        _projects = projects;
        _importer = importer;
        _excel = excel;
    }

    public int MaxMB => ProjectImporter.MaxBytes / 1024 / 1024;

    public void OnGet() { }

    // 양식: 과제·작업 시트(머리글), 목록 시트(지금 쓰는 유형·위치·사용자), 안내 시트
    public async Task<IActionResult> OnGetTemplateAsync() =>
        File(_excel.ImportTemplate(await _projects.ImportListsAsync()), ExcelExporter.ContentType, "과제_가져오기_양식.xlsx");

    public async Task<IActionResult> OnPostPreviewAsync(IFormFile? file)
    {
        try
        {
            var preview = await _projects.PreviewImportAsync(Read(file));
            return new JsonResult(new { ok = true, preview, canImport = preview.Ok });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, bool confirmNewUsers)
    {
        try
        {
            var result = await _projects.ImportAsync(Read(file), confirmNewUsers, Path.GetFileName(file!.FileName));
            return new JsonResult(new
            {
                ok = true, result,
                redirect = result.Ids.Count == 1 ? Url.Page("/Projects/Detail", new { id = result.Ids[0] }) : Url.Page("/Projects/Index", new { sort = "recent", desc = true }),
            });
        }
        catch (RuleException ex)
        {
            return Fail(ex);
        }
    }

    private ImportBook Read(IFormFile? file)
    {
        if (file == null || file.Length == 0) throw new RuleException("가져올 Excel 파일을 선택해 주세요.", "file");
        if (file.Length > ProjectImporter.MaxBytes) throw new RuleException($"파일이 너무 큽니다. {MaxMB}MB 까지 올릴 수 있습니다.", "file");
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("Excel 통합 문서(.xlsx)만 올릴 수 있습니다. 이 화면에서 내려받은 양식에 적어 저장해 주세요.", "file");
        using var stream = file.OpenReadStream();
        return _importer.Read(stream);
    }

    private static JsonResult Fail(RuleException ex) =>
        new(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
}
