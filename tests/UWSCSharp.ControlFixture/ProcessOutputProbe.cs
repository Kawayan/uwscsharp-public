using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 既知の出力を送り、指定した場合だけコンソールを検査します。
/// </summary>
internal static class ProcessOutputProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (3 or 4))
        {
            return 2;
        }

        if (args[..3] is not [string mode, string size, string exit]
            || !int.TryParse(size, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int count)
            || count is < 0 or > 16 * 1024 * 1024
            || !int.TryParse(exit, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int code)
            || mode is not ("duplex" or "stdout" or "utf8"
                or "utf16" or "cp932" or "invalid" or "ordered"
                or "stdin" or "hold" or "parent" or "parent-wait"
                or "environment" or "gated" or "console" or "console-hold"
                or "console-after-gate" or "idle" or "invalid-parent"
                or "overflow-parent"))
        {
            return 2;
        }

        using Stream output = Console.OpenStandardOutput();
        using Stream error = Console.OpenStandardError();
        if (mode == "console" && args.Length == 4)
        {
            const string text = "uwsc-console-output-proof";
            await output.WriteAsync(Encoding.ASCII.GetBytes(text));
            await output.FlushAsync();
            nint handle = GetStdHandle(-11);
            var buffer = new StringBuilder(4096);
            bool console = GetConsoleMode(handle, out _);
            bool read = ReadConsoleOutputCharacterW(handle, buffer, 4096, 0, out _);
            using Stream input = Console.OpenStandardInput();
            await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new
            {
                Console = console,
                Visible = IsWindowVisible(GetConsoleWindow()),
                Output = read && buffer.ToString().Contains(text, StringComparison.Ordinal),
                Eof = input.ReadByte() == -1
            }));
            return code;
        }

        if (mode is "console-hold" or "console-after-gate"
            && args.Length == 4)
        {
            string marker = args[3];
            if (mode == "console-after-gate")
            {
                while (!File.Exists(marker + ".go"))
                {
                    await Task.Delay(25);
                }
            }

            string metadata = JsonSerializer.Serialize(new
            {
                Pid = Environment.ProcessId,
                Window = GetConsoleWindow().ToInt64(),
                OutputIsConsole = GetConsoleMode(GetStdHandle(-11), out _)
            });
            await File.WriteAllTextAsync(marker + ".pending", metadata);
            File.Move(marker + ".pending", marker);
            string interrupt = marker + ".interrupt";
            while (!File.Exists(interrupt))
            {
                await Task.Delay(25);
            }

            if (!GenerateConsoleCtrlEvent(0, 0))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error());
            }

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "idle" && args.Length == 4)
        {
            await PublishAsync(args[3]);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode is "invalid-parent" or "overflow-parent" && args.Length == 4)
        {
            // 標準ハンドル以外として元の取得パイプが子へ残ることも防ぎます。
            if (!SetHandleInformation(GetStdHandle(-11), 1, 0)
                || !SetHandleInformation(GetStdHandle(-12), 1, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            string executable = Environment.ProcessPath!;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "--process-output-probe", "idle", "0", "37", args[3] })
            {
                start.ArgumentList.Add(argument);
            }

            using Process child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(args[3] + ".go"))
            {
                await Task.Delay(25, timeout.Token);
            }

            if (mode == "invalid-parent")
            {
                await output.WriteAsync(new byte[] { 0xff });
            }
            else
            {
                Fill(output, count, 65);
            }

            return code;
        }

        if (args.Length == 4 && mode is not ("hold" or "parent" or "parent-wait"))
        {
            await PublishAsync(args[3] + ".started");
        }

        if (mode is "parent" or "parent-wait")
        {
            if (args.Length != 4)
            {
                return 2;
            }

            ProcessOutputChild.Start(args[3]);
            if (mode == "parent-wait")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
        }
        else if (mode == "hold")
        {
            await output.WriteAsync("child-ready"u8.ToArray());
            await output.FlushAsync();
            if (args.Length == 4)
            {
                await File.WriteAllTextAsync(args[3] + ".pending",
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                File.Move(args[3] + ".pending", args[3]);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else if (mode == "ordered")
        {
            output.Write("stdout-1|"u8);
            output.Flush();
            error.Write("stderr-1|"u8);
            error.Flush();
            output.Write("stdout-2|"u8);
            output.Flush();
            error.Write("stderr-2"u8);
            error.Flush();
        }
        else if (mode == "environment")
        {
            byte[] bytes = Encoding.ASCII.GetBytes(
                $"ACP={GetACP()} OEM={GetOEMCP()} OUTPUT={GetConsoleOutputCP()} PID={Environment.ProcessId}");
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        else if (mode == "gated")
        {
            if (args.Length != 4)
            {
                return 2;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(args[3] + ".go"))
            {
                await Task.Delay(25, timeout.Token);
            }

            try
            {
                await output.WriteAsync("late-output"u8.ToArray());
                await output.FlushAsync();
            }
            catch (IOException writeError)
            {
                await File.WriteAllTextAsync(args[3] + ".write-error",
                    $"{writeError.GetType().FullName} HResult={writeError.HResult}");
            }
        }
        else if (mode == "stdin")
        {
            using Stream input = Console.OpenStandardInput();
            output.WriteByte(input.ReadByte() == -1 ? (byte)1 : (byte)0);
        }
        else if (mode is "duplex" or "stdout")
        {
            Task stdout = Task.Factory.StartNew(() => Fill(output, count, 65),
                CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Task stderr = Task.Factory.StartNew(() => Fill(error,
                mode == "duplex" ? count : 0, 90), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        else
        {
            byte[] bytes;
            if (mode == "invalid")
            {
                bytes = [0, 0xf0, 0x9f, 0x98, 0xff];
            }
            else
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Encoding encoding = mode switch
                {
                    "utf16" => new UnicodeEncoding(false, false, true),
                    "cp932" => Encoding.GetEncoding(932),
                    _ => new UTF8Encoding(false, true)
                };
                string text = mode == "cp932"
                    ? "日本語\r\nsecond\0tail" : "日本語😀\r\nsecond\0tail";
                bytes = encoding.GetBytes(text);
            }

            foreach (byte value in bytes)
            {
                await output.WriteAsync(new byte[] { value }).ConfigureAwait(false);
            }

            await output.FlushAsync().ConfigureAwait(false);
        }

        if (args.Length == 4 && mode is not ("hold" or "parent" or "parent-wait"))
        {
            await PublishAsync(args[3]);
        }

        return code;
    }

    private static async Task PublishAsync(string marker)
    {
        await File.WriteAllTextAsync(marker + ".pending",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.Move(marker + ".pending", marker);
    }

    private static void Fill(Stream stream, int count, byte value)
    {
        byte[] block = new byte[8192];
        Array.Fill(block, value);
        while (count > 0)
        {
            int size = Math.Min(count, block.Length);
            stream.Write(block, 0, size);
            count -= size;
        }

        stream.Flush();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int kind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal,
        uint processGroup);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(nint handle,
        StringBuilder text, uint count, uint origin, out uint read);
}
