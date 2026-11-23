using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

// 現在の配置が所有する関連付け候補だけを操作します。
internal static class AssociationRegistration
{
    private const string ClassesPath = @"Software\Classes";
    private const string OpenWithPath = ClassesPath + @"\.uws\OpenWithProgids";
    internal static string CandidatePath =>
        ClassesPath + @"\" + AssociationRegistrationReader.CandidateName;

    internal static void Register(string executablePath, Action? afterValidation = null)
    {
        string path = Path.GetFullPath(executablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("関連付け起動用EXEがありません。", path);
        }

        using var registry = new TransactedRegistry();
        PinExisting(registry);
        AssociationOwnership state = Classify(registry, path);
        if (state == AssociationOwnership.Conflict)
        {
            throw new InvalidOperationException("既存の関連付け候補が別の配置に属するか、変更されています。");
        }

        if (state == AssociationOwnership.Owned)
        {
            return;
        }

        // テストでは、この時点の外部変更を再現します。
        afterValidation?.Invoke();
        CreateOwned(registry, path, Guid.NewGuid().ToString("N"));
        registry.Commit();
        NotifyShell();
    }

    internal static void CreateOwned(
        TransactedRegistry registry, string path, string? token)
    {
        using (RegistryKey key = registry.Create(CandidatePath, requireNew: true))
        {
            key.SetValue("", "UWSC# Script", RegistryValueKind.String);
            key.SetValue("OwnerPath", path, RegistryValueKind.String);
            if (token is not null)
            {
                key.SetValue("OwnerToken", token, RegistryValueKind.String);
            }
        }

        using (RegistryKey key = registry.Create(CandidatePath + @"\shell\open\command"))
        {
            key.SetValue("", $"\"{path}\" \"%1\"", RegistryValueKind.String);
        }

        using (RegistryKey key = registry.Create(OpenWithPath))
        {
            if (key.GetValueNames().Contains(AssociationRegistrationReader.CandidateName,
                StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("関連付け候補が同時に作成されました。");
            }

            key.SetValue(AssociationRegistrationReader.CandidateName,
                Array.Empty<byte>(), RegistryValueKind.None);
        }

        if (Classify(registry, path) != AssociationOwnership.Owned)
        {
            throw new InvalidOperationException("関連付け候補の登録内容を確認できません。");
        }

    }

    internal static void Unregister(string executablePath, Action? afterValidation = null)
    {
        string path = Path.GetFullPath(executablePath);
        using var registry = new TransactedRegistry();
        PinExisting(registry);
        if (Classify(registry, path) != AssociationOwnership.Owned)
        {
            throw new InvalidOperationException("関連付け候補が存在しないか、別の配置に属するか、変更されています。");
        }

        // テストでは、この時点の外部変更を再現します。
        afterValidation?.Invoke();
        RemoveOwned(registry);
        registry.Commit();
        NotifyShell();
    }

    internal static void RemoveOwned(TransactedRegistry registry)
    {
        using (RegistryKey openWith = registry.Open(OpenWithPath, writable: true)
            ?? throw new InvalidOperationException("関連付け候補が変更されました。"))
        {
            openWith.DeleteValue(AssociationRegistrationReader.CandidateName);
        }

        registry.DeleteTree(ClassesPath, AssociationRegistrationReader.CandidateName);
    }

    internal static void ReplaceToken(TransactedRegistry registry, string path,
        string? expected, string? replacement)
    {
        AssociationSnapshot snapshot = AssociationRegistrationReader.Read(registry, path);
        if (AssociationOwnershipPolicy.Classify(snapshot, path) != AssociationOwnership.Owned ||
            !string.Equals(snapshot.OwnerToken, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("関連付け候補の所有状態が変更されました。");
        }

        using RegistryKey candidate = registry.Open(CandidatePath, writable: true)
            ?? throw new InvalidOperationException("関連付け候補が変更されました。");
        if (replacement is null)
        {
            candidate.DeleteValue("OwnerToken", throwOnMissingValue: false);
        }
        else
        {
            candidate.SetValue("OwnerToken", replacement, RegistryValueKind.String);
        }
    }

    internal static AssociationOwnership Classify(
        TransactedRegistry registry, string path)
        => AssociationOwnershipPolicy.Classify(
            AssociationRegistrationReader.Read(registry, path), path);

    internal static void PinExisting(TransactedRegistry registry)
    {
        registry.PinExisting(CandidatePath);
        registry.PinExisting(CandidatePath + @"\shell");
        registry.PinExisting(CandidatePath + @"\shell\open");
        registry.PinExisting(CandidatePath + @"\shell\open\command");
        registry.PinExisting(OpenWithPath);
    }

    internal static void NotifyShell()
    {
        SHChangeNotify(0x08000000, 0, nint.Zero, nint.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags,
        nint item1, nint item2);
}
