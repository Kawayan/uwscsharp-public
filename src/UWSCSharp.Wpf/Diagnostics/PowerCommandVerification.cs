using System.IO;
using UWSCSharp.Core;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class PowerCommandVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        Require(SandboxEnvironment.IsCurrent, "Sandbox内の検証");
        var requests = new List<PowerCommandRequest>();
        var errors = new List<Exception>();
        var window = new MainWindow(false, reportError: errors.Add,
            executeSystemPowerCommand: requests.Add)
        {
            ShowActivated = false
        };
        try
        {
            window.Show();
            int result = await window.RunCommandLineAsync("""
                POFF(P_POWEROFF OR P_FORCE)
                POFF(p_monipower3)
                PRINT "完了"
                """, new ScriptRunOptions([])
            {
                SourcePath = Path.Combine(directory, "power-commands.uws")
            });
            Require(result == 0 && errors.Count == 0,
                "通常の再生として完了する");
            Require(requests.SequenceEqual([
                new(PowerCommand.PowerOff, true, false),
                new(PowerCommand.MonitorOn, false, false)
            ]), "電源操作と画面操作が順に実行元へ届く");
            await File.WriteAllTextAsync(receipt, "Passed");
            return 0;
        }
        finally
        {
            window.Close();
        }
    }
}
