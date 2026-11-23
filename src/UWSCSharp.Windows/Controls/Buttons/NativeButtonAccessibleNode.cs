using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// BTN補助の単一MSAA要素を状態・矩形で照合します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeButtonAccessibleNode
{
    internal static bool Read(nint accessible, NativeButtonVariant child,
        Native.Point point, CancellationToken token, out int role, out int state)
        => Match(accessible, child, point, null, token, out role, out state);

    internal static bool Act(nint accessible, NativeButtonVariant child,
        Native.Point point, IListSelectionPlatform platform, CancellationToken token)
        => Match(accessible, child, point, platform, token, out _, out _);

    private static bool Match(nint accessible, NativeButtonVariant child,
        Native.Point point, IListSelectionPlatform? platform,
        CancellationToken token, out int role, out int state)
    {
        role = 0;
        state = 0;
        token.ThrowIfCancellationRequested();
        NativeButtonVariant unwrapped = child;
        while (unwrapped.Type == 0x400c)
        {
            token.ThrowIfCancellationRequested();
            unwrapped = Marshal.PtrToStructure<NativeButtonVariant>(unwrapped.Pointer);
        }

        if (unwrapped.Type == 0)
        {
            return false;
        }

        NativeButtonVariant nativeRole = default;
        NativeButtonVariant nativeState = default;
        Exception? primary = null;
        try
        {
            int result = NativeButtonAccessibleApi.Role(accessible, child, ref nativeRole);
            token.ThrowIfCancellationRequested();
            if (result != 0 || nativeRole.Type != 3
                || nativeRole.Integer is not (43 or 44 or 45))
            {
                return false;
            }

            result = NativeButtonAccessibleApi.State(accessible, child, ref nativeState);
            token.ThrowIfCancellationRequested();
            if (result != 0 || nativeState.Type != 3)
            {
                return false;
            }

            result = NativeButtonAccessibleApi.Location(accessible, child,
                out int left, out int top, out int width, out int height);
            token.ThrowIfCancellationRequested();
            if (result != 0 || point.X < left || point.Y < top
                || WindowsOperationErrors.Add(left, width) < point.X
                || WindowsOperationErrors.Add(top, height) < point.Y)
            {
                return false;
            }

            if (platform is not null)
            {
                // 原本は両操作のHRESULTを成功判定へ使いません。
                NativeButtonAccessibleApi.Select(accessible, 1, child);
                token.ThrowIfCancellationRequested();
                platform.Sleep(20, token);
                for (int index = 0; index < 20; index++)
                {
                    token.ThrowIfCancellationRequested();
                    platform.Sleep(0, token);
                    platform.Pump(token);
                }

                token.ThrowIfCancellationRequested();
                NativeButtonAccessibleApi.DefaultAction(accessible, child);
                token.ThrowIfCancellationRequested();
            }

            role = nativeRole.Integer;
            int mask = role == 44 ? 0x10 : 0x1a;
            state = (nativeState.Integer & mask) != 0 ? 1 : 0;
            if ((nativeState.Integer & 0x20) != 0)
            {
                state = 2;
            }

            return true;
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            cleanup.ClearVariant(ref nativeState);
            cleanup.ClearVariant(ref nativeRole);
            cleanup.Finish();
        }
    }
}
