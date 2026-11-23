using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        /// <summary>
        /// 原点設定とは独立した現在の画面情報を読み取ります。
        /// </summary>
        public Value ReadDisplayVariable(WindowsDisplayVariable variable,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return display.Read(variable, cancellationToken);
            }
            catch (Exception failure) when (WindowsOperationErrors.Contains(failure))
            {
                string name = variable switch
                {
                    WindowsDisplayVariable.MouseX => BuiltinVariableNames.GMouseX,
                    WindowsDisplayVariable.MouseY => BuiltinVariableNames.GMouseY,
                    WindowsDisplayVariable.ScreenWidth => BuiltinVariableNames.GScreenW,
                    WindowsDisplayVariable.ScreenHeight => BuiltinVariableNames.GScreenH,
                    WindowsDisplayVariable.ScreenBitsPerPixel => BuiltinVariableNames.GScreenC,
                    _ => throw new ArgumentOutOfRangeException(nameof(variable))
                };
                scriptErrors.GetValue(failure, _ => name);
                throw;
            }
        }
    }
}
