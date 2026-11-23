using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 登録IDから解決した対象を保持し、別の対象へ結び直さず検証します。
/// </summary>
internal sealed class SelectiveInputTarget
{
    private readonly WindowIdentity identity;
    private readonly IWindowIdentityPlatform platform;
    private readonly WindowRegistrationSnapshot? registration;

    private SelectiveInputTarget(int windowId, nint handle,
        WindowIdentity identity, IWindowIdentityPlatform platform,
        WindowRegistrationSnapshot? registration = null)
    {
        WindowId = windowId;
        Handle = handle;
        this.identity = identity;
        this.platform = platform;
        this.registration = registration;
    }

    internal int WindowId
    {
        get;
    }

    internal nint Handle
    {
        get;
    }

    internal static SelectiveInputTarget Capture(int windowId,
        Func<int, nint> resolve, IWindowIdentityPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(platform);
        if (windowId < 0)
        {
            throw Error(windowId);
        }

        if (windowId == 0)
        {
            // 一般のID=0の前面解決を呼ばず、全体指定を保持します。
            return new(0, 0, default, platform);
        }

        nint handle = resolve(windowId);
        if (handle == 0 || platform.ReadIdentity(handle) is not { } identity
            || identity.ProcessId == 0 || identity.ThreadId == 0)
        {
            throw Error(windowId);
        }

        return new(windowId, handle, identity, platform);
    }

    internal static SelectiveInputTarget CaptureRegistered(int windowId,
        WindowRegistrationTable table)
    {
        if (windowId == 0)
        {
            return new(0, 0, default, table.Platform);
        }

        WindowRegistrationSnapshot? entry = table.GetRegistration(windowId);
        if (entry is null || !entry.Validate())
        {
            throw Error(windowId);
        }

        return new(windowId, entry.Handle, entry.Identity!.Value,
            table.Platform, entry);
    }

    internal void Validate()
    {
        if (WindowId != 0 && (registration is not null
            ? !registration.Validate() : platform.ReadIdentity(Handle) != identity))
        {
            throw Error(WindowId);
        }
    }

    internal bool Matches(SelectiveInputTarget other) =>
        Handle == other.Handle && identity == other.identity
        && ReferenceEquals(registration, other.registration);

    private static InvalidOperationException Error(int windowId) =>
        WindowsOperationErrors.Record(new InvalidOperationException(
            $"{BuiltinFunctionNames.LockHardEx}: ウィンドウID {windowId} は無効または失効しています。"));
}
