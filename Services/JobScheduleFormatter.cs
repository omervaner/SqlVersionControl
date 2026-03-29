namespace SqlVersionControl.Services;

/// <summary>
/// Converts SQL Agent schedule columns (freq_type, freq_interval, etc.)
/// into human-readable strings like "Every 5 min" or "Mon/Wed/Fri at 08:00".
/// </summary>
public static class JobScheduleFormatter
{
    public static string Format(int freqType, int freqInterval, int freqSubdayType,
        int freqSubdayInterval, int activeStartTime)
    {
        var time = FormatTime(activeStartTime);

        return freqType switch
        {
            1 => $"Once at {time}",
            4 => FormatDaily(freqInterval, freqSubdayType, freqSubdayInterval, time),
            8 => FormatWeekly(freqInterval, freqSubdayType, freqSubdayInterval, time),
            16 => $"Day {freqInterval} of every month at {time}",
            32 => FormatMonthlyRelative(freqInterval, time),
            64 => "When SQL Agent starts",
            128 => "When CPU is idle",
            _ => "Unknown schedule"
        };
    }

    private static string FormatDaily(int interval, int subdayType, int subdayInterval, string time)
    {
        var subdayPart = FormatSubday(subdayType, subdayInterval);
        if (subdayPart != null)
            return interval == 1 ? subdayPart : $"Every {interval} day(s), {subdayPart}";

        return interval == 1 ? $"Daily at {time}" : $"Every {interval} day(s) at {time}";
    }

    private static string FormatWeekly(int dayBitmask, int subdayType, int subdayInterval, string time)
    {
        var days = DecodeDayBitmask(dayBitmask);
        var subdayPart = FormatSubday(subdayType, subdayInterval);

        if (subdayPart != null)
            return $"{days}, {subdayPart}";

        return $"{days} at {time}";
    }

    private static string? FormatSubday(int subdayType, int subdayInterval)
    {
        return subdayType switch
        {
            2 => subdayInterval == 1 ? "Every second" : $"Every {subdayInterval}s",
            4 => subdayInterval == 1 ? "Every minute" : $"Every {subdayInterval} min",
            8 => subdayInterval == 1 ? "Every hour" : $"Every {subdayInterval} hour(s)",
            _ => null
        };
    }

    private static string FormatMonthlyRelative(int freqInterval, string time)
    {
        // freqInterval encodes the day-of-week for monthly relative schedules
        var dayName = freqInterval switch
        {
            1 => "Sunday",
            2 => "Monday",
            3 => "Tuesday",
            4 => "Wednesday",
            5 => "Thursday",
            6 => "Friday",
            7 => "Saturday",
            _ => $"day {freqInterval}"
        };
        return $"Monthly on {dayName} at {time}";
    }

    private static string DecodeDayBitmask(int bitmask)
    {
        var days = new List<string>();
        if ((bitmask & 1) != 0) days.Add("Sun");
        if ((bitmask & 2) != 0) days.Add("Mon");
        if ((bitmask & 4) != 0) days.Add("Tue");
        if ((bitmask & 8) != 0) days.Add("Wed");
        if ((bitmask & 16) != 0) days.Add("Thu");
        if ((bitmask & 32) != 0) days.Add("Fri");
        if ((bitmask & 64) != 0) days.Add("Sat");

        if (days.Count == 7) return "Daily";
        if (days.Count == 5 && (bitmask & 1) == 0 && (bitmask & 64) == 0) return "Weekdays";

        return string.Join("/", days);
    }

    /// <summary>
    /// Converts SQL Agent active_start_time (integer HHMMSS) to "HH:mm" string.
    /// </summary>
    private static string FormatTime(int time)
    {
        var hours = time / 10000;
        var minutes = (time / 100) % 100;
        return $"{hours:D2}:{minutes:D2}";
    }

    /// <summary>
    /// Formats a duration in seconds to a human-readable string like "1m 23s" or "2h 5m".
    /// </summary>
    public static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds < 60)
            return $"{totalSeconds}s";
        if (totalSeconds < 3600)
        {
            var m = totalSeconds / 60;
            var s = totalSeconds % 60;
            return s > 0 ? $"{m}m {s}s" : $"{m}m";
        }
        var h = totalSeconds / 3600;
        var min = (totalSeconds % 3600) / 60;
        return min > 0 ? $"{h}h {min}m" : $"{h}h";
    }
}
