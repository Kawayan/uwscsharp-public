using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    internal static Task<int> VerifyRestartCancellationAsync(
        string directory, string receipt) =>
        VerifyRestartCancellationAsync(directory, receipt, useDialog: false);

    internal static Task<int> VerifyRestartConfirmationMessageAsync(
        string directory, string receipt) =>
        VerifyRestartCancellationAsync(directory, receipt, useDialog: true);

    private static async Task<int> VerifyRestartCancellationAsync(
        string directory, string receipt, bool useDialog)
    {
        string script = Path.Combine(directory, "saved.uws");
        const string saved = "PRINT 1";
        const string edited = """
            TRY
                POFF(P_UWSC_REEXEC, TRUE)
                PRINT INT(NULL)
            FINALLY
                PRINT "cleanup"
            ENDTRY
            """;
        await File.WriteAllTextAsync(script, saved, new UTF8Encoding(true));

        int confirmations = 0;
        bool discard = false;
        bool closed = false;
        var errors = new List<Exception>();
        var window = new MainWindow(false, confirmDiscard: useDialog ? null : () =>
        {
            confirmations++;
            return discard;
        }, reportError: errors.Add)
        {
            ShowActivated = false
        };
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            window.scriptPath = script;
            window.Editor.Text = edited;
            nint owner = new WindowInteropHelper(window).Handle;
            Task? chooseNo = useDialog ? Task.Run(() =>
                ConfirmationDialogVerification.ChooseAsync(owner,
                    MessageBoxResult.No, "保存していない変更を破棄しますか？")) : null;
            int result = await window.PlayAsync([]);
            if (chooseNo is not null)
            {
                await chooseNo;
            }

            int expectedConfirmations = useDialog ? 0 : 1;
            Require(result == 1 && confirmations == expectedConfirmations,
                "未保存確認で再起動を取り消す");
            Require(!closed && window.IsVisible && window.pendingRestart is null,
                "編集画面を残し、再起動要求を取り消す");
            Require(window.modified && window.Editor.Text == edited
                && window.scriptPath == script, "未保存の編集とファイルパスを保持する");
            Require(await File.ReadAllTextAsync(script) == saved,
                "保存済みファイルの内容を保持する");
            Require(window.cancellation is null && window.inputCleanup.CanStart
                && errors.Count == 0, "後片付けを終え、エラー表示を追加しない");

            window.Editor.Text = "PRINT 2";
            Require(await window.PlayAsync([]) == 0
                && confirmations == expectedConfirmations,
                "同じ画面から再生できる");
            discard = true;
            if (useDialog)
            {
                window.modified = false;
            }

            window.Close();
            Require(closed && confirmations == expectedConfirmations * 2
                && window.pendingRestart is null,
                "通常終了で取り消した要求を使わない");
            await File.WriteAllTextAsync(receipt, "Passed");
            return 0;
        }
        finally
        {
            if (!closed)
            {
                discard = true;
                window.pendingRestart = null;
                if (useDialog)
                {
                    window.modified = false;
                }

                window.Close();
            }
        }
    }
}
