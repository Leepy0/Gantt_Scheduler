using System.Globalization;
using System.Text.RegularExpressions;

namespace GanttScheduler.Domain;

// 붙여넣기·Excel 가져오기에서 사람이 적은 글자를 값으로 읽는다(계산 규칙이 아니라 입력 해석).
// Excel 에서 복사한 날짜는 셀 서식대로 글자가 되므로 흔한 형식을 모두 받는다
public static class InputText
{
    // 연도가 있는 형식: 2026-10-12, 2026.10.12, 2026/10/12, 2026.10.12., 2026년 10월 12일, 20261012 (뒤의 요일 '(월)' 은 무시)
    private static readonly Regex FullDate = new(
        @"^(?<y>\d{4})\s*(?:[-./]|년)\s*(?<m>\d{1,2})\s*(?:[-./]|월)\s*(?<d>\d{1,2})\s*(?:일|\.)?\s*(?:\([^)]*\))?$");
    private static readonly Regex CompactDate = new(@"^(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})$");
    // 연도가 없는 형식(올해): 10/12, 10.12, 10-12, 10월 12일
    private static readonly Regex ShortDate = new(
        @"^(?<m>\d{1,2})\s*(?:[-./]|월)\s*(?<d>\d{1,2})\s*(?:일|\.)?\s*(?:\([^)]*\))?$");
    // 미국식 월/일/연도 (Excel 영문 서식에서 복사한 경우): 10/12/2026
    private static readonly Regex UsDate = new(@"^(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})$");

    // 읽지 못하면 false. 빈 글자도 false (호출한 쪽이 '비어 있음'과 '형식 오류'를 구분한다).
    // today: 연도 없는 형식에 붙일 올해 (한국 날짜)
    public static bool TryDate(string? text, DateOnly today, out DateOnly date)
    {
        date = default;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        // Excel 날짜·시각 셀을 그대로 복사하면 '2026-10-12 0:00:00' 처럼 시각이 붙는다
        var space = t.IndexOf(' ');
        if (space > 0 && Regex.IsMatch(t[(space + 1)..], @"^\d{1,2}:\d{2}(:\d{2})?$")) t = t[..space];

        Match m;
        if ((m = FullDate.Match(t)).Success || (m = CompactDate.Match(t)).Success || (m = UsDate.Match(t)).Success)
            return Make(m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value, out date);
        if ((m = ShortDate.Match(t)).Success)
            return Make(today.Year.ToString(CultureInfo.InvariantCulture), m.Groups["m"].Value, m.Groups["d"].Value, out date);
        return false;
    }

    private static bool Make(string y, string m, string d, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(y, out var year) || !int.TryParse(m, out var month) || !int.TryParse(d, out var day)) return false;
        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month)) return false;
        date = new DateOnly(year, month, day);
        return date >= Rules.MinDate && date <= Rules.MaxDate;
    }

    // 붙여넣은 한 줄을 칸으로 나눈다. Excel·표에서 복사하면 TAB 이다. TAB 이 없으면 쉼표(CSV)로 나눈다
    public static List<string> Cells(string line)
    {
        var parts = line.Contains('\t') ? line.Split('\t') : line.Split(',');
        return parts.Select(x => x.Trim().Trim('"').Trim()).ToList();
    }

    // 여러 줄 글자를 줄 목록으로 (빈 줄은 뺀다). 줄 번호는 원래 글의 몇 번째 줄인지(1부터)
    public static List<(int Line, string Text)> Lines(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n')
            .Select((l, i) => (Line: i + 1, Text: l))
            .Where(x => x.Text.Trim().Length > 0)
            .ToList();
}
