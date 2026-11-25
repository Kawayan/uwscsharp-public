using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// この検証は使い捨てSandbox内だけで実行します。
internal static class AssociationRegistrationVerification
{
    private const string Candidate = @"Software\Classes\UWSCSharp.Script.1";
    private const string OpenWith = @"Software\Classes\.uws\OpenWithProgids";

    internal static int Run(string receipt)
    {
        // 利用者の関連付けを書き換えるため、Sandbox外では何も変更せずに終了します。
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("関連付け登録の検証はSandbox専用です。");
        }

        var checks = new List<string>();
        string directory = Path.GetDirectoryName(Path.GetFullPath(receipt))!;
        string first = Path.Combine(directory, "association-first.exe");
        string second = Path.Combine(directory, "association-second.exe");
        try
        {
            using (RegistryKey? candidate = Registry.CurrentUser.OpenSubKey(Candidate))
            using (RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(OpenWith))
            {
                Require(candidate is null &&
                    openWith?.GetValueNames().Contains("UWSCSharp.Script.1",
                        StringComparer.OrdinalIgnoreCase) != true,
                    "検証前の候補が空であること");
            }

            File.WriteAllText(first, "fixture");
            File.WriteAllText(second, "fixture");
            using (RegistryKey openWith = Registry.CurrentUser.CreateSubKey(OpenWith))
            {
                openWith.SetValue("Other.Test.Script", Array.Empty<byte>(), RegistryValueKind.None);
            }

            AssociationRegistration.Register(first);
            checks.Add("register-first");
            AssociationRegistration.Register(first);
            checks.Add("register-idempotent");
            foreach (string target in new[]
            {
                Candidate, Candidate + @"\shell", Candidate + @"\shell\open",
                Candidate + @"\shell\open\command"
            })
            {
                checks.Add(VerifyConcurrentChange(first, target));
            }
            VerifyUnregisterRace(first);
            checks.Add("unregister-race-preserves-external-data");
            ExpectConflict(() => AssociationRegistration.Register(second));
            checks.Add("conflict-other-owner-register");
            ExpectConflict(() => AssociationRegistration.Unregister(second));
            checks.Add("conflict-other-owner-unregister");
            using (RegistryKey candidate = Registry.CurrentUser.OpenSubKey(Candidate, writable: true)!)
            {
                candidate.SetValue("ForeignValue", "keep", RegistryValueKind.String);
            }

            ExpectConflict(() => AssociationRegistration.Unregister(first));
            using (RegistryKey candidate = Registry.CurrentUser.OpenSubKey(Candidate, writable: true)!)
            {
                Require((string?)candidate.GetValue("ForeignValue") == "keep", "追加された値の保持");
                candidate.DeleteValue("ForeignValue");
            }
            checks.Add("conflict-preserves-foreign-value");

            using (RegistryKey openWith = Registry.CurrentUser.OpenSubKey(OpenWith, writable: true)!)
            {
                openWith.DeleteValue("UWSCSharp.Script.1");
            }
            ExpectConflict(() => AssociationRegistration.Register(first));
            ExpectConflict(() => AssociationRegistration.Unregister(first));
            using (RegistryKey openWith = Registry.CurrentUser.OpenSubKey(OpenWith, writable: true)!)
            {
                openWith.SetValue("UWSCSharp.Script.1", Array.Empty<byte>(), RegistryValueKind.None);
            }
            checks.Add("partial-registration-is-rejected");

            using (RegistryKey command = Registry.CurrentUser.OpenSubKey(
                Candidate + @"\shell\open\command", writable: true)!)
            {
                command.SetValue("", "modified", RegistryValueKind.String);
            }

            ExpectConflict(() => AssociationRegistration.Unregister(first));
            using (RegistryKey command = Registry.CurrentUser.OpenSubKey(
                Candidate + @"\shell\open\command")!)
            {
                Require((string?)command.GetValue("") == "modified", "変更済みコマンドの保持");
            }
            checks.Add("conflict-preserves-changed-command");
            using (RegistryKey command = Registry.CurrentUser.OpenSubKey(
                Candidate + @"\shell\open\command", writable: true)!)
            {
                command.SetValue("", $"\"{first}\" \"%1\"", RegistryValueKind.String);
            }

            AssociationRegistration.Unregister(first);
            using (RegistryKey? candidate = Registry.CurrentUser.OpenSubKey(Candidate))
            using (RegistryKey openWith = Registry.CurrentUser.OpenSubKey(OpenWith)!)
            {
                Require(candidate is null &&
                    !openWith.GetValueNames().Contains("UWSCSharp.Script.1",
                        StringComparer.OrdinalIgnoreCase), "自分の候補の解除");
                Require(openWith.GetValueNames().Contains("Other.Test.Script"),
                    "他の候補の保持");
            }
            checks.Add("unregister-owned");
            checks.Add("other-candidate-preserved");
            VerifyRegisterRace(first);
            checks.Add("register-race-preserves-external-data");
            var window = new AssociationSettingsWindow(first);
            try
            {
                window.Show();
                window.RegisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(window.ResultText.Text.StartsWith("候補を登録しました。"),
                    "画面の登録結果");
                checks.Add("ui-register");
                var otherWindow = new AssociationSettingsWindow(second);
                try
                {
                    otherWindow.Show();
                    otherWindow.RegisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(otherWindow.ResultText.Text.StartsWith("操作に失敗しました:"),
                        "画面の競合通知");
                    checks.Add("ui-conflict");
                }
                finally
                {
                    otherWindow.Close();
                }

                window.UnregisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(window.ResultText.Text == "候補の登録を解除しました。",
                    "画面の解除結果");
                checks.Add("ui-unregister");
            }
            finally
            {
                window.Close();
            }

            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks,
                window_shown = true,
                input_injection = false
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                checks,
                error = error.ToString(),
                window_shown = true,
                input_injection = false
            }));
            return 1;
        }
    }

    private static void ExpectConflict(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("競合が拒否されませんでした。");
    }

    private static string VerifyConcurrentChange(string path, string target)
    {
        bool written = false;
        string outcome = "";
        using (var transaction = new TransactedRegistry())
        {
            transaction.PinExisting(Candidate);
            transaction.PinExisting(Candidate + @"\shell");
            transaction.PinExisting(Candidate + @"\shell\open");
            transaction.PinExisting(Candidate + @"\shell\open\command");
            transaction.PinExisting(OpenWith);
            Require(AssociationOwnershipPolicy.Classify(
                AssociationRegistrationReader.Read(transaction, path), path)
                == AssociationOwnership.Owned, "競合前の所有状態");
            try
            {
                using RegistryKey candidate = Registry.CurrentUser.OpenSubKey(
                    target, writable: true)!;
                candidate.SetValue("ConcurrentValue", "keep", RegistryValueKind.String);
                written = true;
            }
            catch (IOException error)
            {
                // 拒否した理由も記録し、権限など別の失敗と区別します。
                int code = error.HResult & 0xffff;
                Require(code == 6800, $"想定外の外部書込みエラー: {code}");
                outcome = "concurrent-write-conflict-6800";
            }

            if (written)
            {
                try
                {
                    transaction.DeleteTree(@"Software\Classes", "UWSCSharp.Script.1");
                    transaction.Commit();
                }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                {
                    // 検証後の変更によりトランザクションが中止されます。
                }
            }
        }

        using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(target, writable: true);
        Require(existing is not null, "競合後の候補の保持");
        if (written)
        {
            Require((string?)existing!.GetValue("ConcurrentValue") == "keep",
                "競合した値の保持");
            existing.DeleteValue("ConcurrentValue");
            outcome = "concurrent-write-preserved";
        }

        return outcome;
    }

    private static void VerifyUnregisterRace(string path)
    {
        bool written = false;
        bool rejected = false;
        try
        {
            AssociationRegistration.Unregister(path, () =>
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(Candidate, writable: true)!;
                key.SetValue("RaceValue", "keep", RegistryValueKind.String);
                written = true;
            });
        }
        catch (Exception error) when (error is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            rejected = true;
        }

        Require(rejected, "競合時の解除拒否");
        using RegistryKey? candidate = Registry.CurrentUser.OpenSubKey(Candidate, writable: true);
        Require(candidate is not null, "競合後の候補の保持");
        if (written)
        {
            Require((string?)candidate!.GetValue("RaceValue") == "keep", "競合した値の保持");
            candidate.DeleteValue("RaceValue");
        }
    }

    private static void VerifyRegisterRace(string path)
    {
        bool rejected = false;
        try
        {
            AssociationRegistration.Register(path, () =>
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(Candidate);
                key.SetValue("ForeignValue", "keep", RegistryValueKind.String);
            });
        }
        catch (Exception error) when (error is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            rejected = true;
        }

        Require(rejected, "競合時の登録拒否");
        using (RegistryKey? candidate = Registry.CurrentUser.OpenSubKey(Candidate))
        {
            Require((string?)candidate?.GetValue("ForeignValue") == "keep",
                "競合した候補の保持");
        }

        Registry.CurrentUser.DeleteSubKeyTree(Candidate);
    }
}
