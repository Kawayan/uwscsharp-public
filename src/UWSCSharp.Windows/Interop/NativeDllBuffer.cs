using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// ネイティブ呼出しと書戻しの間だけ使う、ゼロ初期化した領域を所有します。
/// </summary>
internal sealed class NativeDllBuffer : IDisposable
{
    private readonly object gate = new();
    private readonly BufferHandle handle;
    private bool disposed;

    private NativeDllBuffer(int length, BufferHandle handle)
    {
        Length = length;
        this.handle = handle;
    }

    internal int Length
    {
        get;
    }

    internal static NativeDllBuffer Allocate(int length,
        INativeDllBufferAllocator? allocator = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        allocator ??= NativeDllBufferAllocator.Instance;
        var handle = new BufferHandle(allocator);
        try
        {
            // 論理長0でも所有領域を確保します。NULL引数の契約とは別です。
            handle.Allocate(Math.Max(1, length));
            byte[] zeros = new byte[Math.Min(Math.Max(1, length), 4096)];
            int offset = 0;
            do
            {
                int count = Math.Min(zeros.Length, Math.Max(1, length) - offset);
                Marshal.Copy(zeros, 0, handle.DangerousGetHandle() + offset, count);
                offset += count;
            } while (offset < length);

            return new NativeDllBuffer(length, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 処理が戻るまで領域を保持します。ポインターを処理外へ持ち出さないでください。
    /// </summary>
    internal T WithPointer<T>(Func<nint, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        bool retained = false;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                handle.DangerousAddRef(ref retained);
            }

            return operation(handle.DangerousGetHandle());
        }
        finally
        {
            if (retained)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// 全領域を取得してから処理を呼び、取得途中の失敗でも参照を解放します。
    /// </summary>
    internal static T WithPointers<T>(IReadOnlyList<NativeDllBuffer> buffers,
        Func<IReadOnlyList<nint>, T> operation)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(operation);
        NativeDllBuffer[] snapshot = [.. buffers];
        foreach (NativeDllBuffer buffer in snapshot)
        {
            ArgumentNullException.ThrowIfNull(buffer);
        }

        bool[] retained = new bool[snapshot.Length];
        nint[] pointers = new nint[snapshot.Length];
        try
        {
            for (int index = 0; index < snapshot.Length; index++)
            {
                NativeDllBuffer buffer = snapshot[index];
                lock (buffer.gate)
                {
                    ObjectDisposedException.ThrowIf(buffer.disposed, buffer);
                    buffer.handle.DangerousAddRef(ref retained[index]);
                }

                pointers[index] = buffer.handle.DangerousGetHandle();
            }

            return operation(Array.AsReadOnly(pointers));
        }
        finally
        {
            for (int index = snapshot.Length - 1; index >= 0; index--)
            {
                if (retained[index])
                {
                    snapshot[index].handle.DangerousRelease();
                }
            }
        }
    }

    internal byte[] Read(int offset, int count)
    {
        ValidateRange(offset, count);
        return WithPointer(pointer =>
        {
            byte[] result = new byte[count];
            Marshal.Copy(pointer + offset, result, 0, count);
            return result;
        });
    }

    internal void Write(int offset, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateRange(offset, bytes.Length);
        WithPointer(pointer =>
        {
            Marshal.Copy(bytes, 0, pointer + offset, bytes.Length);
            return 0;
        });
    }

    /// <summary>
    /// 新しい利用を拒否し、使用中の処理が全て戻ってから領域を解放します。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        handle.Dispose();
    }

    private void ValidateRange(int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > Length || count > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "所有領域の範囲を超えています。");
        }
    }

    private sealed class BufferHandle(INativeDllBufferAllocator allocator)
        : SafeHandle(0, ownsHandle: true)
    {
        /// <summary>
        /// 領域を確保していないかどうかを取得します。
        /// </summary>
        public override bool IsInvalid => handle == 0;

        internal void Allocate(int length)
        {
            SetHandle(allocator.Allocate(length));
            if (IsInvalid)
            {
                throw new OutOfMemoryException("DLL呼出し用の領域を確保できませんでした。");
            }
        }

        protected override bool ReleaseHandle()
        {
            allocator.Free(handle);
            return true;
        }
    }
}

internal interface INativeDllBufferAllocator
{
    nint Allocate(int length);

    void Free(nint address);
}

internal sealed class NativeDllBufferAllocator : INativeDllBufferAllocator
{
    internal static NativeDllBufferAllocator Instance { get; } = new();

    /// <summary>
    /// 要求されたバイト数の領域を確保します。
    /// </summary>
    public nint Allocate(int length) => Marshal.AllocHGlobal(length);

    /// <summary>
    /// 所有する領域を解放します。
    /// </summary>
    public void Free(nint address) => Marshal.FreeHGlobal(address);
}
