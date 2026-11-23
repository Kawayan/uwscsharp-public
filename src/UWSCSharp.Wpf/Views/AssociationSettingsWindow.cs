using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

// Windowsの既定選択とは別に、現在の配置の候補を管理します。
internal sealed class AssociationSettingsWindow : Window
{
    private readonly string executable;
    internal Button RegisterButton
    {
        get;
    }
    internal Button UnregisterButton
    {
        get;
    }
    internal TextBlock ResultText
    {
        get;
    }

    internal AssociationSettingsWindow(string executable)
    {
        this.executable = executable;
        Title = "スクリプトの関連付け";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(16), Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "現在の配置を .uws の候補として登録または解除します。\n" +
                "既定アプリは Windows の設定で選択してください。\n" +
                "ZIP を移動する前に解除し、移動後に再登録してください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var buttons = new WrapPanel();
        RegisterButton = CreateButton("候補を登録");
        UnregisterButton = CreateButton("登録を解除");
        Button close = CreateButton("閉じる");
        close.IsCancel = true;
        RegisterButton.Click += (_, _) => Apply(register: true);
        UnregisterButton.Click += (_, _) => Apply(register: false);
        buttons.Children.Add(RegisterButton);
        buttons.Children.Add(UnregisterButton);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        ResultText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 10, 2, 0)
        };
        AutomationProperties.SetLiveSetting(ResultText, AutomationLiveSetting.Polite);
        panel.Children.Add(ResultText);
        Content = panel;
    }

    private static Button CreateButton(string text)
        => new()
        {
            Content = text,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(2)
        };

    private void Apply(bool register)
    {
        try
        {
            if (register)
            {
                AssociationRegistration.Register(executable);
            }
            else
            {
                AssociationRegistration.Unregister(executable);
            }

            ResultText.Text = register
                ? "候補を登録しました。既定アプリは Windows の設定で選択してください。"
                : "候補の登録を解除しました。";
        }
        catch (Exception error)
        {
            ResultText.Text = "操作に失敗しました: " + error.Message;
        }
    }
}
