using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// トップレベル窓から取得した、変更できない観測情報です。
/// </summary>
public sealed record WindowsObservedWindow(
    WindowsCapturedWindow Target, string Title, string ClassName);

internal interface IWindowObservationPlatform
{
    void Enumerate(Func<nint, bool> visit);

    WindowsObservedWindow? Read(nint window);
}

/// <summary>
/// 入力や前面化を行わず、上限付きでトップレベル窓を観測します。
/// </summary>
public static partial class WindowsWindowObserver
{
    /// <summary>
    /// 1観測で列挙する窓の上限です。
    /// </summary>
    public const int MaximumWindows = 4096;

    /// <summary>
    /// タイトルのUTF-16単位数の上限です。
    /// </summary>
    public const int MaximumTitleLength = 32768;

    /// <summary>
    /// クラス名のUTF-16単位数の上限です。
    /// </summary>
    public const int MaximumClassNameLength = 512;

    /// <summary>
    /// 完全な観測だけを返し、不完全な取得は例外で通知します。
    /// </summary>
    public static IReadOnlyList<WindowsObservedWindow> Capture(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("窓の観測にはWindowsが必要です。");
        }
        return Capture(new NativePlatform(), token);
    }

    internal static IReadOnlyList<WindowsObservedWindow> Capture(
        IWindowObservationPlatform platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var windows = new List<WindowsObservedWindow>();
        var handles = new HashSet<nint>();
        int visited = 0;
        Exception? failure = null;
        platform.Enumerate(handle =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (++visited > MaximumWindows || handle == 0 || !handles.Add(handle))
                {
                    throw new InvalidDataException("窓の列挙が上限または形式に違反しています。");
                }
                WindowsObservedWindow? window = platform.Read(handle);
                token.ThrowIfCancellationRequested();
                if (window is not null)
                {
                    if (window.Target.Handle != handle || window.Target.ProcessId == 0
                        || window.Target.ThreadId == 0
                        || !ValidText(window.Title, MaximumTitleLength)
                        || !ValidText(window.ClassName, MaximumClassNameLength))
                    {
                        throw new InvalidDataException("窓の観測が上限または形式に違反しています。");
                    }
                    windows.Add(window);
                }
                return true;
            }
            catch (Exception error)
            {
                // 例外をネイティブの列挙コールバックの境界へ出しません。
                failure = error;
                return false;
            }
        });
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(windows.ToArray());
    }

    private static bool ValidText(string? text, int maximum) =>
        text is not null && text.Length <= maximum && !text.Contains('\0');

    private sealed partial class NativePlatform : IWindowObservationPlatform
    {
        private readonly NativeWindowIdentityPlatform identity = new();
        private readonly StringBuilder title = new(MaximumTitleLength + 2);
        private readonly StringBuilder className = new(MaximumClassNameLength + 2);

        public void Enumerate(Func<nint, bool> visit)
        {
            bool interrupted = false;
            bool complete = Native.EnumWindows((window, _) =>
            {
                bool next = visit(window);
                interrupted |= !next;
                return next;
            }, 0);
            if (!complete && !interrupted)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "窓の列挙に失敗しました。");
            }
        }

        public WindowsObservedWindow? Read(nint window)
        {
            WindowIdentity? before = identity.ReadIdentity(window);
            if (before is null)
            {
                return Native.IsWindow(window)
                    ? throw new IOException("窓の所有者を取得できません。") : null;
            }

            try
            {
                title.Clear();
                if (before.Value.ProcessId == (uint)Environment.ProcessId)
                {
                    if (Native.ReadText(window, Native.WmGetText, title.Capacity, title,
                        Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out _) == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "自分の窓の文字列取得が完了しません。");
                    }
                }
                else
                {
                    int count = ReadWindowText(window, title, title.Capacity);
                    int error = Marshal.GetLastPInvokeError();
                    if (count == 0 && error != 0)
                    {
                        throw new Win32Exception(error, "窓のタイトルを取得できません。");
                    }
                }
                className.Clear();
                if (ReadClassName(window, className, className.Capacity) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "窓のクラス名を取得できません。");
                }
                WindowIdentity? after = identity.ReadIdentity(window);
                if (after is null && !Native.IsWindow(window))
                {
                    return null;
                }
                if (after != before)
                {
                    throw new IOException("観測中に窓の所有者が変わりました。");
                }
                return new(new(window, before.Value.ProcessId, before.Value.ThreadId),
                    title.ToString(), className.ToString());
            }
            catch (Exception error) when (error is Win32Exception or IOException)
            {
                if (!Native.IsWindow(window))
                {
                    return null;
                }
                throw;
            }
        }
    }
}
