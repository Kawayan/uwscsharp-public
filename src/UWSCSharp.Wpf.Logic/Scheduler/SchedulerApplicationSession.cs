namespace UWSCSharp.Wpf;

/// <summary>
/// 本体の寿命に合わせ、監視世代を越えて起動時受付を1回に制限します。
/// </summary>
internal sealed class SchedulerApplicationSession
{
    private int startupAccepted;

    internal bool TryAcceptStartup() =>
        Interlocked.CompareExchange(ref startupAccepted, 1, 0) == 0;
}
