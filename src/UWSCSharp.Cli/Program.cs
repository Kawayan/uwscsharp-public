using UWSCSharp.Cli;
using UWSCSharp.Windows;

if (OperatingSystem.IsWindows() && WindowsAudioHost.TryRun(args, out int audioExit))
{
    return audioExit;
}

if (OperatingSystem.IsWindows() && WindowsCommandHost.TryRun(args, out int hostExit))
{
    return hostExit;
}

return await CliApplication.RunAsync(args);
