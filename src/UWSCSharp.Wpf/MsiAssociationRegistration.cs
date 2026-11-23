using System.IO;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

// MSIの実行とロールバックの間に必要な状態を、関連付けと同じトランザクションで保存します。
internal static class MsiAssociationRegistration
{
    private const string JournalRoot = @"Software\UWSCSharp\AssociationTransactions";
    private const string Absent = "absent";
    private const string Legacy = "legacy";

    internal static void Install(string executable, string productCode, bool productWasInstalled)
    {
        string path = ValidateExecutable(executable);
        string product = Token(productCode);
        Recover(path, product, productWasInstalled, featureWasInstalled: false);
        using var registry = new TransactedRegistry();
        AssociationRegistration.PinExisting(registry);
        AssociationSnapshot snapshot = AssociationRegistrationReader.Read(registry, path);
        AssociationOwnership ownership = AssociationOwnershipPolicy.Classify(snapshot, path);
        if (ownership == AssociationOwnership.Conflict)
        {
            throw new InvalidOperationException("既存の関連付け候補を引き継げません。現在の配置で解除してから再試行してください。");
        }

        if (ownership == AssociationOwnership.Owned && snapshot.OwnerToken == product)
        {
            return;
        }

        string before = ownership == AssociationOwnership.Absent
            ? Absent : snapshot.OwnerToken ?? Legacy;
        WriteJournal(registry, product, "install", path, before, product);
        if (ownership == AssociationOwnership.Absent)
        {
            AssociationRegistration.CreateOwned(registry, path, product);
        }
        else
        {
            AssociationRegistration.ReplaceToken(registry, path, snapshot.OwnerToken, product);
        }

        registry.Commit();
        AssociationRegistration.NotifyShell();
    }

    internal static void Uninstall(string executable, string productCode)
    {
        string path = Path.GetFullPath(executable);
        string product = Token(productCode);
        Recover(path, product, productWasInstalled: true, featureWasInstalled: true);
        using var registry = new TransactedRegistry();
        AssociationRegistration.PinExisting(registry);
        AssociationSnapshot snapshot = AssociationRegistrationReader.Read(registry, path);
        AssociationOwnership ownership = AssociationOwnershipPolicy.Classify(snapshot, path);
        if (ownership == AssociationOwnership.Absent ||
            (ownership == AssociationOwnership.Owned && snapshot.OwnerToken != product))
        {
            return;
        }

        if (ownership != AssociationOwnership.Owned)
        {
            throw new InvalidOperationException("関連付け候補が変更されているため、解除できません。");
        }

        WriteJournal(registry, product, "uninstall", path, product, Absent);
        AssociationRegistration.RemoveOwned(registry);
        registry.Commit();
        AssociationRegistration.NotifyShell();
    }

    internal static void Disable(string executable, string productCode, string? previousProduct)
    {
        string path = Path.GetFullPath(executable);
        string product = Token(productCode);
        string owner = string.IsNullOrWhiteSpace(previousProduct) ? product : Token(previousProduct);
        bool wasInstalled = string.IsNullOrWhiteSpace(previousProduct);
        Recover(path, product, wasInstalled, featureWasInstalled: wasInstalled);
        using var registry = new TransactedRegistry();
        AssociationRegistration.PinExisting(registry);
        AssociationSnapshot snapshot = AssociationRegistrationReader.Read(registry, path);
        AssociationOwnership ownership = AssociationOwnershipPolicy.Classify(snapshot, path);
        if (ownership == AssociationOwnership.Absent ||
            (ownership == AssociationOwnership.Owned && snapshot.OwnerToken != owner))
        {
            return;
        }
        if (ownership != AssociationOwnership.Owned)
        {
            throw new InvalidOperationException("関連付け候補が変更されているため、解除できません。");
        }

        WriteJournal(registry, product, "disable", path, owner, Absent);
        AssociationRegistration.RemoveOwned(registry);
        registry.Commit();
        AssociationRegistration.NotifyShell();
    }

    internal static void Rollback(string executable, string productCode, string operation)
    {
        string path = Path.GetFullPath(executable);
        string product = Token(productCode);
        if (operation is not ("install" or "uninstall" or "disable"))
        {
            throw new ArgumentException("ロールバック対象が不正です。", nameof(operation));
        }

        using var registry = new TransactedRegistry();
        AssociationRegistration.PinExisting(registry);
        Journal? journal = ReadJournal(registry, product, operation, path);
        if (journal is null)
        {
            return;
        }

        Restore(registry, path, journal.Value);
        DeleteJournal(registry, product, operation);
        registry.Commit();
        AssociationRegistration.NotifyShell();
    }

