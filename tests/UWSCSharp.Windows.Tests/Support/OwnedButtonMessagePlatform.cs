using System.Text;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 配送後の所有controlを検証する試験だけで、post要求を同期配送します。
// 製品側の非同期配送や完了保証を再現するplatformではありません。
internal class OwnedButtonMessagePlatform(Action<Action>? batch = null) : OwnedListSelectionPlatform
{
    private (nint Window, uint Message, nint Parameter, nint Data)? down;

    public override void Post(nint window, uint message, nint parameter, nint data)
    {
        var name = new StringBuilder(256);
        Native.GetClassNameW(window, name, name.Capacity);
        if (name.ToString().Equals("BUTTON", StringComparison.OrdinalIgnoreCase))
        {
            Native.GetWindowThreadProcessId(window, out uint process);
            Assert.Equal((uint)Environment.ProcessId, process);
            if (batch is not null && message is 0x201 or 0x203 or 0x204 or 0x206)
            {
                Assert.Null(down);
                down = (window, message, parameter, data);
                return;
            }

            if (batch is not null && down is { } pending && message is 0x202 or 0x205)
            {
                Assert.Equal(pending.Window, window);
                down = null;
                // 2つのpostの間へ、別の実入力messageが入り込む同期試験固有の隙間を作りません。
                batch(() =>
                {
                    Native.SendMessageW(window, pending.Message, pending.Parameter, pending.Data);
                    Native.SendMessageW(window, message, parameter, data);
                });
                return;
            }

            Native.SendMessageW(window, message, parameter, data);
        }
        else
        {
            base.Post(window, message, parameter, data);
        }
    }
}
