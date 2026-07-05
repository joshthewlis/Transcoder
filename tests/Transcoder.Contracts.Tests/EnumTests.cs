using Transcoder.Contracts;
using Xunit;

namespace Transcoder.Contracts.Tests;

public sealed class EnumTests
{
    [Fact]
    public void Prober_role_can_be_combined_with_transcoder_role()
    {
        var roles = WorkerRole.Prober | WorkerRole.Transcoder;
        Assert.True(roles.HasFlag(WorkerRole.Prober));
        Assert.True(roles.HasFlag(WorkerRole.Transcoder));
    }
}
