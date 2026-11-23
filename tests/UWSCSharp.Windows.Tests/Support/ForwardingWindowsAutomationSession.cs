using UWSCSharp.Core;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 観測用の派生セッションでも、実行先の既定動作へ脱落させず転送します。
/// </summary>
internal abstract class ForwardingWindowsAutomationSession(
    IWindowsAutomationSession inner) : IWindowsAutomationSession
{
    protected IWindowsAutomationSession Inner { get; } = inner;

    public CancellationToken ExecutionStopToken => Inner.ExecutionStopToken;

    public bool CanPause => Inner.CanPause;

    public bool RequiresInputPolling => Inner.RequiresInputPolling;

    public bool RequiresAutomationFailurePolling => Inner.RequiresAutomationFailurePolling;

    public IReadOnlyList<string> AllItems => Inner.AllItems;

    public IReadOnlyList<Value> AllOleItems => Inner.AllOleItems;

    public IReadOnlyList<int> AllWindowIds => Inner.AllWindowIds;

    public WindowsImageResult ImageResult => Inner.ImageResult;

    public virtual IWindowsAutomationSession Fork() => Inner.Fork();

    public IScriptHotkeySource CreateHotkeySource() => Inner.CreateHotkeySource();

    public IWorkerThreadPriority? CreateWorkerThreadPriority() =>
        Inner.CreateWorkerThreadPriority();

    public void ConfigureDialogContext(string name) => Inner.ConfigureDialogContext(name);

    public void ConfigureDialogOption(string name,
        IReadOnlyList<Value> values, CancellationToken token) =>
        Inner.ConfigureDialogOption(name, values, token);

    public Value ReadDisplayVariable(WindowsDisplayVariable variable,
        CancellationToken token) => Inner.ReadDisplayVariable(variable, token);

    public string? GetScriptErrorMessage(string name, Exception error) =>
        Inner.GetScriptErrorMessage(name, error);

    public string? GetObjectScriptErrorMessage(Exception error) =>
        Inner.GetObjectScriptErrorMessage(error);

    public bool IsComError(Exception error) => Inner.IsComError(error);

    public virtual ValueTask SetComEventAsync(Value target,
        string? interfaceName, string? eventName,
        Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>>? callback, CancellationToken token) =>
        Inner.SetComEventAsync(target, interfaceName, eventName, callback, token);

    public WindowsAutomationFailure? TakeAutomationFailure() =>
        Inner.TakeAutomationFailure();

    public void PollInput(CancellationToken token) => Inner.PollInput(token);

    public virtual void ConfigurePause(ScriptPauseControl? control,
        CancellationToken token) => Inner.ConfigurePause(control, token);

    public virtual ValueTask<Value> InvokeAsync(string name,
        IReadOnlyList<Value> arguments, CancellationToken token) =>
        Inner.InvokeAsync(name, arguments, token);

    public ValueTask<Value> InvokeMemberAsync(Value target, string name,
        IReadOnlyList<Value> arguments, Value? assigned,
        CancellationToken token) =>
        Inner.InvokeMemberAsync(target, name, arguments, assigned, token);

    public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(
        Value target, string name,
        IReadOnlyList<WindowsMemberArgument> arguments,
        Value? assigned, CancellationToken token) =>
        Inner.InvokeMemberWithReferencesAsync(target, name, arguments, assigned, token);

    public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(
        Value target, CancellationToken token) => Inner.EnumerateObjectsAsync(target, token);

    public virtual ValueTask DisposeAsync() => Inner.DisposeAsync();
}
