using System.IO;

namespace UWSCSharp.Wpf;

internal sealed class LimitedMemoryStream(int maximumBytes,
    string errorMessage) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckSize(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckSize(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        CheckSize(1);
        base.WriteByte(value);
    }

    private void CheckSize(int count)
    {
        if (Length + count > maximumBytes)
        {
            throw new InvalidDataException(errorMessage);
        }
    }
}
