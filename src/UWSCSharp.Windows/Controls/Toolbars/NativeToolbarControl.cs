using System.Buffers.Binary;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeToolbarControl
{
    private readonly record struct Button(int Id, byte State, byte Style, nint Text);

    private sealed record Entry(int Index, Button Button, string Name);

    internal static Task<(int Matches, bool Found, bool Success)> ClickAsync(nint target,
        string name, int occurrence, bool on, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Click(target, name, occurrence, on, token));
        }

        return NativeControlWorker.RunAsync(active => Click(target, name, occurrence, on, active), token);
    }

    internal static (int Matches, bool Found, bool Success) Click(nint target,
        string name, int occurrence, bool on, CancellationToken token,
        NativeToolbarClickBuffer? shared = null, bool partial = false,
        IListSelectionPlatform? platform = null, bool back = false, int kind = 64)
    {
        if (shared is not null)
        {
            return ClickCore(target, name, occurrence, on, token, shared, partial,
                platform ?? new NativeListSelectionPlatform(), back, kind);
        }

        using var owner = new NativeControlBufferOwner(Native.GetAncestor(target, 2));
        return ClickCore(target, name, occurrence, on, token,
            new NativeToolbarClickBuffer(owner), partial,
            platform ?? new NativeListSelectionPlatform(), back, kind);
    }

    private static (int Matches, bool Found, bool Success) ClickCore(nint target,
        string name, int occurrence, bool on, CancellationToken token,
        NativeToolbarClickBuffer shared, bool partial, IListSelectionPlatform platform, bool back, int kind)
    {
        int matches = 0;
        string desired = ButtonName.Normalize(NativeListNameMatcher.AsciiUpper(
            partial ? name : NativeListControl.Trim(name)));
        foreach (Entry entry in ReadClickEntries(target, shared, token))
        {
            string candidate = NativeListNameMatcher.AsciiUpper(entry.Name);
            if (!NativeListNameMatcher.Compare(ref candidate, desired, partial)
                || (++matches != occurrence && occurrence > 0))
            {
                continue;
            }

            if (shared.IsShellRoot())
            {
                // 原版は一致後に再判定し、返値にかかわらず再表示から矩形取得へ進みます。
                _ = Message(target, 0x404, entry.Button.Id, 0, token);
            }

            (nint _, Native.Rect rectangle) = shared.ReadRectangle(target, entry.Index, token);
            var preparation = new NativeListSelectionBackend(shared.Root, target,
                combo: false, back: back, platform: platform);
            preparation.PrepareTarget(token);
            token.ThrowIfCancellationRequested();
            _ = Message(target, 0x448, entry.Index, 0, token);
            try
            {
                if ((kind & 1024) != 0)
                {
                    int width = WindowsOperationErrors.Subtract(rectangle.Right, rectangle.Left);
                    int height = WindowsOperationErrors.Subtract(rectangle.Bottom, rectangle.Top);
                    Native.Point screen = platform.ToScreen(target,
                        new Native.Point(rectangle.Left, rectangle.Top));
                    token.ThrowIfCancellationRequested();
                    platform.MoveCursor(new Native.Point(WindowsOperationErrors.Add(screen.X, width / 2),
                        WindowsOperationErrors.Add(screen.Y, height / 2)));
                    token.ThrowIfCancellationRequested();
                }

                if ((kind & 0x3800) != 0)
                {
                    NativeControlMouse.Post(target, kind,
                        new Native.Point(rectangle.Left, rectangle.Top), platform, token);
                    return (matches, true, true);
                }

                if (((entry.Button.State & 3) != 0) == on)
                {
                    // 原版の同値経路は状態変更せず、待機とhot itemの再設定を行います。
                    var waiting = new NativeListSelectionBackend(shared.Root, target,
                        combo: false, back: true, platform: platform);
                    waiting.Sleep(20, token);
                    waiting.Yield(20, token);
                    _ = Message(target, 0x448, entry.Index, 0, token);
                    return (matches, true, true);
                }

                NativeControlMouse.Post(target, kind,
                    new Native.Point(rectangle.Left, rectangle.Top), platform, token);
                if (!on)
                {
                    // 送信後の状態ではなく、名前取得時に保存した状態で解除を決めます。
                    if ((entry.Button.State & 2) != 0)
                    {
                        _ = Message(target, 0x403, entry.Button.Id, 0, token);
                    }

                    if ((entry.Button.State & 1) != 0)
                    {
                        _ = Message(target, 0x402, entry.Button.Id, 0, token);
                    }
                }

                return (matches, true, true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && !token.IsCancellationRequested)
            {
                // 原版は一致後の操作例外を捕捉して成功退出へ進みます。
                return (matches, true, true);
            }
        }

        return (matches, false, false);
    }

    internal static Task<List<string>> ReadAsync(nint target, bool includeDisabled,
        CancellationToken token, Action<string>? onItem = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Read(target, includeDisabled, token, onItem));
        }

        return NativeControlWorker.RunAsync(active => Read(target, includeDisabled, active, onItem), token);
    }

    private static nint Message(nint target, uint message, nint index,
        nint pointer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint result = Native.SendMessageW(target, message, index, pointer);
        token.ThrowIfCancellationRequested();
        if (!Native.IsWindow(target))
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARが取得中に破棄されました。"));
        }

        return result;
    }

    private static Button ReadButton(nint target, int index,
        NativeControlTextBuffer buffer, CancellationToken token)
    {
        buffer.Prepare();
        if (Message(target, 0x417, index, buffer.ItemAddress, token) == 0)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARのbutton情報を取得できません。"));
        }

        byte[] data = buffer.ReadItemBytes();
        nint text = buffer.Is64Bit
            ? unchecked((nint)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24)))
            : unchecked((nint)(long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        return new(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)), data[8], data[9], text);
    }

    private static List<string> Read(nint target, bool includeDisabled,
        CancellationToken token, Action<string>? onItem = null) =>
        [.. ReadEntries(target, includeDisabled, token, onItem).Where(entry => entry.Name.Length > 1)
            .Select(entry => entry.Name)];

    private static IEnumerable<Entry> ReadClickEntries(nint target,
        NativeToolbarClickBuffer shared, CancellationToken token)
    {
        nint count = Message(target, 0x418, 0, 0, token);
        if (count < 0 || count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARのbutton数が範囲外です。"));
        }

        for (int index = 0; index < (int)count; index++)
        {
            Button button = ReadClickButton(target, index, shared, token);

            if ((button.State & 4) == 0
                || ((button.State & 8) != 0 && !shared.IsShellRoot()))
            {
                continue;
            }

            string name = ButtonName.Normalize(shared.ReadText(target, button.Id, token));
            yield return new(index, button, name);
        }
    }

    private static Button ReadClickButton(nint target, int index,
        NativeToolbarClickBuffer shared, CancellationToken token)
    {
        ReadOnlySpan<byte> bytes = shared.ReadButton(target, index, token);
        return new Button(BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            bytes[8], bytes[9], 0);
    }

    private static List<Entry> ReadEntries(nint target, bool includeDisabled,
        CancellationToken token, Action<string>? onItem = null)
    {
        nint count = Message(target, 0x418, 0, 0, token);
        if (count < 0 || count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARのbutton数が範囲外です。"));
        }

        using var buffer = new NativeControlTextBuffer(target, ControlTextKind.Toolbar);
        List<Entry> result = [];
        int characters = 0;
        for (int index = 0; index < (int)count; index++)
        {
            Button button = ReadButton(target, index, buffer, token);
            if ((button.Style & 1) != 0 || (button.State & 8) != 0
                || (!includeDisabled && (button.State & 4) == 0)
                || button.Text == -1)
            {
                continue;
            }

            string text;
            if ((nuint)button.Text <= 65535)
            {
                nint packed = unchecked((nint)((uint)button.Text << 16 | 65534));
                nint copied = Message(target, 0x45b, packed, buffer.TextAddress, token);
                if (copied < 0)
                {
                    continue;
                }

                text = buffer.Read();
                nint length = Message(target, 0x44b, button.Id, 0, token);
                if (length != text.Length)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("TOOLBAR文字列の容量超過または変更を検出しました。"));
                }
            }
            else
            {
                text = buffer.ReadStringAt(button.Text, token);
            }

            if (ReadButton(target, index, buffer, token) != button)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TOOLBARのbutton情報が取得中に変化しました。"));
            }

            string name = ButtonName.Normalize(text);
            if (name.Length == 0)
            {
                continue;
            }

            if (name.Length > NativeListControl.MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TOOLBARの文字列取得上限を超えました。"));
            }

            characters += name.Length;
            result.Add(new(index, button, name));
            if (name.Length > 1)
            {
                onItem?.Invoke(name);
            }
        }

        if (Message(target, 0x418, 0, 0, token) != count)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARのbutton数が取得中に変化しました。"));
        }

        return result;
    }
}
