namespace UWSCSharp.Windows;

/// <summary>
/// 実行ファイルに付属する32ビットDLL中継を探します。
/// </summary>
internal static class NativeDllRelayHost
{
    internal static string? Find()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "dll-relay",
            "UWSCSharp.DllRelay.dll");
        return File.Exists(path) ? path : null;
    }

    internal static Task<NativeDllRelayClient> StartAsync()
    {
        string host = Find()
            ?? throw new InvalidOperationException("32ビットDLLの中継プログラムが見つかりません。");
        string runtime = Path.Combine(Path.GetDirectoryName(host)!, "runtime", "dotnet.exe");
        if (!File.Exists(runtime))
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            runtime = Path.Combine(directory, "dotnet", "dotnet.exe");
            if (directory.Length == 0 || !File.Exists(runtime))
            {
                throw new InvalidOperationException("32ビットDLLの中継にはx86版の.NETが必要です。");
            }
        }

        return NativeDllRelayClient.StartAsync(runtime, host, CancellationToken.None);
    }
}
