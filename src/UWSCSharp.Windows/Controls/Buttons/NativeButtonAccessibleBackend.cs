namespace UWSCSharp.Windows;

internal interface IButtonAccessibleOperations : IDisposable
{
    bool Matches(nint root, CancellationToken token);

    int Read(nint root, nint target, CancellationToken token,
        ref int role, out bool accessibleAction);

    bool Act(nint root, nint target, CancellationToken token);
}

/// <summary>
/// BTN補助の座標wrapperとraw MSAAを接続します。
/// </summary>
internal sealed class NativeButtonAccessibleBackend : IButtonAccessibleOperations
{
    private readonly object gate = new();
    private readonly IButtonAccessibleSource source;
    private readonly IListSelectionPlatform windows;
    private readonly NativeButtonPointWindow points;
    private readonly Func<nint, int> extendedStyle;
    private readonly Func<nint, nint> parent;
    private readonly IButtonRouteGatePlatform route;
    private bool disposed;

    internal NativeButtonAccessibleBackend(IListSelectionPlatform windows,
        NativeButtonPointWindow points, IButtonAccessibleSource? source = null,
        Func<nint, int>? extendedStyle = null, Func<nint, nint>? parent = null,
        IButtonRouteGatePlatform? route = null)
    {
        this.windows = windows;
        this.points = points;
        this.source = source ?? new NativeButtonAccessibleSource();
        this.extendedStyle = extendedStyle ?? (window => Native.GetWindowLongW(window, -20));
        this.parent = parent ?? Native.GetParent;
        this.route = route ?? new NativeButtonRouteGatePlatform();
    }

    public bool Matches(nint root, CancellationToken token)
        => NativeButtonRouteGate.Matches(root, route, token);

    public int Read(nint root, nint target, CancellationToken token,
        ref int role, out bool accessibleAction)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var session = new NativeButtonAccessibleSession(source);
            var platform = new Platform(this, session);
            Exception? primary = null;
            try
            {
                return NativeButtonAccessibleRoute.Read(root, target, platform,
                    token, ref role, out accessibleAction);
            }
            catch (Exception failure)
            {
                primary = failure;
                throw;
            }
            finally
            {
                session.Dispose(primary);
            }
        }
    }

    public bool Act(nint root, nint target, CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var session = new NativeButtonAccessibleSession(source);
            Exception? primary = null;
            try
            {
                return NativeButtonAccessibleRoute.Act(root, target,
                    new Platform(this, session), token);
            }
            catch (Exception failure)
            {
                primary = failure;
                throw;
            }
            finally
            {
                session.Dispose(primary);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                (source as IDisposable)?.Dispose();
            }
        }
    }

    private sealed class Platform(NativeButtonAccessibleBackend owner,
        NativeButtonAccessibleSession session) : IButtonAccessibleRoutePlatform
    {
        public bool Available() => owner.source.Available();

        public Native.Rect Bounds(nint window) => owner.windows.Bounds(window);

        public int ExtendedStyle(nint window) => owner.extendedStyle(window);

        public nint Parent(nint window) => owner.parent(window);

        public nint Foreground() => owner.windows.ForegroundWindow();

        public bool Iconic(nint window) => owner.windows.IsIconic(window);

        public int Metric(int index) => owner.windows.Metric(index);

        public nint PointWindow(Native.Point point, CancellationToken token)
            => owner.points.Find(point, token);

        public bool ReadPoint(Native.Point point, ref int role, out int state,
            CancellationToken token)
            => session.ReadPoint(point, ref role, out state, token);

        public void ReadRoot(nint root, Native.Point point, ref int role,
            out int state, CancellationToken token)
            => session.ReadRoot(root, point, ref role, out state, token);

        public bool ActRoot(nint root, Native.Point point, CancellationToken token)
            => session.ActRoot(root, point, owner.windows, token);
    }
}
