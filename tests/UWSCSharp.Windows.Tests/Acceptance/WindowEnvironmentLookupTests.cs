using System.ComponentModel;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowEnvironmentLookupTests
{
    [Theory]
    [InlineData("GET_ACTIVE_WIN")]
    [InlineData("GET_FROMPOINT_WIN")]
    [InlineData("GET_FROMPOINT_OBJ")]
    public async Task SelectsForegroundOrPointWithoutChangingEnvironment(string selector)
    {
        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            owner => child = owner.Child("STATIC", "PointChild", 0, 10));
        var environment = new Environment { Root = fixture.Root, Child = child };
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(
            "物理入力は禁止です。"), windowLookup: environment);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT IDTOHND(GETID({selector},\"ignored\",-1,\"ignored\"))", output.Add);
        nint expected = selector == "GET_FROMPOINT_OBJ" ? child : fixture.Root;
        Assert.Equal([((long)expected).ToString()], output);
        Assert.Equal(selector switch
        {
            "GET_ACTIVE_WIN" => ["foreground"],
            "GET_FROMPOINT_WIN" => ["cursor", "point", "root"],
            _ => ["cursor", "point"]
        }, environment.Calls);
    }

    [Theory]
    [InlineData("GET_ACTIVE_WIN")]
    [InlineData("GET_FROMPOINT_WIN")]
    [InlineData("GET_FROMPOINT_OBJ")]
    public async Task MissingNativeTargetReturnsNotFound(string selector)
    {
        var environment = new Environment();
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { }, windowLookup: environment))
            .RunAsync($"PRINT GETID({selector})", output.Add);
        Assert.Equal(["-1"], output);
    }

    [Fact]
    public async Task KnownCursorFailureCanBeCaughtWhileUnknownFailurePropagates()
    {
        var known = new Environment
        {
            Failure = WindowsOperationErrors.Record(new Win32Exception(5))
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { }, windowLookup: known))
            .RunAsync("TRY\nGETID(GET_FROMPOINT_OBJ)\nEXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
        Assert.Equal(["caught"], output);

        var failure = new IOException("未分類の環境障害");
        var unknown = new Environment { Failure = failure };
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { }, windowLookup: unknown))
                .RunAsync("TRY\nGETID(GET_FROMPOINT_OBJ)\nEXCEPT\nPRINT \"caught\"\nENDTRY", _ => { }));
        Assert.Same(failure, error);
    }

    private sealed class Environment : IWindowLookupEnvironment
    {
        internal nint Root
        {
            get;
            init;
        }

        internal nint Child
        {
            get;
            init;
        }

        internal Exception? Failure
        {
            get;
            init;
        }

        internal List<string> Calls
        {
            get;
        } = [];

        public nint ForegroundWindow()
        {
            Calls.Add("foreground");
            return Root;
        }

        public Native.Point CursorPosition()
        {
            Calls.Add("cursor");
            if (Failure is not null)
            {
                throw Failure;
            }

            return new Native.Point(-20, 40);
        }

        public nint WindowAt(Native.Point point)
        {
            Assert.Equal(-20, point.X);
            Assert.Equal(40, point.Y);
            Calls.Add("point");
            return Child;
        }

        public nint RootOf(nint window)
        {
            Assert.Equal(Child, window);
            Calls.Add("root");
            return Root;
        }
    }
}
