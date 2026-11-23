using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッドの低レベルフックから修飾子だけの通知を作ります。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeModifierHotkeys
{
    private readonly ModifierHotkeyBindings bindings = new();
    private readonly Action<int> notify;
    private readonly ModifierHookProcedure procedure;
    private readonly IModifierHookBackend backend;
    private nint hook;
    private GCHandle lifetime;
    private ExceptionDispatchInfo? failure;
    private Dictionary<int, bool>? initialTransitions;

    internal NativeModifierHotkeys(Action<int> notify, IModifierHookBackend? backend = null)
    {
        this.notify = notify;
        this.backend = backend ?? new NativeModifierHookBackend();
        procedure = OnKeyboard;
    }

    internal bool Contains(int id) => bindings.Contains(id);

    internal bool IsCallbackRooted => lifetime.IsAllocated;

    internal void ThrowIfFaulted() => failure?.Throw();

    internal bool Register(int id, int modifiers)
    {
        ThrowIfFaulted();
        if (!bindings.CanRegister(id, modifiers))
        {
            return false;
        }

        if (hook == 0)
        {
            lifetime = GCHandle.Alloc(this);
            try
            {
                initialTransitions = [];
                hook = backend.Install(procedure);
                if (hook == 0)
                {
                    lifetime.Free();
                    return false;
                }

                // フック内の非同期キー状態は更新前なので、初期採取だけに使用します。
                int[] keys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
                bindings.Reset(keys.Where(backend.IsPressed));

                // 採取中に届いた押下・解放で、古い採取値を上書きします。
                foreach (KeyValuePair<int, bool> transition in initialTransitions)
                {
                    bindings.Apply(transition.Key, transition.Value);
                }

                ThrowIfFaulted();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
                try
                {
                    Shutdown();
                }
                catch (Exception cleanup)
                {
                    error.Data["UWSCSharp.ModifierHookCleanupErrors"] = cleanup;
                }

                throw;
            }
            finally
            {
                initialTransitions = null;
            }
        }

        return bindings.Register(id, modifiers);
    }

    internal bool Unregister(int id)
    {
        if (!bindings.Contains(id))
        {
            return false;
        }

        if (bindings.Count == 1)
        {
            if (!backend.Remove(hook))
            {
                // 解除が確認できなければ、登録とコールバックの参照を保持します。
                return false;
            }

            hook = 0;
            lifetime.Free();
            bindings.Reset([]);
        }

        return bindings.Remove(id);
    }

    internal void Shutdown()
    {
        if (hook != 0 && !backend.Remove(hook))
        {
            throw new InvalidOperationException("修飾子監視の解除に失敗しました。");
        }

        hook = 0;
        if (lifetime.IsAllocated)
        {
            lifetime.Free();
        }

        bindings.Clear();
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code == 0 && failure is null)
        {
            try
            {
                bool pressed = message is 0x0100 or 0x0104;
                if (pressed || message is 0x0101 or 0x0105)
                {
                    KeyboardData key = Marshal.PtrToStructure<KeyboardData>(data);
                    int physicalKey = ModifierHotkeyBindings.Normalize(
                        key.Key, key.ScanCode, key.Flags);
                    if (initialTransitions is { } transitions)
                    {
                        if (physicalKey != 0)
                        {
                            transitions[physicalKey] = pressed;
                        }
                    }
                    else if (bindings.Apply(physicalKey, pressed) is { } id)
                    {
                        notify(id);
                    }
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        }

        // スクリプトの通知が失敗しても、他のフックと入力先へ必ず渡します。
        return backend.Next(code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint Key;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
