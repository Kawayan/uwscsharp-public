using System.Diagnostics;

namespace UWSCSharp.Windows;

/// <summary>
/// 最大2接点の開始、移動、終端と取消を実行単位で管理します。
/// </summary>
internal sealed class TouchInputSession(ITouchInputBackend backend)
{
    internal const uint Down = 0x10006;
    internal const uint Update = 0x20006;
    internal const uint Up = 0x40000;
    internal const uint Cancel = Up | 0x8000;
    private readonly List<TouchContact> contacts = [];
    private readonly List<Native.Point> destinations = [];
    private long? lastFrame;
    private bool disposed;

    internal bool HasHeldInput => contacts.Count != 0;

    internal async ValueTask InvokeAsync(int mode, Native.Point position,
        CancellationToken token, Func<int, CancellationToken, Task> delay)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        backend.ValidatePosition(position);
        if (mode != 2)
        {
            if (contacts.Count == 2 || destinations.Count != 0
                || mode == 0 && contacts.Count != 0)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException(
                        "タッチは最大2接点です。保持中はDOWNと対応するUPを組にしてください。"));
            }

            TouchContact[] frame = [.. contacts,
                new((uint)contacts.Count + 1, Down, position)];
            await SendAsync(frame, token, delay).ConfigureAwait(false);
        }

        if (mode == 1 || contacts.Count == 0)
        {
            return;
        }

        if (mode == 0)
        {
            await delay(20, token).ConfigureAwait(false);
        }

        if (destinations.Count < contacts.Count)
        {
            destinations.Add(position);
        }
        else
        {
            // 終端送信が失敗した後のUPでも、同じ接点を再試行できます。
            destinations[^1] = position;
        }

        if (destinations.Count != contacts.Count)
        {
            return;
        }

        Native.Point[] starts = [.. contacts.Select(contact => contact.Position)];
        long steps = 0;
        for (int index = 0; index < starts.Length; index++)
        {
            steps = Math.Max(steps, Math.Max(
                Math.Abs((long)destinations[index].X - starts[index].X),
                Math.Abs((long)destinations[index].Y - starts[index].Y)));
        }

        // 接点ごとの始点と終点を使い、全接点を同じフレームで移動します。
        for (long step = 1; step <= steps; step++)
        {
            var frame = new TouchContact[contacts.Count];
            for (int index = 0; index < frame.Length; index++)
            {
                Native.Point start = starts[index];
                Native.Point end = destinations[index];
                double progress = (double)step / steps;
                var point = new Native.Point(
                    checked((int)Math.Round(start.X + ((long)end.X - start.X) * progress)),
                    checked((int)Math.Round(start.Y + ((long)end.Y - start.Y) * progress)));
                frame[index] = contacts[index] with
                {
                    Flags = Update,
                    Position = point
                };
            }

            await SendAsync(frame, token, delay).ConfigureAwait(false);
        }

        await SendAsync([.. contacts.Select(contact => contact with
        {
            Flags = Up
        })], token, delay).ConfigureAwait(false);
        contacts.Clear();
        destinations.Clear();
    }

    private async ValueTask SendAsync(IReadOnlyList<TouchContact> frame,
        CancellationToken token, Func<int, CancellationToken, Task> delay)
    {
        if (lastFrame is long previous)
        {
            double remaining = 3 - Stopwatch.GetElapsedTime(previous).TotalMilliseconds;
            if (remaining > 0)
            {
                await delay((int)Math.Ceiling(remaining), token).ConfigureAwait(false);
            }
        }

        token.ThrowIfCancellationRequested();
        backend.Send(frame);
        lastFrame = Stopwatch.GetTimestamp();
        // 送信済みフレームだけを記録し、取消時にも最後の座標で解放します。
        contacts.Clear();
        contacts.AddRange(frame.Select(contact => contact with
        {
            Flags = Update
        }));
    }

    internal async ValueTask ReleaseAsync(List<Exception> errors)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (contacts.Count != 0)
            {
                await SendAsync([.. contacts.Select(contact => contact with
                {
                    Flags = Cancel
                })], CancellationToken.None, Task.Delay).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            errors.Add(failure);
        }
        finally
        {
            contacts.Clear();
            destinations.Clear();
            try
            {
                backend.Dispose();
            }
            catch (Exception failure)
            {
                errors.Add(failure);
            }
        }
    }
}
