using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UWSCSharp.Windows.Tests;

internal sealed class LauncherDocumentProbe : IDisposable
{
    private const string Classes = @"Software\Classes\";
    private readonly string extension;
    private readonly string program;
    private bool disposed;

    private LauncherDocumentProbe(string directory, string name)
    {
        string suffix = Guid.NewGuid().ToString("N");
        extension = ".uwsc-launch-document-" + suffix;
        program = "UWSCSharp.Launcher.DocumentProbe." + suffix;
        DocumentPath = Path.Combine(directory, name + extension);
        ReceiptBase = Path.Combine(directory, "child-" + name);
    }

    internal string DocumentPath
    {
        get;
    }

    internal string ReceiptBase
    {
        get;
    }

    internal static LauncherDocumentProbe Create(string directory, string name, string executable)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("文書の関連付け検証はSandbox内だけで実行します。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("検証用の文書名が不正です。", nameof(name));
        }

        if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(executable)
            || !File.Exists(executable))
        {
            throw new ArgumentException("検証用の配置とハンドラーを絶対パスで指定してください。");
        }

        var probe = new LauncherDocumentProbe(directory, name);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(probe.DocumentPath, "launcher document probe");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Classes + probe.extension))
            {
                key.SetValue("", probe.program);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Classes + probe.program + @"\shell"))
            {
                key.SetValue("", "inspect");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                Classes + probe.program + @"\shell\inspect\command"))
            {
                key.SetValue("", $"\"{executable}\" --launcher-child-probe "
                    + $"\"{probe.ReceiptBase}.json\" \"{probe.ReceiptBase}.go\" \"%1\"");
            }

            SHChangeNotify(0x08000000, 0, 0, 0);
            return probe;
        }
        catch (Exception primary)
        {
            try
            {
                probe.Dispose();
            }
            catch (Exception cleanup)
            {
                primary.Data["関連付け準備の回収エラー"] = cleanup;
            }

            throw;
        }
    }

    /// <summary>
    /// この検証だけが作成した関連付けを回収します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        var errors = new List<Exception>();
        foreach (string key in new[] { extension, program })
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(Classes + key, throwOnMissingSubKey: false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        SHChangeNotify(0x08000000, 0, 0, 0);
        if (errors.Count > 0)
        {
            throw new AggregateException("文書関連付けの回収に失敗しました。", errors);
        }

        disposed = true;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint change, uint flags, nint first, nint second);
}
