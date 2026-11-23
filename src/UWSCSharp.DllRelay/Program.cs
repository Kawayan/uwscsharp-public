using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Windows;

[assembly: SupportedOSPlatform("windows")]

namespace UWSCSharp.DllRelay;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X86
            || args is not ["--pipe", var pipeName])
        {
            Console.Error.WriteLine("DLL中継にはWindows x86と接続先が必要です。");
            return 2;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(15_000);
            using var session = new NativeDllRelaySession();
            session.Serve(pipe);
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException
            or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
