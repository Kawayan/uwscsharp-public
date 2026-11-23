using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ScriptHotkeyFrontendTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli", false, false)]
    [InlineData("UWSCSharp.Cli", true, false)]
    [InlineData("UWSCSharp.Wpf", false, false)]
    [InlineData("UWSCSharp.Wpf", true, false)]
    [InlineData("UWSCSharp.Wpf", false, true)]
    [InlineData("UWSCSharp.Wpf", true, true)]
    public async Task FrontendDeliversHotkeyAndExitsWithCallbackCode(
        string frontend, bool modifierOnly, bool stopWithApplicationHotkey)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-hotkey-frontend-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        CapturedProcess? process = null;
        Exception? primary = null;
        int key = modifierOnly ? 0 : 0x7A;
        ushort[] pressed = modifierOnly ? [0xA2, 0xA0] : [0xA2, 0xA0, 0x7A];
        try
        {
            string ready = Path.Combine(directory, "ready.txt");
            string receipt = Path.Combine(directory, "receipt.txt");
            string script = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(script, $$"""
                DIM registered=SETHOTKEY({{key}},MOD_CONTROL+MOD_SHIFT,"Pressed")
                WriteReceipt("{{ready}}",registered)
                WHILE TRUE
                    SLEEP(0.02)
                WEND
                PROCEDURE Pressed()
                    WriteReceipt("{{receipt}}",HOTKEY_VK+":"+HOTKEY_MOD)
                    {{(stopWithApplicationHotkey ? "" : "EXITEXIT(7)")}}
                FEND
                PROCEDURE WriteReceipt(path,text)
                    DIM file=FOPEN(path,F_WRITE8)
                    FPUT(file,text,0)
                    FCLOSE(file)
                FEND
                """, new UTF8Encoding(true), timeout.Token);
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            process = await CapturedProcess.StartAsync(executable,
                $"\"{executable}\" \"{script}\"", timeout.Token);
            Task<byte[]> output = process.ReadOutputAsync(cancellationToken: timeout.Token);
            while (!File.Exists(ready) || new FileInfo(ready).Length == 0)
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.Equal("1", (await File.ReadAllTextAsync(ready, timeout.Token)).Trim());
            await UiInputDriver.ActivateAsync(fixture.Root);
            foreach (ushort value in pressed)
            {
                Send(value, false);
            }

            if (stopWithApplicationHotkey)
            {
                while (!File.Exists(receipt) || new FileInfo(receipt).Length == 0)
                {
                    await Task.Delay(20, timeout.Token);
                }

                // スクリプトのキーを解放してから、WPFの既定の停止キーを押します。
                foreach (ushort value in pressed.Reverse())
                {
                    Send(value, true);
                }

                Send(0x79, false);
                Send(0x79, true);
            }

            uint exit = await process.WaitForExitAsync(timeout.Token);
            string text = Encoding.UTF8.GetString(await output);
            Assert.True(exit == (stopWithApplicationHotkey ? 130u : 7u), $"exit={exit}\n{text}");
            Assert.Equal($"{key}:6", (await File.ReadAllTextAsync(receipt, timeout.Token)).Trim());
            await using var next = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
            Assert.True(next.TryRegister(new ScriptHotkey(key, 6), () => { }));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            List<Exception> cleanupErrors = [];
            foreach (ushort value in pressed.Append((ushort)0x79).Reverse())
            {
                try
                {
                    Send(value, true);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                }
            }

            if (process is not null)
            {
                Exception cleanup = primary ?? new InvalidOperationException("検証ホストの終了処理");
                await process.StopAfterFailureAsync(cleanup);
                process.Dispose();
                if (primary is null && cleanup.Data.Count != 0)
                {
                    cleanupErrors.Add(cleanup);
                }
            }

            Directory.Delete(directory, recursive: true);
            if (cleanupErrors.Count != 0)
            {
                var cleanup = new AggregateException(cleanupErrors);
                if (primary is null)
                {
                    throw cleanup;
                }

                primary.Data["UWSCSharp.TestCleanupErrors"] = cleanup;
            }
        }
    }

    private static void Send(ushort key, bool up) => NativeInputSender.Send(new Native.Input
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = key, Flags = up ? 2u : 0 }
        }
    });
}
