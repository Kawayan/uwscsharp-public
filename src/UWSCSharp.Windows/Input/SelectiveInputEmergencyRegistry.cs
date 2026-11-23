namespace UWSCSharp.Windows;

/// <summary>
/// callbackから所有者のロックを取らず、同じプロセスの対象へ要求します。
/// </summary>
internal sealed class SelectiveInputEmergencyRegistry
{
    internal static SelectiveInputEmergencyRegistry Shared { get; } = new();

    private readonly object gate = new();
    private WeakReference<SelectiveInputEmergencyStop>[] targets = [];
    internal SelectiveInputEmergencyChord Chord { get; } = new();
    private int monitors;

    internal void AttachMonitor(IEnumerable<int> pressed)
    {
        lock (gate)
        {
            if (monitors == 0)
            {
                Chord.Reset(pressed, mergeObserved: true);
            }
            monitors++;
        }
    }

    internal void DetachMonitor()
    {
        lock (gate)
        {
            if (--monitors == 0)
            {
                Chord.Uninitialize();
            }
        }
    }

    internal void Register(SelectiveInputEmergencyStop target)
    {
        lock (gate)
        {
            // 弱参照だけを公開し、終了済み実行を一覧が保持し続けません。
            WeakReference<SelectiveInputEmergencyStop>[] next =
                [.. targets.Where(entry => entry.TryGetTarget(out _)), new(target)];
            Volatile.Write(ref targets, next);
        }
    }

    internal void RequestAll()
    {
        foreach (WeakReference<SelectiveInputEmergencyStop> entry in Volatile.Read(ref targets))
        {
            if (entry.TryGetTarget(out SelectiveInputEmergencyStop? target))
            {
                target.Request();
            }
        }
    }
}
