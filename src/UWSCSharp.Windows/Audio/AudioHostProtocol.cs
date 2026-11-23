using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UWSCSharp.Windows;

/// <summary>
/// 長さ付きUTF8 JSONを読み書きします。同じ方向の書込みは呼出し元で直列化します。
/// </summary>
internal static class AudioHostProtocol
{
    internal const int Version = 1;
    internal const int MaximumPayloadBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task WriteAsync(Stream stream, AudioHostMessage message,
        CancellationToken cancellationToken = default)
    {
        Validate(message);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        ValidateLength(payload.Length);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AudioHostMessage?> ReadAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(first), cancellationToken)
            .ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        ValidateLength(length);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = StrictUtf8.GetCharCount(payload);
            using var document = JsonDocument.Parse(payload,
                new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
            RejectDuplicateProperties(document.RootElement);
            AudioHostMessage message = document.RootElement
                .Deserialize<AudioHostMessage>(Options)
                ?? throw new InvalidDataException("音声通知がnullです。");
            Validate(message);
            return message;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException
            or EncoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException("音声通知を厳密に読み取れません。", error);
        }
    }

    private static void ValidateLength(int length)
    {
        if (length is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("音声通知の長さが範囲外です。");
        }
    }

    internal static void Validate(AudioHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != Version || !Enum.IsDefined(message.Type))
        {
            throw new InvalidDataException("音声通知の版または種類に対応していません。");
        }

        bool play = message.Type == AudioHostMessageType.Play;
        bool failed = message.Type == AudioHostMessageType.Failed;
        if ((message.Request is not null) != play
            || (message.Failure is not null) != failed)
        {
            throw new InvalidDataException("音声通知の種類と内容が一致しません。");
        }

        if (message.Request is { } request)
        {
            if (!Enum.IsDefined(request.Kind) || request.Source is null
                || request.Source.Contains('\0')
                || (request.Kind == AudioPlaybackKind.Beep
                    ? request.Source.Length != 0 : request.Source.Length == 0)
                || (request.OutputDevice is not null
                    && request.Kind != AudioPlaybackKind.WaveFile))
            {
                throw new InvalidDataException("音声再生要求の内容が不正です。");
            }

            _ = StrictUtf8.GetByteCount(request.Source);
        }

        if (message.Failure is { } failure)
        {
            if (string.IsNullOrEmpty(failure.Message))
            {
                throw new InvalidDataException("音声処理の失敗理由がありません。");
            }

            _ = StrictUtf8.GetByteCount(failure.Message);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("音声通知に重複した項目があります。");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}
