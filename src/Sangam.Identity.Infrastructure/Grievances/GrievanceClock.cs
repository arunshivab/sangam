using System.Globalization;
using Microsoft.Extensions.Configuration;
using Sangam.Shared;

namespace Sangam.Identity.Infrastructure.Grievances;

/// <summary>
/// The grievance deadlines (D-D), counted in India time: acknowledge by the end of the second working day after the
/// day of receipt (Monday to Friday, less the holidays in <c>Sangam:Grievance:Holidays</c>), and resolve within thirty
/// days of receipt. Both numbers come from <c>Sangam:Grievance</c>, the same settings the grievance page shows.
/// </summary>
public sealed class GrievanceClock
{
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>Initialises the clock.</summary>
    /// <param name="configuration">Configuration (<c>Sangam:Grievance</c>).</param>
    public GrievanceClock(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AcknowledgeWorkingDays = Math.Max(1, configuration.GetValue("Sangam:Grievance:AcknowledgeWorkingDays", 2));
        ResponseDays = Math.Max(1, configuration.GetValue("Sangam:Grievance:ResponseDays", 30));
        _holidays = [.. (configuration["Sangam:Grievance:Holidays"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) ? day : (DateOnly?)null)
            .OfType<DateOnly>()];
    }

    /// <summary>Working days to acknowledge.</summary>
    public int AcknowledgeWorkingDays { get; }

    /// <summary>Days to resolve.</summary>
    public int ResponseDays { get; }

    /// <summary>The end (23:59:59 IST) of the Nth working day after the day of receipt.</summary>
    /// <param name="receivedAt">When it was received.</param>
    public DateTimeOffset AcknowledgeBy(DateTimeOffset receivedAt)
    {
        DateOnly day = DateOnly.FromDateTime(IndiaTime.ToIndia(receivedAt).DateTime);
        int counted = 0;
        while (counted < AcknowledgeWorkingDays)
        {
            day = day.AddDays(1);
            if (IsWorkingDay(day))
            {
                counted++;
            }
        }

        return EndOf(day);
    }

    /// <summary>Thirty days (by default) after receipt, to the end of that day in India.</summary>
    /// <param name="receivedAt">When it was received.</param>
    public DateTimeOffset ResolveBy(DateTimeOffset receivedAt)
        => EndOf(DateOnly.FromDateTime(IndiaTime.ToIndia(receivedAt).DateTime).AddDays(ResponseDays));

    /// <summary>Whether a day is a working day.</summary>
    /// <param name="day">The day.</param>
    public bool IsWorkingDay(DateOnly day) => day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && !_holidays.Contains(day);

    private static DateTimeOffset EndOf(DateOnly day) => new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59)), IndiaTime.Offset).ToUniversalTime();
}
