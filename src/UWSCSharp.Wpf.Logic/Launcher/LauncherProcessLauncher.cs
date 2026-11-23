using System.Diagnostics;
using System.IO;

namespace UWSCSharp.Wpf;

internal static class LauncherProcessLauncher
{
    internal static ProcessStartInfo CreateStartInfo(
        LauncherCommand command, string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);
        LauncherSlots.ValidateText(applicationDirectory);
        if (!Path.IsPathFullyQualified(applicationDirectory))
        {
            throw new ArgumentException("配置フォルダーは絶対パスで指定してください。",
                nameof(applicationDirectory));
        }

        if (!File.Exists(command.Path))
        {
            throw new FileNotFoundException(
                "ランチに登録した対象ファイルが見つかりません。", command.Path);
        }

        bool script = command.Kind == LauncherCommandKind.Script;
        string executable = script
            ? Path.Combine(applicationDirectory, "UWSCSharp.Association.exe")
            : command.Path;
        if (script && !File.Exists(executable))
        {
            throw new FileNotFoundException(
                "同じ配置に関連付け用の実行ファイルが見つかりません。", executable);
        }

        // 文書の動作名を固定せず、登録された既定動作をOSに選ばせる。
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = command.Kind == LauncherCommandKind.Document,
            WorkingDirectory = Path.GetDirectoryName(command.Path)!
        };
        if (script)
        {
            start.ArgumentList.Add(command.Path);
        }

        foreach (string argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    internal static int? Start(LauncherCommand command,
        string applicationDirectory)
    {
        ProcessStartInfo start = CreateStartInfo(command, applicationDirectory);
        // ハンドルだけを解放し、終了待ちや本体終了時の子の停止は行わない。
        using var process = Process.Start(start);
        if (process is null && !start.UseShellExecute)
        {
            throw new InvalidOperationException("ランチのプロセスを開始できませんでした。");
        }

        // 既定アプリは既存プロセスで開く場合もあり、PIDを返さないことがある。
        return process?.Id;
    }
}
