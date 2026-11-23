using UWSCSharp.Windows;

namespace UWSCSharp.Windows.Tests;

// 専用controlへのmessageだけを実行し、desktop全体の状態変更を抑止します。
internal class OwnedListSelectionPlatform : NativeListSelectionPlatform
{
    public override short KeyState(int key) => 0;

    public override Native.Point Cursor() => new(0, 0);

    public override void MoveCursor(Native.Point point)
    {
    }

    public override bool OwnsPointer(nint root, Native.Point point) => true;

    public override void Activate(nint root, CancellationToken token) => token.ThrowIfCancellationRequested();

    public override bool IsHostWindow(nint window) => false;

    public override bool Attach(uint target, uint current, bool attach) => false;

    public override void Foreground(nint window) => throw new InvalidOperationException();

    public override void Focus(nint window) => throw new InvalidOperationException();

    public override void Priority(uint value)
    {
    }

    public override void LockUpdates(nint window)
    {
    }
}
