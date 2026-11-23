using System.ComponentModel;
using System.Text.Json;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

// 外側の監視が開始通知を出した後だけ、ゲストで入力禁止APIを検証します。
internal static class InputOwnershipProbe
{
    internal static async Task RunAsync(string receipt, bool hold = false,
        bool exitOwnerThread = false)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力禁止の検証はSandbox専用です。");
        }

        using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(receipt + ".go"))
        {
            await Task.Delay(10, readyDeadline.Token);
        }

        if (exitOwnerThread)
        {
            try
            {
                await RunOwnerThreadExitAsync(receipt);
            }
            catch (Exception error)
            {
                await PublishReceiptAsync(receipt, new
                {
                    failure = error.ToString()
                });
            }

            return;
        }

        bool blocked = false;
        bool sent = false;
        bool released = false;
        bool otherBlockRejected = false;
        bool otherSendRejected = false;
        bool otherKeyDown = false;
        bool ownerKeyDown = false;
        bool ownerKeyUp = false;
        bool initialKeyUp = false;
        bool nextOwnerKeyDown = false;
        bool reacquired = false;
        string? failure = null;
        try
        {
            var observed = new ObservedBackend();
            await using var owner = new InputOwnershipDispatcher(observed);
            await owner.SetBlockedAsync(true);
            blocked = true;
            try
            {
                if (hold)
                {
                    await WriteReceiptAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }

                await owner.SendAsync(Key(true));
                initialKeyUp = observed.Up;
                (otherBlockRejected, otherSendRejected, otherKeyDown) = await Task.Run(ProbeContender);
                try
                {
                    await owner.SendAsync(Key(false));
                }
                finally
                {
                    await owner.SendAsync(Key(true));
                }
                ownerKeyDown = observed.Down;
                ownerKeyUp = observed.Up;
                sent = true;
            }
            finally
            {
                await owner.SetBlockedAsync(false);
                released = true;
            }

            await Task.Run(() =>
            {
                var next = new NativeInputOwnershipBackend();
                next.SetBlocked(true);
                try
                {
                    try
                    {
                        next.Send(Key(false));
                        nextOwnerKeyDown = ReadKeyAfterSettling();
                    }
                    finally
                    {
                        next.Send(Key(true));
                    }
                }
                finally
                {
                    next.SetBlocked(false);
                }
            });
            reacquired = true;
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }

        await WriteReceiptAsync();

        Task WriteReceiptAsync() =>
            PublishReceiptAsync(receipt, new
            {
                blocked,
                sent,
                released,
                otherBlockRejected,
                otherSendRejected,
                initialKeyUp,
                otherKeyDown,
                ownerKeyDown,
                ownerKeyUp,
                nextOwnerKeyDown,
                reacquired,
                failure
            });
    }

    private static async Task RunOwnerThreadExitAsync(string receipt)
    {
        var acquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                new NativeInputOwnershipBackend().SetBlocked(true);
                // 明示解除を呼ばずに、入力禁止を取得したスレッドを終えます。
                acquired.SetResult();
            }
            catch (Exception error)
            {
                acquired.SetException(error);
            }
        })
        {
            IsBackground = true
        };
        owner.Start();
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (!owner.Join(TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("入力禁止の所有スレッドが終了しませんでした。");
        }

        var next = new NativeInputOwnershipBackend();
        next.SetBlocked(true);
        next.SetBlocked(false);
        await PublishReceiptAsync(receipt, new
        {
            blocked = true,
            ownerThreadExited = true,
            releasedByOwner = false,
            reacquired = true,
            failure = (string?)null
        });
    }

    private static async Task PublishReceiptAsync<T>(string receipt, T result)
    {
        string pending = receipt + ".pending";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(result));
        File.Move(pending, receipt);
    }

    private static (bool BlockRejected, bool SendRejected, bool KeyDown) ProbeContender()
    {
        var other = new NativeInputOwnershipBackend();
        bool blockRejected = false;
        bool sendRejected = false;
        bool acquired = false;
        bool keyDown = false;
        try
        {
            other.SetBlocked(true);
            acquired = true;
        }
        catch (Win32Exception)
        {
            blockRejected = true;
        }

        if (acquired)
        {
            // 想定外に取得できた場合も、このスレッドで解除します。
            other.SetBlocked(false);
        }

        try
        {
            other.Send(Key(false));
            keyDown = ReadKeyAfterSettling();
        }
        catch (Win32Exception)
        {
            sendRejected = true;
        }
        finally
        {
            try
            {
                other.Send(Key(true));
            }
            catch (Win32Exception) when (sendRejected)
            {
                // DOWNも拒否された場合のUP拒否は、同じ観測結果として扱います。
            }
        }

        return (blockRejected, sendRejected, keyDown);
    }

    private static bool ReadKeyAfterSettling()
    {
        Thread.Sleep(50);
        return (new NativeKeyStateBackend().ReadAsync(135) & 0x8000) != 0;
    }

    private sealed class ObservedBackend : IInputOwnershipBackend
    {
        private readonly NativeInputOwnershipBackend native = new();
        internal bool Down
        {
            get; private set;
        }
        internal bool Up
        {
            get; private set;
        }

        public void SetBlocked(bool blocked) => native.SetBlocked(blocked);

        public void Send(Native.Input input)
        {
            native.Send(input);
            bool down = ReadKeyAfterSettling();
            if ((input.Data.Keyboard.Flags & 2) == 0)
            {
                Down = down;
            }
            else
            {
                Up = !down;
            }
        }
    }

    private static Native.Input Key(bool up) => new()
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput
            {
                Vk = 135,
                Flags = up ? 2u : 0
            }
        }
    };
}
