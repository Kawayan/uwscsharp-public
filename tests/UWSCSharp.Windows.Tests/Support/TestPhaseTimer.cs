using System.Diagnostics;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 受入テストの工程時間をTRXの出力へ残します。
/// </summary>
internal sealed class TestPhaseTimer(ITestOutputHelper output, string phase)
    : IDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();

    /// <summary>
    /// 失敗時も工程の経過時間を記録します。
    /// </summary>
    public void Dispose()
    {
        output.WriteLine($"performance: phase={phase};elapsed-ms={elapsed.Elapsed.TotalMilliseconds:F3}");
    }

    internal static async Task RunAsync(
        ITestOutputHelper output, string phase, Func<Task> action)
    {
        using var timer = new TestPhaseTimer(output, phase);
        await action();
    }
}
