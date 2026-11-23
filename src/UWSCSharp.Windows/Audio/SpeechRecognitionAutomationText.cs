using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識語のCY・DATE・BOOL・DECIMALをAutomation APIで文字列化します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class SpeechRecognitionAutomationText
{
    private const uint UserDefaultLocale = 0x400;

    internal static bool TryFormat(Value value, out string text)
    {
        object? scalar = value.GetUnchangedNativeScalar();
        int status;
        nint result;
        switch (value.NativeVariantType)
        {
            case 6 when scalar is decimal currency:
                status = VarBstrFromCy(decimal.ToOACurrency(currency),
                    UserDefaultLocale, 0, out result);
                break;
            case 7 when scalar is DateTime or double:
                double date = scalar is DateTime time
                    ? time.ToOADate() : (double)scalar;
                status = VarBstrFromDate(date, UserDefaultLocale, 0, out result);
                break;
            case 11 when scalar is bool flag:
                status = VarBstrFromBool(flag ? (short)-1 : (short)0,
                    UserDefaultLocale, 0, out result);
                break;
            case 14 when scalar is decimal number:
                text = FormatDecimal(number);
                return true;
            default:
                text = string.Empty;
                return false;
        }

        try
        {
            // 別のCOM操作のIErrorInfoを、この変換の失敗へ混入させません。
            CheckStatus(status);
            text = result == 0 ? string.Empty : Marshal.PtrToStringBSTR(result);
            return true;
        }
        finally
        {
            Marshal.FreeBSTR(result);
        }
    }

    private static string FormatDecimal(decimal number)
    {
        // VARIANTと返却BSTRの所有はCLRのAutomation marshallerへ渡します。
        object source = number;
        int status = VariantChangeTypeEx(out object converted, ref source,
            UserDefaultLocale, 0, 8);
        CheckStatus(status);
        return converted as string
            ?? throw new InvalidDataException("認識語の変換結果が文字列ではありません。");
    }

    private static void CheckStatus(int status)
    {
        if (status < 0)
        {
            // HRESULT由来の失敗だけを登録し、別操作のIErrorInfoは参照しません。
            throw ComOperationErrors.Record(Marshal.GetExceptionForHR(status, new nint(-1))!);
        }
    }
}
