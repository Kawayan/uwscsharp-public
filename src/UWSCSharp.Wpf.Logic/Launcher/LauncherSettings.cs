using System.IO;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal enum LauncherCommandKind
{
    Script,
    Executable,
    Document
}

internal abstract class LauncherEntry
{
    protected LauncherEntry(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        LauncherSlots.ValidateText(title);
        Title = title;
    }

    internal string Title
    {
        get;
    }
}

internal sealed class LauncherCommand : LauncherEntry
{
    internal LauncherCommand(string title, string path, LauncherCommandKind kind,
        IEnumerable<string> arguments, string baseDirectory) : base(title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(arguments);
        LauncherSlots.ValidateText(path);
        LauncherSlots.ValidateText(baseDirectory);
        Path = System.IO.Path.GetFullPath(path, baseDirectory);
        LauncherSlots.ValidateText(Path);
        string extension = System.IO.Path.GetExtension(Path);
        LauncherCommandKind expected = extension.Equals(".uws", StringComparison.OrdinalIgnoreCase)
            ? LauncherCommandKind.Script
            : extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? LauncherCommandKind.Executable : LauncherCommandKind.Document;
        if (kind != expected)
        {
            throw new InvalidDataException("ランチ項目の種別と拡張子が一致しません。");
        }

        var copied = new List<string>();
        foreach (string argument in arguments)
        {
            LauncherSlots.ValidateText(argument);
            if (copied.Count == UwscArray.MaximumElements)
            {
                throw new InvalidDataException("ランチ項目の引数が多すぎます。");
            }

            copied.Add(argument);
        }

        if (kind == LauncherCommandKind.Document && copied.Count != 0)
        {
            throw new InvalidDataException("既定アプリで開くファイルには引数を指定できません。");
        }

        Kind = kind;
        Arguments = copied.AsReadOnly();
    }

    internal string Path
    {
        get;
    }

    internal LauncherCommandKind Kind
    {
        get;
    }

    internal IReadOnlyList<string> Arguments
    {
        get;
    }
}

internal sealed class LauncherGroup : LauncherEntry
{
    internal LauncherGroup(string title, IEnumerable<LauncherCommand?> slots) : base(title)
    {
        Slots = LauncherSlots.Copy(slots);
    }

    internal IReadOnlyList<LauncherCommand?> Slots
    {
        get;
    }

    internal LauncherGroup WithSlot(int index, LauncherCommand? command)
    {
        LauncherSlots.ValidateIndex(index);
        LauncherCommand?[] copy = [.. Slots];
        copy[index] = command;
        return new LauncherGroup(Title, copy);
    }

    internal LauncherGroup MoveSlot(int from, int to) =>
        new(Title, LauncherSlots.Move(Slots, from, to));
}

internal sealed class LauncherSettings
{
    internal const int SlotCount = 35;

    internal LauncherSettings(IEnumerable<LauncherEntry?>? slots = null,
        ScriptHotkey? shortcut = null)
    {
        Slots = LauncherSlots.Copy(slots ?? []);
        if (shortcut is ScriptHotkey key)
        {
            bool supported = key.VirtualKey is >= 48 and <= 57
                or >= 65 and <= 90 or >= 112 and <= 135;
            if (!supported || key.Modifiers is <= 0 or > 15)
            {
                throw new InvalidDataException("ランチ用ホットキーの組合せが不正です。");
            }

            if (SelectiveInputEmergencyShortcut.IsReserved(key))
            {
                throw new InvalidDataException("緊急解除専用キーはランチに登録できません。");
            }
        }

        Shortcut = shortcut;
    }

    internal IReadOnlyList<LauncherEntry?> Slots
    {
        get;
    }

    internal ScriptHotkey? Shortcut
    {
        get;
    }

    internal void ValidateShortcutAvailability(IEnumerable<ScriptHotkey> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        if (Shortcut is ScriptHotkey key && occupied.Contains(key))
        {
            throw new InvalidDataException("ランチ用ホットキーが本体の操作キーと競合します。");
        }
    }

    internal LauncherSettings WithSlot(int index, LauncherEntry? entry)
    {
        LauncherSlots.ValidateIndex(index);
        LauncherEntry?[] copy = [.. Slots];
        copy[index] = entry;
        return new LauncherSettings(copy, Shortcut);
    }

    internal LauncherSettings MoveSlot(int from, int to) =>
        new(LauncherSlots.Move(Slots, from, to), Shortcut);
}

internal static class LauncherSlots
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static IReadOnlyList<T?> Copy<T>(IEnumerable<T?> slots) where T : class
    {
        ArgumentNullException.ThrowIfNull(slots);
        var copy = new T?[LauncherSettings.SlotCount];
        int index = 0;
        foreach (T? slot in slots)
        {
            if (index == copy.Length)
            {
                throw new InvalidDataException("ランチの登録枠は各階層35件までです。");
            }

            copy[index++] = slot;
        }

        return Array.AsReadOnly(copy);
    }

    internal static T?[] Move<T>(IReadOnlyList<T?> slots, int from, int to) where T : class
    {
        ValidateIndex(from);
        ValidateIndex(to);
        T?[] copy = [.. slots];
        T? selected = copy[from];
        if (from < to)
        {
            Array.Copy(copy, from + 1, copy, from, to - from);
        }
        else if (from > to)
        {
            Array.Copy(copy, to, copy, to + 1, from - to);
        }

        copy[to] = selected;
        return copy;
    }

    internal static void ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, LauncherSettings.SlotCount);
    }

    internal static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0'))
        {
            throw new InvalidDataException("ランチ項目の文字列にNULは指定できません。");
        }

        try
        {
            // JSON保存で孤立サロゲートが別の文字へ置換される前に拒否する。
            StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("ランチ項目の文字列に不正なUTF16があります。", error);
        }
    }
}
