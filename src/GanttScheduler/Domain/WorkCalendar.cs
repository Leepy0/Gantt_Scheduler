namespace GanttScheduler.Domain;

// 근무일 달력 — 토·일과 공휴일을 뺀다.
// 하루씩 세면 기간이 길수록 느리고 날짜 끝(DateOnly.MaxValue)에서 넘친다. 그래서 "그날까지의 누적 근무일 수(서수)"를
// 주 단위 산술(평일 수)과 정렬된 공휴일 목록의 이분 탐색으로 구하고, 모든 계산을 이 서수의 차이로 한다.
public class WorkCalendar
{
    private readonly HashSet<DateOnly> _holidays;
    private readonly int[] _weekdayHolidays;          // 평일에 걸린 공휴일의 DayNumber (정렬, 중복 없음). 주말 공휴일은 근무일 수에 영향이 없다
    private static readonly int MaxDay = DateOnly.MaxValue.DayNumber;

    public WorkCalendar(IEnumerable<DateOnly> holidays)
    {
        _holidays = new HashSet<DateOnly>(holidays);
        _weekdayHolidays = _holidays.Where(IsWeekday).Select(d => d.DayNumber).OrderBy(n => n).ToArray();
    }

    public static readonly WorkCalendar WeekendsOnly = new(Array.Empty<DateOnly>());

    private static bool IsWeekday(DateOnly d) => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;

    public bool IsWorkingDay(DateOnly d) => IsWeekday(d) && !_holidays.Contains(d);

    // d 가 근무일이면 그대로, 아니면 다음 근무일
    public DateOnly NextWorkingDay(DateOnly d) => IsWorkingDay(d) ? d : AddWorkingDays(d, 1);

    // from~to 사이(양 끝 포함)의 근무일 수
    public int WorkingDays(DateOnly from, DateOnly to) =>
        to < from ? 0 : Ordinal(to.DayNumber) - Ordinal(from.DayNumber - 1);

    // d 에서 근무일 n 일만큼 이동 (n < 0 이면 과거로). n = 0 이면 그대로.
    // 날짜 범위를 넘으면 DateOnly.MinValue/MaxValue 에서 멈춘다(입력 검증이 범위를 막으므로 실제로는 닿지 않는다)
    public DateOnly AddWorkingDays(DateOnly d, int n)
    {
        if (n == 0) return d;
        var k = Ordinal(d.DayNumber);
        // 휴일에서 하루 뒤로 가면 직전 근무일(서수 k)에 닿는다. 근무일에서는 그 전 근무일(k-1)
        long target = n > 0 ? (long)k + n : (long)k + n + (IsWorkingDay(d) ? 0 : 1);
        return DateOfOrdinal(target);
    }

    // a 에서 b 까지의 근무일 거리 (b 가 뒤면 양수). AddWorkingDays(a, Distance(a, b)) 는 b 이전의 마지막 근무일 또는 b
    public int Distance(DateOnly a, DateOnly b) => Ordinal(b.DayNumber) - Ordinal(a.DayNumber);

    // 날짜를 기준일 from 에서 to 로 근무일 거리 그대로 옮긴다(과제 복제). 결과는 근무일이고,
    // d 가 휴일이면 그 직전 근무일과 같은 자리로 본다. from = to 이고 d 가 근무일이면 d 그대로
    public DateOnly Shift(DateOnly d, DateOnly from, DateOnly to) =>
        DateOfOrdinal((long)Ordinal(d.DayNumber) - Ordinal(from.DayNumber) + Ordinal(to.DayNumber));

    // 선행 작업이 끝난 뒤 후행 작업이 시작할 수 있는 가장 이른 날
    public DateOnly EarliestStartAfter(DateOnly predecessorEnd, int lagDays) =>
        AddWorkingDays(predecessorEnd, 1 + Math.Max(0, lagDays));

    // ── 서수 계산 ───────────────────────────────────────────────────────

    // DayNumber 0(0001-01-01)은 월요일이다. 0..day 사이의 평일 수 (day = -1 이면 0)
    private static long Weekdays(long day)
    {
        var days = day + 1;
        return days / 7 * 5 + Math.Min(days % 7, 5);
    }

    // k 번째(1부터) 평일의 DayNumber
    private static long WeekdayAt(long k)
    {
        var j = k - 1;
        return j / 5 * 7 + j % 5;
    }

    // day 이하의 평일 공휴일 수 (이분 탐색)
    private int HolidaysUpTo(long day)
    {
        int lo = 0, hi = _weekdayHolidays.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_weekdayHolidays[mid] <= day) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // 0..day 사이의 근무일 수. 휴일의 서수는 직전 근무일과 같다
    private int Ordinal(long day) => (int)(Weekdays(day) - HolidaysUpTo(day));

    // 서수가 target 인 근무일 (= 서수가 target 이 되는 가장 이른 날).
    // 평일 서수에 그 날까지의 공휴일 수를 더해 가며 고정점을 찾는다. 단조 증가라 아래에서 다가가면 가장 이른 고정점에 멈추고,
    // 그날은 근무일이다(공휴일이면 그 전 평일도 고정점이 되므로 모순). 공휴일 묶음을 건널 때만 몇 번 더 돈다
    private DateOnly DateOfOrdinal(long target)
    {
        if (target < 1) return DateOnly.MinValue;
        if (target > Ordinal(MaxDay)) return DateOnly.MaxValue;
        var day = WeekdayAt(target);
        while (true)
        {
            var next = WeekdayAt(target + HolidaysUpTo(day));
            if (next == day) break;
            day = next;
        }
        return day > MaxDay ? DateOnly.MaxValue : DateOnly.FromDayNumber((int)day);
    }
}
