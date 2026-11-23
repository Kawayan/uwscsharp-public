using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Cli;

/// <summary>
/// 待機しない読取りで、回収操作の入力だけを取得します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeConsoleRecoveryInput : ICliRecoveryInput
{
    private const ushort ReadNoWait = 0x0002;
    private const uint ProcessedInput = 0x0001;
    private readonly nint input;
    private readonly nint module;
    private readonly uint originalMode;
    private readonly ReadInput read;
    private readonly InputRecord[] buffer = new InputRecord[64];
    private readonly bool restoreMode;
    private bool disposed;

    internal NativeConsoleRecoveryInput(bool activate = true)
    {
        if (Console.IsInputRedirected || Console.IsErrorRedirected)
        {
            throw new InvalidOperationException("手動回収には標準入力と標準エラーの端末接続が必要です。");
        }

        input = GetStdHandle(-10);
        Check(GetConsoleMode(input, out uint mode), "標準入力の端末を確認できません。");
        Check(GetConsoleMode(GetStdHandle(-12), out _), "標準エラーの端末を確認できません。");
        originalMode = mode;
        if (!NativeLibrary.TryLoad("kernel32.dll", typeof(NativeConsoleRecoveryInput).Assembly,
            DllImportSearchPath.System32, out nint loaded))
        {
            throw new NotSupportedException("入力を待機せずに読み取るOS部品を取得できません。");
        }

        module = loaded;
        try
        {
            if (!NativeLibrary.TryGetExport(module, "ReadConsoleInputExW", out nint function))
            {
                throw new NotSupportedException("入力を待機せずに読み取るOS APIがありません。");
            }

            read = Marshal.GetDelegateForFunctionPointer<ReadInput>(function);
            if (activate && (mode & ProcessedInput) == 0)
            {
                Check(SetConsoleMode(input, mode | ProcessedInput), "Ctrl+Cの入力処理を設定できません。");
                restoreMode = true;
            }
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    internal static void Validate()
    {
        using var input = new NativeConsoleRecoveryInput(activate: false);
    }

    public CliRecoveryBatch ReadBatch()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(read(input, buffer, (uint)buffer.Length, out uint count, ReadNoWait),
            "回収操作の入力を読み取れません。");
        List<CliRecoveryKey> keys = [];
        for (int index = 0; index < count; index++)
        {
            InputRecord record = buffer[index];
            if (record.EventType == 1 && record.KeyDown != 0 && record.Character != '\0')
            {
                keys.Add(new CliRecoveryKey(record.Character, record.RepeatCount));
            }
        }

        return new CliRecoveryBatch(keys, count != 0);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (restoreMode)
            {
                Check(SetConsoleMode(input, originalMode), "端末の入力設定を復元できません。");
            }
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    private static void Check(bool success, string message)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), message);
        }
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] internal ushort EventType;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort RepeatCount;
        [FieldOffset(14)] internal char Character;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReadInput(nint input, [Out] InputRecord[] buffer,
        uint length, out uint count, ushort flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int kind);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
