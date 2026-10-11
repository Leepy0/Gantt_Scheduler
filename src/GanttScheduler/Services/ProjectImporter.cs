using System.Globalization;
using ClosedXML.Excel;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// Excel 가져오기 파일 읽기 (ClosedXML). 시트 '과제'·'작업'을 머리글(1행)과 줄로만 바꾸고, 검사·등록은 ProjectService 가 한다.
// 섀도 빌드에서는 컴파일되지 않는다(tools/shadow/FakeExcel.cs 의 대체 구현)
public class ProjectImporter
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public ImportBook Read(Stream stream)
    {
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new RuleException("Excel(.xlsx) 파일을 읽지 못했습니다. 이 화면에서 내려받은 양식에 적어 저장한 파일을 올려 주세요.", "file");
        }
        using (wb)
        {
            return new ImportBook(Sheet(wb, ProjectService.ImportColumns.ProjectSheet), Sheet(wb, ProjectService.ImportColumns.WorkSheet));
        }
    }

    private static ImportSheet? Sheet(XLWorkbook wb, string name)
    {
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim() == name);
        if (ws == null) return null;
        var used = ws.RangeUsed();
        if (used == null) return new ImportSheet(name, new(), new());
        var lastCol = used.LastColumn().ColumnNumber();
        var lastRow = used.LastRow().RowNumber();
        var headers = Enumerable.Range(1, lastCol).Select(c => Text(ws.Cell(1, c))).ToList();
        var rows = new List<ImportRow>();
        for (var r = 2; r <= lastRow; r++)
            rows.Add(new ImportRow(r, Enumerable.Range(1, lastCol).Select(c => Text(ws.Cell(r, c))).ToList()));
        return new ImportSheet(name, headers, rows);
    }

    // 셀 글자: 날짜 셀은 yyyy-MM-dd(셀 서식과 관계없이), 숫자는 그대로, 나머지는 글자
    private static string Text(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsDateTime) return DateOnly.FromDateTime(v.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (v.IsNumber) return v.GetNumber().ToString(CultureInfo.InvariantCulture);
        if (v.IsBoolean) return v.GetBoolean() ? "TRUE" : "FALSE";
        if (v.IsText) return v.GetText().Trim();
        return "";
    }
}
