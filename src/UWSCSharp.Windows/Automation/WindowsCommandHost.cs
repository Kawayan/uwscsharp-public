using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 実行元を使い、コンソール表示とNUL入力を両立する起動情報です。
/// </summary>
public sealed partial record WindowsCommandHost(string Executable, string? ManagedAssembly = null)
{
    private const string HostArgument = "--uwsc-command-host";
    private static readonly ConsoleHandler Handler = type => type is 0 or 1;

    /// <summary>
    /// コマンド起動入口を実装した現在の実行元を取得します。
    /// </summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "dotnet起動時にだけ位置を読み、単一EXEの空文字列は起動引数に追加しません。")]
    public static WindowsCommandHost ForCurrentProcess()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("実行元を取得できません。");
        string? assembly = Path.GetFileNameWithoutExtension(executable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Assembly.GetEntryAssembly()?.Location : null;
        return new(executable, assembly);
    }

    internal string BuildCommandLine(string commandLine, bool visible)
        => BuildCommandLine(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            commandLine, visible ? 1 : 0);

    internal string BuildCommandLine(string executable, string commandLine, int display)
    {
        string prefix = $"\"{Executable}\"";
        if (!string.IsNullOrEmpty(ManagedAssembly))
        {
            prefix += $" \"{ManagedAssembly}\"";
        }

        string payload = Convert.ToBase64String(
            new UnicodeEncoding(false, false, true).GetBytes(commandLine));
        string target = Convert.ToBase64String(
            new UnicodeEncoding(false, false, true).GetBytes(executable));
        return $"{prefix} {HostArgument} {target} {payload} {display}";
    }

    /// <summary>
    /// 通常のUIや出力設定より前に、専用の起動要求を処理します。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TryRun(string[] arguments, out int exitCode)
    {
        exitCode = 2;
        if (arguments.Length == 0 || arguments[0] != HostArgument)
        {
            return false;
        }

        try
        {
            if (arguments.Length == 3 && arguments[2] is "0" or "1")
            {
                string command = new UnicodeEncoding(false, false, true)
                    .GetString(Convert.FromBase64String(arguments[1]));
                // コマンドの非0終了と、ホスト自身の失敗を分けます。
                _ = Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    command, arguments[2] == "1" ? 1 : 0);
                exitCode = 0;
            }
            else if (arguments.Length == 4 && arguments[3] is "0" or "1" or "2")
            {
                var encoding = new UnicodeEncoding(false, false, true);
                string executable = encoding.GetString(Convert.FromBase64String(arguments[1]));
                string command = encoding.GetString(Convert.FromBase64String(arguments[2]));
                _ = Run(executable, command, arguments[3][0] - '0');
                exitCode = 0;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException
            or ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine(error.Message);
            exitCode = 1;
        }

        return true;
    }

    private static int Run(string executable, string command, int display)
    {
        if (command.Contains('\0') || executable.Contains('\0')
            || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("コマンドにNULを指定できません。");
        }

        bool visible = display != 0;
        if (visible && GetConsoleWindow() == 0 && !AllocConsole())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (display == 2)
        {
            Native.ShowWindow(GetConsoleWindow(), 2);
        }

        var security = new CapturedProcess.SecurityAttributes
        {
            Size = Marshal.SizeOf<CapturedProcess.SecurityAttributes>(),
            Inherit = true
        };
        using SafeFileHandle input = CapturedProcess.CreateFileW("NUL",
            0x80000000, 3, ref security, 3, 0, 0);
        if (input.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using SafeFileHandle output = OutputHandle(-11, ref security);
        using SafeFileHandle error = OutputHandle(-12, ref security);
        using var inherited = new CapturedProcess.HandleInheritanceList(input, output, error);
        var startup = new CapturedProcess.StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = Marshal.SizeOf<CapturedProcess.StartupInfoEx>(),
                Flags = 0x100,
                Input = input.DangerousGetHandle(),
                Output = output.DangerousGetHandle(),
                Error = error.DangerousGetHandle()
            },
            Attributes = inherited.Pointer
        };
        bool registered = visible && SetConsoleCtrlHandler(Handler, true);
        try
        {
            if (!CapturedProcess.CreateProcessW(executable, new StringBuilder(command),
                0, 0, true, 0x00080000 | (visible ? 0u : 0x08000000),
                0, null, ref startup, out CapturedProcess.ProcessInfo information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var process = new SafeFileHandle(information.Process, true);
            using var thread = new SafeFileHandle(information.Thread, true);
            if (CapturedProcess.WaitForSingleObject(process, uint.MaxValue) != 0
                || !CapturedProcess.GetExitCodeProcess(process, out uint code))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return unchecked((int)code);
        }
        finally
        {
            if (registered)
            {
                SetConsoleCtrlHandler(Handler, false);
            }
        }
    }

    private static SafeFileHandle OutputHandle(int kind,
        ref CapturedProcess.SecurityAttributes security)
    {
        nint source = GetStdHandle(kind);
        if (source is 0 or -1)
        {
            SafeFileHandle sink = CapturedProcess.CreateFileW("NUL",
                0x40000000, 3, ref security, 3, 0, 0);
            if (sink.IsInvalid)
            {
                var failure = new Win32Exception(Marshal.GetLastWin32Error());
                sink.Dispose();
                throw failure;
            }

            return sink;
        }

        if (!DuplicateHandle((nint)(-1), source, (nint)(-1),
            out SafeFileHandle duplicate, 0, true, 2))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            duplicate.Dispose();
            throw failure;
        }

        return duplicate;
    }

    private delegate bool ConsoleHandler(uint type);
}
