using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using UWSCSharp.Windows;

[assembly: SupportedOSPlatform("windows")]

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 新規プロセス内で背景取得のGDI解放を厳密に測定します。
/// </summary>
internal static class GdiCaptureProbe
{
    internal static int Run(bool injectLeak)
    {
        nint window = CreateWindowExW(0, "STATIC", "GDI capture probe",
            0, 0, 0, 100, 100, 0, 0, 0, 0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        nint leaked = 0;
        try
        {
            // 初回のネイティブ初期化を測定へ含めません。
            Exercise(window);
            uint before = GetGuiResources(GetCurrentProcess(), 0);
            Exercise(window);
            if (injectLeak)
            {
                nint dc = ImageNative.GetDC(0);
                try
                {
                    leaked = ImageNative.CreateCompatibleBitmap(dc, 2, 2);
                    if (leaked == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    ImageNative.ReleaseDC(0, dc);
                }
            }
            uint after = GetGuiResources(GetCurrentProcess(), 0);
            bool passed = before == after;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                before,
                after,
                injectLeak,
                passed,
                failures = 20,
                exceptions = 20,
                cancellations = 1
            }));
            return passed ? 0 : 1;
        }
        finally
        {
            // 負の検証で保持したbitmapも、測定終了後には解放します。
            if (leaked != 0)
            {
                ImageNative.DeleteObject(leaked);
            }
            Native.DestroyWindow(window);
        }
    }

    private static void Exercise(nint window)
    {
        using var cancellation = new CancellationTokenSource();
        var cancel = new NativeImageCapture((_, _) =>
        {
            cancellation.Cancel();
            return true;
        });
        var fail = new NativeImageCapture((_, _) => false);
        var exception = new NativeImageCapture((_, _) =>
            throw new InvalidOperationException("描画失敗"));
        for (int index = 0; index < 20; index++)
        {
            Expect<Win32Exception>(() => fail.CaptureWindow(
                window, new(0, 0, 2, 2), default));
            Expect<InvalidOperationException>(() => exception.CaptureWindow(
                window, new(0, 0, 2, 2), default));
        }
        OperationCanceledException failure = Expect<OperationCanceledException>(() =>
            cancel.CaptureWindow(window, new(0, 0, 2, 2), cancellation.Token));
        if (failure.CancellationToken != cancellation.Token)
        {
            throw new InvalidOperationException("取消トークンが一致しません。");
        }
        Expect<ArgumentException>(() => fail.CaptureWindow(
            window, new(-1, 0, 2, 2), default));
        Expect<Win32Exception>(() => fail.CaptureWindow(
            -1, new(0, 0, 2, 2), default));
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T failure) when (failure.GetType() == typeof(T))
        {
            return failure;
        }
        throw new InvalidOperationException($"{typeof(T).Name}が発生しませんでした。");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string name,
        string text, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
