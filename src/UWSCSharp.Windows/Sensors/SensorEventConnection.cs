using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 通知元の借用参照を保持し、通知の停止と参照の解放を所有します。
/// </summary>
internal sealed class SensorEventConnection : IDisposable
{
    private readonly SensorCallbackSink sink;
    private readonly bool manager;
    private readonly ISensorEventNativeApi nativeApi;
    private nint source;

    private SensorEventConnection(SensorCallbackSink sink,
        bool manager, ISensorEventNativeApi nativeApi)
    {
        this.sink = sink;
        this.manager = manager;
        this.nativeApi = nativeApi;
    }

    /// <summary>
    /// 借用元と通知sinkは、呼出し元のCOM apartmentで使用します。
    /// </summary>
    internal static SensorEventConnection Connect(nint source,
        SensorCallbackSink sink, ISensorEventNativeApi? nativeApi = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.VerifyAccess();
        if (source == 0)
        {
            throw new ArgumentException("センサー通知元がNULLです。", nameof(source));
        }

        nativeApi ??= SensorEventNativeApi.Instance;
        var connection = new SensorEventConnection(sink,
            sink is SensorManagerEventSink, nativeApi);
        sink.BeginConnect();
        try
        {
            nativeApi.AddRef(source);
        }
        catch
        {
            sink.Close();
            throw;
        }

        connection.source = source;
        bool attempted = false;
        try
        {
            sink.VerifyOpen();
            nint pointer = nativeApi.GetSinkPointer(sink);
            if (pointer == 0)
            {
                throw new COMException("通知sinkの参照がNULLです。",
                    unchecked((int)0x80004003));
            }

            Exception? registrationFailure = null;
            try
            {
                sink.VerifyOpen();
                attempted = true;
                ThrowForResult(nativeApi.SetSink(source, pointer, connection.manager));
            }
            catch (Exception error)
            {
                registrationFailure = error;
                throw;
            }
            finally
            {
                connection.Release(pointer, registrationFailure);
            }

            sink.VerifyOpen();
            return connection;
        }
        catch (Exception primary)
        {
            sink.Close();
            try
            {
                if (attempted)
                {
                    ThrowForResult(nativeApi.SetSink(source, 0, connection.manager));
                }
            }
            catch (Exception cleanup)
            {
                AddCleanup(primary, cleanup);
            }
            finally
            {
                connection.source = 0;
                connection.Release(source, primary);
            }

            throw;
        }
    }

    internal Exception? TakeCallbackFailure() => sink.TakeFailure();

    public void Dispose()
    {
        sink.VerifyAccess();
        nint released = source;
        if (released == 0)
        {
            return;
        }

        source = 0;
        sink.Close();
        Exception? primary = null;
        try
        {
            ThrowForResult(nativeApi.SetSink(released, 0, manager));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Release(released, primary);
        }
    }

    private void Release(nint instance, Exception? primary)
    {
        try
        {
            nativeApi.Release(instance);
        }
        catch (Exception cleanup) when (primary is not null)
        {
            AddCleanup(primary, cleanup);
        }
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "UWSCSharp.SensorEventCleanup";
        primary.Data[key] = primary.Data[key] is Exception previous
            ? new AggregateException(previous, cleanup) : cleanup;
    }

    private static void ThrowForResult(int result) =>
        SensorNativeErrors.ThrowForResult(result);
}
