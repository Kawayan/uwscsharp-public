using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeLinkAvailabilityTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void MissingWindowIsNotInvokable()
    {
        Assert.False(NativeLinkAvailability.CanInvoke(0));
    }
}
