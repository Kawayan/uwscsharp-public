using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class DosCommandHostTests
{
    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task HiddenDosCommandHonorsUserAutoRun()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const string settings = @"Software\Microsoft\Command Processor";
        using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(settings);
        Assert.Null(machine?.GetValue("AutoRun"));
        bool existed;
        using (RegistryKey? prior = Registry.CurrentUser.OpenSubKey(settings))
        {
            existed = prior is not null;
            Assert.Null(prior?.GetValue("AutoRun"));
        }

        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        using var deadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(settings))
            {
                key.SetValue("AutoRun", "echo uwsc-autorun", RegistryValueKind.String);
            }

            Assert.Equal("uwsc-autorun\r\nuwsc-body\r\n",
                (await InvokeAsync()).Text);
        }
        finally
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                settings, writable: true))
            {
                key?.DeleteValue("AutoRun", throwOnMissingValue: false);
            }

            if (!existed)
            {
                Registry.CurrentUser.DeleteSubKey(settings,
                    throwOnMissingSubKey: false);
            }
        }

        Assert.Equal("uwsc-body\r\n", (await InvokeAsync()).Text);

        ValueTask<Value> InvokeAsync() => session.InvokeAsync(
            BuiltinFunctionNames.DosCmd, [Value.String("echo uwsc-body")],
            deadline.Token);
    }

    [Fact]
    public async Task FailingHostDoesNotMasqueradeAsEmptyResult()
    {
        string executable = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".uws");
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = new WindowsCommandOptions
            {
                CommandHost = new WindowsCommandHost(executable, missing)
            }
        }.CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
                [Value.String("echo ignored"), Value.Numeric(0), Value.Numeric(1)], timeout.Token));
        Assert.Contains("異常終了", error.Message);
        Assert.Empty(error.Data);
        Assert.NotNull(session.GetScriptErrorMessage(BuiltinFunctionNames.DosCmd, error));
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task VisibleHostHasRealConsoleOutputAndNullInput(string frontend)
    {
        string marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string host = FixturePaths.Get(frontend, frontend + ".exe");
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = new WindowsCommandOptions { CommandHost = new WindowsCommandHost(host) }
        }.CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Value result = await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
                [Value.String($"\"{fixture}\" --process-output-probe console 0 37 \"{marker}\""),
                    Value.Numeric(0), Value.Numeric(1)], timeout.Token);
            Assert.Equal("", result.Text);
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            foreach (string property in new[] { "Console", "Visible", "Output", "Eof" })
            {
                Assert.True(proof.RootElement.GetProperty(property).GetBoolean(), property);
            }
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", "close-message")]
    [InlineData("UWSCSharp.Wpf", "close-message")]
    [InlineData("UWSCSharp.Cli", "control-event")]
    [InlineData("UWSCSharp.Wpf", "control-event")]
    [InlineData("UWSCSharp.Cli", "close-button")]
    [InlineData("UWSCSharp.Wpf", "close-button")]
    [InlineData("UWSCSharp.Cli", "control-key")]
    [InlineData("UWSCSharp.Wpf", "control-key")]
    [InlineData("UWSCSharp.Cli", "cancellation")]
    [InlineData("UWSCSharp.Wpf", "cancellation")]
    [Trait("Category", "SandboxOnly")]
    public async Task OwnedConsoleInterruptionEndsDosCommandAndAllowsRetry(
        string frontend, string action)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-dos-console-").FullName;
        string marker = Path.Combine(directory, "console.json");
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string host = FixturePaths.Get(frontend, frontend + ".exe");
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = new WindowsCommandOptions
            {
                CommandHost = new WindowsCommandHost(host)
            }
        }.CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        CancellationToken token = timeout.Token;
        using var commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        string command = $"\"{fixture}\" --process-output-probe "
            + $"console-hold 0 37 \"{marker}\"";
        Task<Value> running = session.InvokeAsync(BuiltinFunctionNames.DosCmd,
            [Value.String(command), Value.Numeric(0), Value.Numeric(1)],
            commandCancellation.Token).AsTask();
        try
        {
            while (!File.Exists(marker))
            {
                Assert.False(running.IsCompleted);
                await Task.Delay(25, token);
            }

            using var ready = JsonDocument.Parse(
                await File.ReadAllTextAsync(marker, token));
            using var child = Process.GetProcessById(
                ready.RootElement.GetProperty("Pid").GetInt32());
            _ = child.SafeHandle;
            nint console = (nint)ready.RootElement.GetProperty("Window").GetInt64();
            Assert.True(Native.IsWindowVisible(console));
            Assert.False(Native.IsIconic(console));
            Assert.False(running.IsCompleted);
            switch (action)
            {
                case "close-message":
                    Assert.True(Native.PostMessageW(console, 0x0010, 0, 0));
                    break;
                case "control-event":
                    await File.WriteAllTextAsync(marker + ".interrupt", "", token);
                    break;
                case "close-button":
                    ConsoleTestInput.ClickCloseButton(console);
                    break;
                case "control-key":
                    ConsoleTestInput.SendControlC(console);
                    break;
                case "cancellation":
                    commandCancellation.Cancel();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            if (action == "cancellation")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => running);
                await child.WaitForExitAsync(token);
            }
            else if (action is "control-event" or "control-key")
            {
                Value interrupted = await running.WaitAsync(token);
                Assert.Equal("", interrupted.Text);
                await child.WaitForExitAsync(token);
                Assert.Equal(unchecked((int)0xc000013a), child.ExitCode);
            }
            else
            {
                IOException failure = await Assert.ThrowsAsync<IOException>(
                    () => running.WaitAsync(token));
                Assert.Contains("表示用コマンドホストが異常終了", failure.Message);
                Assert.NotNull(session.GetScriptErrorMessage(
                    BuiltinFunctionNames.DosCmd, failure));
                await child.WaitForExitAsync(token);
            }

            Value result = await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
                [Value.String("echo retry")], token);
            Assert.Equal("retry\r\n", result.Text);
        }
        finally
        {
            timeout.Cancel();
            await Record.ExceptionAsync(() => running);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    [Trait("Category", "SandboxOnly")]
    public async Task NoWaitReturnsBeforeTheDisplayedConsoleCloses(
        string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-dos-console-").FullName;
        string marker = Path.Combine(directory, "console.json");
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string host = FixturePaths.Get(frontend, frontend + ".exe");
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = new WindowsCommandOptions
            {
                CommandHost = new WindowsCommandHost(host)
            }
        }.CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        CancellationToken token = timeout.Token;
        string command = $"\"{fixture}\" --process-output-probe "
            + $"console-hold 0 37 \"{marker}\"";
        Process? child = null;
        try
        {
            Value launched = await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
                [Value.String(command), Value.Numeric(1), Value.Numeric(1)], token);
            Assert.Equal("", launched.Text);
            while (!File.Exists(marker))
            {
                await Task.Delay(25, token);
            }

            using var ready = JsonDocument.Parse(
                await File.ReadAllTextAsync(marker, token));
            child = Process.GetProcessById(
                ready.RootElement.GetProperty("Pid").GetInt32());
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            nint console = (nint)ready.RootElement.GetProperty("Window").GetInt64();
            Assert.True(Native.IsWindowVisible(console));
            Assert.False(Native.IsIconic(console));
            Assert.True(Native.PostMessageW(console, 0x0010, 0, 0));
            await child.WaitForExitAsync(token);

            Value retry = await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
                [Value.String("echo retry")], token);
            Assert.Equal("retry\r\n", retry.Text);
        }
        finally
        {
            if (child is not null && !child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            child?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    [Trait("Category", "SandboxOnly")]
    public async Task DisplayedNoWaitCommandSurvivesFrontendExit(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-doscmd-no-wait-").FullName;
        try
        {
            string script = Path.Combine(directory, "main.uws");
            string marker = Path.Combine(directory, "console.json");
            string returned = Path.Combine(directory, "returned.txt");
            string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
                "UWSCSharp.ControlFixture.exe");
            await File.WriteAllTextAsync(script, $$"""
                DIM quote=CHR(34)
                DIM cmd=quote+"{{fixture}}"+quote
                cmd=cmd+" --process-output-probe console-after-gate 0 37 "
                cmd=cmd+quote+"{{marker}}"+quote
                DIM result=DOSCMD(cmd,TRUE,TRUE)
                DIM f=FOPEN("{{returned}}",F_WRITE8)
                FPUT(f,"value=["+result+"]",0)
                FCLOSE(f)
                """, new UTF8Encoding(true));

            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            const char quote = '"';
            string commandLine = $"{quote}{executable}{quote} {quote}{script}{quote}";
            await using SandboxProcessWatchdog watchdog =
                await SandboxProcessWatchdog.StartAsync(executable,
                    commandLine, TimeSpan.FromSeconds(30));
            using var process = Process.GetProcessById(
                checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            CancellationToken token = deadline.Token;
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, process.ExitCode == 0
                ? null : Encoding.UTF8.GetString(
                    await watchdog.Output.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Equal("value=[]\r\n", await File.ReadAllTextAsync(returned, token));
            Assert.False(File.Exists(marker));

            await File.WriteAllTextAsync(marker + ".go", "go", token);
            while (!File.Exists(marker))
            {
                await Task.Delay(25, token);
            }

            using var ready = JsonDocument.Parse(
                await File.ReadAllTextAsync(marker, token));
            using var child = Process.GetProcessById(
                ready.RootElement.GetProperty("Pid").GetInt32());
            Assert.False(child.HasExited);
            Assert.True(ready.RootElement.GetProperty("OutputIsConsole").GetBoolean());
            nint console = (nint)ready.RootElement.GetProperty("Window").GetInt64();
            Assert.True(Native.IsWindowVisible(console));
            Assert.False(Native.IsIconic(console));
            Assert.True(Native.PostMessageW(console, 0x0010, 0, 0));
            await child.WaitForExitAsync(token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task FrontendScriptUsesPublicCommandAndReturnsResult(string frontend)
    {
        string directory = Path.Combine(Path.GetTempPath(), "uwsc command " + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "command.uws");
        string receipt = Path.Combine(directory, "result.txt");
        string redirected = Path.Combine(directory, "日本語 & (確認).txt");
        await File.WriteAllTextAsync(script, $$"""
            DIM text=DOSCMD("echo public-result")
            DIM f=FOPEN("{{receipt}}",F_WRITE8)
            DIM ansiCode=DOSCMD("echo ignored & exit /b 37",FALSE,FALSE,2)
            DIM unicodeCode=DOSCMD("echo ignored & exit /b -1",FALSE,FALSE,3)
            DIM quote=CHR(34)
            DIM target=quote+"{{redirected}}"+quote
            DIM command="echo first>"+target+"&&echo second>>"+target+"&&exit /b 37"
            DIM shown=DOSCMD(command,FALSE,TRUE,2)
            DIM br=CHR(13)+CHR(10)
            DIM modes="mode2="+ansiCode+":"+VARTYPE(ansiCode)+br
            modes=modes+"mode3="+unicodeCode+":"+VARTYPE(unicodeCode)
            modes=modes+br+"shown="+shown+":"+VARTYPE(shown)
            FPUT(f,text+br+modes,0)
            FCLOSE(f)
            """, new UTF8Encoding(true));
        var start = new ProcessStartInfo(FixturePaths.Get(frontend, frontend + ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(script);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
            Assert.Equal(
                "public-result\r\n\r\nmode2=37:3\r\nmode3=-1:3\r\nshown=:258\r\n",
                await File.ReadAllTextAsync(receipt));
            Assert.Equal("first\r\nsecond\r\n",
                await File.ReadAllTextAsync(redirected));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}
