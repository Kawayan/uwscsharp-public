using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 既存の実体を開き、保存先の別名とファイル識別子を照合します。
/// </summary>
public static partial class WindowsStoragePath
{
    /// <summary>
    /// 未作成の末尾は、最も近い既存の親の解決結果へ連結します。
    /// </summary>
    public static string Resolve(string path)
    {
        string existing = Path.GetFullPath(path);
        var suffix = new Stack<string>();
        while (true)
        {
            using SafeFileHandle handle = Open(existing);
            if (!handle.IsInvalid)
            {
                if (suffix.Count > 0 && (File.GetAttributes(existing)
                    & FileAttributes.Directory) == 0)
                {
                    throw new IOException("ログの保存先の親がフォルダーではありません。");
                }
                var buffer = new StringBuilder(32768);
                uint size = GetFinalPathNameByHandleW(handle, buffer,
                    (uint)buffer.Capacity, 0);
                if (size == 0 || size >= buffer.Capacity)
                {
                    throw new IOException("ログの実パスを照合できません。",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }
                string resolved = buffer.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = @"\\" + resolved[8..];
                }
                else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    resolved = resolved[4..];
                }
                while (suffix.TryPop(out string? component))
                {
                    resolved = Path.Combine(resolved, component);
                }
                return Path.GetFullPath(resolved);
            }
            int error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3))
            {
                throw new IOException("ログの保存先を照合できません。",
                    new Win32Exception(error));
            }
            try
            {
                if ((File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("リンク先が存在しないログの保存先は指定できません。");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing)
            {
                throw new IOException("ログの保存先の親を照合できません。");
            }
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }
    }

    /// <summary>
    /// 既存ファイルの物理的な識別子を取得します。
    /// </summary>
    public static (ulong Volume, Guid Id) Identity(string path)
    {
        using SafeFileHandle handle = Open(Path.GetFullPath(path));
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle,
            18, out FileIdentity identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            throw new IOException("ログのファイル識別子を照合できません。",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (identity.Id == Guid.Empty)
        {
            throw new IOException("ログのファイル識別子が空です。");
        }
        return (identity.Volume, identity.Id);
    }

    private static SafeFileHandle Open(string path) =>
        CreateFileW(path, 0x80, 7, 0, 3, 0x02000000, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public Guid Id;
    }
}
