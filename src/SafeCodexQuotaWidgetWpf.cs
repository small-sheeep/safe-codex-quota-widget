using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SafeCodexQuotaWidget
{
    internal static class WpfProgram
    {
        private static Mutex _singleInstance;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
                return RunProbe();

            bool createdNew;
            _singleInstance = new Mutex(true, @"Local\SafeCodexQuotaWidget", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Codex 额度悬浮窗已经在运行。", "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            NativeMethods.EnableHighDpiRendering();
            Application application = new Application();
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            int result = application.Run(new WpfQuotaWindow());
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
            return result;
        }

        private static int RunProbe()
        {
            try
            {
                QuotaSnapshot snapshot = CodexQuotaClient.ReadQuotaAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine("CodexPath=" + snapshot.CodexPath);
                Console.WriteLine("Publisher=" + snapshot.Publisher);
                Console.WriteLine("Plan=" + snapshot.PlanType);
                Console.WriteLine("PrimaryRemaining=" + FormatProbeWindow(snapshot.Primary));
                Console.WriteLine("SecondaryRemaining=" + FormatProbeWindow(snapshot.Secondary));
                Console.WriteLine("ExtraKind=" + (snapshot.Extra == null ? "n/a" : snapshot.Extra.Kind.ToString()));
                Console.WriteLine("ExtraLimitId=" + (snapshot.Extra == null ? "n/a" : snapshot.Extra.LimitId));
                Console.WriteLine("ExtraLimitName=" + (snapshot.Extra == null ? "n/a" : snapshot.Extra.LimitName));
                Console.WriteLine("ExtraRemaining=" + FormatProbeWindow(snapshot.Extra == null ? null : snapshot.Extra.Window));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static string FormatProbeWindow(QuotaWindow value)
        {
            if (value == null) return "n/a";
            return value.RemainingPercent + "%" +
                   (value.WindowDurationMinutes.HasValue ? ", duration " + value.WindowDurationMinutes.Value + " min" : "") +
                   (value.ResetsAt.HasValue ? ", resets " + value.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
        }
    }

    internal enum PointerTapAction
    {
        None,
        Expand,
        Collapse
    }

    internal static class PointerGestureRules
    {
        public static bool ShouldStartDragOnRelease(bool alreadyDragging, double heldMilliseconds,
            double distance, int holdMilliseconds, double moveTolerance)
        {
            return !alreadyDragging &&
                   (heldMilliseconds >= holdMilliseconds || distance > moveTolerance);
        }

        public static PointerTapAction GetTapAction(bool wasDragging, bool isExpanded,
            double heldMilliseconds, double distance, int holdMilliseconds, double moveTolerance)
        {
            if (wasDragging || heldMilliseconds >= holdMilliseconds || distance > moveTolerance)
                return PointerTapAction.None;
            return isExpanded ? PointerTapAction.Collapse : PointerTapAction.Expand;
        }
    }

    internal static class HoverActivationRules
    {
        public static bool IsActive(bool isExpanded, bool gaugeMouseOver, bool frameMouseOver)
        {
            return isExpanded ? gaugeMouseOver : gaugeMouseOver || frameMouseOver;
        }
    }

    internal sealed class WpfQuotaWindow : Window
    {
        private const double OrbSize = 100;
        private const double HostWidth = 556;
        private const double HostHeight = 202;
        private const double LeftExpandedFrameLeft = 221;
        private const double RightExpandedFrameLeft = 4;
        private const double ExpandedFrameTop = 10;
        private const double ExpandedFrameWidth = 324;
        private const double ExpandedHeightWithExtra = 183;
        private const double ExpandedHeightCompact = 142;
        private const double FixedGaugeLeft = 221;
        private const double ExpandedGaugeTop = 44;
        private const double ScreenEdgeMargin = 8;
        private const int HoldMilliseconds = 220;
        private const double ClickMoveTolerance = 4;
        private static readonly int[] AutomaticRetryDelaysMilliseconds = { 1000, 1500, 2000 };

        private readonly Canvas _windowCanvas;
        private readonly AnimatedBorder _frame;
        private readonly Canvas _expandedLayer;
        private readonly TranslateTransform _expandedLayerOffset;
        private readonly TextBlock _stateText;
        private readonly TextBlock _primaryCaption;
        private readonly TextBlock _primaryValue;
        private readonly TextBlock _secondaryCaption;
        private readonly TextBlock _secondaryValue;
        private readonly Border _primaryCard;
        private readonly Border _secondaryCard;
        private readonly Border _planCard;
        private readonly TextBlock _planValue;
        private readonly TextBlock _statusText;
        private readonly WpfQuotaGauge _gauge;
        private readonly Border _expandedHoverSheen;
        private readonly TranslateTransform _expandedHoverSheenOffset;
        private readonly GradientStop _expandedHoverEdgeStart;
        private readonly GradientStop _expandedHoverAccentStart;
        private readonly GradientStop _expandedHoverAccentEnd;
        private readonly GradientStop _expandedHoverEdgeEnd;
        private readonly GradientStop _frameBorderStart;
        private readonly GradientStop _frameBorderEnd;
        private readonly GradientStop _backgroundMiddle;
        private readonly GradientStop _backgroundEnd;
        private readonly SolidColorBrush _statusDotFill;
        private readonly SolidColorBrush _statusDotStroke;
        private readonly Button _pinButton;
        private readonly Button _refreshButton;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _holdTimer;
        private readonly DispatcherTimer _dragTimer;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private HwndSource _hwndSource;
        private Color _accent = Color.FromRgb(95, 218, 167);
        private Color _accentSecondary = Color.FromRgb(94, 191, 255);
        private bool _refreshing;
        private bool _hasDisplayedQuota;
        private bool _disposed;
        private bool _isExpanded;
        private bool _isAnimating;
        private bool _pointerDown;
        private bool _dragging;
        private bool _expandedHoverActive;
        private bool _expandsLeft = true;
        private bool _hasExtraQuota;
        private bool? _pendingExtraLayout;
        private bool _hasPendingAccent;
        private int? _pendingAccentRemaining;
        private TaskCompletionSource<bool> _activeAnimationCompletion;
        private Point _pressScreen;
        private Point _dragScreen;
        private DateTime _pressTime;

        public WpfQuotaWindow()
        {
            Title = "安全版 Codex 额度";
            ImageSource applicationIcon = LoadApplicationIcon();
            if (applicationIcon != null) Icon = applicationIcon;
            Width = HostWidth;
            Height = HostHeight;
            MinWidth = HostWidth;
            MaxWidth = HostWidth;
            MinHeight = HostHeight;
            MaxHeight = HostHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            _windowCanvas = new Canvas();
            _windowCanvas.Width = HostWidth;
            _windowCanvas.Height = HostHeight;
            Content = _windowCanvas;

            _frame = new AnimatedBorder();
            _frame.Width = OrbSize;
            _frame.Height = OrbSize;
            _frame.AnimatedRadius = OrbSize / 2;
            _frame.BorderThickness = new Thickness(1);
            LinearGradientBrush frameBorder = CreateBorderBrush(_accent, _accentSecondary);
            _frameBorderStart = frameBorder.GradientStops[0];
            _frameBorderEnd = frameBorder.GradientStops[1];
            _frame.BorderBrush = frameBorder;
            LinearGradientBrush windowBackground = CreateWindowBackground();
            _backgroundMiddle = windowBackground.GradientStops[1];
            _backgroundEnd = windowBackground.GradientStops[2];
            _frame.Background = windowBackground;
            _frame.SnapsToDevicePixels = false;
            _frame.ClipToBounds = true;

            Canvas canvas = new Canvas();
            canvas.Width = ExpandedFrameWidth;
            canvas.Height = ExpandedHeightWithExtra;
            _frame.Child = canvas;
            Canvas.SetLeft(_frame, FixedGaugeLeft);
            Canvas.SetTop(_frame, ExpandedGaugeTop);
            _windowCanvas.Children.Add(_frame);

            _expandedHoverSheen = new Border();
            _expandedHoverSheen.Width = ExpandedFrameWidth;
            _expandedHoverSheen.Height = ExpandedHeightWithExtra;
            _expandedHoverSheen.CornerRadius = new CornerRadius(22);
            _expandedHoverSheen.Opacity = 0;
            _expandedHoverSheen.IsHitTestVisible = false;
            LinearGradientBrush hoverSheen = new LinearGradientBrush();
            hoverSheen.StartPoint = new Point(0, 0);
            hoverSheen.EndPoint = new Point(1, 1);
            _expandedHoverEdgeStart = new GradientStop(
                Color.FromArgb(18, _accent.R, _accent.G, _accent.B), 0);
            _expandedHoverAccentStart = new GradientStop(Color.FromArgb(82, _accent.R, _accent.G, _accent.B), 0.38);
            _expandedHoverAccentEnd = new GradientStop(Color.FromArgb(68, _accentSecondary.R, _accentSecondary.G, _accentSecondary.B), 0.62);
            _expandedHoverEdgeEnd = new GradientStop(
                Color.FromArgb(14, _accentSecondary.R, _accentSecondary.G, _accentSecondary.B), 1);
            hoverSheen.GradientStops.Add(_expandedHoverEdgeStart);
            hoverSheen.GradientStops.Add(_expandedHoverAccentStart);
            hoverSheen.GradientStops.Add(new GradientStop(Color.FromArgb(45, 255, 255, 255), 0.50));
            hoverSheen.GradientStops.Add(_expandedHoverAccentEnd);
            hoverSheen.GradientStops.Add(_expandedHoverEdgeEnd);
            _expandedHoverSheenOffset = new TranslateTransform(-0.055, 0);
            hoverSheen.RelativeTransform = _expandedHoverSheenOffset;
            _expandedHoverSheen.Background = hoverSheen;
            canvas.Children.Add(_expandedHoverSheen);

            _expandedLayer = new Canvas();
            _expandedLayer.Width = ExpandedFrameWidth;
            _expandedLayer.Height = ExpandedHeightWithExtra;
            _expandedLayer.Opacity = 0;
            _expandedLayer.IsHitTestVisible = false;
            _expandedLayerOffset = new TranslateTransform(12, 3);
            _expandedLayer.RenderTransform = _expandedLayerOffset;
            canvas.Children.Add(_expandedLayer);

            Ellipse statusDot = new Ellipse();
            statusDot.Width = 7.5;
            statusDot.Height = 7.5;
            _statusDotFill = new SolidColorBrush(_accent);
            _statusDotStroke = new SolidColorBrush(Color.FromArgb(80, _accent.R, _accent.G, _accent.B));
            statusDot.Fill = _statusDotFill;
            statusDot.Stroke = _statusDotStroke;
            statusDot.StrokeThickness = 3;
            Canvas.SetLeft(statusDot, 12);
            Canvas.SetTop(statusDot, 14);
            _expandedLayer.Children.Add(statusDot);

            TextBlock title = CreateText("Codex 额度", 12.0, FontWeights.Bold, Colors.White);
            title.Width = 132;
            title.Height = 18;
            Canvas.SetLeft(title, 26);
            Canvas.SetTop(title, 5);
            _expandedLayer.Children.Add(title);

            _stateText = CreateText("读取中", 8.8, FontWeights.Normal, Color.FromRgb(174, 190, 214));
            _stateText.Width = 138;
            _stateText.Height = 13;
            Canvas.SetLeft(_stateText, 26);
            Canvas.SetTop(_stateText, 23);
            _expandedLayer.Children.Add(_stateText);

            _pinButton = CreateHeaderButton("置顶", 44, 24, 10.3, -1);
            Canvas.SetLeft(_pinButton, 171);
            Canvas.SetTop(_pinButton, 5);
            _pinButton.Click += delegate { Topmost = !Topmost; UpdatePinButton(); };
            _expandedLayer.Children.Add(_pinButton);

            _refreshButton = CreateHeaderButton("↻", 25, 25, 13.5, -2);
            Canvas.SetLeft(_refreshButton, 220);
            Canvas.SetTop(_refreshButton, 5);
            _refreshButton.Click += async delegate { await RefreshQuotaAsync(false); };
            _expandedLayer.Children.Add(_refreshButton);

            Button minimizeButton = CreateHeaderButton("−", 25, 25, 13.5, -3);
            Canvas.SetLeft(minimizeButton, 250);
            Canvas.SetTop(minimizeButton, 5);
            minimizeButton.Click += async delegate { await CollapseAsync(); };
            _expandedLayer.Children.Add(minimizeButton);

            Button closeButton = CreateHeaderButton("×", 27, 27, 15, -2);
            closeButton.Foreground = new SolidColorBrush(Color.FromRgb(255, 151, 161));
            Canvas.SetLeft(closeButton, 280);
            Canvas.SetTop(closeButton, 4);
            closeButton.Click += delegate { Close(); };
            _expandedLayer.Children.Add(closeButton);

            _gauge = new WpfQuotaGauge();
            _gauge.Width = OrbSize;
            _gauge.Height = OrbSize;
            _gauge.SetAccent(_accent, _accentSecondary, 0);
            Canvas.SetLeft(_gauge, FixedGaugeLeft);
            Canvas.SetTop(_gauge, ExpandedGaugeTop);
            _gauge.MouseEnter += delegate { AnimatePointerHover(true); };
            _gauge.MouseLeave += delegate { AnimatePointerHover(false); };
            _windowCanvas.Children.Add(_gauge);

            _primaryCard = CreateCard("主额度窗口", false, out _primaryCaption, out _primaryValue);
            _primaryCard.Width = 190;
            _primaryCard.Height = 39;
            Canvas.SetLeft(_primaryCard, 12);
            Canvas.SetTop(_primaryCard, 42);
            _expandedLayer.Children.Add(_primaryCard);

            _secondaryCard = CreateCard("额外额度", false, out _secondaryCaption, out _secondaryValue);
            _secondaryCard.Width = 190;
            _secondaryCard.Height = 39;
            _secondaryCard.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(_secondaryCard, 12);
            Canvas.SetTop(_secondaryCard, 86);
            _expandedLayer.Children.Add(_secondaryCard);

            TextBlock planCaption;
            _planCard = CreateCard("账户方案", true, out planCaption, out _planValue);
            _planCard.Width = 190;
            _planCard.Height = 25;
            Canvas.SetLeft(_planCard, 12);
            Canvas.SetTop(_planCard, 87);
            _expandedLayer.Children.Add(_planCard);

            _statusText = CreateText("正在安全读取…", 8.0, FontWeights.Normal, Color.FromRgb(154, 174, 202));
            _statusText.Width = 188;
            _statusText.Height = 13;
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusText.Cursor = Cursors.Help;
            Canvas.SetLeft(_statusText, 14);
            Canvas.SetTop(_statusText, 119);
            _statusText.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) ShowStatusDetails();
            };
            _expandedLayer.Children.Add(_statusText);

            _windowCanvas.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;
            _windowCanvas.MouseMove += OnFrameMouseMove;
            _windowCanvas.MouseLeftButtonUp += OnFrameMouseLeftButtonUp;
            _windowCanvas.MouseEnter += delegate
            {
                if (!_isExpanded) AnimatePointerHover(true);
            };
            _windowCanvas.MouseLeave += delegate { AnimatePointerHover(false); };
            _windowCanvas.LostMouseCapture += OnFrameLostMouseCapture;

            _holdTimer = new DispatcherTimer();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(HoldMilliseconds);
            _holdTimer.Tick += OnHoldTimerTick;

            _dragTimer = new DispatcherTimer(DispatcherPriority.Render);
            _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
            _dragTimer.Tick += OnDragTimerTick;

            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(60);
            _refreshTimer.Tick += async delegate { await RefreshQuotaAsync(true); };

            Loaded += async delegate
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - ScreenEdgeMargin - FixedGaugeLeft - OrbSize;
                Top = area.Top + 40;
                await RefreshQuotaAsync(true);
            };
            SourceInitialized += OnSourceInitialized;
            Closing += OnClosing;
        }

        private static LinearGradientBrush CreateWindowBackground()
        {
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(14, 22, 39), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(16, 51, 61), 0.53));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(48, 29, 70), 1));
            return brush;
        }

        private static ImageSource LoadApplicationIcon()
        {
            try
            {
                string executable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(executable))
                {
                    if (icon == null) return null;
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
            }
            catch
            {
                return null;
            }
        }

        private static LinearGradientBrush CreateBorderBrush(Color accent, Color secondary)
        {
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(225, accent.R, accent.G, accent.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(205, secondary.R, secondary.G, secondary.B), 1));
            return brush;
        }

        private static TextBlock CreateText(string text, double size, FontWeight weight, Color color)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontFamily = new FontFamily("Microsoft YaHei UI");
            block.FontSize = size;
            block.FontWeight = weight;
            block.Foreground = new SolidColorBrush(color);
            block.VerticalAlignment = VerticalAlignment.Center;
            return block;
        }

        private static Button CreateHeaderButton(string text, double width, double height, double size, double textOffset)
        {
            Button button = new Button();
            button.Width = width;
            button.Height = height;
            button.BorderThickness = new Thickness(0);
            button.Background = new SolidColorBrush(Color.FromRgb(43, 55, 83));
            button.Foreground = new SolidColorBrush(Color.FromRgb(222, 232, 244));
            button.Focusable = false;
            button.Cursor = Cursors.Hand;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;

            TextBlock content = CreateText(text, size, FontWeights.SemiBold, Colors.White);
            content.Margin = new Thickness(0, textOffset, 0, 0);
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
            button.Content = content;

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13.5));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;

            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.TemplateProperty, template));
            Trigger hover = new Trigger();
            hover.Property = Button.IsMouseOverProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(57, 73, 107))));
            style.Triggers.Add(hover);
            Trigger pressed = new Trigger();
            pressed.Property = ButtonBase.IsPressedProperty;
            pressed.Value = true;
            pressed.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(67, 55, 101))));
            style.Triggers.Add(pressed);
            button.Style = style;
            return button;
        }

        private static Border CreateCard(string caption, bool compact, out TextBlock captionText, out TextBlock valueText)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(compact ? 15 : 16);
            card.BorderThickness = new Thickness(0.65);
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 135, 169, 216));
            LinearGradientBrush background = new LinearGradientBrush();
            background.StartPoint = new Point(0, 0);
            background.EndPoint = new Point(1, 1);
            background.GradientStops.Add(new GradientStop(Color.FromRgb(34, 55, 78), 0));
            background.GradientStops.Add(new GradientStop(Color.FromRgb(51, 35, 72), 1));
            card.Background = background;

            Canvas content = new Canvas();
            card.Child = content;
            captionText = CreateText(caption, 9.6, FontWeights.Normal, Color.FromRgb(170, 187, 214));
            captionText.Width = compact ? 78 : 172;
            captionText.Height = 16;
            Canvas.SetLeft(captionText, 12);
            Canvas.SetTop(captionText, compact ? 6 : 2);
            content.Children.Add(captionText);

            valueText = CreateText("--", compact ? 11.8 : 12.5, FontWeights.Bold, Colors.White);
            valueText.Height = 22;
            if (compact)
            {
                valueText.Width = 92;
                valueText.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(valueText, 84);
                Canvas.SetTop(valueText, 3);
            }
            else
            {
                valueText.Width = 172;
                Canvas.SetLeft(valueText, 12);
                Canvas.SetTop(valueText, 16);
            }
            content.Children.Add(valueText);
            return card;
        }

        private async Task RefreshQuotaAsync(bool automatic)
        {
            if (_refreshing || _closing.IsCancellationRequested) return;
            CancellationToken cancellationToken = _closing.Token;
            _refreshing = true;
            _refreshTimer.Stop();
            _refreshButton.IsEnabled = false;
            bool preserveExistingQuota = automatic && _hasDisplayedQuota;
            if (!preserveExistingQuota) _stateText.Text = "安全读取中";
            _statusText.Text = automatic ? "正在自动刷新额度…" : "正在验证签名并读取额度…";
            try
            {
                QuotaSnapshot snapshot;
                if (automatic)
                {
                    snapshot = await AsyncRetryPolicy.ExecuteAsync(
                        delegate { return CodexQuotaClient.ReadQuotaAsync(cancellationToken); },
                        AutomaticRetryDelaysMilliseconds,
                        ShouldRetryAutomaticRefresh,
                        delegate(int retryNumber, int totalRetries, int delayMilliseconds, Exception ex)
                        {
                            _statusText.Text = FormatRetryStatus(retryNumber, totalRetries, delayMilliseconds);
                            _statusText.Tag = ex.ToString();
                            _statusText.ToolTip = "正在自动重试；双击查看上一次错误";
                        },
                        cancellationToken);
                }
                else
                {
                    snapshot = await CodexQuotaClient.ReadQuotaAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                int? remaining = LowestRemaining(snapshot.Primary, snapshot.Secondary);
                SetAccent(remaining);
                _gauge.RemainingPercent = remaining;
                _primaryValue.Text = FormatWindow(snapshot.Primary);
                _primaryCaption.Text = FormatWindowCaption(snapshot.Primary, "主额度窗口");
                // Keep parsing the server response for diagnostics. Spark is filtered
                // in the UI because it is a separate model allowance, not the plan cycle.
                UpdateExtraQuota(snapshot.Extra);
                _planValue.Text = FormatPlanType(snapshot.PlanType);
                _stateText.Text = StateText(remaining);
                _statusText.Text = "已更新 " + DateTime.Now.ToString("HH:mm") + " · 60秒后刷新";
                _statusText.Tag = snapshot.CodexPath;
                _statusText.ToolTip = snapshot.CodexPath;
                _hasDisplayedQuota = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _hasDisplayedQuota = false;
                SetAccent(null);
                _stateText.Text = "读取失败";
                _gauge.RemainingPercent = null;
                _primaryValue.Text = "--";
                _secondaryValue.Text = "--";
                _primaryCaption.Text = "主额度窗口";
                _secondaryCaption.Text = "额外额度";
                _planValue.Text = "--";
                _statusText.Text = FriendlyError(ex.Message);
                _statusText.Tag = ex.ToString();
                _statusText.ToolTip = "双击查看详细错误";
            }
            finally
            {
                _refreshing = false;
                if (!_disposed)
                {
                    _refreshButton.IsEnabled = true;
                    if (!cancellationToken.IsCancellationRequested) _refreshTimer.Start();
                }
            }
        }

        private static bool ShouldRetryAutomaticRefresh(Exception error)
        {
            if (error is OperationCanceledException ||
                error is System.IO.FileNotFoundException ||
                error is UnauthorizedAccessException)
            {
                return false;
            }

            string details = error.ToString();
            return details.IndexOf("签名", StringComparison.OrdinalIgnoreCase) < 0 &&
                   details.IndexOf("signed", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string FormatRetryStatus(int retryNumber, int totalRetries, int delayMilliseconds)
        {
            string delaySeconds = (delayMilliseconds / 1000.0).ToString("0.#");
            return "读取暂时失败 · " + delaySeconds + "秒后重试 " + retryNumber + "/" + totalRetries;
        }

        private void UpdateExtraQuota(ExtraQuota extra)
        {
            bool isSpark = extra != null &&
                extra.Kind == ExtraQuotaKind.ModelLimit &&
                ((!string.IsNullOrWhiteSpace(extra.LimitName) &&
                  extra.LimitName.IndexOf("Spark", StringComparison.OrdinalIgnoreCase) >= 0) ||
                 string.Equals(extra.LimitId, "codex_bengalfox", StringComparison.OrdinalIgnoreCase));
            bool hasExtra = extra != null && extra.Window != null && !isSpark;
            if (hasExtra)
            {
                _secondaryCaption.Text = FormatExtraCaption(extra);
                _secondaryValue.Text = FormatWindow(extra.Window);
            }
            else
            {
                _secondaryCaption.Text = "额外额度";
                _secondaryValue.Text = "";
            }

            if (_isAnimating)
            {
                _pendingExtraLayout = hasExtra;
                return;
            }
            ApplyExtraLayout(hasExtra, _isExpanded);
        }

        private void ApplyExtraLayout(bool hasExtra, bool animate)
        {
            _pendingExtraLayout = null;
            double targetPlanTop = hasExtra ? 130 : 87;
            double targetStatusTop = hasExtra ? 161 : 119;
            double targetHeight = hasExtra ? ExpandedHeightWithExtra : ExpandedHeightCompact;
            bool changed = _hasExtraQuota != hasExtra;
            _hasExtraQuota = hasExtra;
            _secondaryCard.BeginAnimation(UIElement.OpacityProperty, null);
            _secondaryCard.Opacity = 1;
            _secondaryCard.Visibility = hasExtra ? Visibility.Visible : Visibility.Collapsed;

            if (animate && changed)
            {
                AnimateCanvasTop(_planCard, targetPlanTop, 180);
                AnimateCanvasTop(_statusText, targetStatusTop, 180);
                double currentHeight = _frame.ActualHeight > 0 ? _frame.ActualHeight : _frame.Height;
                _frame.Height = targetHeight;
                CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                DoubleAnimation height = new DoubleAnimation(currentHeight, targetHeight,
                    new Duration(TimeSpan.FromMilliseconds(180)));
                height.EasingFunction = easing;
                height.FillBehavior = FillBehavior.Stop;
                _frame.BeginAnimation(FrameworkElement.HeightProperty, height);

                if (hasExtra)
                {
                    _secondaryCard.Opacity = 1;
                    DoubleAnimation opacity = new DoubleAnimation(0, 1,
                        new Duration(TimeSpan.FromMilliseconds(190)));
                    opacity.EasingFunction = easing;
                    opacity.FillBehavior = FillBehavior.Stop;
                    _secondaryCard.BeginAnimation(UIElement.OpacityProperty, opacity);
                }
            }
            else
            {
                Canvas.SetTop(_planCard, targetPlanTop);
                Canvas.SetTop(_statusText, targetStatusTop);
                if (_isExpanded) _frame.Height = targetHeight;
            }
        }

        private static void AnimateCanvasTop(UIElement element, double target, int milliseconds)
        {
            double current = Canvas.GetTop(element);
            if (double.IsNaN(current)) current = target;
            Canvas.SetTop(element, target);
            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation animation = new DoubleAnimation(current, target,
                new Duration(TimeSpan.FromMilliseconds(milliseconds)));
            animation.EasingFunction = easing;
            animation.FillBehavior = FillBehavior.Stop;
            element.BeginAnimation(Canvas.TopProperty, animation);
        }

        private void SetAccent(int? remaining)
        {
            if (_isAnimating)
            {
                _pendingAccentRemaining = remaining;
                _hasPendingAccent = true;
                return;
            }

            Color primary;
            Color secondary;
            Color backgroundMiddle;
            Color backgroundEnd;
            GetAccentPalette(remaining, out primary, out secondary, out backgroundMiddle, out backgroundEnd);

            bool changed = !primary.Equals(_accent) || !secondary.Equals(_accentSecondary);
            _accent = primary;
            _accentSecondary = secondary;
            int milliseconds = IsLoaded && changed ? 360 : 0;

            Color frameStartColor = primary;
            Color frameEndColor = secondary;
            if (_expandedHoverActive)
            {
                frameStartColor = MixColor(primary, Colors.White, 0.24);
                frameEndColor = MixColor(secondary, Colors.White, 0.18);
            }
            AnimateColor(_frameBorderStart, GradientStop.ColorProperty,
                Color.FromArgb(_expandedHoverActive ? (byte)245 : (byte)225,
                    frameStartColor.R, frameStartColor.G, frameStartColor.B), milliseconds);
            AnimateColor(_frameBorderEnd, GradientStop.ColorProperty,
                Color.FromArgb(_expandedHoverActive ? (byte)232 : (byte)205,
                    frameEndColor.R, frameEndColor.G, frameEndColor.B), milliseconds);
            AnimateColor(_expandedHoverAccentStart, GradientStop.ColorProperty,
                Color.FromArgb(82, primary.R, primary.G, primary.B), milliseconds);
            AnimateColor(_expandedHoverAccentEnd, GradientStop.ColorProperty,
                Color.FromArgb(68, secondary.R, secondary.G, secondary.B), milliseconds);
            AnimateColor(_expandedHoverEdgeStart, GradientStop.ColorProperty,
                Color.FromArgb(18, primary.R, primary.G, primary.B), milliseconds);
            AnimateColor(_expandedHoverEdgeEnd, GradientStop.ColorProperty,
                Color.FromArgb(14, secondary.R, secondary.G, secondary.B), milliseconds);
            AnimateColor(_backgroundMiddle, GradientStop.ColorProperty, backgroundMiddle, milliseconds);
            AnimateColor(_backgroundEnd, GradientStop.ColorProperty, backgroundEnd, milliseconds);
            AnimateColor(_statusDotFill, SolidColorBrush.ColorProperty, primary, milliseconds);
            AnimateColor(_statusDotStroke, SolidColorBrush.ColorProperty,
                Color.FromArgb(90, primary.R, primary.G, primary.B), milliseconds);

            Color cardBorder = Color.FromArgb(_expandedHoverActive ? (byte)145 : (byte)105,
                primary.R, primary.G, primary.B);
            AnimateBorderColor(_primaryCard, cardBorder, milliseconds);
            AnimateBorderColor(_secondaryCard, cardBorder, milliseconds);
            AnimateBorderColor(_planCard, cardBorder, milliseconds);
            AnimateTextColor(_stateText, MixColor(primary, Colors.White, 0.28), milliseconds);
            _gauge.SetAccent(primary, secondary, milliseconds);
            UpdatePinButton();
        }

        private static void GetAccentPalette(int? remaining, out Color primary, out Color secondary,
            out Color backgroundMiddle, out Color backgroundEnd)
        {
            if (!remaining.HasValue)
            {
                primary = Color.FromRgb(142, 155, 170);
                secondary = Color.FromRgb(178, 194, 211);
                backgroundMiddle = Color.FromRgb(34, 42, 53);
                backgroundEnd = Color.FromRgb(42, 34, 53);
                return;
            }

            if (remaining.Value < 20)
            {
                primary = Color.FromRgb(255, 103, 115);
                secondary = Color.FromRgb(255, 91, 143);
                backgroundMiddle = Color.FromRgb(61, 30, 43);
                backgroundEnd = Color.FromRgb(54, 25, 51);
                return;
            }

            if (remaining.Value < 50)
            {
                primary = Color.FromRgb(255, 196, 87);
                secondary = Color.FromRgb(255, 139, 92);
                backgroundMiddle = Color.FromRgb(59, 44, 34);
                backgroundEnd = Color.FromRgb(55, 31, 57);
                return;
            }

            primary = Color.FromRgb(95, 218, 167);
            secondary = Color.FromRgb(94, 191, 255);
            backgroundMiddle = Color.FromRgb(16, 51, 61);
            backgroundEnd = Color.FromRgb(48, 29, 70);
        }

        private static void AnimateBorderColor(Border border, Color target, int milliseconds)
        {
            SolidColorBrush brush = border.BorderBrush as SolidColorBrush;
            if (brush == null)
            {
                border.BorderBrush = new SolidColorBrush(target);
                return;
            }
            AnimateColor(brush, SolidColorBrush.ColorProperty, target, milliseconds);
        }

        private static void AnimateTextColor(TextBlock text, Color target, int milliseconds)
        {
            SolidColorBrush brush = text.Foreground as SolidColorBrush;
            if (brush == null)
            {
                text.Foreground = new SolidColorBrush(target);
                return;
            }
            AnimateColor(brush, SolidColorBrush.ColorProperty, target, milliseconds);
        }

        private static void AnimateColor(Animatable target, DependencyProperty property, Color targetColor, int milliseconds)
        {
            Color current = (Color)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, targetColor);
            if (milliseconds <= 0 || current.Equals(targetColor)) return;

            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            ColorAnimation animation = new ColorAnimation(current, targetColor,
                new Duration(TimeSpan.FromMilliseconds(milliseconds)));
            animation.EasingFunction = easing;
            animation.FillBehavior = FillBehavior.Stop;
            target.BeginAnimation(property, animation);
        }

        private static Color MixColor(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private void UpdatePinButton()
        {
            TextBlock content = _pinButton.Content as TextBlock;
            if (content != null)
            {
                content.Text = Topmost ? "置顶" : "普通";
                AnimateTextColor(content, Topmost ? _accent : Color.FromRgb(183, 196, 214), 180);
            }
        }

        private double CurrentGaugeLeft
        {
            get { return FixedGaugeLeft; }
        }

        private double CurrentExpandedFrameLeft
        {
            get { return _expandsLeft ? RightExpandedFrameLeft : LeftExpandedFrameLeft; }
        }

        private double CurrentExpandedHeight
        {
            get { return _hasExtraQuota ? ExpandedHeightWithExtra : ExpandedHeightCompact; }
        }

        private void UpdateExpansionSideFromOrb()
        {
            if (_isExpanded || _isAnimating) return;
            Rect area = SystemParameters.WorkArea;
            double orbCenter = Left + CanvasPosition(_gauge, true) + OrbSize / 2;
            bool shouldExpandLeft = orbCenter >= area.Left + area.Width / 2;
            SetExpansionSide(shouldExpandLeft, true);
        }

        private void SetExpansionSide(bool expandsLeft, bool preserveOrbScreenPosition)
        {
            if (_isExpanded || _isAnimating) return;
            _expandsLeft = expandsLeft;

            ApplyHorizontalContentLayout();
            _expandedLayerOffset.X = _expandsLeft ? 12 : -12;
        }

        private void ApplyHorizontalContentLayout()
        {
            bool contentOnRight = !_expandsLeft;
            Canvas.SetLeft(_primaryCard, contentOnRight ? ExpandedFrameWidth - 12 - _primaryCard.Width : 12);
            Canvas.SetLeft(_secondaryCard, contentOnRight ? ExpandedFrameWidth - 12 - _secondaryCard.Width : 12);
            Canvas.SetLeft(_planCard, contentOnRight ? ExpandedFrameWidth - 12 - _planCard.Width : 12);
            Canvas.SetLeft(_statusText, contentOnRight ? ExpandedFrameWidth - 14 - _statusText.Width : 14);
        }

        private void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || _isAnimating || IsPointerActionExcluded(e.OriginalSource as DependencyObject)) return;
            _pointerDown = true;
            _dragging = false;
            _pressTime = DateTime.UtcNow;
            _pressScreen = GetCursorScreenPosition();
            _dragScreen = _pressScreen;
            _holdTimer.Stop();
            _dragTimer.Stop();
            if (!_windowCanvas.CaptureMouse())
            {
                _pointerDown = false;
                e.Handled = true;
                return;
            }
            _holdTimer.Start();
            e.Handled = true;
        }

        private void OnHoldTimerTick(object sender, EventArgs e)
        {
            _holdTimer.Stop();
            if (!_pointerDown || _isAnimating || Mouse.LeftButton != MouseButtonState.Pressed) return;
            BeginDragging(GetCursorScreenPosition());
        }

        private void OnFrameMouseMove(object sender, MouseEventArgs e)
        {
            if (!_pointerDown) return;
            Point screen = GetCursorScreenPosition();
            Point delta = DeviceDeltaToDip(screen.X - _pressScreen.X, screen.Y - _pressScreen.Y);
            double distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (!_dragging)
            {
                if (distance <= ClickMoveTolerance) return;
                BeginDragging(screen);
                return;
            }
            MoveWindowForCursor(screen);
        }

        private void BeginDragging(Point cursorScreen)
        {
            if (_dragging || !_pointerDown || _isAnimating) return;
            _holdTimer.Stop();
            _dragging = true;
            _windowCanvas.Cursor = Cursors.SizeAll;
            AnimateExpandedHover(false, 90);
            _gauge.SetHover(!_isExpanded, 100);
            MoveWindowForCursor(cursorScreen);
            _dragTimer.Start();
        }

        private void MoveWindowForCursor(Point cursorScreen)
        {
            Point delta = DeviceDeltaToDip(cursorScreen.X - _dragScreen.X, cursorScreen.Y - _dragScreen.Y);
            _dragScreen = cursorScreen;
            if (Math.Abs(delta.X) < 0.01 && Math.Abs(delta.Y) < 0.01) return;

            Rect area = SystemParameters.WorkArea;
            if (_isExpanded)
            {
                double frameLeft = CanvasPosition(_frame, true);
                double frameTop = CanvasPosition(_frame, false);
                double frameWidth = _frame.ActualWidth > 0 ? _frame.ActualWidth : _frame.Width;
                double frameHeight = _frame.ActualHeight > 0 ? _frame.ActualHeight : _frame.Height;
                double inset = ScreenEdgeMargin / 2;
                Left = Clamp(Left + delta.X,
                    area.Left + inset - frameLeft,
                    area.Right - inset - frameLeft - frameWidth);
                Top = Clamp(Top + delta.Y,
                    area.Top + inset - frameTop,
                    area.Bottom - inset - frameTop - frameHeight);
                return;
            }

            double gaugeLeft = CanvasPosition(_gauge, true);
            double desiredOrbLeft = Left + gaugeLeft + delta.X;
            double desiredOrbTop = Top + ExpandedGaugeTop + delta.Y;
            double frameAboveOrb = ExpandedGaugeTop - ExpandedFrameTop;
            double frameBelowOrb = CurrentExpandedHeight - frameAboveOrb - OrbSize;
            double verticalInset = ScreenEdgeMargin / 2;
            double orbLeft = Clamp(desiredOrbLeft,
                area.Left + ScreenEdgeMargin,
                area.Right - ScreenEdgeMargin - OrbSize);
            double orbTop = Clamp(desiredOrbTop,
                area.Top + verticalInset + frameAboveOrb,
                area.Bottom - verticalInset - OrbSize - frameBelowOrb);
            Left = orbLeft - gaugeLeft;
            Top = orbTop - ExpandedGaugeTop;
        }

        private void OnDragTimerTick(object sender, EventArgs e)
        {
            if (!_pointerDown || !_dragging || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _dragTimer.Stop();
                return;
            }

            MoveWindowForCursor(GetCursorScreenPosition());
        }

        private async void OnFrameMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pointerDown || e.ChangedButton != MouseButton.Left) return;
            Point screen = GetCursorScreenPosition();
            Point delta = DeviceDeltaToDip(screen.X - _pressScreen.X, screen.Y - _pressScreen.Y);
            double distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            double heldMilliseconds = (DateTime.UtcNow - _pressTime).TotalMilliseconds;
            if (PointerGestureRules.ShouldStartDragOnRelease(
                _dragging, heldMilliseconds, distance, HoldMilliseconds, ClickMoveTolerance))
            {
                BeginDragging(screen);
            }
            bool wasDragging = _dragging;
            if (wasDragging) MoveWindowForCursor(screen);
            PointerTapAction tapAction = PointerGestureRules.GetTapAction(
                wasDragging, _isExpanded, heldMilliseconds, distance, HoldMilliseconds, ClickMoveTolerance);

            _holdTimer.Stop();
            _dragTimer.Stop();
            _pointerDown = false;
            _dragging = false;
            _windowCanvas.Cursor = Cursors.Arrow;
            if (_windowCanvas.IsMouseCaptured) _windowCanvas.ReleaseMouseCapture();
            if (wasDragging && !_isExpanded) UpdateExpansionSideFromOrb();
            AnimatePointerHover(IsWidgetMouseOver());
            e.Handled = true;

            if (tapAction == PointerTapAction.Collapse)
            {
                await CollapseAsync();
            }
            else if (tapAction == PointerTapAction.Expand)
            {
                await ExpandAsync();
            }
        }

        private Point GetCursorScreenPosition()
        {
            NativeMethods.NativePoint nativePoint;
            if (NativeMethods.GetCursorPos(out nativePoint))
                return new Point(nativePoint.X, nativePoint.Y);
            return PointToScreen(Mouse.GetPosition(this));
        }

        private void OnFrameLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_pointerDown) return;
            bool wasDragging = _dragging;
            _holdTimer.Stop();
            _dragTimer.Stop();
            _pointerDown = false;
            _dragging = false;
            _windowCanvas.Cursor = Cursors.Arrow;
            if (wasDragging && !_isExpanded) UpdateExpansionSideFromOrb();
            AnimatePointerHover(IsWidgetMouseOver());
        }

        private bool IsWidgetMouseOver()
        {
            return HoverActivationRules.IsActive(_isExpanded, _gauge.IsMouseOver, _frame.IsMouseOver);
        }

        private bool IsPointerActionExcluded(DependencyObject current)
        {
            while (current != null)
            {
                if (current is Button || ReferenceEquals(current, _statusText)) return true;
                try { current = VisualTreeHelper.GetParent(current); }
                catch { current = LogicalTreeHelper.GetParent(current); }
            }
            return false;
        }

        private Point DeviceDeltaToDip(double x, double y)
        {
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null) return new Point(x, y);
            return source.CompositionTarget.TransformFromDevice.Transform(new Point(x, y));
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (_hwndSource != null) _hwndSource.AddHook(WindowMessageHook);
        }

        private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int HtTransparent = -1;
            if (message != WmNcHitTest) return IntPtr.Zero;

            long packed = lParam.ToInt64();
            int screenX = unchecked((short)(packed & 0xffff));
            int screenY = unchecked((short)((packed >> 16) & 0xffff));
            Point frameStart = _frame.PointToScreen(new Point(0, 0));
            Point frameEnd = _frame.PointToScreen(new Point(
                _frame.ActualWidth > 0 ? _frame.ActualWidth : _frame.Width,
                _frame.ActualHeight > 0 ? _frame.ActualHeight : _frame.Height));
            Point gaugeStart = _gauge.PointToScreen(new Point(0, 0));
            Point gaugeEnd = _gauge.PointToScreen(new Point(
                _gauge.ActualWidth > 0 ? _gauge.ActualWidth : _gauge.Width,
                _gauge.ActualHeight > 0 ? _gauge.ActualHeight : _gauge.Height));
            bool insideFrame = screenX >= Math.Min(frameStart.X, frameEnd.X) &&
                               screenX <= Math.Max(frameStart.X, frameEnd.X) &&
                               screenY >= Math.Min(frameStart.Y, frameEnd.Y) &&
                               screenY <= Math.Max(frameStart.Y, frameEnd.Y);
            bool insideGauge = screenX >= Math.Min(gaugeStart.X, gaugeEnd.X) &&
                               screenX <= Math.Max(gaugeStart.X, gaugeEnd.X) &&
                               screenY >= Math.Min(gaugeStart.Y, gaugeEnd.Y) &&
                               screenY <= Math.Max(gaugeStart.Y, gaugeEnd.Y);
            if (!insideFrame && !insideGauge)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }
            return IntPtr.Zero;
        }

        private void AnimatePointerHover(bool hover)
        {
            if (_isAnimating || _dragging) return;
            if (_isExpanded)
            {
                bool circleHover = hover && _gauge.IsMouseOver;
                AnimateExpandedHover(circleHover, circleHover ? 190 : 125);
                _gauge.SetHover(circleHover, circleHover ? 170 : 110);
                return;
            }

            AnimateExpandedHover(false, 100);
            _gauge.SetHover(hover, 140);
        }

        private void AnimateExpandedHover(bool active, int milliseconds)
        {
            if (active && (!_isExpanded || _isAnimating || _dragging)) active = false;
            if (_expandedHoverActive == active) return;
            _expandedHoverActive = active;

            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));

            double currentOpacity = _expandedHoverSheen.Opacity;
            double targetOpacity = active ? 0.58 : 0;
            _expandedHoverSheen.BeginAnimation(UIElement.OpacityProperty, null);
            _expandedHoverSheen.Opacity = targetOpacity;
            DoubleAnimation opacity = new DoubleAnimation(currentOpacity, targetOpacity, duration);
            opacity.EasingFunction = easing;
            opacity.FillBehavior = FillBehavior.Stop;
            _expandedHoverSheen.BeginAnimation(UIElement.OpacityProperty, opacity);

            double currentOffset = _expandedHoverSheenOffset.X;
            double targetOffset = active ? 0.025 : -0.055;
            _expandedHoverSheenOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _expandedHoverSheenOffset.X = targetOffset;
            DoubleAnimation offset = new DoubleAnimation(currentOffset, targetOffset, duration);
            offset.EasingFunction = easing;
            offset.FillBehavior = FillBehavior.Stop;
            _expandedHoverSheenOffset.BeginAnimation(TranslateTransform.XProperty, offset);

            Thickness currentThickness = _frame.BorderThickness;
            Thickness targetThickness = new Thickness(active ? 1.45 : 1.0);
            _frame.BeginAnimation(Border.BorderThicknessProperty, null);
            _frame.BorderThickness = targetThickness;
            ThicknessAnimation thickness = new ThicknessAnimation(currentThickness, targetThickness, duration);
            thickness.EasingFunction = easing;
            thickness.FillBehavior = FillBehavior.Stop;
            _frame.BeginAnimation(Border.BorderThicknessProperty, thickness);

            Color start = active ? MixColor(_accent, Colors.White, 0.24) : _accent;
            Color end = active ? MixColor(_accentSecondary, Colors.White, 0.18) : _accentSecondary;
            AnimateColor(_frameBorderStart, GradientStop.ColorProperty,
                Color.FromArgb(active ? (byte)245 : (byte)225, start.R, start.G, start.B), milliseconds);
            AnimateColor(_frameBorderEnd, GradientStop.ColorProperty,
                Color.FromArgb(active ? (byte)232 : (byte)205, end.R, end.G, end.B), milliseconds);

            Color cardBorder = Color.FromArgb(active ? (byte)145 : (byte)105,
                _accent.R, _accent.G, _accent.B);
            AnimateBorderColor(_primaryCard, cardBorder, milliseconds);
            AnimateBorderColor(_secondaryCard, cardBorder, milliseconds);
            AnimateBorderColor(_planCard, cardBorder, milliseconds);
        }

        private Task ExpandAsync()
        {
            UpdateExpansionSideFromOrb();
            return AnimateWindowAsync(true);
        }

        private Task CollapseAsync()
        {
            return AnimateWindowAsync(false);
        }

        private Task AnimateWindowAsync(bool expand)
        {
            if (_isAnimating || expand == _isExpanded) return Task.FromResult(true);
            AnimateExpandedHover(false, 80);
            _isAnimating = true;
            _expandedLayer.IsHitTestVisible = false;
            _gauge.SetHover(false, 80);

            double startFrameLeft = CanvasPosition(_frame, true);
            double startFrameTop = CanvasPosition(_frame, false);
            double startFrameWidth = _frame.ActualWidth > 0 ? _frame.ActualWidth : _frame.Width;
            double startFrameHeight = _frame.ActualHeight > 0 ? _frame.ActualHeight : _frame.Height;
            double startRadius = _frame.AnimatedRadius;
            double startOpacity = _expandedLayer.Opacity;
            double targetFrameLeft = expand ? CurrentExpandedFrameLeft : CurrentGaugeLeft;
            double targetFrameTop = expand ? ExpandedFrameTop : ExpandedGaugeTop;
            double targetFrameWidth = expand ? ExpandedFrameWidth : OrbSize;
            double targetFrameHeight = expand
                ? (_hasExtraQuota ? ExpandedHeightWithExtra : ExpandedHeightCompact)
                : OrbSize;
            double targetRadius = expand ? 22 : OrbSize / 2;

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            _activeAnimationCompletion = completion;

            int totalMilliseconds = expand ? 320 : 285;
            int horizontalHold = expand ? 0 : 45;
            int horizontalFinish = expand ? 285 : 275;
            int verticalHold = expand ? 25 : 15;
            int verticalFinish = expand ? 300 : 225;
            int contentHold = expand ? 100 : 0;
            int contentFinish = expand ? 315 : 90;
            int contentSlideFinish = expand ? 300 : 120;
            double collapsedContentOffset = _expandsLeft ? 12 : -12;

            QuarticEase horizontalEasing = new QuarticEase();
            horizontalEasing.EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseInOut;
            QuarticEase verticalEasing = new QuarticEase();
            verticalEasing.EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseInOut;
            CubicEase contentEasing = new CubicEase();
            contentEasing.EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn;

            DoubleAnimationUsingKeyFrames widthAnimation = CreateStagedAnimation(
                startFrameWidth, targetFrameWidth, horizontalHold, horizontalFinish, totalMilliseconds, horizontalEasing);
            DoubleAnimationUsingKeyFrames heightAnimation = CreateStagedAnimation(
                startFrameHeight, targetFrameHeight, verticalHold, verticalFinish, totalMilliseconds, verticalEasing);
            DoubleAnimationUsingKeyFrames frameLeftAnimation = CreateStagedAnimation(
                startFrameLeft, targetFrameLeft, horizontalHold, horizontalFinish, totalMilliseconds, horizontalEasing);
            DoubleAnimationUsingKeyFrames frameTopAnimation = CreateStagedAnimation(
                startFrameTop, targetFrameTop, verticalHold, verticalFinish, totalMilliseconds, verticalEasing);

            DoubleAnimationUsingKeyFrames opacityAnimation = CreateStagedAnimation(
                startOpacity, expand ? 1 : 0, contentHold, contentFinish, totalMilliseconds, contentEasing);
            DoubleAnimationUsingKeyFrames contentXAnimation = CreateStagedAnimation(
                _expandedLayerOffset.X, expand ? 0 : collapsedContentOffset,
                contentHold, contentSlideFinish, totalMilliseconds, contentEasing);
            DoubleAnimationUsingKeyFrames contentYAnimation = CreateStagedAnimation(
                _expandedLayerOffset.Y, expand ? 0 : 3, contentHold, contentSlideFinish, totalMilliseconds, contentEasing);
            DoubleAnimationUsingKeyFrames radiusAnimation = CreateStagedAnimation(
                startRadius, targetRadius, verticalHold, verticalFinish, totalMilliseconds, verticalEasing);

            Brush animatedBackground = _frame.Background;
            Brush animatedBorder = _frame.BorderBrush;
            DoubleAnimationUsingKeyFrames backgroundOpacityAnimation = CreateShellOpacityAnimation(
                expand, false, totalMilliseconds);
            DoubleAnimationUsingKeyFrames borderOpacityAnimation = CreateShellOpacityAnimation(
                expand, true, totalMilliseconds);

            _frame.Width = targetFrameWidth;
            _frame.Height = targetFrameHeight;
            Canvas.SetLeft(_frame, targetFrameLeft);
            Canvas.SetTop(_frame, targetFrameTop);
            _frame.AnimatedRadius = targetRadius;
            _expandedLayer.Opacity = expand ? 1 : 0;
            _expandedLayerOffset.X = expand ? 0 : collapsedContentOffset;
            _expandedLayerOffset.Y = expand ? 0 : 3;
            if (animatedBackground != null) animatedBackground.Opacity = 1;
            if (animatedBorder != null) animatedBorder.Opacity = 1;

            widthAnimation.Completed += delegate
            {
                ClearVisualAnimations();
                _expandedLayer.IsHitTestVisible = expand;
                _isExpanded = expand;
                _isAnimating = false;
                if (_pendingExtraLayout.HasValue)
                    ApplyExtraLayout(_pendingExtraLayout.Value, expand);
                if (_hasPendingAccent)
                {
                    int? pendingAccent = _pendingAccentRemaining;
                    _hasPendingAccent = false;
                    _pendingAccentRemaining = null;
                    SetAccent(pendingAccent);
                }
                if (_activeAnimationCompletion == completion) _activeAnimationCompletion = null;
                if (!expand) UpdateExpansionSideFromOrb();
                AnimatePointerHover(IsWidgetMouseOver());
                completion.TrySetResult(true);
            };

            _frame.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
            _frame.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
            _frame.BeginAnimation(Canvas.LeftProperty, frameLeftAnimation);
            _frame.BeginAnimation(Canvas.TopProperty, frameTopAnimation);
            _frame.BeginAnimation(AnimatedBorder.AnimatedRadiusProperty, radiusAnimation);
            _expandedLayer.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
            _expandedLayerOffset.BeginAnimation(TranslateTransform.XProperty, contentXAnimation);
            _expandedLayerOffset.BeginAnimation(TranslateTransform.YProperty, contentYAnimation);
            if (animatedBackground != null)
                animatedBackground.BeginAnimation(Brush.OpacityProperty, backgroundOpacityAnimation);
            if (animatedBorder != null)
                animatedBorder.BeginAnimation(Brush.OpacityProperty, borderOpacityAnimation);
            return completion.Task;
        }

        private static double CanvasPosition(UIElement element, bool horizontal)
        {
            double value = horizontal ? Canvas.GetLeft(element) : Canvas.GetTop(element);
            return double.IsNaN(value) ? 0 : value;
        }

        private void ClearVisualAnimations()
        {
            _frame.BeginAnimation(FrameworkElement.WidthProperty, null);
            _frame.BeginAnimation(FrameworkElement.HeightProperty, null);
            _frame.BeginAnimation(Canvas.LeftProperty, null);
            _frame.BeginAnimation(Canvas.TopProperty, null);
            _frame.BeginAnimation(AnimatedBorder.AnimatedRadiusProperty, null);
            _expandedLayer.BeginAnimation(UIElement.OpacityProperty, null);
            _expandedLayerOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _expandedLayerOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _secondaryCard.BeginAnimation(UIElement.OpacityProperty, null);
            _planCard.BeginAnimation(Canvas.TopProperty, null);
            _statusText.BeginAnimation(Canvas.TopProperty, null);
            if (_frame.Background != null)
            {
                _frame.Background.BeginAnimation(Brush.OpacityProperty, null);
                _frame.Background.Opacity = 1;
            }
            if (_frame.BorderBrush != null)
            {
                _frame.BorderBrush.BeginAnimation(Brush.OpacityProperty, null);
                _frame.BorderBrush.Opacity = 1;
            }
        }

        private static DoubleAnimationUsingKeyFrames CreateShellOpacityAnimation(
            bool expand,
            bool border,
            int totalMilliseconds)
        {
            int dipTime = expand ? 70 : 140;
            int recoveryTime = expand ? 220 : 225;
            double dipOpacity = border ? (expand ? 0.20 : 0.24) : (expand ? 0.44 : 0.48);
            double recoveryOpacity = border ? 0.66 : 0.76;
            CubicEase fadeEase = new CubicEase();
            fadeEase.EasingMode = EasingMode.EaseOut;

            DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(totalMilliseconds));
            animation.FillBehavior = FillBehavior.Stop;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                dipOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dipTime)),
                fadeEase));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                recoveryOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(recoveryTime)),
                fadeEase));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMilliseconds)),
                fadeEase));
            return animation;
        }

        private static DoubleAnimationUsingKeyFrames CreateStagedAnimation(
            double from,
            double to,
            int holdMilliseconds,
            int finishMilliseconds,
            int totalMilliseconds,
            IEasingFunction easing)
        {
            DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(totalMilliseconds));
            animation.FillBehavior = FillBehavior.Stop;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (holdMilliseconds > 0)
            {
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                    from,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(holdMilliseconds))));
            }
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                to,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(finishMilliseconds)),
                easing));
            if (finishMilliseconds < totalMilliseconds)
            {
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                    to,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMilliseconds))));
            }
            return animation;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void ShowStatusDetails()
        {
            string detail = _statusText.Tag as string;
            MessageBox.Show(string.IsNullOrWhiteSpace(detail) ? _statusText.Text : detail,
                "额度读取详情", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_disposed) return;
            _disposed = true;
            _holdTimer.Stop();
            _dragTimer.Stop();
            _refreshTimer.Stop();
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WindowMessageHook);
                _hwndSource = null;
            }
            ClearVisualAnimations();
            if (_activeAnimationCompletion != null)
            {
                _activeAnimationCompletion.TrySetCanceled();
                _activeAnimationCompletion = null;
            }
            _closing.Cancel();
            _closing.Dispose();
        }

        private static string FormatWindow(QuotaWindow value)
        {
            if (value == null) return "--";
            string reset = FormatReset(value.ResetsAt);
            return value.RemainingPercent + "% 剩余" + (string.IsNullOrEmpty(reset) ? "" : " · " + reset);
        }

        private static string FormatWindowCaption(QuotaWindow value, string fallback)
        {
            if (value == null || !value.WindowDurationMinutes.HasValue) return fallback;
            int minutes = value.WindowDurationMinutes.Value;
            if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440) + " 天窗口";
            if (minutes > 0 && minutes % 60 == 0) return (minutes / 60) + " 小时窗口";
            if (minutes > 0) return minutes + " 分钟窗口";
            return fallback;
        }

        private static string FormatExtraCaption(ExtraQuota extra)
        {
            if (extra == null || extra.Window == null) return "额外额度";
            if (extra.Kind == ExtraQuotaKind.SecondaryWindow)
                return "额外额度 · " + FormatWindowCaption(extra.Window, "额度窗口");

            string modelName = extra.LimitName ?? "独立模型";
            if (modelName.IndexOf("Spark", StringComparison.OrdinalIgnoreCase) >= 0)
                modelName = "Spark";
            else if (modelName.Length > 12)
                modelName = modelName.Substring(0, 12) + "…";

            string period = "独立额度";
            if (extra.Window.WindowDurationMinutes.HasValue)
            {
                int minutes = extra.Window.WindowDurationMinutes.Value;
                if (minutes == 10080) period = "周额度";
                else if (minutes > 0 && minutes % 1440 == 0) period = (minutes / 1440) + "天额度";
                else if (minutes > 0 && minutes % 60 == 0) period = (minutes / 60) + "小时额度";
            }
            return "额外额度 · " + modelName + " " + period;
        }

        private static string FormatPlanType(string planType)
        {
            return PlanDisplayFormatter.Format(planType);
        }

        private static int? LowestRemaining(params QuotaWindow[] windows)
        {
            QuotaWindow[] available = windows.Where(w => w != null).ToArray();
            if (available.Length == 0) return null;
            return available.Min(w => w.RemainingPercent);
        }

        private static string FormatReset(DateTime? resetUtc)
        {
            if (!resetUtc.HasValue) return "";
            TimeSpan remaining = resetUtc.Value.ToUniversalTime() - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0) return "即将重置";
            if (remaining.TotalDays >= 1) return ((int)remaining.TotalDays) + "天" + remaining.Hours + "小时后";
            if (remaining.TotalHours >= 1) return ((int)remaining.TotalHours) + "小时" + remaining.Minutes + "分后";
            return Math.Max(1, remaining.Minutes) + "分钟后";
        }

        private static string StateText(int? remaining)
        {
            if (!remaining.HasValue) return "状态未知";
            if (remaining.Value == 0) return "额度已用完";
            if (remaining.Value < 20) return "额度紧张";
            if (remaining.Value < 50) return "额度需要留意";
            return "额度充足";
        }

        private static string FriendlyError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "读取失败；双击状态栏查看详情";
            if (message.IndexOf("signed", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("签名", StringComparison.OrdinalIgnoreCase) >= 0)
                return "安全校验未通过；未启动 Codex";
            if (message.IndexOf("find", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("找到", StringComparison.OrdinalIgnoreCase) >= 0)
                return "未找到可信的 Codex 安装";
            return message.Length > 52 ? message.Substring(0, 52) + "…" : message;
        }
    }

    internal sealed class AnimatedBorder : Border
    {
        public static readonly DependencyProperty AnimatedRadiusProperty = DependencyProperty.Register(
            "AnimatedRadius",
            typeof(double),
            typeof(AnimatedBorder),
            new PropertyMetadata(0.0, OnAnimatedRadiusChanged));

        public double AnimatedRadius
        {
            get { return (double)GetValue(AnimatedRadiusProperty); }
            set { SetValue(AnimatedRadiusProperty, value); }
        }

        private static void OnAnimatedRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            AnimatedBorder border = (AnimatedBorder)sender;
            double radius = Math.Max(0, (double)e.NewValue);
            border.CornerRadius = new CornerRadius(radius);
        }
    }

    internal sealed class WpfQuotaGauge : Grid
    {
        private readonly Ellipse _hoverHalo;
        private readonly ScaleTransform _hoverHaloScale;
        private readonly Ellipse _hoverRipple;
        private readonly ScaleTransform _hoverRippleScale;
        private readonly Ellipse _hoverRing;
        private readonly ScaleTransform _hoverRingScale;
        private readonly Ellipse _baseCircle;
        private readonly System.Windows.Shapes.Path _progressPath;
        private readonly TextBlock _valueText;
        private readonly SolidColorBrush _valueBrush;
        private readonly GradientStop _progressAccentStart;
        private readonly GradientStop _progressAccentEnd;
        private readonly GradientStop _haloAccentStart;
        private readonly GradientStop _haloAccentEnd;
        private readonly GradientStop _ringAccentStart;
        private readonly GradientStop _ringAccentEnd;
        private readonly SolidColorBrush _rippleBrush;
        private Color _accent;
        private Color _accentSecondary;
        private int? _remainingPercent;
        private bool _hoverActive;

        public int? RemainingPercent
        {
            get { return _remainingPercent; }
            set
            {
                _remainingPercent = value;
                _valueText.Text = value.HasValue ? value.Value + "%" : "--%";
                UpdateArc();
            }
        }

        public WpfQuotaGauge()
        {
            _accent = Color.FromRgb(95, 218, 167);
            _accentSecondary = Color.FromRgb(94, 191, 255);
            SnapsToDevicePixels = false;

            _hoverHalo = new Ellipse();
            _hoverHalo.Margin = new Thickness(1);
            _hoverHalo.Opacity = 0;
            _hoverHalo.IsHitTestVisible = false;
            _hoverHalo.RenderTransformOrigin = new Point(0.5, 0.5);
            _hoverHaloScale = new ScaleTransform(0.82, 0.82);
            _hoverHalo.RenderTransform = _hoverHaloScale;
            RadialGradientBrush haloBrush = new RadialGradientBrush();
            haloBrush.Center = new Point(0.5, 0.5);
            haloBrush.GradientOrigin = new Point(0.5, 0.5);
            haloBrush.RadiusX = 0.5;
            haloBrush.RadiusY = 0.5;
            haloBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.58));
            _haloAccentStart = new GradientStop(Color.FromArgb(225, _accent.R, _accent.G, _accent.B), 0.82);
            _haloAccentEnd = new GradientStop(Color.FromArgb(175, _accentSecondary.R, _accentSecondary.G, _accentSecondary.B), 0.95);
            haloBrush.GradientStops.Add(_haloAccentStart);
            haloBrush.GradientStops.Add(_haloAccentEnd);
            haloBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _hoverHalo.Fill = haloBrush;
            Children.Add(_hoverHalo);

            _hoverRipple = new Ellipse();
            _hoverRipple.Margin = new Thickness(5);
            _hoverRipple.StrokeThickness = 2.4;
            _hoverRipple.Opacity = 0;
            _hoverRipple.IsHitTestVisible = false;
            _hoverRipple.RenderTransformOrigin = new Point(0.5, 0.5);
            _hoverRippleScale = new ScaleTransform(0.72, 0.72);
            _hoverRipple.RenderTransform = _hoverRippleScale;
            _rippleBrush = new SolidColorBrush(Color.FromArgb(230, _accent.R, _accent.G, _accent.B));
            _hoverRipple.Stroke = _rippleBrush;
            Children.Add(_hoverRipple);

            RadialGradientBrush circleFill = new RadialGradientBrush();
            circleFill.GradientStops.Add(new GradientStop(Color.FromRgb(35, 34, 61), 0));
            circleFill.GradientStops.Add(new GradientStop(Color.FromRgb(25, 35, 55), 1));
            _baseCircle = new Ellipse();
            _baseCircle.Margin = new Thickness(6.5);
            _baseCircle.Fill = circleFill;
            _baseCircle.Stroke = new SolidColorBrush(Color.FromRgb(55, 69, 93));
            _baseCircle.StrokeThickness = 6.5;
            Children.Add(_baseCircle);

            _hoverRing = new Ellipse();
            _hoverRing.Margin = new Thickness(6.5);
            _hoverRing.StrokeThickness = 6.5;
            _hoverRing.Opacity = 0;
            _hoverRing.IsHitTestVisible = false;
            _hoverRing.RenderTransformOrigin = new Point(0.5, 0.5);
            _hoverRingScale = new ScaleTransform(0.94, 0.94);
            _hoverRing.RenderTransform = _hoverRingScale;
            LinearGradientBrush ringBrush = new LinearGradientBrush();
            ringBrush.StartPoint = new Point(0, 0);
            ringBrush.EndPoint = new Point(1, 1);
            _ringAccentStart = new GradientStop(_accent, 0);
            _ringAccentEnd = new GradientStop(_accentSecondary, 1);
            ringBrush.GradientStops.Add(_ringAccentStart);
            ringBrush.GradientStops.Add(_ringAccentEnd);
            _hoverRing.Stroke = ringBrush;
            Children.Add(_hoverRing);

            _progressPath = new System.Windows.Shapes.Path();
            _progressPath.StrokeThickness = 6.5;
            _progressPath.StrokeStartLineCap = PenLineCap.Round;
            _progressPath.StrokeEndLineCap = PenLineCap.Round;
            _progressPath.SnapsToDevicePixels = false;
            LinearGradientBrush progressBrush = new LinearGradientBrush();
            progressBrush.StartPoint = new Point(0, 0);
            progressBrush.EndPoint = new Point(1, 1);
            _progressAccentStart = new GradientStop(_accent, 0);
            _progressAccentEnd = new GradientStop(_accentSecondary, 1);
            progressBrush.GradientStops.Add(_progressAccentStart);
            progressBrush.GradientStops.Add(_progressAccentEnd);
            _progressPath.Stroke = progressBrush;
            Children.Add(_progressPath);

            _valueText = CreateGaugeText("--%", 24.5, FontWeights.Bold, Colors.White);
            _valueBrush = (SolidColorBrush)_valueText.Foreground;
            _valueText.Margin = new Thickness(0, -12, 0, 0);
            Children.Add(_valueText);

            TextBlock caption = CreateGaugeText("剩余", 9.2, FontWeights.Normal, Color.FromRgb(178, 195, 220));
            caption.Margin = new Thickness(0, 31, 0, 0);
            Children.Add(caption);

            SizeChanged += delegate { UpdateArc(); };
        }

        public void SetAccent(Color primary, Color secondary, int milliseconds)
        {
            _accent = primary;
            _accentSecondary = secondary;
            AnimateAccentColor(_progressAccentStart, GradientStop.ColorProperty, primary, milliseconds);
            AnimateAccentColor(_progressAccentEnd, GradientStop.ColorProperty, secondary, milliseconds);
            AnimateAccentColor(_ringAccentStart, GradientStop.ColorProperty, primary, milliseconds);
            AnimateAccentColor(_ringAccentEnd, GradientStop.ColorProperty, secondary, milliseconds);
            AnimateAccentColor(_haloAccentStart, GradientStop.ColorProperty,
                Color.FromArgb(225, primary.R, primary.G, primary.B), milliseconds);
            AnimateAccentColor(_haloAccentEnd, GradientStop.ColorProperty,
                Color.FromArgb(175, secondary.R, secondary.G, secondary.B), milliseconds);
            AnimateAccentColor(_rippleBrush, SolidColorBrush.ColorProperty,
                Color.FromArgb(230, primary.R, primary.G, primary.B), milliseconds);
        }

        public void SetHover(bool active, int milliseconds)
        {
            bool wasActive = _hoverActive;
            _hoverActive = active;
            double targetHaloOpacity = active ? 0.94 : 0;
            double targetHaloScale = active ? 1 : 0.82;
            double targetRingOpacity = active ? 0.48 : 0;
            double targetRingScale = active ? 1 : 0.94;
            double targetStroke = active ? 8.0 : 6.5;
            Color targetTextColor = active ? Color.FromRgb(232, 255, 249) : Colors.White;
            Duration duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(active ? 190 : 120, milliseconds)));
            CubicEase easing = new CubicEase();
            easing.EasingMode = EasingMode.EaseOut;

            AnimateDouble(_hoverHalo, UIElement.OpacityProperty, _hoverHalo.Opacity, targetHaloOpacity, duration, easing);
            AnimateDouble(_hoverHaloScale, ScaleTransform.ScaleXProperty, _hoverHaloScale.ScaleX, targetHaloScale, duration, easing);
            AnimateDouble(_hoverHaloScale, ScaleTransform.ScaleYProperty, _hoverHaloScale.ScaleY, targetHaloScale, duration, easing);
            AnimateDouble(_hoverRing, UIElement.OpacityProperty, _hoverRing.Opacity, targetRingOpacity, duration, easing);
            AnimateDouble(_hoverRingScale, ScaleTransform.ScaleXProperty, _hoverRingScale.ScaleX, targetRingScale, duration, easing);
            AnimateDouble(_hoverRingScale, ScaleTransform.ScaleYProperty, _hoverRingScale.ScaleY, targetRingScale, duration, easing);
            AnimateDouble(_progressPath, Shape.StrokeThicknessProperty, _progressPath.StrokeThickness, targetStroke, duration, easing);

            ColorAnimation textAnimation = new ColorAnimation(_valueBrush.Color, targetTextColor, duration);
            textAnimation.EasingFunction = easing;
            textAnimation.FillBehavior = FillBehavior.Stop;
            _valueBrush.Color = targetTextColor;
            _valueBrush.BeginAnimation(SolidColorBrush.ColorProperty, textAnimation);

            if (active && !wasActive) StartHoverRipple();
            else if (!active) FadeHoverRipple();
        }

        private static void AnimateDouble(DependencyObject target, DependencyProperty property, double from, double to, Duration duration, IEasingFunction easing)
        {
            DoubleAnimation animation = new DoubleAnimation(from, to, duration);
            animation.EasingFunction = easing;
            animation.FillBehavior = FillBehavior.Stop;
            target.SetValue(property, to);
            UIElement element = target as UIElement;
            if (element != null)
            {
                element.BeginAnimation(property, animation);
                return;
            }

            Animatable animatable = target as Animatable;
            if (animatable != null) animatable.BeginAnimation(property, animation);
        }

        private void StartHoverRipple()
        {
            Duration duration = new Duration(TimeSpan.FromMilliseconds(430));
            CubicEase outwardEase = new CubicEase();
            outwardEase.EasingMode = EasingMode.EaseOut;

            DoubleAnimationUsingKeyFrames opacity = new DoubleAnimationUsingKeyFrames();
            opacity.Duration = duration;
            opacity.FillBehavior = FillBehavior.Stop;
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85)), outwardEase));
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430)), outwardEase));

            DoubleAnimation scaleX = new DoubleAnimation(0.72, 1.08, duration);
            scaleX.EasingFunction = outwardEase;
            scaleX.FillBehavior = FillBehavior.Stop;
            DoubleAnimation scaleY = new DoubleAnimation(0.72, 1.08, duration);
            scaleY.EasingFunction = outwardEase;
            scaleY.FillBehavior = FillBehavior.Stop;

            _hoverRipple.Opacity = 0;
            _hoverRippleScale.ScaleX = 1.08;
            _hoverRippleScale.ScaleY = 1.08;
            _hoverRipple.BeginAnimation(UIElement.OpacityProperty, opacity);
            _hoverRippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            _hoverRippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }

        private void FadeHoverRipple()
        {
            Duration duration = new Duration(TimeSpan.FromMilliseconds(100));
            CubicEase easing = new CubicEase();
            easing.EasingMode = EasingMode.EaseOut;
            AnimateDouble(_hoverRipple, UIElement.OpacityProperty, _hoverRipple.Opacity, 0, duration, easing);
        }

        private static TextBlock CreateGaugeText(string text, double size, FontWeight weight, Color color)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontFamily = new FontFamily("Microsoft YaHei UI");
            block.FontSize = size;
            block.FontWeight = weight;
            block.Foreground = new SolidColorBrush(color);
            block.HorizontalAlignment = HorizontalAlignment.Center;
            block.VerticalAlignment = VerticalAlignment.Center;
            block.TextAlignment = TextAlignment.Center;
            return block;
        }

        private static void AnimateAccentColor(Animatable target, DependencyProperty property,
            Color targetColor, int milliseconds)
        {
            Color current = (Color)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, targetColor);
            if (milliseconds <= 0 || current.Equals(targetColor)) return;

            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            ColorAnimation animation = new ColorAnimation(current, targetColor,
                new Duration(TimeSpan.FromMilliseconds(milliseconds)));
            animation.EasingFunction = easing;
            animation.FillBehavior = FillBehavior.Stop;
            target.BeginAnimation(property, animation);
        }

        private void UpdateArc()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0 || !_remainingPercent.HasValue || _remainingPercent.Value <= 0)
            {
                _progressPath.Data = null;
                return;
            }

            double radius = Math.Min(width, height) / 2 - 6.5;
            Point center = new Point(width / 2, height / 2);
            int percent = Math.Max(0, Math.Min(100, _remainingPercent.Value));
            if (percent >= 100)
            {
                _progressPath.Data = new EllipseGeometry(center, radius, radius);
                return;
            }

            double sweep = percent * 3.6;
            Point start = PointOnCircle(center, radius, -90);
            Point end = PointOnCircle(center, radius, -90 + sweep);
            PathFigure figure = new PathFigure();
            figure.StartPoint = start;
            figure.IsClosed = false;
            ArcSegment arc = new ArcSegment();
            arc.Point = end;
            arc.Size = new Size(radius, radius);
            arc.IsLargeArc = sweep > 180;
            arc.SweepDirection = SweepDirection.Clockwise;
            figure.Segments.Add(arc);
            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            _progressPath.Data = geometry;
        }

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
    }
}
