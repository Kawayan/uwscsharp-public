using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace UWSCSharp.UiaFixture;

internal static partial class Program
{
    private static void RunGetStrTarget(string receipt, string kind)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力禁止の検証対象はSandbox専用です。");
        }
        var app = new Application();
        FrameworkElement target = kind == "edit"
            ? new TextBox { Text = "入力禁止中の本文", Width = 180, Height = 30 }
            : new TextBlock { Text = "入力禁止中の本文", Width = 180, Height = 30 };
        object content = kind == "status"
            ? new StatusBar { Items = { target } } : target;
        var window = new Window
        {
            Title = "GETSTR入力禁止検証",
            Left = 300,
            Top = 250,
            Width = 360,
            Height = 200,
            ShowActivated = false,
            Content = content
        };
        window.ContentRendered += (_, _) =>
        {
            FrameworkElement selected = content is StatusBar status
                ? (FrameworkElement)status.ItemContainerGenerator.ContainerFromItem(target) : target;
            Point origin = selected.PointToScreen(new Point());
            Point end = selected.PointToScreen(new Point(selected.ActualWidth, selected.ActualHeight));
            File.WriteAllText(receipt + ".pending", JsonSerializer.Serialize(new
            {
                window = (long)new WindowInteropHelper(window).Handle,
                left = (int)origin.X,
                top = (int)origin.Y,
                right = (int)end.X,
                bottom = (int)end.Y
            }));
            File.Move(receipt + ".pending", receipt);
        };
        app.Run(window);
    }
}
