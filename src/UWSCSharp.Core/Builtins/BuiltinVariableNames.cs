namespace UWSCSharp.Core;

/// <summary>
/// 実行時に公開する特殊変数名です。
/// </summary>
internal static class BuiltinVariableNames
{
    internal const string AllImgX = "ALL_IMG_X";
    internal const string AllImgY = "ALL_IMG_Y";
    internal const string AllItemList = "ALL_ITEM_LIST";
    internal const string AllOleItem = "ALL_OLE_ITEM";
    internal const string AllWinId = "ALL_WIN_ID";
    internal const string GetdirFiles = "GETDIR_FILES";
    internal const string GetAppdataDir = "GET_APPDATA_DIR";
    internal const string GetWinDir = "GET_WIN_DIR";
    internal const string GetSysDir = "GET_SYS_DIR";
    internal const string GetCurDir = "GET_CUR_DIR";
    internal const string GetUwscDir = "GET_UWSC_DIR";
    internal const string GetUwscName = "GET_UWSC_NAME";
    internal const string GetUwscVer = "GET_UWSC_VER";
    internal const string GetUwscPro = "GET_UWSC_PRO";
    internal const string GMouseX = "G_MOUSE_X";
    internal const string GMouseY = "G_MOUSE_Y";
    internal const string GScreenW = "G_SCREEN_W";
    internal const string GScreenH = "G_SCREEN_H";
    internal const string GScreenC = "G_SCREEN_C";
    internal const string GImgX = "G_IMG_X";
    internal const string GImgY = "G_IMG_Y";
    internal const string ParamStr = "PARAM_STR";
    internal const string HotkeyVk = "HOTKEY_VK";
    internal const string HotkeyMod = "HOTKEY_MOD";
    internal const string Result = "RESULT";
    internal const string TryErrmsg = "TRY_ERRMSG";
    internal const string TryErrline = "TRY_ERRLINE";
    internal const string ComErrFlg = "COM_ERR_FLG";
    internal const string EventPrm = "EVENT_PRM";

    internal static bool IsExecutionInformation(string name) =>
        name.Equals(GetAppdataDir, StringComparison.OrdinalIgnoreCase)
        || IsHostContextInformation(name)
        || TryGetDisplayVariable(name, out _);

    internal static bool IsLiveInformation(string name) =>
        name.Equals(GetCurDir, StringComparison.OrdinalIgnoreCase)
        || TryGetDisplayVariable(name, out _);

    internal static bool TryGetDisplayVariable(string name,
        out WindowsDisplayVariable variable)
    {
        if (name.Equals(GMouseX, StringComparison.OrdinalIgnoreCase))
        {
            variable = WindowsDisplayVariable.MouseX;
        }
        else if (name.Equals(GMouseY, StringComparison.OrdinalIgnoreCase))
        {
            variable = WindowsDisplayVariable.MouseY;
        }
        else if (name.Equals(GScreenW, StringComparison.OrdinalIgnoreCase))
        {
            variable = WindowsDisplayVariable.ScreenWidth;
        }
        else if (name.Equals(GScreenH, StringComparison.OrdinalIgnoreCase))
        {
            variable = WindowsDisplayVariable.ScreenHeight;
        }
        else if (name.Equals(GScreenC, StringComparison.OrdinalIgnoreCase))
        {
            variable = WindowsDisplayVariable.ScreenBitsPerPixel;
        }
        else
        {
            variable = default;
            return false;
        }

        return true;
    }

    internal static bool IsHostContextInformation(string name) =>
        name.Equals(GetWinDir, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetSysDir, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetCurDir, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetUwscDir, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetUwscName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetUwscVer, StringComparison.OrdinalIgnoreCase)
        || name.Equals(GetUwscPro, StringComparison.OrdinalIgnoreCase);
}
