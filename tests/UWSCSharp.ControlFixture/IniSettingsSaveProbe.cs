using UWSCSharp.IO;

namespace UWSCSharp.ControlFixture;

internal static class IniSettingsSaveProbe
{
    internal static async Task RunAsync(string mode, string ini,
        string ready, string release)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = timeout.Token;
        await File.WriteAllTextAsync(ready, "ready", token);
        while (!File.Exists(release))
        {
            await Task.Delay(10, token);
        }

        ValueTask save = mode switch
        {
            "window" => new ApplicationWindowSettings(true).SaveAsync(ini, token),
            "log" => new ScriptLogDefaults(123).SaveAsync(ini, token),
            "log-alternate" => new ScriptLogDefaults(456).SaveAsync(ini, token),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        await save;
    }
}
