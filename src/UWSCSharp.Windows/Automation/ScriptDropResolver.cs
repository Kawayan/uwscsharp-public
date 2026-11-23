using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// ドロップから確定した対象と起動引数を保持します。
/// </summary>
internal sealed record DroppedScript(string Path, IReadOnlyList<string> Arguments);

/// <summary>
/// リンクから読んだ対象と、未分割の起動引数です。
/// </summary>
internal sealed record ScriptShortcut(string TargetPath, string Arguments);

/// <summary>
/// リンクを実行せず、スクリプトの対象と引数へ変換します。
/// </summary>
internal sealed partial class ScriptDropResolver(
    Func<string, ScriptShortcut>? readShortcut = null,
    Func<string, bool>? fileExists = null)
{
    internal DroppedScript Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new(path, []);
        }

        string link = System.IO.Path.GetFullPath(path);
        Func<string, bool> exists = fileExists ?? File.Exists;
        if (!exists(link))
        {
            throw new FileNotFoundException("ショートカットが見つかりません。", link);
        }
        ScriptShortcut value = (readShortcut ?? ReadShortcut)(link);
        if (string.IsNullOrWhiteSpace(value.TargetPath)
            || !System.IO.Path.IsPathFullyQualified(value.TargetPath)
            || !System.IO.Path.GetExtension(value.TargetPath).Equals(".uws", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ショートカットの対象には.uwsファイルを指定してください。");
        }
        string target = System.IO.Path.GetFullPath(value.TargetPath);
        if (!exists(target))
        {
            throw new FileNotFoundException("ショートカットの対象が見つかりません。", target);
        }
        return new(target, SplitArguments(value.Arguments));
    }

    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Contains('\0'))
        {
            throw new ArgumentException("起動引数にNUL文字は使えません。", nameof(arguments));
        }
        if (arguments.Length == 0)
        {
            return [];
        }
        // 先頭要素だけの特別な解釈を、固定の実行ファイル名へ適用します。
        nint pointer = CommandLineToArgvW("uwsc-link " + arguments, out int count);
        if (pointer == 0)
        {
            throw new IOException("ショートカットの引数を分割できません。",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        try
        {
            string[] result = new string[count - 1];
            for (int index = 1; index < count; index++)
            {
                result[index - 1] = Marshal.PtrToStringUni(
                    Marshal.ReadIntPtr(pointer, index * nint.Size))!;
            }
            return Array.AsReadOnly(result);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static ScriptShortcut ReadShortcut(string path)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!);
            ((IPersistFile)instance!).Load(path, 0);
            var link = (IShellLink)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            // Resolveは呼ばず、保存済みの対象と引数だけを読みます。
            link.GetPath(target, target.Capacity, 0, 0);
            link.GetArguments(arguments, arguments.Capacity);
            if (target.Length >= target.Capacity - 1 || arguments.Length >= arguments.Capacity - 1)
            {
                throw new IOException("ショートカットの対象または引数が長すぎます。");
            }
            return new(target.ToString(), arguments.ToString());
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            throw new IOException("ショートカットを読み取れません。", error);
        }
        finally
        {
            if (instance is not null)
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            int capacity, nint findData, uint flags);

        void GetIDList(out nint list);

        void SetIDList(nint list);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    }
}
