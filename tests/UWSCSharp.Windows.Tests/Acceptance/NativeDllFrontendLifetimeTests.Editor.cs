using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeDllFrontendLifetimeTests
{
    private const string EditorRerunScript = "PRINT \"editor-rerun\"\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorStopsAndReplaysSelection(bool popup)
    {
        string executable = FixturePaths.Get(
            "UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        string title = "uwsc-editor-selection-" + Guid.NewGuid();
        using Process process = StartCancellationProcess(
            executable, Path.GetDirectoryName(executable)!, []);
        try
        {
            await EditorWaitAsync(() =>
            {
                process.Refresh();
                return Task.FromResult(process.MainWindowHandle != 0);
            });
            nint window = FindOwnedMainWindow(process.Id);
            await EditorWaitAsync(
                () => EditorEnabledAsync(window, "PlayButton"));
            await EditorElementAsync(window, "EditorPanel", (element, owned) =>
            {
                EditorPattern<IUiaExpandCollapsePattern>(
                    element, owned, 10005).Expand();
                return true;
            });
            string select = popup
                ? "DIM items[1]=\"A\",\"B\"\nresult=POPUPMENU(items,500,300)"
                : "result=SLCTBOX(SLCT_BTN OR SLCT_NUM,0,\"選択\",\"A\",\"B\")";
            await SetEditorValueAsync(window, $$"""
                OPTION DLGTITLE="{{title}}"
                {{select}}
                PRINT "selected="+result
                """);
            await EditorInvokeAsync(window, "PlayButton");
            nint selection = await FindSelectionAsync();
            await EditorInvokeAsync(window, "StopButton");
            await EditorWaitAsync(
                () => EditorEnabledAsync(window, "PlayButton", allowMissing: true));
            Assert.False(Native.IsWindow(selection));
            Assert.Equal("実行を停止しました",
                await EditorNameAsync(window, "StatusLabel"));
            Assert.DoesNotContain("selected=",
                await EditorValueAsync(window, "Output"));

            await EditorInvokeAsync(window, "PlayButton");
            selection = await FindSelectionAsync();
            if (popup)
            {
                PopupMenuIntegrationTests.SendMenuKey(0x28, process.Id);
                PopupMenuIntegrationTests.SendMenuKey(0x28, process.Id);
                PopupMenuIntegrationTests.SendMenuKey(0x0d, process.Id);
            }
            else
            {
                nint button = GetDlgItem(selection, 0x5001);
                Assert.NotEqual(0, button);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
            }
            await EditorWaitAsync(
                () => EditorEnabledAsync(window, "PlayButton", allowMissing: true));
            Assert.False(Native.IsWindow(selection));
            Assert.Equal("実行が完了しました",
                await EditorNameAsync(window, "StatusLabel"));
            Assert.Equal("selected=1",
                (await EditorValueAsync(window, "Output")).Trim());

            Task<nint> FindSelectionAsync() => popup
                ? PopupMenuIntegrationTests.FindPopupAsync(process.Id)
                : MessageBoxIntegrationTests.FindWindowAsync(title, process.Id);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(Timeout);
            }
        }
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("normal", true)]
    [InlineData("exit", false)]
    [InlineData("exit", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("close", false)]
    [InlineData("close", true)]
    public async Task EditorPlaybackRetainsNativeCallsAndCanRunAgain(
        string mode, bool externalCall)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var probe = new Probe(externalCall, exit: mode == "exit");
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        using Process process = StartCancellationProcess(executable,
            probe.Directory, []);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        string stage = "starting";
        string firstOutput = "";
        string firstStatus = "";
        string rerunOutput = "";
        bool sourceMatches = false;
        bool discarded = false;
        bool forcedKill = false;
        try
        {
            await EditorWaitAsync(() =>
            {
                process.Refresh();
                return Task.FromResult(process.MainWindowHandle != 0);
            });
            nint window = FindOwnedMainWindow(process.Id);
            await EditorWaitAsync(() => EditorEnabledAsync(window, "PlayButton"));
            await EditorElementAsync(window, "EditorPanel", (element, owned) =>
            {
                EditorPattern<IUiaExpandCollapsePattern>(element, owned, 10005).Expand();
                return true;
            });
            string source = await File.ReadAllTextAsync(probe.Main);
            await SetEditorValueAsync(window, source);
            string readback = await EditorValueAsync(window, "Editor");
            sourceMatches = NormalizeEditorText(source) == NormalizeEditorText(readback);
            Assert.True(sourceMatches);
            await EditorInvokeAsync(window, "PlayButton");
            stage = "playing";
            await WaitForBlockedCallsAsync(process, probe);
            Assert.True(await EditorReadOnlyAsync(window));
            Assert.False(await EditorEnabledAsync(window, "PlayButton"));
            Assert.True(await EditorEnabledAsync(window, "StopButton"));

            if (mode is "normal" or "exit")
            {
                probe.Set("parent-release");
                await EditorWaitAsync(async () =>
                    (await EditorValueAsync(window, "Output")).Contains(
                        "parent=11", StringComparison.Ordinal));
                await ObserveCancellationWaitAsync(process, probe,
                    parentBlocked: false);
            }
            else
            {
                if (mode == "stop")
                {
                    await EditorInvokeAsync(window, "StopButton");
                }
                else
                {
                    Assert.True(Native.PostMessageW(window, 0x0010, 0, 0));
                }
                await ObserveCancellationWaitAsync(process, probe);
                Assert.True(Native.IsWindow(window));
                Assert.Equal((nint)0, OwnedEditorDialog(process.Id));
                Assert.False(await EditorEnabledAsync(window, "PlayButton"));
            }

            probe.Set("child-release");
            probe.Set("parent-release");
            await EditorWaitAsync(() => EditorEnabledAsync(window, "PlayButton"));
            stage = "first-finished";
            Assert.False(process.HasExited);
            Assert.False(await EditorReadOnlyAsync(window));
            Assert.False(await EditorEnabledAsync(window, "StopButton"));
            firstOutput = NormalizeEditorText(await EditorValueAsync(window, "Output"));
            string expected = mode is "stop" or "close"
                ? CancellationOutput(externalCall)
                : string.Join("\n", ExpectedOutput(externalCall)) + "\n";
            Assert.Equal(expected, firstOutput);
            firstStatus = await EditorNameAsync(window, "StatusLabel");
            Assert.Equal(mode switch
            {
                "stop" or "close" => "実行を停止しました",
                "exit" => "終了コード 7 で実行が終了しました",
                _ => "実行が完了しました"
            }, firstStatus);
            AssertCancellationEvents(probe);
            process.Refresh();
            Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(), module =>
                string.Equals(module.FileName, probe.Library,
                    StringComparison.OrdinalIgnoreCase));

            await SetEditorValueAsync(window, EditorRerunScript);
            await EditorInvokeAsync(window, "PlayButton");
            await EditorWaitAsync(async () =>
                await EditorEnabledAsync(window, "PlayButton")
                && NormalizeEditorText(await EditorValueAsync(window, "Output"))
                    == "editor-rerun\n");
            rerunOutput = NormalizeEditorText(await EditorValueAsync(window, "Output"));
            Assert.Equal("実行が完了しました",
                await EditorNameAsync(window, "StatusLabel"));
            Assert.False(await EditorReadOnlyAsync(window));
            stage = "rerun-finished";

            Assert.True(Native.PostMessageW(window, 0x0010, 0, 0));
            nint dialog = 0;
            await EditorWaitAsync(() =>
            {
                dialog = OwnedEditorDialog(process.Id);
                return Task.FromResult(dialog != 0);
            });
            var message = new StringBuilder();
            Native.EnumChildWindows(dialog, (child, _) =>
            {
                var text = new StringBuilder(512);
                Native.GetWindowTextW(child, text, text.Capacity);
                message.Append(text);
                return true;
            }, 0);
            Assert.Contains("保存していない変更", message.ToString(), StringComparison.Ordinal);
            nint yes = GetDlgItem(dialog, 6);
            Assert.NotEqual((nint)0, yes);
            Assert.True(Native.PostMessageW(yes, 0x00f5, 0, 0));
            discarded = true;
            await exited.WaitAsync(Timeout);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stdout.WaitAsync(Timeout));
            Assert.Equal("", await stderr.WaitAsync(Timeout));
            stage = "completed";
        }
        finally
        {
            probe.Set("child-release");
            probe.Set("parent-release");
            if (!process.HasExited)
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
                if (!process.HasExited)
                {
                    forcedKill = true;
                    process.Kill(entireProcessTree: true);
                }
                await exited.WaitAsync(Timeout);
            }
            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                externalCall,
                stage,
                sourceMatches,
                firstOutput,
                firstStatus,
                rerunOutput,
                discarded,
                forcedKill,
                code = process.ExitCode,
                stdout = await stdout.WaitAsync(Timeout),
                stderr = await stderr.WaitAsync(Timeout),
                events = probe.Snapshot(),
                executable_sha256 = Hash(executable),
                frontend_assembly_sha256 = Hash(Path.ChangeExtension(executable, ".dll")),
                fixture_sha256 = Hash(probe.Library)
            }));
        }
    }

    private static string NormalizeEditorText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static Task<bool> EditorEnabledAsync(
        nint window, string id, bool allowMissing = false) =>
        EditorElementAsync(window, id, (element, _) =>
            Assert.IsType<bool>(element.Property(30010)), allowMissing);

    private static Task<string> EditorNameAsync(nint window, string id) =>
        EditorElementAsync(window, id, (element, _) =>
            Assert.IsType<string>(element.Property(30005)));

    private static Task<string> EditorValueAsync(nint window, string id) =>
        EditorElementAsync(window, id, (element, owned) =>
            EditorPattern<IUiaValue>(element, owned, 10002).Value());

    private static Task<bool> EditorReadOnlyAsync(nint window) =>
        EditorElementAsync(window, "Editor", (element, owned) =>
            EditorPattern<IUiaValue>(element, owned, 10002).IsReadOnly());

    private static Task<bool> SetEditorValueAsync(nint window, string source) =>
        EditorElementAsync(window, "Editor", (element, owned) =>
        {
            EditorPattern<IUiaValue>(element, owned, 10002).SetValue(source);
            return true;
        });

    private static Task<bool> EditorInvokeAsync(nint window, string id) =>
        EditorElementAsync(window, id, (element, owned) =>
        {
            Assert.True(Assert.IsType<bool>(element.Property(30010)));
            EditorPattern<IUiaInvoke>(element, owned, 10000).Invoke();
            return true;
        });

    private static T EditorPattern<T>(IUiaElement element,
        List<object> owned, int pattern) where T : class
    {
        Guid iid = typeof(T).GUID;
        object value = element.PatternAs(pattern, ref iid);
        owned.Add(value);
        return (T)value;
    }

    private static Task<T> EditorElementAsync<T>(nint window, string id,
        Func<IUiaElement, List<object>, T> read, bool allowMissing = false) => Task.Run(() =>
    {
        var owned = new List<object>();
        try
        {
            var automation = (IUiaAutomation)new UiaComObject();
            owned.Add(automation);
            IUiaElement root = automation.ElementFromHandle(window);
            owned.Add(root);
            IUiaCondition condition = automation.CreatePropertyCondition(30011, id);
            owned.Add(condition);
            IUiaElement? element = root.FindFirst(4, condition);
            if (element is null && allowMissing)
            {
                return default!;
            }

            Assert.NotNull(element);
            owned.Add(element);
            return read(element, owned);
        }
        finally
        {
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                Marshal.ReleaseComObject(owned[index]);
            }
        }
    }).WaitAsync(Timeout);

    private static async Task EditorWaitAsync(Func<Task<bool>> ready)
    {
        var waiting = Stopwatch.StartNew();
        while (!await ready())
        {
            Assert.True(waiting.Elapsed < Timeout, "編集画面の状態待ちが期限を超えました。");
            await Task.Delay(20);
        }
    }

    private static nint OwnedEditorDialog(int processId)
    {
        var windows = new List<nint>();
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint owner);
            var name = new StringBuilder(64);
            Native.GetClassNameW(window, name, name.Capacity);
            if (owner == (uint)processId && name.ToString() == "#32770"
                && Native.IsWindowVisible(window))
            {
                windows.Add(window);
            }
            return true;
        }, 0);
        Assert.True(windows.Count <= 1);
        return windows.Count == 0 ? 0 : windows[0];
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDlgItem(nint dialog, int item);
}
