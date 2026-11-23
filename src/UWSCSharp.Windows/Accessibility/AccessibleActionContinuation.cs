using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// MSAAの継続位置をCOM参照なしでsession内に保持します。
/// </summary>
internal sealed class AccessibleActionContinuation
{
    internal readonly record struct Snapshot(long Version, int[]? Path);

    private readonly object gate = new();
    private long version;
    private nint root;
    private uint process;
    private uint thread;
    private int[]? path;

    internal Snapshot Begin(nint window, int flags)
    {
        uint owner = Native.GetWindowThreadProcessId(window, out uint processId);
        lock (gate)
        {
            version = unchecked(version + 1);
            if (root != window || process != processId || thread != owner
                || ((ClickItemFlags)flags & ClickItemFlags.Continue) == 0)
            {
                path = null;
            }

            root = window;
            process = processId;
            thread = owner;
            return new Snapshot(version, path?.ToArray());
        }
    }

    internal void Save(Snapshot snapshot, int[] selectedPath)
    {
        lock (gate)
        {
            // 取消後に残るworkerが、後続呼出しの位置を書き換えないようにします。
            if (snapshot.Version == version)
            {
                path = [.. selectedPath];
            }
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            version = unchecked(version + 1);
            path = null;
            root = 0;
            process = 0;
            thread = 0;
        }
    }
}
