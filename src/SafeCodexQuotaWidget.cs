using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SafeCodexQuotaWidget
{
    internal static class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
            {
                return RunProbe();
            }

            bool createdNew;
            _singleInstance = new Mutex(true, @"Local\SafeCodexQuotaWidget", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Codex 额度悬浮窗已经在运行。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            NativeMethods.EnableHighDpiRendering();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WidgetForm());
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
            return 0;
        }

        private static int RunProbe()
        {
            try
            {
                QuotaSnapshot snapshot = CodexQuotaClient.ReadQuotaAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.OutputEncoding = Encoding.UTF8;
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

    internal sealed class WidgetForm : Form
    {
        private readonly Color _background = Color.FromArgb(17, 21, 34);
        private readonly Label _stateLabel;
        private readonly Label _primaryValue;
        private readonly Label _secondaryValue;
        private readonly Label _primaryCaption;
        private readonly Label _secondaryCaption;
        private readonly Label _planValue;
        private readonly Label _statusLabel;
        private readonly PercentDial _dial;
        private readonly Button _pinButton;
        private readonly Button _refreshButton;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private Color _accent = Color.FromArgb(95, 218, 167);
        private bool _refreshing;

        public WidgetForm()
        {
            Text = "安全版 Codex 额度";
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(392, 248);
            MinimumSize = MaximumSize = Size;
            BackColor = _background;
            ForeColor = Color.White;
            Opacity = 1.0;
            TopMost = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;

            Label title = NewLabel("Codex 额度", 32, 5, 150, 30, 12.5F, FontStyle.Bold, Color.White);
            _stateLabel = NewLabel("读取中", 32, 34, 150, 18, 8F, FontStyle.Regular, Color.FromArgb(172, 185, 205));

            _pinButton = NewButton("置顶", 228, 6, 58, 29);
            _pinButton.Click += delegate { TopMost = !TopMost; UpdatePinButton(); };
            _refreshButton = NewButton("↻", 292, 6, 28, 29);
            _refreshButton.Font = new Font("Segoe UI Symbol", 13F, FontStyle.Bold);
            ((GlassButton)_refreshButton).TextOffsetY = -2;
            _refreshButton.Click += async delegate { await RefreshQuotaAsync(); };
            Button minimizeButton = NewButton("−", 326, 6, 28, 29);
            minimizeButton.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            ((GlassButton)minimizeButton).TextOffsetY = -2;
            minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            Button closeButton = NewButton("×", 360, 6, 28, 29);
            closeButton.Font = new Font("Segoe UI", 13F, FontStyle.Regular);
            ((GlassButton)closeButton).TextOffsetY = -2;
            closeButton.ForeColor = Color.FromArgb(255, 151, 151);
            closeButton.Click += delegate { ExitApplication(); };

            _dial = new PercentDial();
            _dial.Location = new Point(14, 64);
            _dial.Size = new Size(128, 128);
            _dial.BackColor = Color.Transparent;
            _dial.RemainingPercent = null;
            _dial.Accent = _accent;

            AddCard("主额度窗口", out _primaryCaption, out _primaryValue, 150, 61, 228, 50);
            AddCard("短周期窗口", out _secondaryCaption, out _secondaryValue, 150, 116, 228, 50);
            Label planCaption;
            AddCard("账户方案", out planCaption, out _planValue, 150, 171, 228, 34, true);

            _statusLabel = NewLabel("正在通过已签名的 Codex 读取额度…", 18, 219, 356, 16, 7.5F, FontStyle.Regular, Color.FromArgb(151, 165, 188));
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Cursor = Cursors.Help;
            _statusLabel.DoubleClick += delegate { ShowStatusDetails(); };

            Controls.AddRange(new Control[] { title, _stateLabel, _pinButton, _refreshButton, minimizeButton, closeButton, _dial, _statusLabel });

            AttachDrag(this);
            AttachDrag(title);
            AttachDrag(_stateLabel);

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 60000;
            _refreshTimer.Tick += async delegate { await RefreshQuotaAsync(); };

            Load += async delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Location = new Point(area.Right - Width - 20, area.Top + 20);
                UpdateRoundedRegion();
                _refreshTimer.Start();
                await RefreshQuotaAsync();
            };
            FormClosing += OnFormClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle &= ~0x00020000;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.DisableWindowShadow(Handle);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = RoundedRectangle(bounds, 30))
            using (LinearGradientBrush background = new LinearGradientBrush(
                bounds,
                Color.FromArgb(16, 23, 38),
                Color.FromArgb(35, 25, 55),
                28F))
            {
                e.Graphics.FillPath(background, path);
                Region oldClip = e.Graphics.Clip;
                e.Graphics.SetClip(path);
                DrawGlow(e.Graphics, new Rectangle(250, -76, 210, 210), Color.FromArgb(105, 67, 210, 255));
                DrawGlow(e.Graphics, new Rectangle(-88, 102, 220, 220), Color.FromArgb(78, 82, 238, 184));
                e.Graphics.Clip = oldClip;
                oldClip.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            Rectangle borderBounds = new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            using (GraphicsPath path = RoundedRectangle(borderBounds, 29))
            using (LinearGradientBrush borderBrush = new LinearGradientBrush(
                borderBounds,
                Color.FromArgb(210, _accent),
                Color.FromArgb(190, 173, 148, 255),
                18F))
            using (Pen border = new Pen(borderBrush, 1.15F))
            {
                e.Graphics.DrawPath(border, path);
            }
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(45, _accent)))
            using (SolidBrush dot = new SolidBrush(_accent))
            {
                e.Graphics.FillEllipse(halo, 12, 15, 15, 15);
                e.Graphics.FillEllipse(dot, 15, 18, 9, 9);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
        }

        private void AddCard(string caption, out Label captionLabel, out Label valueLabel, int x, int y, int width, int height, bool compact = false)
        {
            GradientCard card = new GradientCard();
            card.Location = new Point(x, y);
            card.Size = new Size(width, height);
            captionLabel = NewDetachedLabel(caption, 14, compact ? 7 : 5, 110, 17, 8F, FontStyle.Regular, Color.FromArgb(168, 181, 205));
            valueLabel = NewDetachedLabel("--", compact ? 96 : 14, compact ? 5 : 22, compact ? 118 : 204, compact ? 23 : 23, compact ? 10.5F : 10.5F, FontStyle.Bold, Color.White);
            if (compact) valueLabel.TextAlign = ContentAlignment.MiddleRight;
            card.Controls.Add(captionLabel);
            card.Controls.Add(valueLabel);
            Controls.Add(card);
        }

        private Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = NewDetachedLabel(text, x, y, width, height, size, style, color);
            Controls.Add(label);
            return label;
        }

        private static Label NewDetachedLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(x, y);
            label.Size = new Size(width, height);
            label.Font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private Button NewButton(string text, int x, int y, int width, int height)
        {
            GlassButton button = new GlassButton();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, height);
            button.ForeColor = Color.FromArgb(215, 224, 232);
            button.TabStop = false;
            return button;
        }

        private static void DrawGlow(Graphics graphics, Rectangle bounds, Color centerColor)
        {
            using (GraphicsPath ellipse = new GraphicsPath())
            {
                ellipse.AddEllipse(bounds);
                using (PathGradientBrush glow = new PathGradientBrush(ellipse))
                {
                    glow.CenterColor = centerColor;
                    glow.SurroundColors = new[] { Color.FromArgb(0, centerColor.R, centerColor.G, centerColor.B) };
                    graphics.FillPath(glow, ellipse);
                }
            }
        }

        private async Task RefreshQuotaAsync()
        {
            if (_refreshing || _closing.IsCancellationRequested) return;
            _refreshing = true;
            _refreshButton.Enabled = false;
            _stateLabel.Text = "安全读取中";
            _statusLabel.Text = "正在验证 OpenAI 签名并读取额度…";
            try
            {
                QuotaSnapshot snapshot = await CodexQuotaClient.ReadQuotaAsync(_closing.Token);
                int? remaining = LowestRemaining(snapshot.Primary, snapshot.Secondary);
                SetAccent(remaining);
                _dial.RemainingPercent = remaining;
                _dial.Accent = _accent;
                _primaryValue.Text = FormatWindow(snapshot.Primary);
                _secondaryValue.Text = snapshot.Secondary == null ? "当前未提供" : FormatWindow(snapshot.Secondary);
                _primaryCaption.Text = FormatWindowCaption(snapshot.Primary, "主额度窗口");
                _secondaryCaption.Text = FormatWindowCaption(snapshot.Secondary, "短周期窗口");
                _planValue.Text = FormatPlanType(snapshot.PlanType);
                _stateLabel.Text = StateText(remaining);
                _statusLabel.Text = "已安全读取 · " + DateTime.Now.ToString("HH:mm") + " · 60 秒后刷新";
                _statusLabel.Tag = snapshot.CodexPath;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetAccent(null);
                _stateLabel.Text = "读取失败";
                _dial.RemainingPercent = null;
                _dial.Accent = _accent;
                _primaryValue.Text = "--";
                _secondaryValue.Text = "--";
                _primaryCaption.Text = "主额度窗口";
                _secondaryCaption.Text = "短周期窗口";
                _planValue.Text = "--";
                _statusLabel.Text = FriendlyError(ex.Message);
                _statusLabel.Tag = ex.ToString();
            }
            finally
            {
                _refreshButton.Enabled = true;
                _refreshing = false;
            }
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
            if (remaining.Value < 10) return "额度偏低";
            return "额度充足";
        }

        private void SetAccent(int? remaining)
        {
            _accent = !remaining.HasValue
                ? Color.FromArgb(142, 155, 170)
                : remaining.Value == 0
                    ? Color.FromArgb(255, 103, 115)
                    : remaining.Value < 10
                        ? Color.FromArgb(255, 196, 87)
                        : Color.FromArgb(95, 218, 167);
            Invalidate();
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

        private void UpdatePinButton()
        {
            _pinButton.Text = TopMost ? "置顶" : "普通";
            _pinButton.ForeColor = TopMost ? _accent : Color.FromArgb(180, 190, 201);
        }

        private void ShowSecurityInfo()
        {
            string path = _statusLabel.Tag as string;
            MessageBox.Show(
                "本程序不读取 Token、不写注册表、不开机自启、无网络库。\r\n\r\n" +
                "只会直接启动经 Windows 验签且发布者为 OpenAI 的 codex.exe。\r\n\r\n" +
                "本次路径：\r\n" + (string.IsNullOrEmpty(path) ? "尚未成功读取" : path),
                "安全说明",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ShowStatusDetails()
        {
            string detail = _statusLabel.Tag as string;
            MessageBox.Show(
                string.IsNullOrWhiteSpace(detail) ? _statusLabel.Text : detail,
                "额度读取详情",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ExitApplication()
        {
            _closing.Cancel();
            _refreshTimer.Stop();
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _closing.Cancel();
            _refreshTimer.Dispose();
            _closing.Dispose();
        }

        private void AttachDrag(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
            };
        }

        private void UpdateRoundedRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Region old = Region;
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 30))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
        }

        internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class GradientCard : Panel
    {
        public GradientCard()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = WidgetForm.RoundedRectangle(bounds, 18))
            using (LinearGradientBrush fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(39, 50, 75),
                Color.FromArgb(42, 35, 66),
                22F))
            {
                e.Graphics.FillPath(fill, path);
            }
        }
    }

    internal sealed class GlassButton : Button
    {
        private bool _hover;
        private bool _pressed;

        public int TextOffsetY { get; set; }

        public GlassButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.FromArgb(37, 43, 67);
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            Region old = Region;
            using (GraphicsPath path = WidgetForm.RoundedRectangle(new Rectangle(0, 0, Width, Height), 14))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pressed = e.Button == MouseButtons.Left;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color start = _pressed
                ? Color.FromArgb(57, 67, 98)
                : _hover ? Color.FromArgb(64, 78, 113) : Color.FromArgb(45, 56, 83);
            Color end = _pressed
                ? Color.FromArgb(62, 49, 92)
                : _hover ? Color.FromArgb(63, 57, 104) : Color.FromArgb(47, 42, 72);
            using (GraphicsPath path = WidgetForm.RoundedRectangle(bounds, 14))
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, start, end, 28F))
            {
                e.Graphics.FillPath(fill, path);
            }

            Rectangle textBounds = bounds;
            textBounds.Offset(0, TextOffsetY);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                Enabled ? ForeColor : Color.FromArgb(110, ForeColor),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class PercentDial : Control
    {
        private int? _remainingPercent;
        private Color _accent;

        public int? RemainingPercent
        {
            get { return _remainingPercent; }
            set { _remainingPercent = value; Invalidate(); }
        }

        public Color Accent
        {
            get { return _accent; }
            set { _accent = value; Invalidate(); }
        }

        public PercentDial()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);
            _accent = Color.FromArgb(95, 218, 167);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            RectangleF circle = new RectangleF(8, 8, Width - 16, Height - 16);
            Rectangle gradientBounds = Rectangle.Round(circle);
            using (GraphicsPath basePath = new GraphicsPath())
            using (Pen track = new Pen(Color.FromArgb(72, 93, 111, 145), 8F))
            {
                basePath.AddEllipse(circle);
                using (PathGradientBrush baseBrush = new PathGradientBrush(basePath))
                {
                    baseBrush.CenterColor = Color.FromArgb(35, 34, 61);
                    baseBrush.SurroundColors = new[] { Color.FromArgb(25, 35, 55) };
                    e.Graphics.FillEllipse(baseBrush, circle);
                }
                e.Graphics.DrawArc(track, circle, -90, 360);
                if (_remainingPercent.HasValue && _remainingPercent.Value > 0)
                {
                    using (LinearGradientBrush progressBrush = new LinearGradientBrush(
                        gradientBounds,
                        _accent,
                        Color.FromArgb(93, 188, 255),
                        30F))
                    using (Pen progress = new Pen(progressBrush, 8F))
                    {
                        progress.StartCap = LineCap.Round;
                        progress.EndCap = LineCap.Round;
                        e.Graphics.DrawArc(progress, circle, -90, 360F * _remainingPercent.Value / 100F);
                    }
                }
            }

            string value = _remainingPercent.HasValue ? _remainingPercent.Value + "%" : "--%";
            using (Font valueFont = new Font("Segoe UI", 23F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font captionFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush white = new SolidBrush(Color.White))
            using (SolidBrush muted = new SolidBrush(Color.FromArgb(174, 187, 210)))
            {
                SizeF valueSize = e.Graphics.MeasureString(value, valueFont);
                float valueY = (Height - valueSize.Height) / 2F - 7F;
                e.Graphics.DrawString(value, valueFont, white, (Width - valueSize.Width) / 2F, valueY);
                string caption = "剩余";
                SizeF captionSize = e.Graphics.MeasureString(caption, captionFont);
                e.Graphics.DrawString(caption, captionFont, muted, (Width - captionSize.Width) / 2F, valueY + valueSize.Height - 1F);
            }
        }
    }

    internal sealed class QuotaWindow
    {
        public int UsedPercent { get; set; }
        public int RemainingPercent { get; set; }
        public int? WindowDurationMinutes { get; set; }
        public DateTime? ResetsAt { get; set; }
    }

    internal static class PlanDisplayFormatter
    {
        public static string Format(string planType)
        {
            if (string.IsNullOrWhiteSpace(planType)) return "未知";
            if (string.Equals(planType, "prolite", StringComparison.OrdinalIgnoreCase)) return "Pro 5×";
            if (string.Equals(planType, "pro", StringComparison.OrdinalIgnoreCase)) return "Pro 20×";
            return planType.ToUpperInvariant();
        }
    }

    internal enum ExtraQuotaKind
    {
        SecondaryWindow,
        ModelLimit
    }

    internal sealed class ExtraQuota
    {
        public ExtraQuotaKind Kind { get; set; }
        public string LimitId { get; set; }
        public string LimitName { get; set; }
        public QuotaWindow Window { get; set; }
    }

    internal sealed class QuotaSnapshot
    {
        public string PlanType { get; set; }
        public QuotaWindow Primary { get; set; }
        public QuotaWindow Secondary { get; set; }
        public ExtraQuota Extra { get; set; }
        public string CodexPath { get; set; }
        public string Publisher { get; set; }
    }

    internal static class AsyncRetryPolicy
    {
        public static async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            int[] retryDelaysMilliseconds,
            Func<Exception, bool> shouldRetry,
            Action<int, int, int, Exception> onRetry,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (retryDelaysMilliseconds == null) throw new ArgumentNullException("retryDelaysMilliseconds");

            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Exception failure;
                try
                {
                    return await operation();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (attempt >= retryDelaysMilliseconds.Length ||
                    (shouldRetry != null && !shouldRetry(failure)))
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    throw failure;
                }

                int delayMilliseconds = retryDelaysMilliseconds[attempt];
                if (delayMilliseconds < 0)
                    throw new ArgumentOutOfRangeException("retryDelaysMilliseconds", delayMilliseconds, "重试间隔不能为负数。");

                if (onRetry != null)
                {
                    onRetry(attempt + 1, retryDelaysMilliseconds.Length, delayMilliseconds, failure);
                }

                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }
            }
        }
    }

    internal static class CodexQuotaClient
    {
        private const int TimeoutMilliseconds = 12000;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken)
        {
            TrustedExecutable trusted = TrustedCodexLocator.Find();
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = trusted.Path;
            start.Arguments = "app-server --listen stdio://";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            // .NET Framework's compile-time reference does not expose this newer
            // property, but current Windows runtimes do. Set it before starting
            // so the first JSON-RPC request is not prefixed with a UTF-8 BOM.
            System.Reflection.PropertyInfo inputEncodingProperty =
                typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");
            if (inputEncodingProperty != null)
                inputEncodingProperty.SetValue(start, Utf8NoBom, null);
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = new Process())
            {
                process.StartInfo = start;
                StringBuilder error = new StringBuilder();
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        lock (error) error.AppendLine(e.Data);
                    }
                };

                try
                {
                    Encoding previousConsoleInputEncoding = null;
                    bool restoreConsoleInputEncoding = false;
                    if (inputEncodingProperty == null)
                    {
                        try
                        {
                            previousConsoleInputEncoding = Console.InputEncoding;
                            Console.InputEncoding = Utf8NoBom;
                            restoreConsoleInputEncoding = true;
                        }
                        catch { }
                    }
                    try
                    {
                        if (!process.Start()) throw new InvalidOperationException("无法启动已验证的 Codex。 ");
                    }
                    finally
                    {
                        if (restoreConsoleInputEncoding)
                        {
                            try { Console.InputEncoding = previousConsoleInputEncoding; } catch { }
                        }
                    }
                    process.BeginErrorReadLine();
                    await SendRequestAsync(process, 1, "initialize", new Dictionary<string, object>
                    {
                        { "clientInfo", new Dictionary<string, object>
                            {
                                { "name", "safe-codex-quota-widget" },
                                { "title", "Safe Codex Quota Widget" },
                                { "version", "1.0.0" }
                            }
                        },
                        { "capabilities", null }
                    }, cancellationToken);

                    Dictionary<string, object> response = await SendRequestAsync(process, 2, "account/rateLimits/read", null, cancellationToken);
                    Dictionary<string, object> result = GetDictionary(response, "result");
                    if (result == null) throw new InvalidOperationException("Codex 未返回额度结果。 ");
                    QuotaSnapshot snapshot = ParseSnapshot(result);
                    snapshot.CodexPath = trusted.Path;
                    snapshot.Publisher = trusted.Publisher;
                    return snapshot;
                }
                catch (Exception ex)
                {
                    string details;
                    lock (error) details = error.ToString().Trim();
                    if (!string.IsNullOrEmpty(details) && !(ex is OperationCanceledException))
                        throw new InvalidOperationException(details, ex);
                    throw;
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch { }
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.WaitForExit(1500); } catch { }
                }
            }
        }

        private static async Task<Dictionary<string, object>> SendRequestAsync(Process process, int id, string method, object parameters, CancellationToken cancellationToken)
        {
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["id"] = id;
            request["method"] = method;
            if (parameters != null) request["params"] = parameters;
            byte[] requestBytes = Utf8NoBom.GetBytes(Json.Serialize(request) + "\n");
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<string> readTask = process.StandardOutput.ReadLineAsync();
                Task delayTask = Task.Delay(TimeoutMilliseconds, cancellationToken);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed != readTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Codex 请求超时：" + method);
                }

                string line = await readTask;
                if (line == null) throw new InvalidOperationException("Codex 在返回额度前退出。 ");
                Dictionary<string, object> message;
                try { message = Json.DeserializeObject(line) as Dictionary<string, object>; }
                catch { continue; }
                if (message == null || !message.ContainsKey("id")) continue;
                if (Convert.ToInt32(message["id"]) != id) continue;
                Dictionary<string, object> rpcError = GetDictionary(message, "error");
                if (rpcError != null)
                {
                    object errorMessage;
                    throw new InvalidOperationException(rpcError.TryGetValue("message", out errorMessage) ? Convert.ToString(errorMessage) : "Codex 返回错误。 ");
                }
                return message;
            }
        }

        private static QuotaSnapshot ParseSnapshot(Dictionary<string, object> result)
        {
            Dictionary<string, object> snapshot = null;
            Dictionary<string, object> byId = GetDictionary(result, "rateLimitsByLimitId");
            if (byId != null)
            {
                snapshot = GetDictionary(byId, "codex");
                if (snapshot == null)
                {
                    snapshot = byId.Values
                        .OfType<Dictionary<string, object>>()
                        .FirstOrDefault(candidate => string.Equals(
                            ReadString(candidate, "limitId"), "codex", StringComparison.OrdinalIgnoreCase));
                }
            }
            if (snapshot == null)
            {
                Dictionary<string, object> legacy = GetDictionary(result, "rateLimits");
                string legacyId = ReadString(legacy, "limitId");
                if (legacy != null && (string.IsNullOrWhiteSpace(legacyId) ||
                    string.Equals(legacyId, "codex", StringComparison.OrdinalIgnoreCase)))
                    snapshot = legacy;
            }
            if (snapshot == null) throw new InvalidOperationException("Codex 未返回可识别的额度快照。 ");

            object plan;
            QuotaSnapshot value = new QuotaSnapshot();
            value.PlanType = snapshot.TryGetValue("planType", out plan) ? Convert.ToString(plan) : "unknown";
            value.Primary = ParseWindow(GetDictionary(snapshot, "primary"));
            value.Secondary = ParseWindow(GetDictionary(snapshot, "secondary"));
            if (value.Secondary != null)
            {
                value.Extra = new ExtraQuota
                {
                    Kind = ExtraQuotaKind.SecondaryWindow,
                    LimitId = "codex",
                    LimitName = "额外额度",
                    Window = value.Secondary
                };
            }
            else if (byId != null)
            {
                value.Extra = byId
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => ParseModelExtra(pair.Key, pair.Value as Dictionary<string, object>))
                    .Where(extra => extra != null)
                    .OrderBy(extra => extra.Window.RemainingPercent)
                    .ThenBy(extra => extra.LimitId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            return value;
        }

        private static ExtraQuota ParseModelExtra(string dictionaryKey, Dictionary<string, object> input)
        {
            if (input == null) return null;
            string limitId = ReadString(input, "limitId");
            if (string.IsNullOrWhiteSpace(limitId)) limitId = dictionaryKey;
            if (string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dictionaryKey, "codex", StringComparison.OrdinalIgnoreCase)) return null;

            string limitName = ReadString(input, "limitName");
            if (string.IsNullOrWhiteSpace(limitName)) return null;
            QuotaWindow window = ParseWindow(GetDictionary(input, "primary")) ??
                                 ParseWindow(GetDictionary(input, "secondary"));
            if (window == null) return null;
            return new ExtraQuota
            {
                Kind = ExtraQuotaKind.ModelLimit,
                LimitId = limitId,
                LimitName = limitName,
                Window = window
            };
        }

        private static QuotaWindow ParseWindow(Dictionary<string, object> input)
        {
            if (input == null) return null;
            int parsedUsed;
            if (!TryToInt(input, "usedPercent", out parsedUsed)) return null;
            int used = Clamp(parsedUsed);
            long resetSeconds = ToLong(input, "resetsAt", 0);
            int duration = ToInt(input, "windowDurationMins", -1);
            return new QuotaWindow
            {
                UsedPercent = used,
                RemainingPercent = Clamp(100 - used),
                WindowDurationMinutes = duration >= 0 ? (int?)duration : null,
                ResetsAt = resetSeconds > 0 ? (DateTime?)new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(resetSeconds) : null
            };
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> input, string key)
        {
            if (input == null) return null;
            object value;
            return input.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static int ToInt(Dictionary<string, object> input, string key, int fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt32(value); } catch { return fallback; }
        }

        private static bool TryToInt(Dictionary<string, object> input, string key, out int parsed)
        {
            parsed = 0;
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null) return false;
            try
            {
                parsed = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadString(Dictionary<string, object> input, string key)
        {
            if (input == null) return null;
            object value;
            return input.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }

        private static long ToLong(Dictionary<string, object> input, string key, long fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt64(value); } catch { return fallback; }
        }

        private static int Clamp(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }
    }

    internal sealed class TrustedExecutable
    {
        public string Path { get; set; }
        public string Publisher { get; set; }
    }

    internal static class TrustedCodexLocator
    {
        public static TrustedExecutable Find()
        {
            List<string> candidates = GetCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> rejected = new List<string>();
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate) || !IsAllowedLocation(candidate)) continue;
                string publisher;
                if (AuthenticodeVerifier.IsTrustedOpenAIFile(candidate, out publisher))
                    return new TrustedExecutable { Path = Path.GetFullPath(candidate), Publisher = publisher };
                rejected.Add(candidate);
            }

            if (rejected.Count > 0)
                throw new InvalidOperationException("找到了 Codex，但其 Windows 签名或 OpenAI 发布者校验未通过；为安全起见没有启动。 ");
            throw new FileNotFoundException("未在 OpenAI Codex 的受限安装目录中找到 codex.exe。请先安装并启动一次 Codex。 ");
        }

        private static IEnumerable<string> GetCandidates()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            yield return Path.Combine(localBin, "codex.exe");

            if (Directory.Exists(localBin))
            {
                DirectoryInfo root = new DirectoryInfo(localBin);
                DirectoryInfo[] directories;
                try { directories = root.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).ToArray(); }
                catch { directories = new DirectoryInfo[0]; }
                foreach (DirectoryInfo directory in directories)
                    yield return Path.Combine(directory.FullName, "codex.exe");
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string rawDirectory in pathValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length == 0) continue;
                string candidate;
                try { candidate = Path.Combine(directory, "codex.exe"); }
                catch { continue; }
                yield return candidate;
            }
        }

        private static bool IsAllowedLocation(string candidate)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { return false; }

            string localRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase)) return true;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string packageRoot = Path.Combine(programFiles, "WindowsApps", "OpenAI.Codex_");
            return full.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) &&
                   full.EndsWith(Path.Combine("app", "resources", "codex.exe"), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsTrustedOpenAIFile(string path, out string publisher)
        {
            publisher = null;
            if (!VerifyFileOffline(path)) return false;
            try
            {
                using (X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    publisher = certificate.Subject;
                    return publisher.IndexOf("OpenAI", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool VerifyFileOffline(string path)
        {
            WinTrustFileInfo fileInfo = new WinTrustFileInfo(path);
            IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                WinTrustData trustData = new WinTrustData(fileInfoPointer);
                Guid action = GenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo
        {
            public int cbStruct = Marshal.SizeOf(typeof(WinTrustFileInfo));
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile = IntPtr.Zero;
            public IntPtr pgKnownSubject = IntPtr.Zero;

            public WinTrustFileInfo(string path)
            {
                pcwszFilePath = path;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;

            public WinTrustData(IntPtr fileInfo)
            {
                cbStruct = Marshal.SizeOf(typeof(WinTrustData));
                pPolicyCallbackData = IntPtr.Zero;
                pSIPClientData = IntPtr.Zero;
                dwUIChoice = 2;
                fdwRevocationChecks = 0;
                dwUnionChoice = 1;
                pFile = fileInfo;
                dwStateAction = 0;
                hWVTStateData = IntPtr.Zero;
                pwszURLReference = IntPtr.Zero;
                dwProvFlags = 0x00001000;
                dwUIContext = 0;
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            public int X;
            public int Y;
        }

        public static void DisableWindowShadow(IntPtr handle)
        {
            try
            {
                int disabled = 1;
                DwmSetWindowAttribute(handle, 2, ref disabled, Marshal.SizeOf(typeof(int)));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public static void EnableHighDpiRendering()
        {
            try
            {
                SetProcessDpiAwareness(2);
            }
            catch (DllNotFoundException)
            {
                TryLegacyDpiAwareness();
            }
            catch (EntryPointNotFoundException)
            {
                TryLegacyDpiAwareness();
            }
        }

        private static void TryLegacyDpiAwareness()
        {
            try { SetProcessDPIAware(); }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);
    }
}
