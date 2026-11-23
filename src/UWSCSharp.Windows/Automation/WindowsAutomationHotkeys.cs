using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    [SupportedOSPlatform("windows")]
    private sealed partial class Session
    {
        /// <summary>
        /// 呼出し側の実行全体が所有し、終了時に解放する通知元を作成します。
        /// </summary>
        public IScriptHotkeySource CreateHotkeySource()
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return scriptHotkeySourceFactory?.Invoke()
                    ?? new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
            }
        }
    }
}
