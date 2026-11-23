namespace UWSCSharp.Windows;

// 解放後の状態を、次の操作が始まるまで同じクリックに対応付けます。
internal sealed class CheckboxRecordingCandidate
{
    private (nint Target, int Before, long Sequence, uint ReleaseTime)? pending;
    private (nint Target, uint ReleaseTime)? discarded;
    private bool requiresLaterEvent;

    internal void AwaitState(nint target, int before, long sequence, uint releaseTime)
    {
        requiresLaterEvent = discarded == (target, releaseTime);
        discarded = null;
        pending = (target, before, sequence, releaseTime);
    }

    internal long? Observe(nint target, int? state)
    {
        if (pending is not { } candidate || candidate.Target != target)
        {
            return null;
        }

        if (state == candidate.Before)
        {
            return null;
        }

        pending = null;
        return state is 0 or 1 ? candidate.Sequence : null;
    }

    internal void Discard()
    {
        if (pending is { } candidate)
        {
            discarded = (candidate.Target, candidate.ReleaseTime);
            pending = null;
        }
    }

    internal bool CanObserve(nint target, uint eventTime) =>
        pending is { } candidate && candidate.Target == target
        // Windowsの32ビット時刻は周回するため、差分を符号付きで比較します。
        && unchecked((int)(eventTime - candidate.ReleaseTime))
            >= (requiresLaterEvent ? 1 : 0);

    internal static int? ReadState(nint target)
    {
        const uint getCheck = 0xf0;
        return Native.SendMessageTimeoutW(target, getCheck, 0, 0,
            Native.SmtoAbortIfHung, 20, out nuint result) != 0 && result <= 1
            ? (int)result : null;
    }
}
