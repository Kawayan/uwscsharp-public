using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ImageScriptErrorTests
{
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

            return new(area.Width, area.Height,
                new int[area.Width * area.Height]);
        }
    }

    private static WindowsAutomation Automation(CaptureFixture capture) => new(
        _ => throw new InvalidOperationException("物理入力は禁止です。"),
        imageCapture: capture);

    [Theory]
    [InlineData("SAVEIMG(\"\", 999)", "ウィンドウが無効")]
    [InlineData("SAVEIMG(\"\", 0, 3, 0, 2, 1)", "取得範囲")]
    [InlineData("CHKIMG(\"{path}\", 0, 3, 0, 1, 1)", "探索範囲")]
    public async Task InvalidImageConditionsReachExcept(
        string call, string message)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "uwsc-image-error-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            new PixelImage(1, 1, [0]).SaveBmp(path, default);
            call = call.Replace("{path}", path, StringComparison.Ordinal);
            List<string> output = [];
            await new Interpreter(windows: Automation(new CaptureFixture())).RunAsync(
                "TRY\n" + call + "\nEXCEPT\nPRINT TRY_ERRLINE\n"
                + "PRINT TRY_ERRMSG\nENDTRY\nPRINT \"after\"",
                output.Add, default);
            Assert.Equal(3, output.Count);
            Assert.Equal("2", output[0]);
            Assert.Contains(message, output[1]);
            Assert.Equal("after", output[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailureKeepsHostIdentity(bool argumentError)
    {
        Exception failure = argumentError
            ? new ArgumentException("SAVEIMGの取得範囲が範囲外です。")
            : new InvalidOperationException("SAVEIMGのウィンドウが無効です。");
        var capture = new CaptureFixture { Failure = failure };
        List<string> output = [];
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter(windows: Automation(capture)).RunAsync(
                "TRY\nTRY\nSAVEIMG()\nEXCEPT\nPRINT \"caught\"\n"
                + "ENDTRY\nFINALLY\nPRINT \"finally\"\nENDTRY",
                output.Add, default));
        Assert.Same(failure, actual);
        Assert.Equal(["finally"], output);
    }

    [Fact]
    public async Task DirectApiKeepsExceptionTypeAndSessionOwnership()
    {
        await using IWindowsAutomationSession session =
            Automation(new CaptureFixture()).CreateSession();
        await using IWindowsAutomationSession other =
            Automation(new CaptureFixture()).CreateSession();
        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("SAVEIMG",
                    [Value.String(""), Value.Numeric(999)], default));
        Assert.Equal(failure.Message,
            session.GetScriptErrorMessage("saveimg", failure));
        Assert.Null(session.GetScriptErrorMessage("CHKIMG", failure));
        Assert.Null(other.GetScriptErrorMessage("SAVEIMG", failure));
        Assert.Null(session.GetScriptErrorMessage("SAVEIMG",
            new InvalidOperationException(failure.Message)));
    }

    [Fact]
    public async Task UncaughtConditionRunsFinallyAndStopsWithScriptLine()
    {
        List<string> output = [];
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: Automation(new CaptureFixture())).RunAsync(
                "TRY\nSAVEIMG(\"\", 999)\nFINALLY\nPRINT \"finally\"\n"
                + "ENDTRY\nPRINT \"after\"", output.Add, default));
        Assert.Equal(2, failure.Line);
        Assert.Contains("ウィンドウが無効", failure.Message);
        Assert.Equal(["finally"], output);
    }

    [Fact]
    public async Task CaptureCancellationBypassesExceptAndKeepsToken()
    {
        using var cancel = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancel.Token);
        var capture = new CaptureFixture { Failure = failure };
        List<string> output = [];
        OperationCanceledException actual =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new Interpreter(windows: Automation(capture)).RunAsync(
                    "TRY\nSAVEIMG()\nEXCEPT\nPRINT \"caught\"\nENDTRY",
                    output.Add, cancel.Token));
        Assert.Equal(cancel.Token, actual.CancellationToken);
        Assert.Empty(output);
    }
}
