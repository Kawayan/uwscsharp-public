using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Wpf;

// 各キーを明示的にトランザクションへ参加させます。
internal sealed class TransactedRegistry : IDisposable
{
    private const int KeyRead = 0x20019;
    private const int KeyWrite = 0x20006;
    private const int Delete = 0x10000;
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private readonly SafeFileHandle transaction;

    internal TransactedRegistry()
    {
        transaction = CreateTransaction(nint.Zero, nint.Zero, 0, 0, 0, 10000, null);
        if (transaction.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal RegistryKey? Open(string path, bool writable)
    {
        using SafeRegistryHandle root = Registry.CurrentUser.Handle;
        int error = RegOpenKeyTransacted(root,
            path, 0, KeyRead | (writable ? KeyWrite | Delete : 0),
            out SafeRegistryHandle key,
            transaction, nint.Zero);
        if (error is FileNotFound or PathNotFound)
        {
            key?.Dispose();
            return null;
        }

        if (error != 0)
        {
            key?.Dispose();
            throw new Win32Exception(error);
        }

        return RegistryKey.FromHandle(key);
    }

    internal RegistryKey Create(string path, bool requireNew = false)
    {
        using SafeRegistryHandle root = Registry.CurrentUser.Handle;
        int error = RegCreateKeyTransacted(root, path,
            0, null, 0, KeyRead | KeyWrite, nint.Zero,
            out SafeRegistryHandle key, out uint disposition, transaction, nint.Zero);
        if (error != 0)
        {
            key?.Dispose();
            throw new Win32Exception(error);
        }

        if (requireNew && disposition != 1)
        {
            key.Dispose();
            throw new InvalidOperationException("登録が同時に作成されました。");
        }

        return RegistryKey.FromHandle(key);
    }

    internal void DeleteTree(string parentPath, string name)
    {
        using RegistryKey target = Open(parentPath + @"\" + name, writable: true)
            ?? throw new InvalidOperationException("登録が変更されました。");
        int error = RegDeleteTree(target.Handle, null);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }

        using RegistryKey parent = Open(parentPath, writable: true)
            ?? throw new InvalidOperationException("登録が変更されました。");
        error = RegDeleteKeyTransacted(parent.Handle, name, 0, 0,
            transaction, nint.Zero);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    internal void PinExisting(string path)
    {
        using RegistryKey? key = Open(path, writable: true);
        if (key is null)
        {
            return;
        }

        string marker = "UWSCSharp.Transaction." + Guid.NewGuid().ToString("N");
        if (key.GetValueNames().Contains(marker, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("一時値が既に存在します。");
        }

        key.SetValue(marker, 1, RegistryValueKind.DWord);
        key.DeleteValue(marker);
    }

    internal void Commit()
    {
        if (!CommitTransaction(transaction))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        transaction.Dispose();
    }

    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateTransaction(
        nint transactionAttributes, nint unitOfWork, uint createOptions,
        uint isolationLevel, uint isolationFlags, uint timeout, string? description);

    [DllImport("ktmw32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);

    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW",
        CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyTransacted(SafeRegistryHandle key,
        string subKey, uint options, int desiredAccess, out SafeRegistryHandle result,
        SafeFileHandle transaction, nint extendedParameter);

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyTransactedW",
        CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyTransacted(SafeRegistryHandle key,
        string subKey, uint reserved, string? keyClass, uint options,
        int desiredAccess, nint securityAttributes, out SafeRegistryHandle result,
        out uint disposition, SafeFileHandle transaction, nint extendedParameter);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteTreeW",
        CharSet = CharSet.Unicode)]
    private static extern int RegDeleteTree(SafeRegistryHandle key, string? subKey);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteKeyTransactedW",
        CharSet = CharSet.Unicode)]
    private static extern int RegDeleteKeyTransacted(SafeRegistryHandle key,
        string subKey, int desiredAccess, uint reserved,
        SafeFileHandle transaction, nint extendedParameter);
}
