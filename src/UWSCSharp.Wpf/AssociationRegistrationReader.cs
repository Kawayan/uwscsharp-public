using Microsoft.Win32;

namespace UWSCSharp.Wpf;

// 値の取得だけを行い、既存の登録を変更しません。
internal static class AssociationRegistrationReader
{
    internal const string CandidateName = "UWSCSharp.Script.1";
    private const string ClassesPath = @"Software\Classes";

    internal static AssociationSnapshot Read(
        TransactedRegistry registry, string executablePath)
    {
        string candidatePath = ClassesPath + @"\" + CandidateName;
        using RegistryKey? candidate = registry.Open(candidatePath, writable: false);
        using RegistryKey? openWith = registry.Open(
            ClassesPath + @"\.uws\OpenWithProgids", writable: false);
        bool hasOpenWith = openWith?.GetValueNames().Contains(
            CandidateName, StringComparer.OrdinalIgnoreCase) == true;
        bool unexpected = hasOpenWith &&
            (openWith!.GetValueKind(CandidateName) != RegistryValueKind.None ||
             openWith.GetValue(CandidateName) is not byte[] { Length: 0 });
        string? owner = null;
        string? token = null;
        string? command = null;
        if (candidate is not null)
        {
            string[] valueNames = candidate.GetValueNames();
            unexpected |= !NamesMatch(valueNames, "", "OwnerPath") &&
                !NamesMatch(valueNames, "", "OwnerPath", "OwnerToken");
            unexpected |= !NamesMatch(candidate.GetSubKeyNames(), "shell");
            unexpected |= ReadString(candidate, "") != "UWSC# Script";
            owner = ReadString(candidate, "OwnerPath");
            token = ReadString(candidate, "OwnerToken");
            unexpected |= valueNames.Contains("OwnerToken", StringComparer.OrdinalIgnoreCase) &&
                token is null;
            using RegistryKey? shell = registry.Open(
                candidatePath + @"\shell", writable: false);
            unexpected |= shell is null || !NamesMatch(shell.GetValueNames()) ||
                !NamesMatch(shell.GetSubKeyNames(), "open");
            using RegistryKey? open = registry.Open(
                candidatePath + @"\shell\open", writable: false);
            unexpected |= open is null || !NamesMatch(open.GetValueNames()) ||
                !NamesMatch(open.GetSubKeyNames(), "command");
            using RegistryKey? commandKey = registry.Open(
                candidatePath + @"\shell\open\command", writable: false);
            unexpected |= commandKey is null ||
                !NamesMatch(commandKey.GetValueNames(), "") ||
                !NamesMatch(commandKey.GetSubKeyNames());
            command = commandKey is null ? null : ReadString(commandKey, "");
        }

        string? expected = candidate is null && !hasOpenWith
            ? null : $"\"{executablePath}\" \"%1\"";
        return new AssociationSnapshot(candidate is not null, hasOpenWith,
            unexpected, owner, token, command, expected);
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ||
            key.GetValueKind(name) != RegistryValueKind.String)
        {
            return null;
        }

        return key.GetValue(name, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static bool NamesMatch(string[] actual, params string[] expected)
        => actual.Length == expected.Length &&
            actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected);
}
