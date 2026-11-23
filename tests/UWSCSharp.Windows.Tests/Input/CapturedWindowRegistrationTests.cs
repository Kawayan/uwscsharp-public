using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CapturedWindowRegistrationTests
{
    [Fact]
    public void MatchingCaptureKeepsIdAndChangedOwnerIsNotRegistered()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int id = table.RegisterExpected(77, new(11, 22));
        Assert.Equal(1, id);
        Assert.Equal(id, table.RegisterExpected(77, new(11, 22)));
        platform.Identity = new(12, 22);
        Assert.Equal(-1, table.RegisterExpected(77, new(11, 22)));
        Assert.Null(table.GetRegistration(2));
        Assert.Equal((nint)77, table.Resolve(id));
        Assert.Equal(2, table.Register(77));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 22)]
    [InlineData(11, 23)]
    public void MissingOrDifferentOwnerCannotCreateId(uint process, uint thread)
    {
        var platform = new Platform { Identity = process == 0 ? null : new(process, thread) };
        using var table = new WindowRegistrationTable(platform);
        Assert.Equal(-1, table.RegisterExpected(77, new(11, 22)));
        Assert.Null(table.GetRegistration(1));
        Assert.Equal(-1, table.RegisterExpected(0, new(11, 22)));
        Assert.Equal(-1, table.RegisterExpected(77, default));
    }

    [Fact]
    public void InvalidatedLookupCannotPublishAnEarlierOwnerRead()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int reads = 0;
        platform.Read = () =>
        {
            reads++;
            if (reads == 1)
            {
                table.Invalidate(77);
                return new(11, 22);
            }
            return new(12, 22);
        };
        Assert.Equal(-1, table.RegisterExpected(77, new(11, 22)));
        Assert.Equal(2, reads);
        Assert.Null(table.GetRegistration(1));
    }

    [Fact]
    public void ExplicitRemovalDoesNotReviveTheOldId()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int old = table.RegisterExpected(77, new(11, 22));
        table.Invalidate(77);
        int current = table.RegisterExpected(77, new(11, 22));
        Assert.NotEqual(old, current);
        Assert.Null(table.GetRegistration(old));
        Assert.Equal(new WindowIdentity(11, 22), table.GetRegistration(current)!.Identity);
    }

    [Fact]
    public async Task InterpreterCaptureBypassesHostCallbackAndChecksOwnerEachTime()
    {
        var platform = new Platform();
        var automation = new WindowsAutomation(_ => { }, selectiveInputPlatform: platform,
            hostWindowHandle: (_, _) => throw new InvalidOperationException("別の窓へ置換しません。"),
            capturedScheduleWindow: new(77, 11, 22));
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync(
            "PRINT GETID(GET_SCHEDULE_WIN)\nPRINT GETID(GET_SCHEDULE_WIN)", value =>
            {
                output.Add(value);
                platform.Identity = new(12, 22);
            });
        Assert.Equal(new[] { "1", "-1" }, output);
    }

    [Fact]
    public async Task ForkKeepsTheCapturedIdentityAndSharedWindowId()
    {
        var platform = new Platform();
        var automation = new WindowsAutomation(_ => { }, selectiveInputPlatform: platform,
            capturedScheduleWindow: new(77, 11, 22));
        await using IWindowsAutomationSession root = automation.CreateSession();
        await using IWindowsAutomationSession fork = root.Fork();
        Value[] args = [Value.String(BuiltinWindowSelectors.GetScheduleWin)];
        Value first = await root.InvokeAsync(BuiltinFunctionNames.GetId, args, CancellationToken.None);
        Value second = await fork.InvokeAsync(BuiltinFunctionNames.GetId, args, CancellationToken.None);
        Assert.Equal(first.Number, second.Number);
        Assert.Equal(1, second.Number);
        platform.Identity = null;
        Assert.Equal(-1, (await fork.InvokeAsync(BuiltinFunctionNames.GetId, args, CancellationToken.None)).Number);
    }

    [Fact]
    public void CapturedWindowRejectsZeroIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new WindowsCapturedWindow(0, 11, 22));
        Assert.Throws<ArgumentException>(() => new WindowsCapturedWindow(77, 0, 22));
        Assert.Throws<ArgumentException>(() => new WindowsCapturedWindow(77, 11, 0));
    }

    private sealed class Platform : IWindowIdentityPlatform
    {
        internal WindowIdentity? Identity = new(11, 22);
        internal Func<WindowIdentity?>? Read;

        public WindowIdentity? ReadIdentity(nint window) => Read is null ? Identity : Read();
    }
}
