using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約workerの通信を長さ・版・識別子で検査します。
/// </summary>
internal static class SchedulerWorkerProtocol
{
    internal const int Version = 2;
    internal const int MaximumRequestBytes = 8 * 1024 * 1024;
    internal const int MaximumControlBytes = 64 * 1024;
    internal static readonly TimeSpan CommunicationTimeout =
        TimeSpan.FromSeconds(10);

    internal static async Task WriteAsync(Stream stream,
        SchedulerWorkerMessage message, CancellationToken token = default,
        TimeProvider? clock = null)
    {
        token.ThrowIfCancellationRequested();
        Validate(message);
        SchedulerWorkerMessageKind kind = Kind(message);
        using var payload = new BoundedPayload(Maximum(kind));
        using (var writer = new Utf8JsonWriter(payload))
        {
            WritePayload(writer, message);
        }

        byte[] header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1),
            checked((int)payload.Length));
        using var deadline = new CommunicationDeadline(token, clock);
        try
        {
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload.GetBuffer().AsMemory(0,
                (int)payload.Length), deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
            when (deadline.IsExpired && !token.IsCancellationRequested)
        {
            throw new TimeoutException("予約workerの送信が10秒で完了しませんでした。", error);
        }
    }

    internal static async Task<SchedulerWorkerMessage> ReadAsync(Stream stream,
        SchedulerWorkerIdentity expected, CancellationToken token = default,
        bool waitForMessage = false, TimeProvider? clock = null)
    {
        ValidateIdentity(expected);
        byte[] header = new byte[5];
        if (waitForMessage)
        {
            if (await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0)
            {
                throw new SchedulerIdleConnectionClosedException();
            }
        }
        using var deadline = new CommunicationDeadline(token, clock);
        try
        {
            return await ReadFrameAsync(stream, expected, header,
                waitForMessage, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
            when (deadline.IsExpired && !token.IsCancellationRequested)
        {
            throw new TimeoutException("予約workerの受信が10秒で完了しませんでした。", error);
        }
    }

    /// <summary>
    /// フレームの期限と呼出し元の取消を連結し、逆順に解放します。
    /// </summary>
    private readonly struct CommunicationDeadline : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly CancellationTokenSource linked;

        internal CommunicationDeadline(CancellationToken token, TimeProvider? clock)
        {
            timeout = new CancellationTokenSource(
                CommunicationTimeout, clock ?? TimeProvider.System);
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token, timeout.Token);
            }
            catch
            {
                timeout.Dispose();
                throw;
            }
        }

        internal CancellationToken Token => linked.Token;

        internal bool IsExpired => timeout.IsCancellationRequested;

        public void Dispose()
        {
            try
            {
                linked.Dispose();
            }
            finally
            {
                timeout.Dispose();
            }
        }
    }

    private static async Task<SchedulerWorkerMessage> ReadFrameAsync(Stream stream,
        SchedulerWorkerIdentity expected, byte[] header,
        bool waitForMessage, CancellationToken token)
    {
        await stream.ReadExactlyAsync(header.AsMemory(waitForMessage ? 1 : 0),
            token).ConfigureAwait(false);
        var kind = (SchedulerWorkerMessageKind)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (!Enum.IsDefined(kind) || length <= 0 || length > Maximum(kind))
        {
            throw new InvalidDataException("予約workerの通信種別または長さが不正です。");
        }

        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            SchedulerJsonValidation.Validate(bytes, 8);
            using var document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 8 });
            SchedulerWorkerMessage message = ReadPayload(kind, document.RootElement);
            Validate(message);
            if (message.Identity != expected)
            {
                throw new InvalidDataException("予約workerの世代または発生識別子が一致しません。");
            }

            return message;
        }
        catch (Exception error) when (error is JsonException
            or InvalidOperationException or FormatException or ArgumentException
            or OverflowException)
        {
            throw new InvalidDataException("予約workerのJSONが不正です。", error);
        }
    }

    private static int Maximum(SchedulerWorkerMessageKind kind) =>
        kind == SchedulerWorkerMessageKind.Request
            ? MaximumRequestBytes : MaximumControlBytes;

    private static SchedulerWorkerMessageKind Kind(SchedulerWorkerMessage message) =>
        message switch
        {
            SchedulerWorkerMessage.Request => SchedulerWorkerMessageKind.Request,
            SchedulerWorkerMessage.Ready => SchedulerWorkerMessageKind.Ready,
            SchedulerWorkerMessage.Start => SchedulerWorkerMessageKind.Start,
            SchedulerWorkerMessage.Cancel => SchedulerWorkerMessageKind.Cancel,
            SchedulerWorkerMessage.Completed => SchedulerWorkerMessageKind.Completed,
            SchedulerWorkerMessage.Started => SchedulerWorkerMessageKind.Started,
            SchedulerWorkerMessage.Heartbeat => SchedulerWorkerMessageKind.Heartbeat,
            SchedulerWorkerMessage.Keyboard => SchedulerWorkerMessageKind.Keyboard,
            _ => throw new InvalidDataException("予約workerの通信種別が不正です。")
        };

    private static void ValidateIdentity(SchedulerWorkerIdentity identity)
    {
        if (identity.Generation == Guid.Empty || identity.OccurrenceId == Guid.Empty)
        {
            throw new InvalidDataException("予約workerの識別子は空にできません。");
        }
    }

    private static void Validate(SchedulerWorkerMessage message)
    {
        ValidateIdentity(message.Identity);
        if (message is SchedulerWorkerMessage.Request request)
        {
            SchedulerValidation.Text(request.Path);
            if (request.EntryId == Guid.Empty || !Path.IsPathFullyQualified(request.Path)
                || !Path.GetExtension(request.Path).Equals(".uws",
                    StringComparison.OrdinalIgnoreCase)
                || request.Arguments.Count > UwscArray.MaximumElements)
            {
                throw new InvalidDataException("予約workerの対象または引数が不正です。");
            }

            foreach (string argument in request.Arguments)
            {
                SchedulerValidation.Text(argument);
            }

            if (request.WindowTrigger is { } trigger
                && (trigger.Target.Handle == 0 || trigger.Target.ProcessId == 0
                    || trigger.Target.ThreadId == 0 || trigger.ObservedAt != request.ObservedAt))
            {
                throw new InvalidDataException("予約workerの捕捉先または観測日時が不正です。");
            }
        }
        else if (message is SchedulerWorkerMessage.Keyboard keyboard)
        {
            if (keyboard.Transaction == Guid.Empty || keyboard.Key is < 0 or > 255
                || !Enum.IsDefined(keyboard.Phase)
                || keyboard.Phase is SchedulerKeyboardPhase.Request or SchedulerKeyboardPhase.Release
                    && (!keyboard.Succeeded || keyboard.Diagnostic is not null))
            {
                throw new InvalidDataException("予約のキー送信通知が不正です。");
            }
            if (keyboard.Diagnostic is not null)
            {
                SchedulerValidation.Text(keyboard.Diagnostic);
            }
        }
        else if (message is SchedulerWorkerMessage.Ready { ProcessId: <= 0 })
        {
            throw new InvalidDataException("予約workerのPIDが不正です。");
        }
        else if (message is SchedulerWorkerMessage.Completed completed)
        {
            if (completed.CancellationCompleted && completed.ExitCode != 130)
            {
                throw new InvalidDataException("予約workerの取消結果が不正です。");
            }

            if (completed.Diagnostic is not null)
            {
                SchedulerValidation.Text(completed.Diagnostic);
            }
        }
    }

    private static void WritePayload(Utf8JsonWriter writer,
        SchedulerWorkerMessage message)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", Version);
        writer.WriteString("generation", message.Identity.Generation);
        writer.WriteString("occurrence", message.Identity.OccurrenceId);
        switch (message)
        {
            case SchedulerWorkerMessage.Request request:
                writer.WriteString("entry", request.EntryId);
                writer.WriteString("path", request.Path);
                writer.WriteStartArray("arguments");
                foreach (string argument in request.Arguments)
                {
                    writer.WriteStringValue(argument);
                }
                writer.WriteEndArray();
                writer.WriteString("observedAt", request.ObservedAt);
                WriteWindow(writer, request.WindowTrigger);
                break;
            case SchedulerWorkerMessage.Ready ready:
                writer.WriteNumber("pid", ready.ProcessId);
                break;
            case SchedulerWorkerMessage.Keyboard keyboard:
                writer.WriteString("transaction", keyboard.Transaction);
                writer.WriteNumber("key", keyboard.Key);
                writer.WriteNumber("phase", (int)keyboard.Phase);
                writer.WriteBoolean("succeeded", keyboard.Succeeded);
                writer.WriteString("diagnostic", keyboard.Diagnostic);
                break;
            case SchedulerWorkerMessage.Completed completed:
                writer.WriteNumber("exitCode", completed.ExitCode);
                writer.WriteBoolean("cancellationCompleted", completed.CancellationCompleted);
                writer.WriteBoolean("inputRecovered", completed.InputRecovered);
                writer.WriteString("diagnostic", completed.Diagnostic);
                break;
        }

        writer.WriteEndObject();
    }

    private static SchedulerWorkerMessage ReadPayload(
        SchedulerWorkerMessageKind kind, JsonElement element)
    {
        string[] extra = kind switch
        {
            SchedulerWorkerMessageKind.Request => ["entry", "path", "arguments", "observedAt", "window"],
            SchedulerWorkerMessageKind.Ready => ["pid"],
            SchedulerWorkerMessageKind.Keyboard =>
                ["transaction", "key", "phase", "succeeded", "diagnostic"],
            SchedulerWorkerMessageKind.Completed =>
                ["exitCode", "cancellationCompleted", "inputRecovered", "diagnostic"],
            _ => []
        };
        string[] keys = ["version", "generation", "occurrence", .. extra];
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!keys.Contains(property.Name, StringComparer.Ordinal)
                || !data.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("予約workerの項目が未知または重複しています。");
            }
        }

        if (data.Count != keys.Length || data["version"].GetInt32() != Version)
        {
            throw new InvalidDataException("予約workerの必須項目または版が不正です。");
        }

        var identity = new SchedulerWorkerIdentity(data["generation"].GetGuid(),
            data["occurrence"].GetGuid());
        if (kind == SchedulerWorkerMessageKind.Request
            && data["arguments"].GetArrayLength() > UwscArray.MaximumElements)
        {
            throw new InvalidDataException("予約workerの引数が多すぎます。");
        }

        return kind switch
        {
            SchedulerWorkerMessageKind.Request => new SchedulerWorkerMessage.Request(
                identity, data["entry"].GetGuid(), Text(data["path"]),
                Array.AsReadOnly(data["arguments"].EnumerateArray().Select(Text).ToArray()),
                data["observedAt"].GetDateTimeOffset(), ReadWindow(data["window"],
                    data["observedAt"].GetDateTimeOffset())),
            SchedulerWorkerMessageKind.Ready =>
                new SchedulerWorkerMessage.Ready(identity, data["pid"].GetInt32()),
            SchedulerWorkerMessageKind.Start => new SchedulerWorkerMessage.Start(identity),
            SchedulerWorkerMessageKind.Cancel => new SchedulerWorkerMessage.Cancel(identity),
            SchedulerWorkerMessageKind.Started => new SchedulerWorkerMessage.Started(identity),
            SchedulerWorkerMessageKind.Heartbeat => new SchedulerWorkerMessage.Heartbeat(identity),
            SchedulerWorkerMessageKind.Keyboard => new SchedulerWorkerMessage.Keyboard(
                identity, data["transaction"].GetGuid(), data["key"].GetInt32(),
                (SchedulerKeyboardPhase)data["phase"].GetInt32(), data["succeeded"].GetBoolean(),
                data["diagnostic"].ValueKind == JsonValueKind.Null
                    ? null : Text(data["diagnostic"])),
            SchedulerWorkerMessageKind.Completed => new SchedulerWorkerMessage.Completed(
                identity, data["exitCode"].GetInt32(),
                data["cancellationCompleted"].GetBoolean(), data["inputRecovered"].GetBoolean(),
                data["diagnostic"].ValueKind == JsonValueKind.Null
                    ? null : Text(data["diagnostic"])),
            _ => throw new InvalidDataException("予約workerの通信種別が不正です。")
        };
    }

    private static string Text(JsonElement element)
    {
        string value = element.GetString()
            ?? throw new InvalidDataException("予約workerの文字列がnullです。");
        SchedulerValidation.Text(value);
        return value;
    }

    private static void WriteWindow(Utf8JsonWriter writer, SchedulerWindowTrigger? trigger)
    {
        if (trigger is null)
        {
            writer.WriteNull("window");
            return;
        }

        writer.WriteStartObject("window");
        writer.WriteNumber("handle", trigger.Target.Handle);
        writer.WriteNumber("pid", trigger.Target.ProcessId);
        writer.WriteNumber("tid", trigger.Target.ThreadId);
        writer.WriteEndObject();
    }

    private static SchedulerWindowTrigger? ReadWindow(JsonElement element,
        DateTimeOffset observedAt)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name is not ("handle" or "pid" or "tid")
                || !fields.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("予約workerの捕捉先が未知または重複しています。");
            }
        }

        if (fields.Count != 3)
        {
            throw new InvalidDataException("予約workerの捕捉先の項目が不足しています。");
        }

        return new(new(fields["handle"].GetUInt64(), fields["pid"].GetUInt32(),
            fields["tid"].GetUInt32()), observedAt.ToUniversalTime());
    }

    private sealed class BoundedPayload(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteArray(buffer, offset, count);

        private void WriteArray(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        private void Check(int count)
        {
            if (Length + count > maximum)
            {
                throw new InvalidDataException("予約workerの送信上限を超えました。");
            }
        }
    }
}

/// <summary>
/// 次の通知を待つ前に閉じた通信を、途中切断と区別します。
/// </summary>
internal sealed class SchedulerIdleConnectionClosedException : EndOfStreamException;
