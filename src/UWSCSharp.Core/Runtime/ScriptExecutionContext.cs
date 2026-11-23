namespace UWSCSharp.Core;

/// <summary>
/// 各実行の開始時に確定する起動情報を保持します。
/// </summary>
internal sealed record ScriptExecutionContext(
    string ApplicationDataDirectory,
    string WindowsDirectory,
    string SystemDirectory,
    string ExecutableDirectory,
    string ScriptName)
{
    internal static ScriptExecutionContext Create(
        ScriptProgram program, ScriptRunOptions options)
    {
        string? sourcePath = program.SourcePath ?? options.SourcePath;
        return new(
            options.ApplicationDataDirectory ?? Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            options.WindowsDirectory ?? Environment.GetFolderPath(
                Environment.SpecialFolder.Windows),
            options.SystemDirectory ?? Environment.SystemDirectory,
            options.ExecutableDirectory ?? Path.TrimEndingDirectorySeparator(
                AppContext.BaseDirectory),
            sourcePath is null ? "" : Path.GetFileName(sourcePath));
    }
}
