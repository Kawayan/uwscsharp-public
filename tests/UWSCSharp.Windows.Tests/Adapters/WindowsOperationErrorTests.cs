using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowsOperationErrorTests
{
    private delegate nint CaptionProcedure(nint window, uint message,
        nint parameter, nint value, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window,
        CaptionProcedure procedure, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window,
        CaptionProcedure procedure, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message,
        nint parameter, nint value);

    private sealed class CaptureFixture : IImageCapture
    {
        internal Exception? Failure;

        public ImageArea Desktop => new(0, 0, 4, 4);

        public PixelImage Capture(ImageArea area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            return new(area.Width, area.Height, new int[area.Width * area.Height]);
        }
    }

    private sealed class ClosedClipboard : ClipboardApi
    {
        internal override bool Open(nint owner)
        {
            Marshal.SetLastPInvokeError(5);
            return false;
        }
    }

    private static WindowsAutomation Automation(CaptureFixture capture) => new(
        _ => throw new InvalidOperationException("物理入力は禁止です。"),
        imageCapture: capture);

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("invalid")]
    [InlineData("save-directory")]
    public async Task ImageFileFailuresCanBeHandled(string condition)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-operation-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "input.bmp");
            if (condition == "empty")
            {
                File.WriteAllBytes(path, []);
            }
            else if (condition == "truncated")
            {
                File.WriteAllBytes(path, [0x42, 0x4d]);
            }
            else if (condition == "invalid")
            {
                File.WriteAllBytes(path, [0x58, 0x59]);
            }

            string call = condition == "save-directory"
                ? $"SAVEIMG(\"{folder}/missing/output.bmp\")"
                : $"PRINT CHKIMG(\"{path}\")";
            List<string> output = [];
            await new Interpreter(windows: Automation(new CaptureFixture())).RunAsync(
                "TRY\n" + call + "\nEXCEPT\nPRINT TRY_ERRLINE\n"
                + "ENDTRY\nPRINT \"after\"", output.Add);
            Assert.Equal(["2", "after"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ClipboardOpenFailureReachesExcept()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            clipboardBackend: new ClipboardBackend(new ClosedClipboard())))
            .RunAsync("TRY\nPRINT GETSTR(0)\nEXCEPT\nPRINT TRY_ERRLINE\n"
                + "ENDTRY\nPRINT \"after\"", output.Add);
        Assert.Equal(["2", "after"], output);
    }

    [Fact]
    public async Task NativeCaptionFailureReachesExceptWithoutClkItemPrefix()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("STATIC", "caption", 0, 10);
        CaptionProcedure procedure = (window, message, parameter, value, _, _) =>
            message == 0xe ? 16_777_217
                : DefSubclassProc(window, message, parameter, value);
        Assert.True(SetWindowSubclass(control, procedure, 1, 0));
        List<string> output = [];
        try
        {
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。")))
                .RunAsync($"id=HNDTOID({(long)fixture.Root})\nTRY\n"
                    + "GETITEM(id, 262144)\nEXCEPT\nPRINT TRY_ERRLINE\n"
                    + "PRINT TRY_ERRMSG\nENDTRY\nPRINT \"after\"", output.Add);
        }
        finally
        {
            Assert.True(RemoveWindowSubclass(control, procedure, 1));
            GC.KeepAlive(procedure);
        }

        Assert.Equal("3", output[0]);
        Assert.Contains("長すぎます", output[1]);
        Assert.DoesNotContain("FucCLKITEM:", output[1]);
        Assert.Equal("after", output[2]);
    }

    [Fact]
    public async Task UnknownBackendWin32FailureKeepsIdentity()
    {
        var failure = new Win32Exception(5, "未分類backend障害");
        List<string> output = [];
        Win32Exception actual = await Assert.ThrowsAsync<Win32Exception>(() =>
            new Interpreter(windows: Automation(new CaptureFixture { Failure = failure }))
                .RunAsync("TRY\nSAVEIMG()\nEXCEPT\nPRINT \"caught\"\nENDTRY",
                    output.Add));
        Assert.Same(failure, actual);
        Assert.Empty(output);
    }

    [Fact]
    public async Task FileErrorMappingKeepsDirectTypeAndSessionOwnership()
    {
        await using IWindowsAutomationSession session = Automation(new CaptureFixture()).CreateSession();
        await using IWindowsAutomationSession other = Automation(new CaptureFixture()).CreateSession();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bmp");
        FileNotFoundException failure = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path)], default));
        Assert.Equal(failure.Message, session.GetScriptErrorMessage("chkimg", failure));
        Assert.Null(session.GetScriptErrorMessage("SAVEIMG", failure));
        Assert.Null(other.GetScriptErrorMessage("CHKIMG", failure));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(1450)]
    [InlineData(1455)]
    public void NativeResourceFailuresAreNotOrdinaryOperationErrors(int code)
    {
        var failure = new Win32Exception(code);
        Assert.Same(failure, WindowsOperationErrors.Record(failure));
        Assert.False(WindowsOperationErrors.Contains(failure));
    }

    [Fact]
    public void CancellationMemoryAndGdiMemoryRemainUnclassified()
    {
        var cancel = new OperationCanceledException();
        var memory = new OutOfMemoryException();
        WindowsOperationErrors.Record(cancel);
        WindowsOperationErrors.Record(memory);
        Assert.False(WindowsOperationErrors.Contains(cancel));
        Assert.False(WindowsOperationErrors.Contains(memory));
        InvalidOperationException gdi = Assert.Throws<InvalidOperationException>(() => JpegWriter.Check(3));
        Assert.False(WindowsOperationErrors.Contains(gdi));
    }
}
