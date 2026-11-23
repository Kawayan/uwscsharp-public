namespace UWSCSharp.Core;

/// <summary>
/// UI通知の回数に依存せず、単調時計から選択期限を求めます。
/// </summary>
internal sealed class SelectionBoxDeadline(TimeProvider clock, double seconds)
{
    private readonly long started = clock.GetTimestamp();

    internal bool Enabled => seconds > 0;

    internal double RemainingFraction => !Enabled ? 1 :
        Math.Clamp(1 - clock.GetElapsedTime(started).TotalSeconds / seconds, 0, 1);

    internal bool Expired => Enabled && RemainingFraction <= 0;
}
