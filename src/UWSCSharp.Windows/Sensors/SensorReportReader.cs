using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// センサーreportを呼出し中だけ保持し、取得値をコピーします。
/// </summary>
internal static class SensorReportReader
{
    /// <summary>
    /// 借用reportを使い、なければセンサーからreportを取得します。
    /// </summary>
    /// <remarks>借用ポインターは参照取得まで有効で、所有COM apartment内で呼び出してください。</remarks>
    internal static object Read(
        nint sensor,
        nint cachedReport,
        SensorDataField field,
        ISensorReportNativeApi? nativeApi = null,
        ISensorPropertyValueNativeApi? propertyApi = null,
        bool locationDefaults = false)
    {
        if (!Enum.IsDefined(field.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        if (field.Kind == SensorDataValueKind.EmptyString)
        {
            return string.Empty;
        }

        if (field.Key is not SensorPropertyKey key)
        {
            throw new ArgumentException("取得項目のPROPERTYKEYがありません。", nameof(field));
        }

        nativeApi ??= SensorReportNativeApi.Instance;
        propertyApi ??= SensorPropertyValueNativeApi.Instance;
        if (cachedReport != 0 && !locationDefaults)
        {
            nativeApi.AddRef(cachedReport);
            Exception? failure = null;
            try
            {
                return ReadReport(cachedReport, key, field.Kind, nativeApi, propertyApi);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                Release(nativeApi, cachedReport, failure);
            }
        }

        if (sensor == 0)
        {
            throw new ArgumentException("report取得元のセンサーがNULLです。", nameof(sensor));
        }

        nativeApi.AddRef(sensor);
        Exception? sensorFailure = null;
        try
        {
            nint report = 0;
            Exception? reportFailure = null;
            try
            {
                int result = nativeApi.GetData(sensor, out report);
                if (result < 0 && locationDefaults)
                {
                    // 原版の通常位置getterは、GetData失敗時に基底の既定値へ戻ります。
                    return field.Kind switch
                    {
                        SensorDataValueKind.Numeric => SensorNumericValue.MissingValue,
                        SensorDataValueKind.String => string.Empty,
                        _ => throw new InvalidOperationException("位置項目の取得種別が不正です。")
                    };
                }

                SensorNativeErrors.ThrowForResult(result);
                if (report == 0)
                {
                    throw new COMException("取得したセンサーreportがNULLです。",
                        unchecked((int)0x80004003));
                }

                return ReadReport(report, key, field.Kind, nativeApi, propertyApi);
            }
            catch (Exception error)
            {
                reportFailure = error;
                throw;
            }
            finally
            {
                if (report != 0)
                {
                    Release(nativeApi, report, reportFailure);
                }
            }
        }
        catch (Exception error)
        {
            sensorFailure = error;
            throw;
        }
        finally
        {
            Release(nativeApi, sensor, sensorFailure);
        }
    }

    private static void Release(ISensorReportNativeApi api, nint instance,
        Exception? previousFailure)
    {
        try
        {
            api.Release(instance);
        }
        catch (Exception error) when (previousFailure is not null)
        {
            throw new AggregateException(previousFailure, error);
        }
    }

    private static object ReadReport(
        nint report,
        SensorPropertyKey key,
        SensorDataValueKind kind,
        ISensorReportNativeApi nativeApi,
        ISensorPropertyValueNativeApi propertyApi) =>
        SensorDataValue.Read(kind, propertyValue =>
        {
            if (nativeApi.GetSensorValue(report, in key, propertyValue) < 0)
            {
                // 失敗時の部分値も解放し、原版と同じ空のPROPVARIANTへ戻します。
                propertyApi.Clear(propertyValue);
                int size = IntPtr.Size == 8 ? 24 : 16;
                for (int offset = 0; offset < size; offset += 8)
                {
                    Marshal.WriteInt64(propertyValue, offset, 0);
                }
            }
        }, propertyApi);
}
