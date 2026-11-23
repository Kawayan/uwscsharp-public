using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class PowerShellHostTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    [Trait("Category", "SandboxOnly")]
    public async Task VisiblePowerShellUsesTheHostCommandLineLimit(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const int maximumLength = 32766;
        const int display = 1;
        using var probe = new Probe(frontend);
        await using IWindowsAutomationSession session = probe.Session;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken token = timeout.Token;
        var host = new WindowsCommandHost(
            FixturePaths.Get(frontend, frontend + ".exe"));
        string receipt = probe.PathFor("boundary.txt");
        string path = receipt.Replace("'", "''", StringComparison.Ordinal);
        string suffix = $"\r\n[IO.File]::WriteAllText('{path}','実行済み')";
        string Command(int padding) => "#" + new string('あ', padding) + suffix;
        int HostLength(int padding) => host.BuildCommandLine(
            WindowsPowerShellCommand.Executable,
            WindowsPowerShellCommand.Build(Command(padding)), display).Length;

        int fits = 0;
        int exceeds = (maximumLength - WindowsPowerShellCommand.Build("").Length)
            / 8 * 3 - suffix.Length - 1;
        Assert.True(HostLength(fits) <= maximumLength);
        Assert.True(HostLength(exceeds) > maximumLength);
        while (exceeds - fits > 1)
        {
            int trial = fits + (exceeds - fits) / 2;
            if (HostLength(trial) <= maximumLength)
            {
                fits = trial;
            }
            else
            {
                exceeds = trial;
            }
        }

        string tooLong = Command(exceeds);
        Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() =>
            session.InvokeAsync(BuiltinFunctionNames.PowerShell,
                [Value.String(tooLong), Value.Numeric(0), Value.Numeric(display)],
                token).AsTask());
        Assert.NotNull(session.GetScriptErrorMessage(BuiltinFunctionNames.PowerShell,
            failure));
        Assert.False(File.Exists(receipt));

        Value visible = await session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(Command(fits)), Value.Numeric(0), Value.Numeric(display)],
            token);
        Assert.Equal(Value.String(""), visible);
        Assert.Equal("実行済み", await File.ReadAllTextAsync(receipt, token));

        File.Delete(receipt);
        await session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(tooLong)], token);
        Assert.Equal("実行済み", await File.ReadAllTextAsync(receipt, token));
    }

    [Theory]
    [InlineData("$PROFILE.CurrentUserCurrentHost")]
    [InlineData("$PROFILE.CurrentUserAllHosts")]
    [InlineData("$PROFILE.AllUsersCurrentHost")]
    [InlineData("$PROFILE.AllUsersAllHosts")]
    [Trait("Category", "SandboxOnly")]
    public async Task ExistingProfileIsNotLoaded(string profileExpression)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Value> InvokeAsync(string source) => session.InvokeAsync(
            BuiltinFunctionNames.PowerShell, [Value.String(source)],
            timeout.Token).AsTask();
        string profile = (await InvokeAsync($"[string]{profileExpression}"))
            .Text.TrimEnd();
        Assert.True(Path.IsPathFullyQualified(profile));
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);

        var file = new FileStream(profile, FileMode.CreateNew, FileAccess.Write);
        try
        {
            await using (file)
            {
                await file.WriteAsync(
                    "$global:UwscProfileProbe = 'loaded'"u8.ToArray());
            }

            Value result = await InvokeAsync(
                $"[IO.File]::Exists({profileExpression});"
                + "[bool](Get-Variable UwscProfileProbe -ErrorAction SilentlyContinue);"
                + $". {profileExpression};"
                + "Get-Variable UwscProfileProbe -ValueOnly");
            Assert.Equal(Value.String("True\r\nFalse\r\nloaded\r\n"), result);
        }
        finally
        {
            File.Delete(profile);
        }
    }

    [Theory]
    [InlineData("RemoteSigned", false, "local-script")]
    [InlineData("RemoteSigned", true, "PSSecurityException")]
    [InlineData("AllSigned", false, "PSSecurityException")]
    [InlineData("Bypass", true, "local-script")]
    [Trait("Category", "SandboxOnly")]
    public async Task HiddenPowerShellRespectsInheritedProcessPolicy(
        string policy, bool internetZone, string expected)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const string policyVariable = "PSExecutionPolicyPreference";
        string? previous = Environment.GetEnvironmentVariable(policyVariable);
        string directory =
            Directory.CreateTempSubdirectory("uwsc-powershell-policy-").FullName;
        string script = Path.Combine(directory, "local.ps1");
        try
        {
            await File.WriteAllTextAsync(script, "'local-script'");
            if (internetZone)
            {
                await File.WriteAllTextAsync(
                    script + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }

            Environment.SetEnvironmentVariable(policyVariable, policy);
            await using IWindowsAutomationSession session =
                new WindowsAutomation().CreateSession();
            using var deadline =
                new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string path = script.Replace("'", "''", StringComparison.Ordinal);
            string source = "$env:PSExecutionPolicyPreference;"
                + "$ErrorActionPreference='Stop';"
                + $"try {{ & '{path}' }} catch {{ $_.Exception.GetType().Name }};"
                + "'after-policy'";
            Value result = await session.InvokeAsync(
                BuiltinFunctionNames.PowerShell, [Value.String(source)],
                deadline.Token);

            string expectedText = $"{policy}\r\n{expected}\r\nafter-policy\r\n";
            Assert.True(result.Text == expectedText, result.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable(policyVariable, previous);
            File.Delete(script);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", 1, false)]
    [InlineData("UWSCSharp.Cli", 2, false)]
    [InlineData("UWSCSharp.Cli", 1, true)]
    [InlineData("UWSCSharp.Cli", 2, true)]
    [InlineData("UWSCSharp.Wpf", 1, false)]
    [InlineData("UWSCSharp.Wpf", 2, false)]
    [InlineData("UWSCSharp.Wpf", 1, true)]
    [InlineData("UWSCSharp.Wpf", 2, true)]
    [InlineData("UWSCSharp.Cli", -1, false)]
    [InlineData("UWSCSharp.Cli", 3, true)]
    [InlineData("UWSCSharp.Wpf", -1, true)]
    [InlineData("UWSCSharp.Wpf", 3, false)]
    public async Task HostPreservesConsoleStateOutputAndInputEof(
        string frontend, int display, bool noWait)
    {
        using var probe = new Probe(frontend);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task<Value> running = probe.Session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(probe.Script), Value.Numeric(noWait ? 1 : 0),
                Value.Numeric(display)], timeout.Token).AsTask();
        Process? engine = null;
        try
        {
            using JsonDocument metadata = await probe.ReadAsync("ready.json", timeout.Token);
            engine = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
            _ = engine.SafeHandle;
            nint window = (nint)metadata.RootElement.GetProperty("window").GetInt64();
            Assert.True(Native.IsWindow(window));
            Assert.True(Native.IsWindowVisible(window));
            Assert.Equal(display == 2, Native.IsIconic(window));
            Assert.False(engine.HasExited);
            if (noWait)
            {
                Assert.Equal("", (await running.WaitAsync(TimeSpan.FromSeconds(5))).Text);
            }
            else
            {
                Assert.False(running.IsCompleted);
            }

            await File.WriteAllTextAsync(probe.PathFor("go"), "go", timeout.Token);
            await engine.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, engine.ExitCode);
            Assert.Equal("", (await running).Text);
            using JsonDocument proof = await probe.ReadAsync("proof.json", timeout.Token);
            foreach (string property in new[] { "console", "output", "eof" })
            {
                Assert.True(proof.RootElement.GetProperty(property).GetBoolean(), property);
            }
        }
        finally
        {
            timeout.Cancel();
            await Record.ExceptionAsync(() => running);
            if (engine is not null)
            {
                if (!engine.HasExited)
                {
                    engine.Kill();
                    await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }

                engine.Dispose();
            }

            await probe.Session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task CancelingVisibleHostStopsPowerShellAndPreservesToken(string frontend)
    {
        using var probe = new Probe(frontend);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task<Value> running = probe.Session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(probe.Script), Value.Numeric(0), Value.Numeric(2)], cancel.Token).AsTask();
        try
        {
            using JsonDocument metadata = await probe.ReadAsync("ready.json", cancel.Token);
            var engine = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
            _ = engine.SafeHandle;
            Task observation = ObserveExitAsync(engine);
            cancel.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.Empty(error.Data);
            await observation;
        }
        finally
        {
            cancel.Cancel();
            await Record.ExceptionAsync(() => running);
            await probe.Session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task FrontendScriptRunsPublicPowerShell(string frontend)
    {
        using var probe = new Probe(frontend);
        string receipt = probe.PathFor("text.txt");
        string script = probe.PathFor("main.uws");
        string shownReceipt = probe.PathFor("日本語 & (確認).txt");
        await File.WriteAllTextAsync(script, $$"""
            DIM text=POWERSHELL("'日本語😀'")
            DIM f=FOPEN("{{receipt}}",F_WRITE8)
            DIM boundary=POWERSHELL("[Console]::Out.Write('A'*8MB);[Console]::Error.Write('B'*8MB)")
            DIM overflow="not-caught"
            TRY
                boundaryCheck=POWERSHELL("[Console]::Out.Write('A'*8MB);[Console]::Error.Write('B'*8MB);[Console]::Error.Write('!')")
            EXCEPT
                overflow="caught"
            ENDTRY
            DIM br=CHR(13)+CHR(10)
            DIM quote=CHR(34)
            DIM command="$name='日本語😀';$text=@"+quote+br
            command=command+quote+"$name"+quote+"\"+br+"second"+br+quote+"@"+br
            command=command+"[IO.File]::WriteAllText('{{shownReceipt}}',$text)"
            DIM shown=POWERSHELL(command,FALSE,TRUE)
            DIM report=text+br+LENGTH(boundary)+":"+COPY(boundary,8388608,2)+":"+overflow
            FPUT(f,report+br+"shown="+shown+":"+VARTYPE(shown),0)
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
            Assert.Equal("日本語😀\r\n\r\n16777216:AB:caught\r\nshown=:258\r\n",
                await File.ReadAllTextAsync(receipt));
            Assert.Equal("\"日本語😀\"\\\r\nsecond",
                await File.ReadAllTextAsync(shownReceipt));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await probe.Session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public Task ClosingOwnedConsoleEndsWaitingPowerShell(string frontend) =>
        CheckClosingConsoleAsync(frontend,
            console => Assert.True(Native.PostMessageW(console, 0x10, 0, 0)));

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    [Trait("Category", "SandboxOnly")]
    public Task ClickingConsoleCloseButtonEndsWaitingPowerShell(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        return CheckClosingConsoleAsync(frontend, ConsoleTestInput.ClickCloseButton);
    }

    private static async Task CheckClosingConsoleAsync(
        string frontend, Action<nint> close)
    {
        using var probe = new Probe(frontend);
        await using IWindowsAutomationSession session = probe.Session;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Task<Value> running = session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(probe.Script), Value.Numeric(0), Value.Numeric(1)],
            timeout.Token).AsTask();
        try
        {
            using JsonDocument ready = await probe.ReadAsync("ready.json", timeout.Token);
            using var engine = Process.GetProcessById(
                ready.RootElement.GetProperty("pid").GetInt32());
            _ = engine.SafeHandle;
            nint console = (nint)ready.RootElement.GetProperty("window").GetInt64();
            Assert.True(Native.IsWindowVisible(console));
            Assert.False(running.IsCompleted);
            close(console);
            await engine.WaitForExitAsync(timeout.Token);
            IOException error = await Assert.ThrowsAsync<IOException>(
                () => running.WaitAsync(timeout.Token));
            Assert.Contains("3221225786", error.Message);
            Assert.False(File.Exists(probe.PathFor("proof.json")));

            Value restarted = await session.InvokeAsync(
                BuiltinFunctionNames.PowerShell,
                [Value.String("'再実行'")], timeout.Token);
            Assert.Equal(Value.String("再実行\r\n"), restarted);

            File.Delete(probe.PathFor("ready.json"));
            running = session.InvokeAsync(BuiltinFunctionNames.PowerShell,
                [Value.String(probe.Script), Value.Numeric(0), Value.Numeric(1)],
                timeout.Token).AsTask();
            using JsonDocument next = await probe.ReadAsync("ready.json", timeout.Token);
            using var nextEngine = Process.GetProcessById(
                next.RootElement.GetProperty("pid").GetInt32());
            _ = nextEngine.SafeHandle;
            nint nextConsole = (nint)next.RootElement.GetProperty("window").GetInt64();
            Assert.True(Native.IsWindowVisible(nextConsole));
            await File.WriteAllTextAsync(probe.PathFor("go"), "go", timeout.Token);
            Assert.Equal(Value.String(""), await running.WaitAsync(timeout.Token));
            await nextEngine.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, nextEngine.ExitCode);
            using JsonDocument proof = await probe.ReadAsync("proof.json", timeout.Token);
            Assert.True(proof.RootElement.GetProperty("output").GetBoolean());
        }
        finally
        {
            timeout.Cancel();
            await Record.ExceptionAsync(() => running);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    [Trait("Category", "SandboxOnly")]
    public async Task ControlCStopsPowerShellInTheOwnedConsole(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var probe = new Probe(frontend);
        await using IWindowsAutomationSession session = probe.Session;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Task<Value> running = session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(probe.Script), Value.Numeric(0), Value.Numeric(1)],
            timeout.Token).AsTask();
        try
        {
            using JsonDocument ready = await probe.ReadAsync("ready.json", timeout.Token);
            using var engine = Process.GetProcessById(
                ready.RootElement.GetProperty("pid").GetInt32());
            _ = engine.SafeHandle;
            nint console = (nint)ready.RootElement.GetProperty("window").GetInt64();
            Assert.True(Native.IsWindowVisible(console));
            Assert.False(Native.IsIconic(console));
            var className = new StringBuilder(128);
            Assert.True(Native.GetClassNameW(console, className, className.Capacity) > 0);
            Assert.Equal("ConsoleWindowClass", className.ToString());
            Assert.False(engine.HasExited);
            ConsoleTestInput.SendControlC(console);

            await engine.WaitForExitAsync(timeout.Token);
            Assert.Equal(Value.String(""), await running.WaitAsync(timeout.Token));
            Assert.False(File.Exists(probe.PathFor("proof.json")));
        }
        finally
        {
            timeout.Cancel();
            await Record.ExceptionAsync(() => running);
        }

    }

    private static async Task ObserveExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, process.ExitCode);
        }
    }

    private sealed class Probe : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(),
            "uwsc ps host " + Guid.NewGuid().ToString("N"));

        internal IWindowsAutomationSession Session
        {
            get;
        }

        internal string Script => $$"""
            Add-Type -TypeDefinition 'using System;using System.Text;using System.Runtime.InteropServices;public static class PsConsoleProof{[DllImport("kernel32.dll")]public static extern IntPtr GetConsoleWindow();[DllImport("kernel32.dll")]public static extern IntPtr GetStdHandle(int n);[DllImport("kernel32.dll")]public static extern bool GetConsoleMode(IntPtr h,out uint m);[DllImport("kernel32.dll",CharSet=CharSet.Unicode)]public static extern bool ReadConsoleOutputCharacterW(IntPtr h,StringBuilder b,uint n,uint origin,out uint read);}';
            $ready=@{pid=$PID;window=[PsConsoleProof]::GetConsoleWindow().ToInt64()};
            [IO.File]::WriteAllText({{Literal(PathFor("ready.json.pending"))}},($ready|ConvertTo-Json -Compress));
            [IO.File]::Move({{Literal(PathFor("ready.json.pending"))}},{{Literal(PathFor("ready.json"))}});
            $deadline=[Diagnostics.Stopwatch]::StartNew();
            while(-not [IO.File]::Exists({{Literal(PathFor("go"))}})){
                if($deadline.ElapsedMilliseconds -gt 30000){exit 99};
                Start-Sleep -Milliseconds 25
            };
            [Console]::Out.Write('ps-console-proof');
            $buffer=[Text.StringBuilder]::new(4096);
            [uint32]$read=0;[uint32]$mode=0;
            $handle=[PsConsoleProof]::GetStdHandle(-11);
            $proof=@{console=[PsConsoleProof]::GetConsoleMode($handle,[ref]$mode);output=([PsConsoleProof]::ReadConsoleOutputCharacterW($handle,$buffer,4096,0,[ref]$read) -and $buffer.ToString().Contains('ps-console-proof'));eof=([Console]::OpenStandardInput().ReadByte() -eq -1)};
            [IO.File]::WriteAllText({{Literal(PathFor("proof.json.pending"))}},($proof|ConvertTo-Json -Compress));
            [IO.File]::Move({{Literal(PathFor("proof.json.pending"))}},{{Literal(PathFor("proof.json"))}});
            """;

        internal Probe(string frontend)
        {
            Directory.CreateDirectory(directory);
            Session = new WindowsAutomation
            {
                Commands = new WindowsCommandOptions
                {
                    CommandHost = new WindowsCommandHost(FixturePaths.Get(frontend, frontend + ".exe"))
                }
            }.CreateSession();
        }

        internal string PathFor(string name) => Path.Combine(directory, name);

        internal async Task<JsonDocument> ReadAsync(string name, CancellationToken token)
        {
            string path = PathFor(name);
            while (!File.Exists(path))
            {
                await Task.Delay(25, token);
            }

            return await ProcessReceiptReader.ReadJsonAsync(path, token);
        }

        public void Dispose()
        {
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }

        private static string Literal(string text) => "'"
            + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
