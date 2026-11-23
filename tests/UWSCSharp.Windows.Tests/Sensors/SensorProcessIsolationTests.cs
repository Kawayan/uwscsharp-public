using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 通常CLI/WPFの実COM・process起動は、Sandbox内だけで確認します。
[Trait("Category", "SandboxOnly")]
public sealed class SensorProcessIsolationTests(ITestOutputHelper output)
{
    private const string Source = """
        DIM readyFile=FOPEN("ready.txt",F_WRITE8)
        FPUT(readyFile,"ready",0)
        FCLOSE(readyFile)
        WHILE !FOPEN("continue.txt",F_EXISTS)
            SLEEP(0.02)
        WEND
        DIM lux=SENSOR(SNSR_LIGHT_LUX)
        DIM unknown=SENSOR(0)
        DIM barcode=SENSOR(SNSR_SCANNER_BARCODEDATA)
        DIM rfid=SENSOR(SNSR_SCANNER_RFIDTAG)
        DIM altitude=SENSOR(SNSR_LOCATION_ALTITUDE)
        DIM resultFile=FOPEN("result.txt",F_WRITE8)
        FPUT(resultFile,SNSR_LIGHT_LUX+"|"+VARTYPE(lux)+"|"+VARTYPE(unknown)+"|"+VARTYPE(barcode)+"|"+VARTYPE(rfid)+"|"+VARTYPE(altitude),0)
        FCLOSE(resultFile)
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardHostReadsSensorAbsenceAndExits(bool wpf)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(Directory.Exists(@"C:\ui-input"));
        uint callerDpi = GetSystemDpiForProcess(0);
        int callerAwareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        output.WriteLine($"caller-dpi-api={callerDpi}; caller-awareness={callerAwareness}");
        Assert.True(callerDpi > 0);
        Assert.InRange(callerAwareness, 0, 2);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string name = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string binaryDirectory = Path.Combine(root.FullName,
            "artifacts", "bin", name, configuration);
        string executable = Path.Combine(binaryDirectory, name + ".exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-sensor-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "日本語 sensor.uws");
            string ready = Path.Combine(directory, "ready.txt");
            string receipt = Path.Combine(directory, "result.txt");
            await File.WriteAllTextAsync(script, Source, new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = binaryDirectory
            };
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                // scriptの開始後、SENSORを呼ぶ前にprocessのDPIを読み取ります。
                await WaitForReadyAsync(process, ready);
                uint processDpi = GetSystemDpiForProcess(process.Handle);
                nint context = GetDpiAwarenessContextForProcess(process.Handle);
                int awareness = GetAwarenessFromDpiAwarenessContext(context);
                output.WriteLine($"host={name}; pid={process.Id}; process-system-dpi-api={processDpi}; process-awareness={awareness}");
                Assert.True(processDpi > 0);
                Assert.NotEqual(0, context);
                Assert.InRange(awareness, 0, 2);
                await File.WriteAllTextAsync(Path.Combine(directory, "continue.txt"), "continue");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                if (!process.HasExited)
                {
                    // このテストが生成したprocessだけを停止してから、結果を回収します。
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                output.WriteLine($"host={name}; exit={process.ExitCode}; stdout={await stdout}; stderr={await stderr}");
                if (File.Exists(receipt))
                {
                    output.WriteLine($"receipt={await File.ReadAllTextAsync(receipt)}");
                }
            }

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(receipt));
            Assert.Equal("20|0|0|0|0|0",
                (await File.ReadAllTextAsync(receipt)).TrimEnd('\r', '\n'));
        }
        finally
        {
            // processの終了後、このテストで作った一時ファイルだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForReadyAsync(Process process, string ready)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            Assert.False(process.HasExited, "script開始前にprocessが終了しました。");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30),
                "script開始の通知が期限内に届きませんでした。");
            await Task.Delay(20);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetSystemDpiForProcess(nint process);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDpiAwarenessContextForProcess(nint process);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
}
