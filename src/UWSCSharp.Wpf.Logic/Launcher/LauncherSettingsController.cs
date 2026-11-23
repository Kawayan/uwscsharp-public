namespace UWSCSharp.Wpf;

internal sealed class LauncherSettingsController(string path)
{
    private LauncherSettingsSnapshot? snapshot;

    internal LauncherSettings? Current => snapshot?.Settings;

    internal Exception? LoadError
    {
        get; private set;
    }

    internal void Reload() => ReloadWithHotkey(_ => { });

    internal void ReloadWithHotkey(Action<LauncherSettings> apply)
    {
        LauncherSettingsSnapshot candidate;
        try
        {
            candidate = LauncherSettingsStore.LoadSnapshot(path);
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException
            or UnauthorizedAccessException or ArgumentException)
        {
            LoadError = error;
            throw;
        }

        // 有効なJSONとOS登録の失敗を分け、初回の競合でも登録内容を編集できます。
        LoadError = null;
        try
        {
            apply(candidate.Settings);
        }
        catch
        {
            snapshot ??= candidate;
            throw;
        }

        snapshot = candidate;
    }

    internal void Save(LauncherSettings candidate)
    {
        if (LoadError is not null || snapshot is null)
        {
            throw new InvalidOperationException("設定を再読込みするか、退避して初期化してください。");
        }

        snapshot = LauncherSettingsStore.Save(path, candidate, snapshot);
    }

    internal string? ResetWithBackup()
    {
        string? backup = LauncherSettingsStore.ResetWithBackup(path,
            out LauncherSettingsSnapshot reset);
        snapshot = reset;
        LoadError = null;
        return backup;
    }
}
