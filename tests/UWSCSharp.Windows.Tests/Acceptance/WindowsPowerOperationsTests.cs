using System.ComponentModel;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowsPowerOperationsTests
{
    [Theory]
    [InlineData("P_POWEROFF", 8u, true)]
    [InlineData("P_POWEROFF OR P_FORCE", 12u, true)]
    [InlineData("P_LOGOFF", 0u, false)]
    [InlineData("P_LOGOFF OR P_FORCE", 4u, false)]
    [InlineData("P_REBOOT", 2u, true)]
    [InlineData("P_REBOOT OR P_FORCE", 6u, true)]
    public async Task SendsSessionFlags(string command, uint expected, bool privilege)
    {
        var events = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = request =>
            {
                WindowsPowerOperations.Execute(request,
                    flags => events.Add($"exit:{flags}"),
                    _ => throw new InvalidOperationException(),
                    operation =>
                    {
                        events.Add("enable");
                        operation();
                        events.Add("restore");
                    },
                    UnexpectedDesktopCommand, UnexpectedExecutionState,
                    UnexpectedRestart);
                return null;
            }
        };
        await new Interpreter().RunWithExitCodeAsync(
            $"POFF({command})\nPRINT \"after\"",
            new CallbackScriptHost(events.Add), default, options);

        string[] expectedEvents = privilege
            ? ["enable", $"exit:{expected}", "restore", "after"]
            : [$"exit:{expected}", "after"];
        Assert.Equal(expectedEvents, events);
    }

    [Theory]
    [InlineData("P_SUSPEND", true)]
    [InlineData("P_SUSPEND2", false)]
    public async Task SendsSuspendMode(string command, bool hibernate)
    {
        var events = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = request =>
            {
                WindowsPowerOperations.Execute(request,
                    _ => throw new InvalidOperationException(),
                    value => events.Add($"suspend:{value}"),
                    operation =>
                    {
                        events.Add("enable");
                        operation();
                        events.Add("restore");
                    },
                    UnexpectedDesktopCommand, UnexpectedExecutionState,
                    UnexpectedRestart);
                return null;
            }
        };
        await new Interpreter().RunWithExitCodeAsync(
            $"POFF({command})",
            new CallbackScriptHost(_ => { }), default, options);

        Assert.Equal(["enable", $"suspend:{hibernate}", "restore"], events);
    }

    [Fact]
    public void DoesNotCallWindowsWhenPrivilegeAcquisitionFails()
    {
        var failure = new Win32Exception(1300);
        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            WindowsPowerOperations.Execute(
                PowerCommandRequest.Read([Value.Numeric(64)]),
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
                _ => throw failure, UnexpectedDesktopCommand, UnexpectedExecutionState,
                UnexpectedRestart));

        Assert.Same(failure, error);
    }

    [Fact]
    public void PropagatesWindowsFailure()
    {
        var failure = new Win32Exception(5);
        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            WindowsPowerOperations.Execute(
                PowerCommandRequest.Read([Value.Numeric(128)]),
                _ => throw failure,
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
                UnexpectedDesktopCommand, UnexpectedExecutionState,
                UnexpectedRestart));

        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData("P_MONIPOWER", 0xF170, 1, false)]
    [InlineData("P_MONIPOWER2", 0xF170, 2, false)]
    [InlineData("p_monipower3", 0xF170, -1, true)]
    [InlineData("P_SCREENSAVE", 0xF140, 0, false)]
    public async Task BroadcastsDesktopCommandWithoutPrivilege(
        string command, int expectedCommand, int expectedParameter,
        bool resetDisplayTimer)
    {
        var calls = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = request =>
            {
                WindowsPowerOperations.Execute(request,
                    _ => throw new InvalidOperationException(),
                    _ => throw new InvalidOperationException(),
                    _ => throw new InvalidOperationException(),
                    (window, message, code, parameter) =>
                    {
                        Assert.Equal((nint)0xFFFF, window);
                        Assert.Equal(0x0112u, message);
                        Assert.Equal((nint)expectedCommand, code);
                        Assert.Equal((nint)expectedParameter, parameter);
                        calls.Add("post");
                    },
                    flags =>
                    {
                        Assert.Equal(2u, flags);
                        calls.Add("display");
                    },
                    UnexpectedRestart);
                return null;
            }
        };
        await new Interpreter().RunWithExitCodeAsync(
            $"POFF({command})",
            new CallbackScriptHost(_ => { }), default, options);

        string[] expected = resetDisplayTimer ? ["post", "display"] : ["post"];
        Assert.Equal(expected, calls);
    }

    [Fact]
    public void PropagatesDesktopBroadcastFailure()
    {
        var failure = new Win32Exception(5);
        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            WindowsPowerOperations.Execute(
                PowerCommandRequest.Read([Value.Numeric(4096)]),
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
                (_, _, _, _) => throw failure,
                UnexpectedExecutionState, UnexpectedRestart));

        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void RoutesRestartToApplication(int flag, bool expected)
    {
        var calls = new List<bool>();
        WindowsPowerOperations.Execute(
            PowerCommandRequest.Read([Value.Numeric(16384), Value.Numeric(flag)]),
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            UnexpectedDesktopCommand, UnexpectedExecutionState, calls.Add);

        Assert.Equal(expected, Assert.Single(calls));
    }

    private static void UnexpectedRestart(bool restartScript) =>
        throw new InvalidOperationException();

    private static void UnexpectedDesktopCommand(
        nint window, uint message, nint command, nint parameter) =>
        throw new InvalidOperationException();

    private static void UnexpectedExecutionState(uint flags) =>
        throw new InvalidOperationException();
}
