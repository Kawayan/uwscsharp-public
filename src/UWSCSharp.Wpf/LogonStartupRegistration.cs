using System.IO;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

internal static class LogonStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "UWSCSharp.Wpf";

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(
                RunKey, writable: true);
            existing?.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("実行ファイルのパスを取得できません。");
        string command = $"\"{executable}\" --single-instance --start-in-tray";
        if (command.Length > 260)
        {
            throw new IOException("自動起動のコマンドがWindowsの長さ制限を超えています。");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Name, command, RegistryValueKind.String);
    }
}
