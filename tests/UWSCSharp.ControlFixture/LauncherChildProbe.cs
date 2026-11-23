using System.Diagnostics;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

internal static class LauncherChildProbe
{
    internal static int Run(string receipt, string release, string[] arguments)
    {
        string prepared = receipt + ".prepared";
        File.WriteAllText(prepared, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            working_directory = Environment.CurrentDirectory,
            arguments
        }));
        File.Move(prepared, receipt);
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(90))
            {
                return 3;
            }

            Thread.Sleep(25);
        }

        File.WriteAllText(receipt + ".done", "completed");
        return 0;
    }
}
