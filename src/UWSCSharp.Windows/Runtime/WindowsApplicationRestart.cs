using System.Diagnostics;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// UWSC#を起動し直すための実行ファイルと引数を組み立てます。
/// </summary>
internal static class WindowsApplicationRestart
{
    internal static ProcessStartInfo CreateStartInfo(
        WindowsCommandHost host, string workingDirectory, bool restartScript,
        ScriptRunOptions script, IReadOnlyList<string> scriptOptions)
    {
        var start = new ProcessStartInfo(host.Executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        if (host.ManagedAssembly is { Length: > 0 } assembly)
        {
            start.ArgumentList.Add(assembly);
        }

        if (!restartScript)
        {
            return start;
        }

        if (script.SourcePath is not { } path || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: 再実行するファイルのパスが必要です。");
        }

        foreach (string option in scriptOptions)
        {
            start.ArgumentList.Add(option);
        }

        start.ArgumentList.Add(path);
        foreach (string argument in script.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
