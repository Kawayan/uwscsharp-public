using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;

namespace UWSCSharp.UiaFixture;

internal static partial class Program
{
    private sealed class SliderProbe : FrameworkElement
    {
        internal double Position = 37;
        internal bool ReadOnly;
        internal bool Compensate;
        internal int Failure;
        internal int ReadFailure;
        internal readonly List<double> Calls = [];

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(SliderProbe owner)
            : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() =>
                AutomationControlType.Slider;

            protected override string GetClassNameCore() => "SliderProbe";

            protected override bool IsControlElementCore() => true;

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this
                    : base.GetPattern(patternInterface);

            public bool IsReadOnly => owner.ReadOnly;

            public double LargeChange => 10;

            public double Maximum => 120;

            public double Minimum => -20;

            public double SmallChange => 1;

            public double Value => owner.ReadFailure == 0 ? owner.Position
                : throw new COMException("専用slider providerの取得失敗です。",
                    owner.ReadFailure);

            public void SetValue(double value)
            {
                owner.Calls.Add(value);
                if (owner.Failure != 0)
                {
                    throw new COMException("専用slider providerの失敗です。", owner.Failure);
                }

                if (owner.ReadOnly || !owner.IsEnabled)
                {
                    throw new InvalidOperationException("変更できません。");
                }

                if (value < Minimum || value > Maximum)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (!owner.Compensate)
                {
                    owner.Position = value;
                }
            }
        }
    }

    private sealed class ScrollProbe : FrameworkElement
    {
        internal double Horizontal = 24;
        internal double Vertical = 42;
        internal readonly List<string> Calls = [];

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(ScrollProbe owner)
            : FrameworkElementAutomationPeer(owner), IScrollProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() =>
                AutomationControlType.Pane;

            protected override string GetClassNameCore() => "ScrollProbe";

            protected override bool IsControlElementCore() => true;

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Scroll ? this
                    : base.GetPattern(patternInterface);

            public bool HorizontallyScrollable => true;

            public bool VerticallyScrollable => true;

            public double HorizontalScrollPercent => owner.Horizontal;

            public double VerticalScrollPercent => owner.Vertical;

            public double HorizontalViewSize => 50;

            public double VerticalViewSize => 25;

            public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount) =>
                throw new InvalidOperationException("Scrollは使用しません。");

            public void SetScrollPercent(double horizontalPercent, double verticalPercent)
            {
                owner.Calls.Add(FormattableString.Invariant($"{horizontalPercent}:{verticalPercent}"));
                if ((horizontalPercent != -1 && horizontalPercent is < 0 or > 100)
                    || (verticalPercent != -1 && verticalPercent is < 0 or > 100))
                {
                    throw new ArgumentOutOfRangeException(nameof(horizontalPercent));
                }

                if (horizontalPercent != -1)
                {
                    owner.Horizontal = horizontalPercent;
                }

                if (verticalPercent != -1)
                {
                    owner.Vertical = verticalPercent;
                }
            }
        }
    }

    private static void RunSliders()
    {
        var application = new Application();
        var first = new SliderProbe { Width = 240, Height = 40 };
        var disabled = new SliderProbe { Width = 240, Height = 40, IsEnabled = false, Position = 61 };
        var vertical = new SliderProbe { Width = 40, Height = 120, Position = 81 };
        var pane = new ScrollProbe { Width = 240, Height = 100 };
        var stack = new StackPanel();
        foreach (FrameworkElement element in new FrameworkElement[] { first, disabled, vertical, pane })
        {
            stack.Children.Add(element);
        }

        var window = new Window
        {
            Title = "UWSCSharp UIA slider fixture",
            Width = 420,
            Height = 480,
            ShowActivated = false,
            Content = stack
        };
        bool announced = false;
        window.ContentRendered += (_, _) =>
        {
            if (!announced)
            {
                announced = true;
                Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
                Console.Out.Flush();
            }
        };
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is string command)
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    string reply = "OK";
                    switch (command)
                    {
                        case "state":
                            reply = FormattableString.Invariant(
                                $"{first.Position},{disabled.Position},{vertical.Position},{pane.Horizontal},{pane.Vertical}")
                                + ";" + string.Join(",", first.Calls.Select(value => value.ToString(CultureInfo.InvariantCulture)))
                                + ";" + string.Join(",", pane.Calls);
                            break;
                        case "geometry":
                            Point origin = stack.PointToScreen(new Point(0, 0));
                            reply = string.Join(";", new FrameworkElement[]
                            {
                                first, disabled, vertical, pane
                            }.Select(element =>
                            {
                                if (PresentationSource.FromVisual(element) is null)
                                {
                                    return "0,0,0,0";
                                }

                                Point start = element.PointToScreen(new Point(0, 0));
                                Point end = element.PointToScreen(new Point(
                                    element.ActualWidth, element.ActualHeight));
                                return FormattableString.Invariant(
                                    $"{(int)start.X - (int)origin.X},{(int)start.Y - (int)origin.Y},{(int)end.X - (int)origin.X},{(int)end.Y - (int)origin.Y}");
                            }));
                            break;
                        case "readonly":
                            first.ReadOnly = true;
                            break;
                        case "compensate":
                            first.Compensate = true;
                            break;
                        case "fault":
                            first.Failure = unchecked((int)0x80004005);
                            break;
                        case "fraction":
                            first.Position = 37.5;
                            break;
                        case "negative-fraction":
                            first.Position = -37.5;
                            break;
                        case "nonfinite":
                            first.Position = double.NaN;
                            break;
                        case "oversized":
                            first.Position = 2147483648d;
                            break;
                        case "read-fault":
                            first.ReadFailure = unchecked((int)0x80004005);
                            break;
                        case "pane":
                            stack.Children.Clear();
                            stack.Children.Add(pane);
                            window.UpdateLayout();
                            break;
                        case "exit":
                            window.Close();
                            return;
                    }

                    Console.WriteLine(reply);
                    Console.Out.Flush();
                });
            }

            await window.Dispatcher.InvokeAsync(window.Close);
        });
        application.Run(window);
    }
}
