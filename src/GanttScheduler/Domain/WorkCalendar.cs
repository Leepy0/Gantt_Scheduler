namespace GanttScheduler.Domain;

// 근무일 달력 — 토·일과 공휴일을 뺀다
public class WorkCalendar
{
    private readonly HashSet<DateOnly> _holidays;

    public WorkCalendar(IEnumerable<DateOnly> holidays) => _holidays = new HashSet<DateOnly>(holidays);

    public static readonly WorkCalendar WeekendsOnly = new(Array.Empty<DateOnly>());

    public bool IsWorkingDay(DateOnly d) =>
        d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday && !_holidays.Contains(d);

    // from~to 사이(양 끝 포함)의 근무일 수
    public int WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsWorkingDay(d)) n++;
        return n;
    }

    // d 에서 근무일 n 일만큼 이동 (n < 0 이면 과거로). n = 0 이면 그대로
    public DateOnly AddWorkingDays(DateOnly d, int n)
    {
        var step = n < 0 ? -1 : 1;
        var left = Math.Abs(n);
        while (left > 0)
        {
            d = d.AddDays(step);
            if (IsWorkingDay(d)) left--;
        }
        return d;
    }

    // a 에서 b 까지의 근무일 거리 (b 가 뒤면 양수). AddWorkingDays(a, Distance(a, b)) 는 b 이전의 마지막 근무일 또는 b
    public int Distance(DateOnly a, DateOnly b)
    {
        if (b >= a) return WorkingDays(a.AddDays(1), b);
        return -WorkingDays(b.AddDays(1), a);
    }

    // 선행 작업이 끝난 뒤 후행 작업이 시작할 수 있는 가장 이른 날
    public DateOnly EarliestStartAfter(DateOnly predecessorEnd, int lagDays) =>
        AddWorkingDays(predecessorEnd, 1 + Math.Max(0, lagDays));
}
