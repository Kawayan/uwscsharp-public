using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarWaitTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : OwnedListSelectionPlatform
    {
        internal List<string> Events { get; } = [];

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add("sleep" + milliseconds);
            if (milliseconds == 20 && failure == "sleep")
            {
                cancel.Cancel();
            }

            if (milliseconds == 20 && failure == "throw")
            {
                throw new InvalidOperationException("wait callback");
            }

            if (milliseconds == 20 && failure == "operation-canceled")
            {
                throw new OperationCanceledException("wait callback");
            }

            token.ThrowIfCancellationRequested();
        }

        public override void Pump(CancellationToken token)
        {
            Events.Add("pump");
            if (failure == "yield")
            {
                cancel.Cancel();
            }

            token.ThrowIfCancellationRequested();
        }
    }

    [Theory]
    [InlineData(4, false, false)]
    [InlineData(5, true, false)]
    [InlineData(6, true, false)]
    [InlineData(7, true, false)]
    [InlineData(4, false, true)]
    [InlineData(5, true, true)]
    [InlineData(6, true, true)]
    [InlineData(7, true, true)]
    public void SameStateOnlyWaitsAndSetsHotItemAgain(byte state, bool on, bool zero)
    {
        Run(state, on, zero, null);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("yield")]
    [InlineData("throw")]
    [InlineData("operation-canceled")]
    public void InterruptedWaitDoesNotPerformStateChange(string failure)
    {
        Run(6, true, false, failure);
    }

    private static void Run(byte state, bool on, bool zero, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "other", 700);
        ToolbarControlTests.Add(toolbar, "target", 701, state: state);
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure);
        int actions = 0;
        fixture.Command = (_, _) => actions++;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, parameter, pointer) =>
        {
            if (message == 0x41d)
            {
                platform.Events.Add("rectangle");
            }

            if (message == 0x448)
            {
                Assert.Equal(1, parameter);
                Assert.Equal(0, pointer);
                platform.Events.Add("hot");
                if (zero)
                {
                    return 0;
                }
            }

            actions += message is 0x402 or 0x403 or 0x201 or 0x202 ? 1 : 0;
            return null;
        });
        if (failure == "throw")
        {
            Assert.Equal((1, true, true), NativeToolbarControl.Click(
                toolbar, "target", 1, on, cancel.Token, platform: platform));
        }
        else if (failure is not null)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => NativeToolbarControl.Click(
                toolbar, "target", 1, on, cancel.Token, platform: platform));
        }
        else
        {
            Assert.Equal((1, true, true), NativeToolbarControl.Click(
                toolbar, "target", 1, on, default, platform: platform));
        }

        List<string> expected = ["rectangle", "hot", "sleep20"];
        if (failure == "yield")
        {
            expected.AddRange(["sleep0", "pump"]);
        }
        else if (failure is null)
        {
            for (int index = 0; index < 20; index++)
            {
                expected.AddRange(["sleep0", "pump"]);
            }

            expected.Add("hot");
        }

        Assert.Equal(expected, platform.Events);
        Assert.Equal(0, actions);
        Assert.Equal(state, Native.SendMessageW(toolbar, 0x412, 701, 0));
        Assert.Null(fixture.CallbackFailure);
    }
}
