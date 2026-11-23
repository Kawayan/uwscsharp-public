using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 共通bufferのraw内容とbyteごとの確定状態を保持します。
/// </summary>
internal sealed class ListEnumerationBuffer
{
    private byte[] data = [];
    private bool[] known = [];

    internal ListEnumerationBuffer(bool zeroInitialized = true, int initialCharacters = 65001)
    {
        if (zeroInitialized)
        {
            data = new byte[checked(initialCharacters * 2)];
            known = new bool[data.Length];
            Array.Fill(known, true);
        }
    }

    private void EnsureCapacity(int characters)
    {
        int bytes = checked(characters * 2);
        if (bytes > data.Length)
        {
            int previous = data.Length;
            Array.Resize(ref data, bytes);
            Array.Fill(data, (byte)0xcd, previous, bytes - previous);
            Array.Resize(ref known, bytes);
        }

    }

    internal byte[] Prepare(int characters)
    {
        EnsureCapacity(characters);
        return (byte[])data.Clone();
    }

    internal void Capture(byte[] next, int copied)
    {
        int written = copied > 0 ? (int)Math.Min(next.Length, ((long)copied + 1) * 2) : 0;
        for (int index = 0; index < next.Length; index++)
        {
            // 未確定byteは実際の変化または正常GETTEXTの書込み範囲で確定します。
            known[index] |= next[index] != data[index] || index < written;
            data[index] = next[index];
        }
    }

    internal void WriteItemData(uint value)
    {
        EnsureCapacity(2);
        for (int index = 0; index < 4; index++)
        {
            data[index] = (byte)(value >> (index * 8));
            known[index] = true;
        }
    }

    internal string Read(int maximumCharacters)
    {
        int limit = Math.Min(maximumCharacters, data.Length / 2);
        for (int index = 0; index < limit; index++)
        {
            int offset = index * 2;
            if (!known[offset] || !known[offset + 1])
            {
                throw WindowsOperationErrors.Record(
                    new NotSupportedException("共通bufferの内容が確定していません。"));
            }

            if (data[offset] == 0 && data[offset + 1] == 0)
            {
                return new string(MemoryMarshal.Cast<byte, char>(data.AsSpan(0, offset)));
            }
        }

        if (limit < maximumCharacters)
        {
            throw WindowsOperationErrors.Record(
                new NotSupportedException("共通bufferの終端以後が確定していません。"));
        }

        return new string(MemoryMarshal.Cast<byte, char>(data.AsSpan(0, limit * 2)));
    }
}
