using Transcoder.Contracts;
using Transcoder.Server.Services;
using Xunit;

namespace Transcoder.Server.Tests;

public sealed class ActiveHoursEvaluatorTests
{
    [Theory]
    [InlineData("2026-01-01T00:45:00Z", true)]
    [InlineData("2026-01-01T01:45:00Z", false)]
    [InlineData("2026-01-01T08:30:00Z", true)]
    public void DefaultStyle_OvernightWindow_PausesAfterStopNewWorkBoundary(string utcNow, bool expectedAllowed)
    {
        var settings = new ExecutionSettingsDto
        {
            ActiveHoursEnabled = true,
            ActiveHoursTimeZoneId = "UTC",
            ActiveHoursStart = "08:30",
            ActiveHoursStop = "02:00",
            StopNewWorkMinutesBefore = 30
        };

        var status = ActiveHoursEvaluator.Evaluate(settings, DateTime.Parse(utcNow).ToUniversalTime());

        Assert.Equal(expectedAllowed, status.AllowStagedWork);
    }
}
