using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarDestructionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFailureReachesScriptExceptOrStopsScript(bool catches)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701, state: 7);
        var platform = new ToolbarOperationTests.RecordingPlatform
        {
            Restoring = () => throw WindowsOperationErrors.Record(new InvalidOperationException("復元失敗")),
        };
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
            listSelectionPlatform: platform);
        var runner = new Interpreter(windows: automation);
        string body = "PRINT CLKITEM(id, \"target\", 64, TRUE)\n";
        string source = $"id = HNDTOID({(long)fixture.Root})\n"
            + (catches ? "TRY\nTRY\n" + body
                + "EXCEPT\nPRINT \"caught\"\nENDTRY\nFINALLY\nPRINT \"cleanup\"\nENDTRY\n" : body)
            + "PRINT \"after\"\n";
        List<string> output = [];
        if (catches)
        {
            await runner.RunAsync(source, output.Add, default);
            Assert.Equal(new[] { "caught", "cleanup", "after" }, output);
        }
        else
        {
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                runner.RunAsync(source, output.Add, default));
            Assert.Contains("FucCLKITEM:復元失敗", failure.Message);
            Assert.Empty(output);
        }

        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("rectangle")]
    [InlineData("first-hot")]
    [InlineData("second-hot")]
    [InlineData("post")]
    [InlineData("post-restore-failure")]
    [InlineData("cancel-second-hot")]
    public async Task DestructionUsesLocalActionBoundaryAndStillReleasesResources(string stage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701, state: 7);
        using var cancel = new CancellationTokenSource();
        int restored = 0;
        int hotItems = 0;
        bool destroyed = false;
        var platform = new ToolbarOperationTests.RecordingPlatform
        {
            Restoring = () =>
            {
                restored++;
                if (stage == "post-restore-failure")
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException("復元失敗"));
                }
            },
            Posting = message =>
            {
                if (stage.StartsWith("post", StringComparison.Ordinal) && message == 0x201)
                {
                    destroyed = Native.DestroyWindow(toolbar);
                    Assert.True(destroyed);
                }
            },
        };
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar,
            (_, _, _) => null, after: (message, _, pointer) =>
            {
                hotItems += message == 0x448 ? 1 : 0;
                bool destroy = stage == "text" && message == 0x44b && pointer != 0
                    || stage == "rectangle" && message == 0x41d
                    || stage == "first-hot" && message == 0x448 && hotItems == 1
                    || stage is "second-hot" or "cancel-second-hot"
                        && message == 0x448 && hotItems == 2;
                if (destroy && !destroyed)
                {
                    destroyed = Native.DestroyWindow(toolbar);
                    Assert.True(destroyed);
                    if (stage == "cancel-second-hot")
                    {
                        cancel.Cancel();
                    }
                }
            });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(64),
            Value.Numeric(stage.StartsWith("post", StringComparison.Ordinal) ? 0 : 1)];
        if (stage == "cancel-second-hot")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (stage is "second-hot" or "post")
        {
            Assert.Equal(Value.Numeric(1),
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
            if (stage == "post-restore-failure")
            {
                Assert.Equal("復元失敗", failure.Message);
            }
        }

        Assert.True(destroyed);
        Assert.False(Native.IsWindow(toolbar));
        Assert.Equal(1, restored);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
        if (stage.StartsWith("post", StringComparison.Ordinal))
        {
            Assert.Equal(new uint[] { 0x201, 0x202 },
                platform.Posts.Select(post => post.Message));
            Assert.Equal("priority32", platform.Events.Last());
        }
        else
        {
            Assert.Empty(platform.Posts);
        }
    }
}
