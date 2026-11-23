namespace UWSCSharp.Windows;

// 無入力は正常と扱い、別経路の入力が届いた場合だけ応答を照合します。
internal sealed class HookLiveness(long graceMilliseconds = 1000)
{
    private readonly long?[] lastHook = new long?[2];
    private readonly long?[] pending = new long?[2];

    internal void Hook(int kind, long now)
    {
        lastHook[kind] = now;
        pending[kind] = null;
    }

    internal void Raw(int kind, long now) => Raw(kind, now, now);

    internal void Raw(int kind, long inputTime, long now)
    {
        // フックは通常WM_INPUTより先に呼ばれるため、直前の通知も認めます。
        if (lastHook[kind] is not { } last || inputTime - last > 200)
        {
            pending[kind] ??= now;
        }
    }

    internal int Missing(long now)
    {
        int result = 0;
        for (int kind = 0; kind < pending.Length; kind++)
        {
            if (pending[kind] is { } since && now - since >= graceMilliseconds)
            {
                result |= 1 << kind;
            }
        }
        return result;
    }

    internal void Reset(bool clearResponses = false)
    {
        if (clearResponses)
        {
            Array.Clear(lastHook);
        }
        Array.Clear(pending);
    }
}

internal interface IRecordingHealthMonitor : IDisposable
{
    void Start();

    void Hook(int kind, uint inputTime);

    int ProcessMessage(nint window, uint message, nuint wParam, nint lParam, uint inputTime);

    void Reset();
}
