using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// HRESULTから変換した実際のネイティブ失敗を識別します。
/// </summary>
internal static class SensorNativeErrors
{
    internal static void ThrowForResult(int result)
    {
        if (result >= 0)
        {
            return;
        }

        try
        {
            Marshal.ThrowExceptionForHR(result, new nint(-1));
        }
        catch (Exception failure)
        {
            // CLR例外型だけでは、ネイティブ失敗と処理上の不具合を区別できません。
            WindowsOperationErrors.Record(failure);
            throw;
        }
    }
}
