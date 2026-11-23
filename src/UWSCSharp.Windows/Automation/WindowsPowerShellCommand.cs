using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// 観測した32bitエンジンへ、コマンドを1つの引数として渡します。
/// </summary>
internal static class WindowsPowerShellCommand
{
    internal static string Executable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32",
        "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string Build(string script)
    {
        string payload;
        try
        {
            payload = Convert.ToBase64String(
                new UnicodeEncoding(false, false, true).GetBytes(script));
        }
        catch (EncoderFallbackException failure)
        {
            throw WindowsOperationErrors.Record(failure);
        }

        // ユーザーの構文エラーより先に、出力の符号化を確定します。
        // dot-sourceで、ラッパーによる追加のローカルスコープを作りません。
        string source = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);"
            + ". $(try{[ScriptBlock]::Create([Text.Encoding]::Unicode.GetString("
            + "[Convert]::FromBase64String('" + payload + "')))}"
            + "catch{if($_.Exception.InnerException -is [Management.Automation.ParseException])"
            + "{[Console]::Error.WriteLine($_.Exception.InnerException.Message);exit 1};throw})";
        string command = Quote(Executable)
            + " -NoProfile -NonInteractive -OutputFormat Text -Command " + Quote(source);
        if (command.Length > 32766)
        {
            throw WindowsOperationErrors.Record(new ArgumentException(
                "PowerShellの起動引数が32766文字を超えています。"));
        }

        return command;
    }

    internal static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            text.Append(character);
            slashes = 0;
        }

        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}
