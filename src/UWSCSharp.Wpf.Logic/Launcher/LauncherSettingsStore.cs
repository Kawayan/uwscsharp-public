using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal sealed record LauncherSettingsSnapshot(
    LauncherSettings Settings, string? ContentHash);

internal static class LauncherSettingsStore
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };

    internal static LauncherSettings Load(string path) =>
        LoadSnapshot(path).Settings;

    internal static LauncherSettingsSnapshot LoadSnapshot(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return new LauncherSettingsSnapshot(new LauncherSettings(), null);
        }
        catch (DirectoryNotFoundException)
        {
            return new LauncherSettingsSnapshot(new LauncherSettings(), null);
        }

        using (stream)
        {
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidDataException("ランチ設定の読込み上限は4MiBです。");
            }

            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException("読込み中にランチ設定のサイズが変わりました。");
            }

            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                json = json[3..];
            }

            try
            {
                FileData data = JsonSerializer.Deserialize<FileData>(json, Options)
                    ?? throw new InvalidDataException("ランチ設定が空です。");
                if (data.Version != CurrentVersion)
                {
                    throw new InvalidDataException("未対応のランチ設定バージョンです。");
                }

                if (data.Slots.Length > LauncherSettings.SlotCount)
                {
                    throw new InvalidDataException("ランチの上位枠が35件を超えています。");
                }

                var settings = new LauncherSettings(
                    data.Slots.Select(ReadEntry), data.Shortcut);
                return new LauncherSettingsSnapshot(settings, ContentHash(bytes));
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                throw new InvalidDataException("ランチ設定の形式が不正です。", error);
            }
        }
    }

    internal static void Save(string path, LauncherSettings settings) =>
        SaveCore(path, settings, null);

    internal static LauncherSettingsSnapshot Save(string path,
        LauncherSettings settings, LauncherSettingsSnapshot expected) =>
        new(settings, SaveCore(path, settings, null, expected));

    // 利用者が明示的に初期化を選んだときだけ呼び、置換対象の原文を退避する。
    internal static string? ResetWithBackup(string path) =>
        ResetWithBackup(path, out _);

    internal static string? ResetWithBackup(string path,
        out LauncherSettingsSnapshot snapshot)
    {
        string backup = System.IO.Path.GetFullPath(path) + "."
            + Guid.NewGuid().ToString("N") + ".rejected.json";
        var settings = new LauncherSettings();
        snapshot = new LauncherSettingsSnapshot(settings,
            SaveCore(path, settings, backup));
        return File.Exists(backup) ? backup : null;
    }

    private static string SaveCore(string path, LauncherSettings settings,
        string? backup, LauncherSettingsSnapshot? expected = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var serialized = new LimitedMemoryStream(MaximumBytes,
            "ランチ設定の保存上限は4MiBです。");
        JsonSerializer.Serialize(serialized, new FileData
        {
            Version = CurrentVersion,
            Shortcut = settings.Shortcut,
            Slots = [.. settings.Slots.Select(WriteEntry)]
        }, Options);
        string destination = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        // エディター間の保存を直列化し、削除と再作成による排他の分裂を防ぎます。
        using var ownership = new FileStream(destination + ".lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        AtomicFileWriter.CommitMode mode = AtomicFileWriter.CommitMode.ReplaceIfPresent;
        if (expected is not null)
        {
            if (LoadSnapshot(destination).ContentHash != expected.ContentHash)
            {
                throw new IOException(
                    "別の保存でランチャー設定が変更されています。設定を再読込みしてください。");
            }

            mode = expected.ContentHash is null
                ? AtomicFileWriter.CommitMode.Move
                : AtomicFileWriter.CommitMode.Replace;
        }

        string hash = ContentHash(serialized.GetBuffer()
            .AsSpan(0, checked((int)serialized.Length)));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        AtomicFileWriter.Write(temporary, destination, file =>
        {
            serialized.Position = 0;
            serialized.CopyTo(file);
        }, mode, "ランチ設定の一時ファイル削除エラー", backup);
        return hash;
    }

    private static string ContentHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static LauncherEntry? ReadEntry(EntryData? data)
    {
        if (data is null)
        {
            return null;
        }

        if (data.Kind == "group")
        {
            if (data.Path is not null || data.Arguments is not null || data.Slots is null)
            {
                throw new InvalidDataException("ランチの階層設定が不正です。");
            }

            if (data.Slots.Length > LauncherSettings.SlotCount)
            {
                throw new InvalidDataException("ランチの子枠が35件を超えています。");
            }

            return new LauncherGroup(data.Title, data.Slots.Select(child =>
                child is null ? null : ReadCommand(child)));
        }

        return ReadCommand(data);
    }

    private static LauncherCommand ReadCommand(EntryData data)
    {
        LauncherCommandKind kind = data.Kind switch
        {
            "script" => LauncherCommandKind.Script,
            "executable" => LauncherCommandKind.Executable,
            "document" => LauncherCommandKind.Document,
            _ => throw new InvalidDataException("ランチ項目の種別が不正です。")
        };
        if (data.Path is null || !System.IO.Path.IsPathFullyQualified(data.Path)
            || data.Arguments is null || data.Slots is not null)
        {
            throw new InvalidDataException("ランチ項目のパス・引数・階層が不正です。");
        }

        return new LauncherCommand(data.Title, data.Path, kind, data.Arguments,
            System.IO.Path.GetPathRoot(data.Path)!);
    }

    private static EntryData? WriteEntry(LauncherEntry? entry) => entry switch
    {
        null => null,
        LauncherGroup group => new EntryData
        {
            Kind = "group",
            Title = group.Title,
            Slots = [.. group.Slots.Select(child => WriteEntry(child))]
        },
        LauncherCommand command => new EntryData
        {
            Kind = command.Kind switch
            {
                LauncherCommandKind.Script => "script",
                LauncherCommandKind.Executable => "executable",
                _ => "document"
            },
            Title = command.Title,
            Path = command.Path,
            Arguments = [.. command.Arguments]
        },
        _ => throw new InvalidDataException("ランチ項目の型が不正です。")
    };

    private sealed class FileData
    {
        public FileData()
        {
        }

        public required int Version
        {
            get; init;
        }

        public ScriptHotkey? Shortcut
        {
            get; init;
        }

        public required EntryData?[] Slots
        {
            get; init;
        }
    }

    private sealed class EntryData
    {
        public EntryData()
        {
        }

        public required string Kind
        {
            get; init;
        }

        public required string Title
        {
            get; init;
        }

        public string? Path
        {
            get; init;
        }

        public string[]? Arguments
        {
            get; init;
        }

        public EntryData?[]? Slots
        {
            get; init;
        }
    }
}
