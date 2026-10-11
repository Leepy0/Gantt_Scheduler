using System.Globalization;
using System.Text;
using GanttScheduler.Services;

namespace GanttScheduler.Pages.Insights;

// 분석 화면의 조건 고르기(유형·법인·조직) — 부분 뷰 _AnalyticsFilter 의 모델. Hidden: 함께 들고 다닐 다른 쿼리 값(범위 등)
public record AnalyticsFilterForm(string Page, AnalyticsFilter Filter, List<PickOption> Categories, List<PickOption> Corporations, List<PickOption> Orgs,
    Dictionary<string, string?> Hidden, bool ShowCategory = true, bool ShowCorporation = true);

// 꺾은선 한 줄. Cls: 색 구분 클래스(site.css 의 .series-*), Values: X 와 같은 길이(값이 없으면 null)
public record ChartSeries(string Name, string Cls, double?[] Values);

// 서버가 그리는 꺾은선 그래프(외부 라이브러리 없이 인라인 SVG). 범례·표는 글자로 함께 보인다(색만으로 구분하지 않는다).
// GapDays: 두 점 사이가 이 일수보다 벌어지면 선을 잇지 않는다 — 기록이 없는 날(서버가 꺼져 있던 날)을 이어 그려 지어내지 않게
// XFrom·XTo: 가로축 범위(고른 기간 전체) — 기록이 최근에만 있으면 오른쪽에만 점이 찍혀 '수집 시작 이후만 있다'가 그대로 보인다
public record LineChart(string Title, List<DateOnly> X, List<ChartSeries> Series, string Unit, int GapDays, double? FixedMax = null,
    DateOnly? XFrom = null, DateOnly? XTo = null)
{
    public const double W = 640, H = 200, Left = 40, Right = 12, Top = 12, Bottom = 28;

    public double MaxY
    {
        get
        {
            if (FixedMax is double f) return f;
            var max = Series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
            return max <= 0 ? 1 : Math.Ceiling(max * 1.1);
        }
    }

    private double Px(int i)
    {
        var from = XFrom ?? (X.Count > 0 ? X[0] : default);
        var to = XTo ?? (X.Count > 0 ? X[^1] : default);
        var span = to.DayNumber - from.DayNumber;
        if (span <= 0) return Left + (W - Left - Right) / 2;
        return Left + (W - Left - Right) * (X[i].DayNumber - from.DayNumber) / span;
    }

    private double Py(double v) => Top + (H - Top - Bottom) * (1 - v / MaxY);

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    // 한 줄의 경로(M/L). 값이 없거나 날짜가 GapDays 넘게 벌어지면 끊는다
    public string Path(ChartSeries s)
    {
        var sb = new StringBuilder();
        var open = false;
        for (var i = 0; i < X.Count; i++)
        {
            if (s.Values[i] is not double v) { open = false; continue; }
            var gap = i > 0 && X[i].DayNumber - X[i - 1].DayNumber > GapDays;
            sb.Append(open && !gap ? " L" : " M").Append(F(Px(i))).Append(',').Append(F(Py(v)));
            open = true;
        }
        return sb.ToString().Trim();
    }

    public IEnumerable<(double X, double Y, double Value, DateOnly Date)> Points(ChartSeries s)
    {
        for (var i = 0; i < X.Count; i++)
            if (s.Values[i] is double v) yield return (Px(i), Py(v), v, X[i]);
    }

    // 세로축 눈금 (0, 중간, 최대)
    public IEnumerable<(double Y, string Label)> YTicks() =>
        new[] { 0, MaxY / 2, MaxY }.Distinct().Select(v => (Py(v), F(v)));

    // 가로축 눈금: 처음·가운데·마지막 날짜
    public IEnumerable<(double X, string Label)> XTicks()
    {
        if (XFrom is DateOnly f && XTo is DateOnly t && t > f)
        {
            var span = t.DayNumber - f.DayNumber;
            foreach (var d in new[] { f, f.AddDays(span / 2), t })
                yield return (Left + (W - Left - Right) * (d.DayNumber - f.DayNumber) / span, d.ToString("MM-dd"));
            yield break;
        }
        if (X.Count == 0) yield break;
        var idx = new[] { 0, X.Count / 2, X.Count - 1 }.Distinct();
        foreach (var i in idx) yield return (Px(i), X[i].ToString("MM-dd"));
    }

    public static string Num(double v) => F(v);
}