    // MSI実行中は状態照会が変化するため、操作前の製品・機能状態を使います。
    private static void Recover(string path, string product, bool productWasInstalled, bool featureWasInstalled)
    {
        int state = productWasInstalled ? 5 : -1;
        int feature = productWasInstalled && featureWasInstalled ? 3 : 2;

        using var registry = new TransactedRegistry();
        AssociationRegistration.PinExisting(registry);
        Journal? install = ReadJournal(registry, product, "install", path);
        Journal? uninstall = ReadJournal(registry, product, "uninstall", path);
        Journal? disable = ReadJournal(registry, product, "disable", path);
        if (uninstall is not null && state == 5)
        {
            Restore(registry, path, uninstall.Value);
        }
        if (disable is not null && (state == -1 || feature == 3))
        {
            Restore(registry, path, disable.Value);
        }
        if (install is not null && feature != 3)
        {
            Restore(registry, path, install.Value);
        }

        if (install is not null)
        {
            DeleteJournal(registry, product, "install");
        }
        if (uninstall is not null)
        {
            DeleteJournal(registry, product, "uninstall");
        }
        if (disable is not null)
        {
            DeleteJournal(registry, product, "disable");
        }
        if (install is not null || uninstall is not null || disable is not null)
        {
            registry.Commit();
            AssociationRegistration.NotifyShell();
        }
    }

    private static void Restore(TransactedRegistry registry, string path, Journal journal)
    {
        AssociationSnapshot current = AssociationRegistrationReader.Read(registry, path);
        AssociationOwnership state = AssociationOwnershipPolicy.Classify(current, path);
        if (journal.Before == Absent)
        {
            if (state == AssociationOwnership.Absent)
            {
                return;
            }
            if (state == AssociationOwnership.Owned && current.OwnerToken == journal.After)
            {
                AssociationRegistration.RemoveOwned(registry);
                return;
            }
        }
        else
        {
            string? previous = journal.Before == Legacy ? null : journal.Before;
            if (state == AssociationOwnership.Absent && journal.After == Absent)
            {
                AssociationRegistration.CreateOwned(registry, path, previous);
                return;
            }
            if (state == AssociationOwnership.Owned && current.OwnerToken == previous)
            {
                return;
            }
            if (state == AssociationOwnership.Owned && current.OwnerToken == journal.After)
            {
                AssociationRegistration.ReplaceToken(registry, path, current.OwnerToken, previous);
                return;
            }
        }

        throw new InvalidOperationException("関連付け候補が変更されたため、元の状態へ戻せません。");
    }

    private static void WriteJournal(TransactedRegistry registry, string product,
        string operation, string path, string before, string after)
    {
        string keyPath = JournalPath(product, operation);
        using RegistryKey key = registry.Create(keyPath, requireNew: true);
        key.SetValue("Path", path, RegistryValueKind.String);
        key.SetValue("Before", before, RegistryValueKind.String);
        key.SetValue("After", after, RegistryValueKind.String);
    }

    private static Journal? ReadJournal(TransactedRegistry registry, string product,
        string operation, string path)
    {
        string keyPath = JournalPath(product, operation);
        registry.PinExisting(keyPath);
        using RegistryKey? key = registry.Open(keyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        string[] names = key.GetValueNames();
        if (key.SubKeyCount != 0 || names.Length != 3 ||
            !names.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Path", "Before", "After"]) ||
            names.Any(name => key.GetValueKind(name) != RegistryValueKind.String))
        {
            throw new InvalidOperationException("MSIの復旧情報が変更されています。");
        }

        string? storedPath = key.GetValue("Path") as string;
        string? before = key.GetValue("Before") as string;
        string? after = key.GetValue("After") as string;
        bool validBefore = before is Absent or Legacy || Guid.TryParseExact(before, "N", out _);
        bool validAfter = operation == "install" ? after == product : after == Absent;
        if (!string.Equals(storedPath, path, StringComparison.OrdinalIgnoreCase) ||
            !validBefore || !validAfter || (operation == "uninstall" && before != product) ||
            (operation == "disable" && !Guid.TryParseExact(before, "N", out _)))
        {
            throw new InvalidOperationException("MSIの復旧情報が不正です。");
        }

        return new Journal(before!, after!);
    }

    private static void DeleteJournal(TransactedRegistry registry, string product, string operation)
    {
        registry.DeleteTree(JournalRoot + @"\" + product, operation);
    }

    private static string JournalPath(string product, string operation)
        => JournalRoot + @"\" + product + @"\" + operation;

    private static string ValidateExecutable(string executable)
    {
        string path = Path.GetFullPath(executable);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("関連付け起動用EXEがありません。", path);
        }
        return path;
    }

    private static string Token(string productCode)
        => Guid.TryParse(productCode, out Guid code) ? code.ToString("N")
            : throw new ArgumentException("MSIの製品コードが不正です。", nameof(productCode));

    private readonly record struct Journal(string Before, string After);


}
