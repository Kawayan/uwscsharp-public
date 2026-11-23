namespace UWSCSharp.Windows;

internal delegate nint ModifierHookProcedure(int code, nuint message, nint data);

/// <summary>
/// 修飾子監視で使用するフックとキー状態のOS操作です。
/// </summary>
internal interface IModifierHookBackend
{
    nint Install(ModifierHookProcedure procedure);

    bool Remove(nint hook);

    bool IsPressed(int key);

    nint Next(int code, nuint message, nint data);
}
