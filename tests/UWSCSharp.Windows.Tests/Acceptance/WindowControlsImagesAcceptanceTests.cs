using System.Globalization;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowControlsImagesAcceptanceTests(ITestOutputHelper timing)
{
    private sealed class Capture : IImageCapture
    {
        private readonly PixelImage image = new(5, 3,
            [0x123456, 0xabcdef, 0x123456, 0xabcdef, 9,
                0x123456, 0xabcdef, 0x123456, 0xabcdef, 9,
                9, 9, 9, 0x123456, 0xabcdef]);

        public ImageArea Desktop => new(0, 0, 5, 3);

        PixelImage IImageCapture.Capture(ImageArea area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int[] pixels = new int[area.Width * area.Height];
            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    pixels[y * area.Width + x] = image[area.X + x, area.Y + y];
                }
            }

            return new(area.Width, area.Height, pixels);
        }
    }

    private sealed class Automation(ITestOutputHelper timing) : IWindowsAutomation
    {
        private readonly WindowsAutomation inner = new(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), imageCapture: new Capture(),
            listSelectionPlatform: new OwnedButtonMessagePlatform());

        internal List<Session> Sessions { get; } = [];

        public IWindowsAutomationSession CreateSession()
        {
            IWindowsAutomationSession native = inner.CreateSession();
            Assert.Equal(WindowsImageResult.Empty, native.ImageResult);
            Assert.Empty(native.AllItems);
            var session = new Session(native, timing);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class Session(
        IWindowsAutomationSession inner, ITestOutputHelper timing)
        : IWindowsAutomationSession
    {
        public IReadOnlyList<string> AllItems => inner.AllItems;

        public WindowsImageResult ImageResult => inner.ImageResult;

        internal List<string> Calls { get; } = [];

        internal bool Disposed
        {
            get; private set;
        }

        public async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            string phase = name == "CLKITEM" && arguments.Count > 1
                ? $"windows-api:{name}:{arguments[1]}"
                : $"windows-api:{name}";
            using var timer = new TestPhaseTimer(timing, phase);
            Calls.Add(name);
            return await inner.InvokeAsync(name, arguments, token);
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Assert.Empty(inner.AllItems);
            Assert.Equal(WindowsImageResult.Empty, inner.ImageResult);
            Disposed = true;
        }
    }

    [Fact]
    public async Task RepresentativeScriptSupportsExitFailureCancellationAndRerun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture("M3代表fixture");
        fixture.Child("BUTTON", "&確認", 3, 10);
        fixture.Child("EDIT", "本文", 0, 60);
        fixture.Child("STATIC", "Caption", 0, 80);
        SliderControlTests.InitializeControls();
        fixture.Child("msctls_trackbar32", "", 0, 110);
        nint list = fixture.Child("LISTBOX", "", 1, 145, height: 40);
        foreach (string item in new[] { "Alpha", "Beta" })
        {
            Assert.NotEqual(0, Native.SendText(list, 0x180, 0, item, 2, 1000, out nuint result));
            Assert.NotEqual(nuint.MaxValue, result);
        }

        string folder = Path.Combine(Path.GetTempPath(), "uwsc-m3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        nint menu = MenuControlTests.CreateMenu();
        nint popup = MenuControlTests.CreatePopupMenu();
        Assert.NotEqual(0, menu);
        Assert.NotEqual(0, popup);
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(popup, 0, 101, "&実行"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)popup, "&ファイル"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            string template = Path.Combine(folder, "template.bmp");
            string saved = Path.Combine(folder, "saved.bmp");
            new PixelImage(2, 1, [0x123456, 0xabcdef]).SaveBmp(template, default);
            var options = new ScriptRunOptions([
                ((long)fixture.Root).ToString(CultureInfo.InvariantCulture), template, saved]);
            string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "window-controls-images-acceptance.uws"));
            var automation = new Automation(timing);
            var runner = new Interpreter(windows: automation);
            string[] expected = ["本文", "1", "1", "確認", "1", "42", "1", "Beta",
                "確認", "Alpha", "Beta", "ファイル\\実行", "本文", "Caption", "6", "1",
                "1193046", "5", "3", "2", "5", "1", "2", "0", "0", "0"];
            List<string> output = [];
            await TestPhaseTimer.RunAsync(timing, "normal", () =>
                runner.RunAsync(source, output.Add, default, options));
            Assert.Equal(expected, output);
            Assert.Equal([0x123456, 0xabcdef], PixelImage.LoadBmp(saved, default).Pixels);
            foreach (string function in new[] { "GETSTR", "CLKITEM", "CHKBTN", "POSACC", "SETSLIDER",
                "GETSLIDER", "GETSLCTLST", "GETITEM", "PEEKCOLOR", "CHKIMG", "SAVEIMG" })
            {
                Assert.Contains(function, automation.Sessions[0].Calls);
            }

            output.Clear();
            await TestPhaseTimer.RunAsync(timing, "rerun", () =>
                runner.RunAsync(source, output.Add, default, options));
            Assert.Equal(expected, output);
            await Assert.ThrowsAsync<ScriptException>(() =>
                TestPhaseTimer.RunAsync(timing, "failure", () => runner.RunAsync(
                    source + "\nPRINT 1 / 0", _ => { }, default, options)));
            await TestPhaseTimer.RunAsync(timing, "exit", () =>
                runner.RunAsync(source + "\nEXIT\nPRINT 1 / 0", _ => { }, default, options));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TestPhaseTimer.RunAsync(timing, "cancellation", () => runner.RunAsync(source,
                    _ => cancellation.Cancel(), cancellation.Token, options)));
            output.Clear();
            await TestPhaseTimer.RunAsync(timing, "empty-session", () =>
                runner.RunAsync("PRINT GETSLIDER(1)", output.Add));
            Assert.Equal(["-999999"], output);
            output.Clear();
            await TestPhaseTimer.RunAsync(timing, "after-cancellation", () =>
                runner.RunAsync(source, output.Add, default, options));
            Assert.Equal(expected, output);
            Assert.Equal(7, automation.Sessions.Count);
            Assert.All(automation.Sessions, session => Assert.True(session.Disposed));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            Assert.True(MenuControlTests.SetMenu(fixture.Root, 0));
            Assert.True(MenuControlTests.DestroyMenu(menu));
            Directory.Delete(folder, true);
        }
    }
}
