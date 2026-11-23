using System.Reflection;

namespace UWSCSharp.Core;

/// <summary>
/// UWSC#実行系の版情報を提供します。
/// </summary>
internal static class ScriptRuntimeInformation
{
    internal static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Assembly assembly = typeof(Interpreter).Assembly;
        string? version = assembly.GetCustomAttribute<
            AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException(
                "UWSC#実行系のバージョンを取得できませんでした。");
    }
}
