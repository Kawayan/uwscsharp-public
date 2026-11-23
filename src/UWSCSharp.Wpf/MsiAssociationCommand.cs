using System.Globalization;
using System.Windows;

namespace UWSCSharp.Wpf;

// MSIの遅延処理とロールバックは専用EXEで受け付けます。
internal static class MsiAssociationCommand
{
    internal static int Run(string[] args, string executable)
    {
        bool rollback = args.Length > 0 && args[0] == "--msi-association-rollback";
        bool interactive = args.Length > 3 &&
            int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int level) &&
            level >= 4;
        try
        {
            if (args.Length != 4)
            {
                throw new ArgumentException("MSI関連付け操作の引数が不正です。");
            }

            switch (args[0])
            {
                case "--msi-association-install":
                    MsiAssociationRegistration.Install(executable, args[1],
                        !string.IsNullOrWhiteSpace(args[2]));
                    break;
                case "--msi-association-uninstall":
                    MsiAssociationRegistration.Uninstall(executable, args[1]);
                    break;
                case "--msi-association-disable":
                    MsiAssociationRegistration.Disable(executable, args[1], args[2]);
                    break;
                case "--msi-association-rollback":
                    MsiAssociationRegistration.Rollback(executable, args[1], args[2]);
                    break;
                default:
                    throw new ArgumentException("MSI関連付け操作が不正です。");
            }

            return 0;
        }
        catch (Exception error)
        {
            App.ReportCommandLineError(error);
            if (interactive && !rollback)
            {
                MessageBox.Show(error.Message, ApplicationInfo.Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 1;
        }
    }
}
