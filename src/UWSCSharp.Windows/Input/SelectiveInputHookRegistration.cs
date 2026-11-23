using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal delegate nint SelectiveInputHookProcedure(int code, nuint message, nint data);

/// <summary>
/// 低レベル入力フックの登録・解除を行います。
/// </summary>
internal interface ISelectiveInputHookBackend
{
    (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure);

    (bool Success, int Error) Remove(nint hook);
}

/// <summary>
/// 所有スレッド上で入力フックの寿命を管理します。コールバック内から操作しません。
/// </summary>
internal sealed class SelectiveInputHookRegistration
{
    private readonly ISelectiveInputHookBackend backend;
    private readonly SelectiveInputHookProcedure keyboard;
    private readonly SelectiveInputHookProcedure mouse;
    private readonly Thread owner = Thread.CurrentThread;
    private nint keyboardHook;
    private nint mouseHook;
    private GCHandle lifetime;
    private bool changing;

    internal SelectiveInputHookRegistration(ISelectiveInputHookBackend backend,
        SelectiveInputHookProcedure keyboard, SelectiveInputHookProcedure mouse)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(mouse);
        this.backend = backend;
        this.keyboard = keyboard;
        this.mouse = mouse;
    }

    internal bool IsCallbackRooted => lifetime.IsAllocated;

    internal bool HasKeyboard => keyboardHook != 0;

    internal void Register(SelectiveInputDevices devices, Action? keyboardInstalled = null)
    {
        CheckAccess();
        if (devices == SelectiveInputDevices.None
            || (devices & ~SelectiveInputDevices.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devices));
        }

        if (keyboardHook != 0 || mouseHook != 0)
        {
            throw new InvalidOperationException("入力フックがまだ登録されています。");
        }

        changing = true;
        try
        {
            lifetime = GCHandle.Alloc(this);
            if ((devices & SelectiveInputDevices.Keyboard) != 0)
            {
                keyboardHook = Install(13, keyboard);
                keyboardInstalled?.Invoke();
            }

            if ((devices & SelectiveInputDevices.Mouse) != 0)
            {
                mouseHook = Install(14, mouse);
            }
        }
        catch (Exception error)
        {
            List<Exception> cleanup = RemoveAll();
            if (cleanup.Count != 0)
            {
                cleanup.Insert(0, error);
                throw new AggregateException("入力フックの登録と回収に失敗しました。", cleanup);
            }

            throw;
        }
        finally
        {
            changing = false;
        }
    }

    internal void Unregister() => Unregister(false);

    internal void Unregister(bool keepEmergencyMonitor)
    {
        CheckAccess();
        changing = true;
        try
        {
            List<Exception> failures = RemoveAll(keepEmergencyMonitor);
            if (failures.Count != 0)
            {
                throw new AggregateException("入力フックの解除に失敗しました。", failures);
            }
        }
        finally
        {
            changing = false;
        }
    }

    private nint Install(int type, SelectiveInputHookProcedure procedure)
    {
        (nint handle, int error) = backend.Install(type, procedure);
        if (handle == 0)
        {
            throw new Win32Exception(error, "入力フックを登録できませんでした。");
        }

        return handle;
    }

    private List<Exception> RemoveAll(bool keepEmergencyMonitor = false)
    {
        var failures = new List<Exception>();
        Remove(ref mouseHook, failures);
        if (!keepEmergencyMonitor || mouseHook == 0)
        {
            Remove(ref keyboardHook, failures);
        }
        if (mouseHook == 0 && keyboardHook == 0 && lifetime.IsAllocated)
        {
            lifetime.Free();
        }

        return failures;
    }

    private void Remove(ref nint hook, List<Exception> failures)
    {
        if (hook == 0)
        {
            return;
        }

        try
        {
            (bool success, int error) = backend.Remove(hook);
            if (!success)
            {
                if (error == 1404)
                {
                    // OSが無効と確認したハンドルは回収し、失敗の情報だけを残します。
                    hook = 0;
                }

                throw new Win32Exception(error, "入力フックを解除できませんでした。");
            }

            hook = 0;
        }
        catch (Exception error)
        {
            // 無効と確認できない解除失敗では、ハンドルを保持して再試行します。
            failures.Add(error);
        }
    }

    private void CheckAccess()
    {
        if (Thread.CurrentThread != owner || changing)
        {
            throw new InvalidOperationException(
                "入力フックの操作は所有スレッドから再入せずに行ってください。");
        }
    }
}
