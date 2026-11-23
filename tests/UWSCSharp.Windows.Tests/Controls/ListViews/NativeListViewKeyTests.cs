using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewKeyTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int key in new[] { 13, 32 })
        {
            foreach (bool back in new[] { false, true })
            {
                foreach (string outcome in new[] { "success", "cancel", "throw" })
                {
                    yield return [key, back, outcome];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EnterAndSpaceUsePreparedTimedSequence(int key, bool back, string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "item");
        using var cancel = new CancellationTokenSource();
        var platform = new NativeTreeSpaceTests.Platform(cancel, outcome, key)
        {
            Root = fixture.Root,
            Target = view,
        };
        int points = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, _, pointer) =>
        {
            if (message == 0x1010)
            {
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                platform.Active = ++points == 3;
                return 0;
            }

            if (message == 0x102c && pointer == 0xf000)
            {
                // Spaceの状態変化だけをモデル化し、後確認の現契約を分離します。
                return key == 13 ? 0 : platform.Events.Contains("down") ? 0x2000 : 0x1000;
            }

            return null;
        });
        (int Matches, bool Found, bool Success) Click() => NativeListViewControl.Click(
            view, "item", 1, true, cancel.Token, platform: platform, back: back);
        if (outcome == "cancel")
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Click());
        }
        else if (outcome == "throw")
        {
            Assert.Equal((1, true, false), Click());
        }
        else
        {
            Assert.Equal((1, true, true), Click());
        }

        List<string> expected = back ? [] : ["attach", "foreground", "focus", "detach"];
        expected.AddRange(["priority256", "down", "up"]);
        if (outcome == "success")
        {
            expected.AddRange(["target-null", "root-null"]);
        }

        expected.Add("priority32");
        Assert.Equal(expected, platform.Events);
        Assert.Equal(3, points);
        Assert.Null(fixture.CallbackFailure);
    }
}
