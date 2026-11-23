using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class DropFileTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string architecture in new[] { "x86", "x64" })
        {
            foreach (string name in new[]
            {
                "default", "child", "parent", "relative", "boolean", "bulk",
                "no-receiver", "hidden", "bounds", "stale", "minimized",
                "canceled", "closed-after-delivery", "canceled-after-post",
                "exit-before-reading", "child-disabled", "child-transparent",
                "child-nested", "exit-before-dispatch"
            })
            {
                yield return [architecture, name];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task SendsFilesWithAdoptedPolicy(string architecture, string name) =>
        VerifyDeliveryAsync(architecture, name);

    [Theory]
    [InlineData("x86", -1)]
    [InlineData("x86", -4)]
    [InlineData("x64", -1)]
    [InlineData("x64", -4)]
    [Trait("Category", "SandboxOnly")]
    public Task SendsChildCoordinatesInPhysicalPixels(
        string architecture, int senderDpi) =>
        VerifyDeliveryAsync(architecture, "child", senderDpi);

    private async Task VerifyDeliveryAsync(
        string architecture, string name, int? senderDpi = null)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture? focus = name == "minimized"
            ? await PumpedControlFixture.CreateAsync(_ => { }) : null;
        List<DropFileReceiver> receivers = [];
        Exception? primary = null;
        try
        {
            string mode = name switch
            {
                "child" => "child-drop",
                "parent" => "child-no-drop",
                "child-disabled" or "child-transparent" or "child-nested" => name,
                "no-receiver" => "root-no-drop",
                "exit-before-dispatch" => "hold-before-pump",
                _ => ""
            };
            DropFileReceiver receiver = await DropFileReceiver.StartAsync(architecture, mode);
            receivers.Add(receiver);
            string directory = Path.Combine(receiver.DirectoryPath, "files");
            Directory.CreateDirectory(directory);
            foreach (string file in new[] { "日本語😀.txt", "last.txt", "1", "0" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, file), "probe");
            }

            await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
            Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
                [Value.Numeric((long)receiver.Window)], CancellationToken.None);
            nint foreground = Native.GetForegroundWindow();
            if (name == "hidden")
            {
                _ = Native.ShowWindow(receiver.Window, 0);
            }
            else if (name == "stale")
            {
                await receiver.CloseAsync();
            }
            else if (name == "minimized")
            {
                await UiInputDriver.ActivateAsync(focus!.Root);
                var timer = Stopwatch.StartNew();
                while (Native.GetForegroundWindow() != focus.Root)
                {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
                    await Task.Delay(25);
                }

                foreground = focus.Root;
                _ = Native.ShowWindow(receiver.Window, 7);
                Assert.True(Native.IsIconic(receiver.Window));
            }

            string root = name == "relative"
                ? Path.GetRelativePath(Environment.CurrentDirectory, directory) : directory;
            var files = Value.FromArray(UwscArray.Create([2],
                [Value.String("日本語😀.txt"), Value.String(""), Value.String("last.txt")]));
            string[] bulkNames = [];
            if (name == "bulk")
            {
                bulkNames = [.. Enumerable.Range(0, 16_384)
                    .Select(index => $"項目-{index:D5}-😀.txt")];
                foreach (string file in bulkNames)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(directory, file), "");
                }

                files = Value.FromArray(UwscArray.Create(
                    [bulkNames.Length - 1], [.. bulkNames.Select(Value.String)]));
            }

            List<Value> arguments = [id];
            if (receiver.Child != 0)
            {
                arguments.AddRange([Value.Numeric(37), Value.Numeric(49)]);
            }
            else if (name == "boolean")
            {
                arguments.AddRange([Value.Numeric(1), Value.Numeric(0)]);
                files = Value.FromArray(UwscArray.Create([1], [Value.Numeric(1), Value.Numeric(0)]));
            }
            else if (name == "bounds")
            {
                arguments.AddRange([Value.Numeric(-1), Value.Numeric(0)]);
            }

            arguments.AddRange([Value.String(root), files]);
            if (name == "canceled")
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Value[] canceledArguments =
                    [id, Value.String(root), Value.String("1")];
                OperationCanceledException error =
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        session.InvokeAsync(BuiltinFunctionNames.DropFile,
                            canceledArguments, cancellation.Token).AsTask());
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }

            if (name is "hidden" or "stale" or "bounds" or "no-receiver")
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await session.InvokeAsync(BuiltinFunctionNames.DropFile, arguments, CancellationToken.None));
                Assert.NotNull(session.GetScriptErrorMessage(BuiltinFunctionNames.DropFile, error));
                Assert.False(File.Exists(Path.Combine(receiver.DirectoryPath, "drop-0.json")));
                output.WriteLine("error=" + error.Message);
                return;
            }

            using var send = new CancellationTokenSource();
            if (name is "canceled-after-post" or "exit-before-reading")
            {
                await File.WriteAllTextAsync(
                    Path.Combine(receiver.DirectoryPath, "hold-drop"), "hold");
            }

            Value result;
            if (senderDpi is int context)
            {
                uint receiverDpi = GetDpiForWindow(receiver.Window);
                output.WriteLine($"sender={context}, receiver-dpi={receiverDpi}");
                Assert.True(receiverDpi > 96, "受信先には拡大DPIが必要です。");
                result = SendWithDpi(session, arguments, context, send.Token);
            }
            else
            {
                result = await session.InvokeAsync(
                    BuiltinFunctionNames.DropFile, arguments, send.Token);
            }
            Assert.Equal(ValueKind.String, result.Kind);
            Assert.Equal("", result.Text);
            if (name is "exit-before-reading" or "exit-before-dispatch")
            {
                if (name == "exit-before-reading")
                {
                    await receiver.TerminateBeforeReadingDropAsync();
                }
                else
                {
                    await receiver.TerminateBeforeDispatchAsync();
                }

                Assert.False(Native.IsWindow(receiver.Window));
                Assert.False(File.Exists(
                    Path.Combine(receiver.DirectoryPath, "drop-0.json")));
                return;
            }

            if (name == "canceled-after-post")
            {
                Assert.False(File.Exists(
                    Path.Combine(receiver.DirectoryPath, "drop-0.json")));
                send.Cancel();
                await File.WriteAllTextAsync(
                    Path.Combine(receiver.DirectoryPath, "read-drop"), "read");
            }

            JsonElement data = await receiver.ReadDropAsync();
            if (name != "bulk")
            {
                output.WriteLine(data.GetRawText());
            }
            else
            {
                ulong scanMilliseconds = data.GetProperty("scan_ms")
                    .GetUInt64();
                output.WriteLine($"scan_ms={scanMilliseconds}");
            }
            Assert.Equal((long)(name is "child" or "child-nested"
                    ? receiver.Child : receiver.Window),
                data.GetProperty("recipient").GetInt64());
            int[] point = [.. data.GetProperty("point").EnumerateArray().Select(value => value.GetInt32())];
            int[] expected = name switch
            {
                "child" => [17, 29],
                "child-nested" => [9, 18],
                "parent" or "child-disabled" or "child-transparent" => [37, 49],
                "boolean" => [1, 0],
                _ => [receiver.Metadata.GetProperty("client_size")[0].GetInt32() / 2,
                    receiver.Metadata.GetProperty("client_size")[1].GetInt32() / 2]
            };
            Assert.Equal(expected, point);
            string[] paths = [.. data.GetProperty("paths").EnumerateArray()
                .Select(path => string.Concat(path.EnumerateArray().Select(unit => (char)unit.GetInt32())))];
            string[] expectedNames = name switch
            {
                "bulk" => bulkNames,
                "boolean" => ["1", "0"],
                _ => ["日本語😀.txt", "last.txt"]
            };
            Assert.Equal(expectedNames.Select(file =>
                Path.Combine(directory, file)), paths);
            if (name == "closed-after-delivery")
            {
                await receiver.CloseAsync();
                Assert.False(Native.IsWindow(receiver.Window));
                Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    session.InvokeAsync(BuiltinFunctionNames.DropFile,
                        arguments, CancellationToken.None).AsTask());
                Assert.NotNull(session.GetScriptErrorMessage(
                    BuiltinFunctionNames.DropFile, error));
            }

            if (name == "minimized")
            {
                Assert.False(Native.IsIconic(receiver.Window));
                Assert.Equal(foreground, Native.GetForegroundWindow());
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await DropFileReceiver.ReleaseAllAsync(receivers, primary);
        }
    }

    private static Value SendWithDpi(IWindowsAutomationSession session,
        IReadOnlyList<Value> arguments, int context, CancellationToken token)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(context);
        Assert.NotEqual(0, previous);
        try
        {
            // DPI認識の設定と復元を同じスレッドで行います。
            return session.InvokeAsync(BuiltinFunctionNames.DropFile,
                arguments, token).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
