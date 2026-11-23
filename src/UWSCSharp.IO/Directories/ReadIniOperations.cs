using UWSCSharp.Core;

namespace UWSCSharp.IO;

public sealed partial class ScriptFiles
{
    private static async ValueTask<Value> ReadIniAsync(
        string path, string section, string key, CancellationToken cancellationToken)
    {
        string value = await ReadIniTextAsync(path, section, key,
            cancellationToken).ConfigureAwait(false);
        return NumericOperations.TryFiniteText(value, out double number)
            ? Value.Numeric(number) : Value.String(value);
    }

    internal static async ValueTask<string> ReadIniTextAsync(
        string path, string section, string key,
        CancellationToken cancellationToken)
    {
        string text = await ReadIniDocumentAsync(path,
            cancellationToken).ConfigureAwait(false);
        return ReadIniText(text, section, key, cancellationToken);
    }

    internal static async ValueTask<string> ReadIniDocumentAsync(
        string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return "";
        }

        string text = ScriptFileText.Decode(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }

    internal static string ReadIniText(string text, string section,
        string key, CancellationToken cancellationToken)
    {
        using var reader = new StringReader(text);
        bool selected = false;
        int lineNumber = 0;
        while (reader.ReadLine() is string line)
        {
            if ((lineNumber++ & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return "";
                }

                selected = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!selected || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            int equals = trimmed.IndexOf('=');
            if (equals < 0 || !trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = trimmed[(equals + 1)..].Trim();
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return "";
    }
}
