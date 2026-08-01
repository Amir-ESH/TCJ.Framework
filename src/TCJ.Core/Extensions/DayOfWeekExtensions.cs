namespace TCJ.Core.Extensions;

public static class DayOfWeekExtensions
{
    public static bool IsWeekend(this DayOfWeek dayOfWeek)
        => dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static bool IsWeekday(this DayOfWeek dayOfWeek)
        => !dayOfWeek.IsWeekend();
}
