namespace TCJ.Core.Extensions;
/// <summary>
/// Provides convenience methods for classifying days of the week.
/// </summary>

public static class DayOfWeekExtensions
{
    /// <summary>
    /// Determines whether the specified day is a weekend day.
    /// </summary>
    /// <param name="dayOfWeek">The day of week to classify.</param>
    /// <returns>true when the condition is satisfied; otherwise, false.</returns>
    public static bool IsWeekend(this DayOfWeek dayOfWeek)
        => dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    /// <summary>
    /// Determines whether the specified day is a weekday.
    /// </summary>
    /// <param name="dayOfWeek">The day of week to classify.</param>
    /// <returns>true when the condition is satisfied; otherwise, false.</returns>

    public static bool IsWeekday(this DayOfWeek dayOfWeek)
        => !dayOfWeek.IsWeekend();
}
