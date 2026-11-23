using System.Runtime.Versioning;
using UWSCSharp.Windows;

namespace UWSCSharp.Cli;

/// <summary>
/// ホストが保持する回収一覧を、CLIの操作受付へ接続します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCliInputCleanup(WindowsSelectiveInputCleanup cleanup) : ICliInputCleanup
{
    public int PendingCount => cleanup.PendingCount;

    public async Task<CliCleanupAttempt> RetryAsync()
    {
        WindowsSelectiveInputCleanupResult result = await cleanup.RetryAsync();
        return new CliCleanupAttempt(result.Errors, result.PendingCount);
    }
}
