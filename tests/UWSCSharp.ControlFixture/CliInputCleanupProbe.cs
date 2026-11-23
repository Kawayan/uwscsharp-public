using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UWSCSharp.Cli;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 所有コンソールだけで手動回収の読取り・取消・復元を確認します。
/// </summary>
internal static class CliInputCleanupProbe
{
    private static readonly Handler Ignore = _ => true;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static int Run(string[] args)
    {
        if (args is not [string cli, string receipt])
        {
            return 2;
        }

        bool owns = false;
        nint input = -1;
        nint output = -1;
        var checks = new List<object>();
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-cli-recovery-" + Guid.NewGuid());
        try
        {
            if (GetConsoleProcessList(new uint[16], 16) != 0)
            {
                Check(FreeConsole());
            }

            Check(AllocConsole());
            owns = true;
            ShowWindow(GetConsoleWindow(), 0);
            Check(SetConsoleCtrlHandler(Ignore, true));
            input = Open("CONIN$");
            output = Open("CONOUT$");
            Check(SetStdHandle(-10, input));
            Check(SetStdHandle(-12, output));
            Check(GetConsoleMode(input, out uint originalMode));
            Require(GetConsoleProcessList(new uint[16], 16) == 1, "所有consoleが分離されていません。");
            Directory.CreateDirectory(directory);
            VerifyCli(cli, directory, checks);
            VerifyRedirectedTerminals(cli, directory, checks);
            foreach (string variant in new[] { "retry", "retry-exit", "exit", "cancel-wait", "cancel-retry", "already-cancelled", "invalid-unicode", "public-api", "public-script-0", "public-script-1", "public-script-2" })
            {
                VerifyRecovery(input, originalMode, variant, checks);
            }
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach (string variant in new[] { "retry", "exit", "cancel-wait", "cancel-retry" })
                {
                    VerifyApplication(input, originalMode, directory, mode, variant, checks);
                }
            }
            VerifyLostInput(ref input, originalMode, checks);
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                console_scope = "owned-console-only",
                checks,
                limits = "コンソール入力はWriteConsoleInput、解除失敗は偽フック。公開スクリプトと通常Mainが共有する実行処理の受入を含むが、配布apphostの実OS障害や物理入力の再現ではない。"
            }), Utf8);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                checks,
                error = error.ToString()
            }), Utf8);
            return 1;
        }
        finally
        {
            SetConsoleCtrlHandler(Ignore, false);
            if (input != -1)
            {
                CloseHandle(input);
            }

            if (output != -1)
            {
                CloseHandle(output);
            }

            if (owns)
            {
                FreeConsole();
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void VerifyCli(string cli, string directory, List<object> checks)
    {
        string script = Path.Combine(directory, "arguments.uws");
        File.WriteAllText(script, "PRINT PARAM_STR[0]\nPRINT PARAM_STR[1]\nEXITEXIT 37\n", Utf8);
        foreach (bool manual in new[] { false, true })
        {
            string[] arguments = manual
                ? [CliLaunchOptions.ManualCleanupOption, script, "001", CliLaunchOptions.ManualCleanupOption]
                : [script, "001", CliLaunchOptions.ManualCleanupOption];
            var start = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Utf8
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            try
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                string text = stdout.GetAwaiter().GetResult().Replace("\r\n", "\n", StringComparison.Ordinal);
                Require(process.ExitCode == 37 && text == "001\n--input-cleanup=manual\n", "起動引数と終了コードが違います。");
                checks.Add(new
                {
                    name = manual ? "manual-no-pending" : "ordinary-arguments",
                    code = process.ExitCode
                });
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
        }
    }

    private static void VerifyRedirectedTerminals(string cli, string directory, List<object> checks)
    {
        foreach (bool redirectInput in new[] { true, false })
        {
            var start = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = redirectInput,
                RedirectStandardError = !redirectInput,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Utf8
            };
            if (!redirectInput)
            {
                start.StandardErrorEncoding = Utf8;
            }

            start.ArgumentList.Add(CliLaunchOptions.ManualCleanupOption);
            start.ArgumentList.Add(Path.Combine(directory, "arguments.uws"));
            start.ArgumentList.Add("must-not-run");
            start.ArgumentList.Add("must-not-run");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            try
            {
                if (redirectInput)
                {
                    process.StandardInput.Close();
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string>? stderr = !redirectInput ? process.StandardError.ReadToEndAsync() : null;
                process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                Require(process.ExitCode == 2 && stdout.GetAwaiter().GetResult() == "", "端末条件が実行前に拒否されません。");
                if (stderr is not null)
                {
                    Require(stderr.GetAwaiter().GetResult().Contains("起動設定", StringComparison.Ordinal), "起動拒否の診断がありません。");
                }
                checks.Add(new
                {
                    name = redirectInput ? "stdin-redirect" : "stderr-redirect",
                    code = process.ExitCode
                });
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
        }
    }

    private static void VerifyLostInput(ref nint input, uint originalMode, List<object> checks)
    {
        Check(SetConsoleMode(input, originalMode & ~1u));
        Check(FlushConsoleInputBuffer(input));
        nint owned = input;
        bool closed = false;
        using var writer = new PromptWriter();
        writer.OnPrompt = () =>
        {
            Check(CloseHandle(owned));
            closed = true;
        };
        try
        {
            var controller = new CliInputCleanupController(new Cleanup(), writer);
            int code = controller.CompleteAsync(37, true, new CliStopRequests(() => { }),
                static () => new NativeConsoleRecoveryInput()).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Require(code == 37 && writer.ToString().Contains("受付", StringComparison.Ordinal)
                && writer.ToString().Contains("後始末", StringComparison.Ordinal), "端末消失時の診断と終了結果が違います。");
            checks.Add(new
            {
                name = "input-handle-loss",
                code,
                input_failure_preserved = true
            });
        }
        finally
        {
            if (closed)
            {
                input = Open("CONIN$");
                Check(SetStdHandle(-10, input));
            }
            Check(SetConsoleMode(input, originalMode));
        }
    }

    private static void VerifyRecovery(nint input, uint originalMode, string variant, List<object> checks)
    {
        Check(SetConsoleMode(input, originalMode & ~1u));
        Check(FlushConsoleInputBuffer(input));
        int scriptStops = 0;
        var stop = new CliStopRequests(() => scriptStops++);
        var fake = new Cleanup();
        ICliInputCleanup cleanup = fake;
        WindowsSelectiveInputCleanup? shared = null;
        Hook? hook = null;
        if (variant == "public-api" || variant.StartsWith("public-script-", StringComparison.Ordinal))
        {
            shared = new WindowsSelectiveInputCleanup();
            hook = new Hook();
            Hook selected = hook;
            var provider = new WindowsAutomation(_ => throw new InvalidOperationException(),
                swappedMouseButtons: false, selectiveInputFactory: (_, devices) =>
                {
                    selected.Devices = devices;
                    return selected;
                })
            {
                SelectiveInputCleanup = shared
            };
            hook.Failures = 2;
            if (variant == "public-api")
            {
                IWindowsAutomationSession root = provider.CreateSession();
                ((ISelectiveInputOwnershipSession)root).SetSelectiveInputAsync(new(0, 1), default).GetAwaiter().GetResult();
                try
                {
                    root.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception) { Require(shared.PendingCount == 1, "失敗した所有者が保持されていません。"); }
            }
            else
            {
                int mode = int.Parse(variant[^1..], System.Globalization.CultureInfo.InvariantCulture);
                bool failed = false;
                try
                {
                    new Interpreter(windows: provider).RunAsync($"LOCKHARDEX(0,{mode})", _ => { })
                        .GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is InvalidOperationException or AggregateException
                    or System.ComponentModel.Win32Exception or ScriptException)
                {
                    failed = true;
                }
                Require(failed && shared.PendingCount == 1 && hook.Stops == 2,
                    "公開スクリプトの終了失敗と未回収所有者");
                Require(selected.Devices == (mode == 0 ? SelectiveInputDevices.All : (SelectiveInputDevices)mode),
                    "公開モードが回収対象へ渡っていません。");
            }
            cleanup = new WindowsCliInputCleanup(shared);
        }

        using var writer = new PromptWriter();
        int prompts = 0;
        writer.OnPrompt = () =>
        {
            prompts++;
            if (variant == "cancel-wait")
            {
                Check(GenerateConsoleCtrlEvent(0, 0));
            }
            else
            {
                Write(input, variant == "exit" || ((variant is "retry-exit" or "invalid-unicode") && prompts > 1)
                ? "exit\r" : variant == "invalid-unicode" ? "再試行\r" : "retry\r");
            }
        };
        fake.Retry = () =>
        {
            if (variant == "cancel-retry")
            {
                Check(GenerateConsoleCtrlEvent(0, 0));
                Require(SpinWait.SpinUntil(() => stop.ExitRequested, 5000), "回収中の取消が届きません。");
            }
            fake.PendingCount = variant == "retry-exit" ? 1 : 0;
            return Task.FromResult(new CliCleanupAttempt([new InvalidOperationException("過去の障害")], fake.PendingCount));
        };
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Request(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (variant == "already-cancelled")
            {
                stop.Request();
            }

            int original = variant == "already-cancelled" ? 130 : 1;
            var controller = new CliInputCleanupController(cleanup, writer);
            int code = controller.CompleteAsync(original, true, stop,
                static () => new NativeConsoleRecoveryInput()).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            int expected = variant is "cancel-wait" or "cancel-retry" or "already-cancelled" ? 130 : 1;
            Require(code == expected, variant + ": 終了コード " + code);
            Check(GetConsoleMode(input, out uint restored));
            Require(restored == (originalMode & ~1u), "端末設定が復元されていません。");
            Require(scriptStops == (variant == "already-cancelled" ? 1 : 0), "回収の取消が実行へ再送されました。");
            if (shared is not null)
            {
                Require(shared!.PendingCount == 0 && !hook!.IsRegistered && hook.Stops == 3, "共有回収APIが接続されていません。");
            }
            else
            {
                Require(fake.Retries == (variant is "exit" or "cancel-wait" or "invalid-unicode" ? 0 : 1), "再回収回数が違います。");
            }

            if (variant == "invalid-unicode")
            {
                Require(writer.ToString().Contains("再試行", StringComparison.Ordinal), "Unicode入力が違います。");
            }

            checks.Add(new
            {
                name = variant,
                code,
                prompts,
                pending = cleanup.PendingCount,
                registered_devices = hook is null ? (int?)null : (int)hook.Devices,
                stops = hook?.Stops,
                restored = true
            });
        }
        finally
        {
            stop.Finish();
            Console.CancelKeyPress -= handler;
            if (shared is not null)
            {
                hook!.Failures = 0;
                shared.RetryAsync().GetAwaiter().GetResult();
            }
            Check(SetConsoleMode(input, originalMode));
        }
    }

    private static void VerifyApplication(nint input, uint originalMode,
        string directory, int mode, string variant, List<object> checks)
    {
        Check(SetConsoleMode(input, originalMode & ~1u));
        Check(FlushConsoleInputBuffer(input));
        string script = Path.Combine(directory, $"application-{mode}-{variant}.uws");
        File.WriteAllText(script, $"LOCKHARDEX(0,{mode})\n", Utf8);
        var hook = new Hook { Failures = 2 };
        var runtime = new ApplicationRuntime(hook);
        using var cancellationObserved = new ManualResetEventSlim();
        ConsoleCancelEventHandler observer = (_, _) => cancellationObserved.Set();
        bool observing = false;
        int prompts = 0;
        using var writer = new PromptWriter();
        writer.OnPrompt = () =>
        {
            prompts++;
            Require(prompts == 1, "回収の受付を繰り返しています。");
            // 共通処理の取消handlerより後に登録し、通知の完了を確認します。
            if (variant is "cancel-wait" or "cancel-retry")
            {
                Console.CancelKeyPress += observer;
                observing = true;
            }
            if (variant == "cancel-wait")
            {
                Check(GenerateConsoleCtrlEvent(0, 0));
            }
            else
            {
                Write(input, variant == "exit" ? "exit\r" : "retry\r");
            }
        };
        hook.OnStop = () =>
        {
            if (variant == "cancel-retry" && hook.Stops == 3)
            {
                Check(GenerateConsoleCtrlEvent(0, 0));
                Require(cancellationObserved.Wait(5000), "再回収中の取消通知が届きません。");
            }
        };
        TextWriter originalError = Console.Error;
        try
        {
            Console.SetError(writer);
            int code = CliApplication.RunAsync(
                [CliLaunchOptions.ManualCleanupOption, script], runtime)
                .WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            bool recovered = variant is "retry" or "cancel-retry";
            int expected = variant.StartsWith("cancel-", StringComparison.Ordinal) ? 130 : 1;
            Require(code == expected, "共通実行処理の終了コード: " + code);
            Require(runtime.Cleanup.PendingCount == (recovered ? 0 : 1), "未回収数が違います。");
            Require(hook.Stops == (recovered ? 3 : 2), "解除試行数が違います。");
            Require(hook.Devices == (mode == 0 ? SelectiveInputDevices.All : (SelectiveInputDevices)mode),
                "公開スクリプトのモードが違います。");
            Require(prompts == 1, "手動回収の受付がありません。");
            Check(GetConsoleMode(input, out uint restored));
            Require(restored == (originalMode & ~1u), "端末設定が復元されていません。");
            checks.Add(new
            {
                name = $"application-script-{mode}-{variant}",
                code,
                pending = runtime.Cleanup.PendingCount,
                prompts,
                stops = hook.Stops,
                registered_devices = (int)hook.Devices,
                restored = true
            });
        }
        finally
        {
            Console.SetError(originalError);
            if (observing)
            {
                Console.CancelKeyPress -= observer;
            }
            hook.OnStop = null;
            hook.Failures = 0;
            runtime.Cleanup.RetryAsync().GetAwaiter().GetResult();
            Require(runtime.Cleanup.PendingCount == 0, "検証用の未回収所有者が残っています。");
            Check(SetConsoleMode(input, originalMode));
        }
    }

    private sealed class ApplicationRuntime(Hook hook) : CliWindowsRuntime
    {
        internal override IWindowsAutomation CreateAutomation(
            Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
            WindowsCommandOptions commands) =>
            new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
                swappedMouseButtons: false, selectiveInputFactory: (_, devices) =>
                {
                    hook.Devices = devices;
                    return hook;
                }, hostWindowHandle: hostWindowHandle)
            {
                Commands = commands,
                SelectiveInputCleanup = Cleanup
            };
    }

    private static nint Open(string name)
    {
        nint handle = CreateFileW(name, 0xC0000000, 3, 0, 3, 0, 0);
        Require(handle != -1, name);
        Check(SetHandleInformation(handle, 1, 1));
        return handle;
    }

    private static void Write(nint input, string text)
    {
        InputRecord[] events = [.. text.Select(c => new InputRecord
        {
            EventType = 1, KeyDown = 1, RepeatCount = 1, Character = c,
            VirtualKey = c == '\r' ? (ushort)13 : (ushort)char.ToUpperInvariant(c)
        })];
        Check(WriteConsoleInputW(input, events, (uint)events.Length, out uint written));
        Require(written == events.Length, "入力が欠けました。");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Check(bool value)
    {
        if (!value)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private sealed class PromptWriter : StringWriter
    {
        internal Action? OnPrompt;
        public override void Write(string? value)
        {
            base.Write(value);
            if (value?.EndsWith(" > ", StringComparison.Ordinal) == true)
            {
                OnPrompt?.Invoke();
            }
        }
    }

    private sealed class Cleanup : ICliInputCleanup
    {
        public int PendingCount { get; set; } = 1;
        internal int Retries;
        internal Func<Task<CliCleanupAttempt>>? Retry;
        public Task<CliCleanupAttempt> RetryAsync()
        {
            Retries++;
            return Retry!.Invoke();
        }
    }

    private sealed class Hook : ISelectiveInputHookSession
    {
        public bool IsRegistered
        {
            get; private set;
        }
        internal int Failures;
        internal int Stops;
        internal SelectiveInputDevices Devices;
        internal Action? OnStop;
        public void Start() => IsRegistered = true;
        public bool Pump() => false;
        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);
        public void RefreshTargets()
        {
        }
        public void Stop()
        {
            Stops++;
            OnStop?.Invoke();
            if (Interlocked.Decrement(ref Failures) >= 0)
            {
                throw new InvalidOperationException("解除失敗");
            }

            IsRegistered = false;
        }
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] internal ushort EventType;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort RepeatCount;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(14)] internal char Character;
    }

    private delegate bool Handler(uint kind);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int kind, nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushConsoleInputBuffer(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteConsoleInputW(nint input, InputRecord[] buffer, uint count, out uint written);
}
