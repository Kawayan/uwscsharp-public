using System.Buffers;
using System.Collections;
using System.Text;

namespace UWSCSharp.IO;

public sealed partial class ScriptLogHost
{
    private sealed class LogEntries : IEnumerable<string>
    {
        private readonly Queue<string> entries = new();
        private long bytes;

        internal int Count => entries.Count;

        internal long OmittedBytes
        {
            get; private set;
        }

        internal void Add(string text, int maximumLines, int? maximumBytes)
        {
            if (maximumLines == 0)
            {
                return;
            }
            while (entries.Count >= maximumLines)
            {
                string removed = entries.Dequeue();
                if (maximumBytes is not null)
                {
                    bytes -= Utf8.GetByteCount(removed) + 2L;
                }
            }
            if (maximumBytes is int limit)
            {
                int size = Utf8.GetByteCount(text) + 2;
                if (size > limit)
                {
                    text = Suffix(text, limit - 2);
                    int retained = Utf8.GetByteCount(text) + 2;
                    OmittedBytes += size - retained;
                    size = retained;
                }
                while (bytes + size > limit && entries.Count > 0)
                {
                    int removed = Utf8.GetByteCount(entries.Dequeue()) + 2;
                    bytes -= removed;
                    OmittedBytes += removed;
                }
                bytes += size;
            }
            entries.Enqueue(text);
        }

        internal void Clear()
        {
            entries.Clear();
            bytes = 0;
            OmittedBytes = 0;
        }

        public IEnumerator<string> GetEnumerator() => entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static string Suffix(string text, int maximumBytes)
        {
            int start = text.Length;
            int bytes = 0;
            while (start > 0)
            {
                OperationStatus status = Rune.DecodeLastFromUtf16(
                    text.AsSpan(0, start), out Rune rune, out int consumed);
                if (status != OperationStatus.Done
                    || bytes + rune.Utf8SequenceLength > maximumBytes)
                {
                    break;
                }
                start -= consumed;
                bytes += rune.Utf8SequenceLength;
            }
            return text[start..];
        }
    }
}
