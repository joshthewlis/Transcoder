using System.Globalization;
using Transcoder.Contracts;

namespace Transcoder.Server.Services;

public static class ActiveHoursEvaluator
{
    public static ActiveHoursStatusDto Evaluate(ExecutionSettingsDto settings, DateTime utcNow)
    {
        var status = new ActiveHoursStatusDto
        {
            Enabled = settings.ActiveHoursEnabled,
            TimeZoneId = string.IsNullOrWhiteSpace(settings.ActiveHoursTimeZoneId) ? "Europe/London" : settings.ActiveHoursTimeZoneId.Trim(),
            Start = string.IsNullOrWhiteSpace(settings.ActiveHoursStart) ? "08:30" : settings.ActiveHoursStart.Trim(),
            Stop = string.IsNullOrWhiteSpace(settings.ActiveHoursStop) ? "02:00" : settings.ActiveHoursStop.Trim(),
            StopNewWorkMinutesBefore = Math.Clamp(settings.StopNewWorkMinutesBefore, 0, 24 * 60 - 1),
            UtcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime()
        };

        if (!status.Enabled)
        {
            status.AllowStagedWork = true;
            status.Message = "Active hours are disabled; staged work can start at any time.";
            return status;
        }

        if (!TryFindTimeZone(status.TimeZoneId, out var timeZone))
        {
            status.AllowStagedWork = true;
            status.Message = $"Active-hours time zone '{status.TimeZoneId}' was not found; staged work is allowed until the setting is fixed.";
            return status;
        }

        if (!TryParseClock(status.Start, out var start) || !TryParseClock(status.Stop, out var stop))
        {
            status.AllowStagedWork = true;
            status.Message = "Active-hours start/stop times are invalid; staged work is allowed until the setting is fixed.";
            return status;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(status.UtcNow, timeZone);
        status.LocalNow = localNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var effectiveStop = Normalize(stop - TimeSpan.FromMinutes(status.StopNewWorkMinutesBefore));
        status.AllowStagedWork = IsWithinWindow(localNow.TimeOfDay, start, effectiveStop);
        status.NextStartUtc = FindNextUtc(localNow, start, timeZone);
        status.StopNewWorkUtc = FindNextUtc(localNow, effectiveStop, timeZone);

        status.Message = status.AllowStagedWork
            ? $"Staged work is allowed. New cleanup/transcode leases stop at {FormatClock(effectiveStop)} local time."
            : $"Staged work is paused by active hours. Next start is {status.NextStartUtc:O}.";

        return status;
    }

    public static bool AppliesToJobType(JobType jobType) =>
        jobType is JobType.Cleanup or JobType.Transcode or JobType.ValidateOutput or JobType.ReplaceOriginal;

    private static bool IsWithinWindow(TimeSpan value, TimeSpan start, TimeSpan stop)
    {
        if (start == stop)
            return true;

        return start < stop
            ? value >= start && value < stop
            : value >= start || value < stop;
    }

    private static TimeSpan Normalize(TimeSpan value)
    {
        var ticksPerDay = TimeSpan.TicksPerDay;
        var ticks = value.Ticks % ticksPerDay;
        if (ticks < 0) ticks += ticksPerDay;
        return TimeSpan.FromTicks(ticks);
    }

    private static bool TryParseClock(string value, out TimeSpan time)
    {
        return TimeSpan.TryParseExact(value, [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);
    }

    private static DateTime? FindNextUtc(DateTime localNow, TimeSpan localClockTime, TimeZoneInfo timeZone)
    {
        for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            var candidateLocal = localNow.Date.AddDays(dayOffset).Add(localClockTime);
            if (candidateLocal <= localNow)
                continue;

            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidateLocal, DateTimeKind.Unspecified), timeZone);
            }
            catch
            {
                // Rare DST edge. Try the next day rather than blocking all work.
            }
        }

        return null;
    }

    private static string FormatClock(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";

    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch
        {
            var fallbackId = timeZoneId.Equals("Europe/London", StringComparison.OrdinalIgnoreCase)
                ? "GMT Standard Time"
                : timeZoneId.Equals("GMT Standard Time", StringComparison.OrdinalIgnoreCase)
                    ? "Europe/London"
                    : null;

            if (fallbackId is not null)
            {
                try
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById(fallbackId);
                    return true;
                }
                catch
                {
                    // fall through
                }
            }

            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
