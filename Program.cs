using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TekDosyaSeriPort
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorForm());
        }
    }

    // --- DİL DESTEĞİ YÖNETİCİSİ ---
    public static class Lang
    {
        public static bool IsEnglish = false;
        public static event Action? Changed;
        public static string Get(string tr, string en) => IsEnglish ? en : tr;
        public static void Toggle() { IsEnglish = !IsEnglish; Changed?.Invoke(); }
    }

    // --- KELİME VURGULAMA VERİ MODELİ ---
    public class HighlightItem
    {
        public string Word { get; set; } = string.Empty;
        public int ForeArgb { get; set; }
        public int BackArgb { get; set; }
    }

    // FIX #8: Emoji desteği için özel RichTextBox
    // FIX NULL: Null-safe event çağrıları
    public class ScrollAwareRichTextBox : RichTextBox
    {
        public event EventHandler? ScrolledToBottom;
        public event EventHandler? Scrolled;
        public event EventHandler? CustomDoubleClick;
        public Func<Keys, bool>? CmdKeyInterceptor;

        [DllImport("user32.dll")]
        private static extern bool GetScrollRange(IntPtr hWnd, int nBar, out int lpMinPos, out int lpMaxPos);
        [DllImport("user32.dll")]
        private static extern int GetScrollPos(IntPtr hWnd, int nBar);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int SB_VERT = 1;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int EM_LINESCROLL = 0x00B6;

        // FIX #8: Emoji ve Unicode desteği için LanguageOption
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // ES_MULTILINE | ES_AUTOVSCROLL
                cp.Style |= 0x00000040;
                return cp;
            }
        }

        public ScrollAwareRichTextBox()
        {
            // FIX #8: Segoe UI Emoji veya benzeri font ile emoji desteği
            // LanguageOption 1 = orcNoIME için geçerli; emoji için varsayılan yeterli
        }

        public bool IsAtBottom()
        {
            GetScrollRange(this.Handle, SB_VERT, out _, out int max);
            int pos = GetScrollPos(this.Handle, SB_VERT);
            return pos >= max - 5;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (CmdKeyInterceptor != null && CmdKeyInterceptor(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0203) CustomDoubleClick?.Invoke(this, EventArgs.Empty);

            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int linesToScroll = -(delta / 120) * 3;
                SendMessage(this.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)linesToScroll);
                Scrolled?.Invoke(this, EventArgs.Empty);
                if (IsAtBottom()) ScrolledToBottom?.Invoke(this, EventArgs.Empty);
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);

            if (m.Msg == 0x0115 || m.Msg == 0x000F || m.Msg == 0x0100)
            {
                Scrolled?.Invoke(this, EventArgs.Empty);
                if (IsAtBottom()) ScrolledToBottom?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public class MonitorForm : Form
    {
        private TableLayoutPanel _mainLayout = null!;
        private FlowLayoutPanel _statusBar = null!;

        private Dictionary<GroupBox, SerialPort> _portMap = new();
        private HashSet<string> _ignoredPorts = new();
        private HashSet<string> _busyPorts = new();

        private Dictionary<string, bool> _atLineStart = new();
        private Dictionary<string, bool> _scrollFrozen = new();
        private Dictionary<string, bool> _logAtLineStart = new();
        private Dictionary<string, bool> _loggingEnabled = new();
        private Dictionary<string, StringBuilder> _receiveBuffer = new();
        private Dictionary<string, System.Windows.Forms.Timer> _flushTimer = new();
        private Dictionary<string, StringBuilder> _frozenBuffer = new();
        private Dictionary<string, (string LastChunk, int RepeatCount, DateTime LastTime)> _repeatCheck = new();

        // FIX #2: Port durdur/devam durumu
        private Dictionary<string, bool> _portPaused = new();

        // Satır tekrar bastırma
        private struct LineRepeatState
        {
            public string Line;
            public int Count;
            public DateTime WindowStart;
            public bool SummaryPending;
        }
        private Dictionary<string, LineRepeatState> _lineRepeat = new();

        private Dictionary<string, Action<string>> _windowSearchActions = new();
        private Dictionary<string, GroupBox> _minimizedBoxes = new();
        private FlowLayoutPanel _minimizedBar = null!;
        private Dictionary<string, bool> _isAnimating = new();

        private List<Form> _detachedForms = new();
        private readonly List<System.Threading.CancellationTokenSource> _activeLoads = new();

        private GroupBox? _zoomedBox = null;
        private System.Windows.Forms.Timer? _portScanner = null;
        private string _logFolderPath = string.Empty;
        private bool _isArranging = false;
        private GroupBox? _dragSource = null;
        private GroupBox? _dragTarget = null;
        private Color _originalDragSourceColor;
        private Color _originalDragTargetColor;
        private readonly string _profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appearance_profiles.json");

        private readonly string _highlightsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "highlight_rules.json");
        private Dictionary<string, (Color Fore, Color Back)> _highlightRules = new(StringComparer.OrdinalIgnoreCase);
        private Regex? _highlightRegex = null;
        private static Random _rnd = new Random();

        private ToolTip _toolTip = new ToolTip();

        private Button btnLanguage = null!;
        private Button btnHelp = null!;
        private Button btnHighlightSettings = null!;
        private Button btnLogs = null!;

        [DllImport("user32.dll")]
        private static extern bool GetScrollRange(IntPtr hWnd, int nBar, out int lpMinPos, out int lpMaxPos);
        [DllImport("user32.dll")]
        private static extern int GetScrollPos(IntPtr hWnd, int nBar);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        private static event Action? GlobalProfileListChanged;

        public MonitorForm()
        {
            this.Size = new Size(1200, 800);
            this.WindowState = FormWindowState.Maximized;
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.Icon = GenerateAppIcon();

            UpdateFormTitle();
            Lang.Changed += UpdateFormTitle;
            Lang.Changed += UpdateUIStrings;

            _logFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(_logFolderPath)) Directory.CreateDirectory(_logFolderPath);

            LoadHighlights();

            Panel bottomContainer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 35,
                BackColor = Color.FromArgb(20, 20, 22)
            };

            Panel rightToolsPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 280,
                BackColor = Color.Transparent
            };

            btnLanguage = new Button { Text = "🇹🇷 TR", Width = 50, Height = 26, Location = new Point(0, 4), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 80, 140), ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnLanguage.FlatAppearance.BorderSize = 0;
            btnLanguage.Click += (s, e) => { Lang.Toggle(); btnLanguage.Text = Lang.IsEnglish ? "🇬🇧 EN" : "🇹🇷 TR"; };

            btnHelp = new Button { Text = "❓ F1", Width = 50, Height = 26, Location = new Point(55, 4), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 150, 136), ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnHelp.FlatAppearance.BorderSize = 0;
            btnHelp.Click += (s, e) => ShowHelpDialog();

            btnHighlightSettings = new Button { Text = "🖍 Vurgu", Width = 80, Height = 26, Location = new Point(110, 4), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(140, 80, 140), ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnHighlightSettings.FlatAppearance.BorderSize = 0;
            btnHighlightSettings.Click += (s, e) => ShowHighlightDialog();

            btnLogs = new Button { Text = "📂 Loglar", Width = 80, Height = 26, Location = new Point(195, 4), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 60, 0), ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnLogs.FlatAppearance.BorderSize = 0;
            btnLogs.FlatAppearance.MouseOverBackColor = Color.FromArgb(120, 90, 10);
            btnLogs.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 45, 0);
            btnLogs.Click += (s, e) => ShowLogBrowserDialog();
            _toolTip.SetToolTip(btnLogs, Lang.Get("Kayıtlı log dosyalarını görüntüle", "View saved log files"));

            rightToolsPanel.Controls.Add(btnLanguage);
            rightToolsPanel.Controls.Add(btnHelp);
            rightToolsPanel.Controls.Add(btnHighlightSettings);
            rightToolsPanel.Controls.Add(btnLogs);

            _statusBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(5, 4, 5, 4),
                AutoScroll = true,
                WrapContents = false
            };

            _minimizedBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MaximumSize = new Size(600, 35),
                BackColor = Color.Transparent,
                Padding = new Padding(4, 4, 0, 4),
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft
            };

            bottomContainer.Controls.Add(_statusBar);
            bottomContainer.Controls.Add(_minimizedBar);
            bottomContainer.Controls.Add(rightToolsPanel);

            _minimizedBar.BringToFront();
            rightToolsPanel.BringToFront();

            _mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                AutoScroll = false,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                RowCount = 1,
                ColumnCount = 1
            };

            ContextMenuStrip bgMenu = new ContextMenuStrip();
            ToolStripMenuItem itemReset = new ToolStripMenuItem();
            ToolStripMenuItem itemOpen = new ToolStripMenuItem();
            ToolStripMenuItem itemLog = new ToolStripMenuItem();

            Action updateMenuTexts = () =>
            {
                itemReset.Text = Lang.Get("Gizlenen Portları Sıfırla (Reset)", "Reset Ignored Ports");
                itemOpen.Text = Lang.Get("Kapalı Port Aç...", "Reopen Closed Port...");
                itemLog.Text = Lang.Get("Log Klasörünü Aç", "Open Log Folder");
            };
            updateMenuTexts();
            Lang.Changed += updateMenuTexts;

            itemReset.Click += (s, e) => ResetIgnoredPorts();
            itemOpen.Click += (s, e) => ReopenIgnoredPort();
            itemLog.Click += (s, e) => System.Diagnostics.Process.Start("explorer.exe", _logFolderPath);

            bgMenu.Items.Add(itemReset);
            bgMenu.Items.Add(itemOpen);
            bgMenu.Items.Add(itemLog);
            _mainLayout.ContextMenuStrip = bgMenu;

            this.Controls.Add(bottomContainer);
            this.Controls.Add(_mainLayout);
            _mainLayout.BringToFront();

            this.Load += (s, e) => { CheckPorts(this, EventArgs.Empty); StartScanner(); };
            this.ResizeEnd += (s, e) => RearrangeGrid();
            this.SizeChanged += (s, e) => RearrangeGrid();
        }

        private void UpdateUIStrings()
        {
            if (btnHighlightSettings == null || btnLogs == null) return;
            btnHighlightSettings.Text = Lang.Get("🖍 Vurgu", "🖍 Highlight");
            btnLogs.Text = Lang.Get("📂 Loglar", "📂 Logs");
            _toolTip.SetToolTip(btnLogs, Lang.Get("Kayıtlı log dosyalarını görüntüle", "View saved log files"));
        }

        private Icon GenerateAppIcon()
        {
            using Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 30, 30));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using Font iconFont = new Font("Consolas", 14, FontStyle.Bold);
                g.DrawString("S", iconFont, Brushes.LimeGreen, new PointF(0, 4));
                g.DrawString("P", iconFont, Brushes.White, new PointF(13, 10));
            }
            IntPtr hIcon = bmp.GetHicon();
            Icon tempIcon = Icon.FromHandle(hIcon);
            Icon result = (Icon)tempIcon.Clone();
            tempIcon.Dispose();
            DestroyIcon(hIcon);
            return result;
        }

        private void UpdateFormTitle()
        {
            if (this.IsDisposed) return;
            this.Text = Lang.Get("Serial Port Studio - (Otomatik Tak/Çıkar + Log + Profil)", "Serial Port Studio - (Auto Plug/Play + Log + Profile)");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1) { ShowHelpDialog(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ════════════════════════════════════════════════════════════════
        //  DETACH
        // ════════════════════════════════════════════════════════════════
        private void DetachWindow(GroupBox groupBox, string windowTitle)
        {
            if (groupBox == null || groupBox.IsDisposed) return;
            if (_zoomedBox == groupBox) ToggleZoom(groupBox);

            bool wasInMain = _mainLayout.Controls.Contains(groupBox);
            if (wasInMain) _mainLayout.Controls.Remove(groupBox);

            string? minPort = _minimizedBoxes.FirstOrDefault(kv => kv.Value == groupBox).Key;
            if (minPort != null) { _minimizedBoxes.Remove(minPort); UpdateMinimizedBar(); }

            Form hostForm = new Form
            {
                Text = Lang.Get($"⧉  {windowTitle}  —  Ayrık Pencere", $"⧉  {windowTitle}  —  Detached Window"),
                Size = new Size(820, 560),
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.FromArgb(40, 40, 40),
                Icon = this.Icon,
                MinimumSize = new Size(420, 320)
            };

            groupBox.Dock = DockStyle.Fill;
            hostForm.Controls.Add(groupBox);

            var btnD = groupBox.Controls.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == "btnDetach");
            if (btnD != null)
            {
                btnD.Text = "⤵";
                btnD.BackColor = Color.FromArgb(0, 120, 80);
                _toolTip.SetToolTip(btnD, Lang.Get("Ana ekrana geri döndür", "Return to main screen"));
            }

            hostForm.FormClosing += (s, e) =>
            {
                if (groupBox != null && !groupBox.IsDisposed)
                {
                    hostForm.Controls.Remove(groupBox);
                    groupBox.Dock = DockStyle.None;
                    _mainLayout.Controls.Add(groupBox);

                    var btnD2 = groupBox.Controls.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == "btnDetach");
                    if (btnD2 != null)
                    {
                        btnD2.Text = "⧉";
                        btnD2.BackColor = Color.FromArgb(50, 80, 50);
                        _toolTip.SetToolTip(btnD2, Lang.Get("Pencereyi ana ekrandan ayır", "Detach window from main screen"));
                    }
                    RearrangeGrid();
                }
                _detachedForms.Remove(hostForm);
            };

            _detachedForms.Add(hostForm);
            hostForm.Show(this);

            if (wasInMain) RearrangeGrid();
            UpdateStatusBar();
        }

        private void ToggleDetach(GroupBox groupBox, string windowTitle)
        {
            if (groupBox == null || groupBox.IsDisposed) return;
            bool isDetached = _detachedForms.Any(f => f.Controls.Contains(groupBox));
            if (isDetached)
            {
                Form? hostForm = _detachedForms.FirstOrDefault(f => f.Controls.Contains(groupBox));
                hostForm?.Close();
            }
            else
            {
                DetachWindow(groupBox, windowTitle);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  YARDIM DİYALOĞU — FIX #7: Güncel bilgilerle
        // ════════════════════════════════════════════════════════════════
        private void ShowHelpDialog()
        {
            using Form hForm = new Form
            {
                Text = Lang.Get("Serial Port Studio - Yardım (F1)", "Serial Port Studio - Help (F1)"),
                Size = new Size(720, 780),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(40, 40, 45)
            };

            Panel rtbContainer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 35), Padding = new Padding(15, 15, 10, 10) };
            RichTextBox rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 35),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None
            };

            string trHelp =
                "📌 GENEL ÖZELLİKLER VE KISAYOLLAR\n\n" +
                "• Otomatik Tak/Çıkar (Plug & Play): USB veya Seri port cihazınızı taktığınızda program otomatik tanır.\n" +
                "• F1: Bu yardım menüsünü açar.\n" +
                "• Ctrl + F: Seçili port penceresinde metin arama çubuğunu açar.\n" +
                "• Pencere Butonları (sağ üst, soldan sağa): Ayır (⧉), Küçült (─), Tam Ekran (◻/🗗), Kapat (✕).\n" +
                "• Çift Tıklama (Zoom): Herhangi bir port ekranına çift tıklayarak tam ekran yapabilirsiniz.\n" +
                "• Sürükle-Bırak: Pencere başlıklarından tutup yerleri değiştirebilirsiniz.\n\n" +
                "─ KÜÇÜLTME (MİNİMİZE)\n\n" +
                "• Port penceresindeki '─' butonuyla pencereyi alt bara küçültebilirsiniz.\n" +
                "• Küçültülmüş pencereler alt sağda turuncu renkli buton olarak görünür (COM butonlarından farklı renkte).\n" +
                "• Yeni veri geldiğinde bu buton kısa süreliğine parlayarak sizi uyarır.\n" +
                "• Butona tıklayarak pencereyi geri getirebilirsiniz.\n\n" +
                "⏸ DURDUR / DEVAM ET\n\n" +
                "• Port penceresindeki '⏸ Durdur' butonuyla COM'dan gelen veri akışını durdurabilirsiniz.\n" +
                "• Durdurulan penceredeki mevcut veriler korunur, silinmez.\n" +
                "• '▶ Devam' butonuna basarak aynı COM portuna yeniden bağlanır ve kaldığı yerden devam eder.\n" +
                "• Bu özellik '⏸ Dondur' (scroll dondurma) özelliğinden farklıdır: Dondur sadece kaydırmayı durdurur,\n" +
                "  Durdur ise veri alımını tamamen durdurur.\n\n" +
                "⧉ PENCERE AYIRMA (DETACH)\n\n" +
                "• Pencere başlığındaki '⧉' butonuyla bir port veya log penceresini ana ekrandan ayırabilirsiniz.\n" +
                "• Ayrılmış pencerede '⤵' butonuna basarak veya pencereyi kapatarak ana ekrana döndürebilirsiniz.\n\n" +
                "📂 LOG GÖRÜNTÜLEYICI\n\n" +
                "• Alt sağdaki '📂 Loglar' butonuyla kayıtlı log dosyalarını listeleyebilirsiniz.\n" +
                "• Log dosyaları yüklenirken ilerleme çubuğu gösterilir.\n" +
                "• Log pencereleri görünüm ayarı, arama (Ctrl+F) ve detach özelliklerine sahiptir.\n\n" +
                "⚙️ GÖRÜNÜM VE PROFİLLER\n\n" +
                "• Port ve Log pencerelerindeki '⚙ Görünüm' butonuyla renk, yazı tipi ve boyutu ayarlayabilirsiniz.\n" +
                "• Profil kaydetmek için '💾 Kaydet' butonuna basın. Silmek için profil butonuna basılı tutun.\n\n" +
                "🖍️ KELİME VURGULAMA (HIGHLIGHT)\n\n" +
                "• '🖍 Vurgu' butonuyla vurgulama listesini yönetebilirsiniz.\n" +
                "• Hızlı Ekleme: Metni seçip sağ tık → 🖍 Vurgula\n" +
                "• Vurgu kuralları dışa/içe aktarılabilir: '🖍 Vurgu' → Dışa Aktar / İçe Aktar butonları.\n" +
                "• Dışa aktarılan dosya insan tarafından okunabilir JSON formatındadır.\n\n" +
                "😀 EMOJİ DESTEĞİ\n\n" +
                "• Port pencereleri emoji karakterlerini (🎗️ 🔴 🟢 ✅ ⚠️ vb.) doğrudan gösterebilir.";

            string enHelp =
                "📌 GENERAL FEATURES & SHORTCUTS\n\n" +
                "• Auto Plug & Play: Detects serial devices automatically when plugged in.\n" +
                "• F1: Opens this help menu.\n" +
                "• Ctrl + F: Opens the search bar in the selected port window.\n" +
                "• Window Buttons (top right, left to right): Detach (⧉), Minimize (─), Maximize (◻/🗗), Close (✕).\n" +
                "• Double Click (Zoom): Double-click on any port screen to make it full screen.\n" +
                "• Drag & Drop: Grab window headers to swap their positions.\n\n" +
                "─ MINIMIZE\n\n" +
                "• Click the '─' button to minimize a port window to the bottom bar.\n" +
                "• Minimized windows appear as orange buttons in the bottom right (different color from COM buttons).\n" +
                "• When new data arrives, the button briefly flashes to alert you.\n" +
                "• Click the button to restore the window.\n\n" +
                "⏸ PAUSE / RESUME\n\n" +
                "• Click the '⏸ Pause' button to stop receiving data from the COM port.\n" +
                "• Existing data in the window is preserved, not deleted.\n" +
                "• Click '▶ Resume' to reconnect to the same COM port and continue from where you left off.\n" +
                "• This is different from '⏸ Freeze' (scroll freeze): Freeze only stops scrolling,\n" +
                "  Pause completely stops data reception.\n\n" +
                "⧉ WINDOW DETACH\n\n" +
                "• Click the '⧉' button to detach a port or log window as a floating window.\n" +
                "• Press '⤵' or close the detached window to return it to the main screen.\n\n" +
                "📂 LOG VIEWER\n\n" +
                "• Click '📂 Logs' at the bottom right to list saved log files.\n" +
                "• A progress bar is shown while loading log files.\n" +
                "• Log windows support appearance settings, search (Ctrl+F), and detach.\n\n" +
                "⚙️ APPEARANCE & PROFILES\n\n" +
                "• Click '⚙ Settings' in any port or log window to customize colors, font, and size.\n" +
                "• Press '💾 Save' to save as a profile. Long-press a profile button to delete it.\n\n" +
                "🖍️ WORD HIGHLIGHTING\n\n" +
                "• Click '🖍 Highlight' to manage the highlight list.\n" +
                "• Quick Add: Select text, right-click → 🖍 Highlight\n" +
                "• Highlight rules can be exported/imported: '🖍 Highlight' → Export / Import buttons.\n" +
                "• Exported files are in human-readable JSON format.\n\n" +
                "😀 EMOJI SUPPORT\n\n" +
                "• Port windows can display emoji characters (🎗️ 🔴 🟢 ✅ ⚠️ etc.) natively.";

            rtb.Text = Lang.Get(trHelp, enHelp);
            rtb.Select(0, rtb.Text.Length);
            rtb.SelectionColor = Color.Silver;

            foreach (var section in new[] {
                "📌 GENEL ÖZELLİKLER VE KISAYOLLAR", "─ KÜÇÜLTME (MİNİMİZE)", "⏸ DURDUR / DEVAM ET",
                "⧉ PENCERE AYIRMA (DETACH)", "📂 LOG GÖRÜNTÜLEYICI", "⚙️ GÖRÜNÜM VE PROFİLLER",
                "🖍️ KELİME VURGULAMA (HIGHLIGHT)", "😀 EMOJİ DESTEĞİ",
                "📌 GENERAL FEATURES & SHORTCUTS", "─ MINIMIZE", "⏸ PAUSE / RESUME",
                "⧉ WINDOW DETACH", "📂 LOG VIEWER", "⚙️ APPEARANCE & PROFILES",
                "🖍️ WORD HIGHLIGHTING", "😀 EMOJI SUPPORT"
            }) HighlightWord(rtb, section, Color.LimeGreen);

            Button btnOk = new Button
            {
                Text = Lang.Get("Anladım", "Got it!"),
                Dock = DockStyle.Bottom,
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => hForm.Close();

            rtbContainer.Controls.Add(rtb);
            hForm.Controls.Add(rtbContainer);
            hForm.Controls.Add(btnOk);
            hForm.ShowDialog(this);
        }

        private void HighlightWord(RichTextBox rtb, string word, Color color)
        {
            if (rtb == null || string.IsNullOrEmpty(word)) return;
            int s_start = rtb.SelectionStart, startIndex = 0, index;
            using Font boldFont = new Font(rtb.Font, FontStyle.Bold);
            while ((index = rtb.Text.IndexOf(word, startIndex)) != -1)
            {
                rtb.Select(index, word.Length);
                rtb.SelectionColor = color;
                rtb.SelectionFont = boldFont;
                startIndex = index + word.Length;
            }
            rtb.SelectionStart = s_start;
            rtb.SelectionLength = 0;
        }

        // ════════════════════════════════════════════════════════════════
        //  FIX #6: Vurgu kuralları dışa/içe aktarma — insan okunabilir format
        // ════════════════════════════════════════════════════════════════
        private void ExportHighlights()
        {
            using var sfd = new SaveFileDialog
            {
                Title = Lang.Get("Vurgu Kurallarını Dışa Aktar", "Export Highlight Rules"),
                Filter = "Highlight Rules (*.hlrules)|*.hlrules|JSON (*.json)|*.json|All Files (*.*)|*.*",
                FileName = "highlight_rules",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Serial Port Studio — Vurgu Kuralları / Highlight Rules");
                sb.AppendLine("# Format: KELIME | YAZI_RENGI_HEX | ZEMIN_RENGI_HEX");
                sb.AppendLine("# Format: WORD   | TEXT_COLOR_HEX | BACK_COLOR_HEX");
                sb.AppendLine("# Örnek / Example: ERROR | #FFFFFF | #CC0000");
                sb.AppendLine("#");
                sb.AppendLine("# Renk formatı: #RRGGBB veya #AARRGGBB");
                sb.AppendLine("# Color format: #RRGGBB or #AARRGGBB");
                sb.AppendLine("");
                foreach (var kv in _highlightRules)
                {
                    string foreHex = ColorToHex(kv.Value.Fore);
                    string backHex = ColorToHex(kv.Value.Back);
                    sb.AppendLine($"{kv.Key} | {foreHex} | {backHex}");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show(
                    Lang.Get($"Dışa aktarma tamamlandı.\n{_highlightRules.Count} kural kaydedildi.", $"Export complete.\n{_highlightRules.Count} rules saved."),
                    Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.Get($"Dışa aktarma hatası: {ex.Message}", $"Export error: {ex.Message}"),
                    Lang.Get("Hata", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportHighlights()
        {
            using var ofd = new OpenFileDialog
            {
                Title = Lang.Get("Vurgu Kurallarını İçe Aktar", "Import Highlight Rules"),
                Filter = "Highlight Rules (*.hlrules)|*.hlrules|JSON (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            try
            {
                string ext = Path.GetExtension(ofd.FileName).ToLower();
                int imported = 0, skipped = 0;

                if (ext == ".json")
                {
                    // Eski JSON formatını da destekle
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<HighlightItem>>(File.ReadAllText(ofd.FileName));
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            if (string.IsNullOrWhiteSpace(item.Word)) { skipped++; continue; }
                            _highlightRules[item.Word] = (Color.FromArgb(item.ForeArgb), Color.FromArgb(item.BackArgb));
                            imported++;
                        }
                    }
                }
                else
                {
                    // Yeni insan okunabilir format
                    string[] lines = File.ReadAllLines(ofd.FileName, Encoding.UTF8);
                    foreach (string rawLine in lines)
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                        string[] parts = line.Split('|');
                        if (parts.Length < 3) { skipped++; continue; }
                        string word = parts[0].Trim();
                        if (string.IsNullOrEmpty(word)) { skipped++; continue; }
                        try
                        {
                            Color fore = HexToColor(parts[1].Trim());
                            Color back = HexToColor(parts[2].Trim());
                            _highlightRules[word] = (fore, back);
                            imported++;
                        }
                        catch { skipped++; }
                    }
                }

                SaveHighlights();
                BuildHighlightRegex();
                ApplyHighlightsToAllWindows();
                MessageBox.Show(
                    Lang.Get($"İçe aktarma tamamlandı.\n✅ {imported} kural yüklendi.\n⚠️ {skipped} satır atlandı.",
                             $"Import complete.\n✅ {imported} rules loaded.\n⚠️ {skipped} lines skipped."),
                    Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.Get($"İçe aktarma hatası: {ex.Message}", $"Import error: {ex.Message}"),
                    Lang.Get("Hata", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ColorToHex(Color c)
        {
            if (c.A == 255) return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static Color HexToColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
                return Color.FromArgb(255,
                    Convert.ToInt32(hex.Substring(0, 2), 16),
                    Convert.ToInt32(hex.Substring(2, 2), 16),
                    Convert.ToInt32(hex.Substring(4, 2), 16));
            if (hex.Length == 8)
                return Color.FromArgb(
                    Convert.ToInt32(hex.Substring(0, 2), 16),
                    Convert.ToInt32(hex.Substring(2, 2), 16),
                    Convert.ToInt32(hex.Substring(4, 2), 16),
                    Convert.ToInt32(hex.Substring(6, 2), 16));
            throw new FormatException($"Geçersiz renk formatı: #{hex}");
        }

        private void LoadHighlights()
        {
            if (File.Exists(_highlightsPath))
            {
                try
                {
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<HighlightItem>>(File.ReadAllText(_highlightsPath));
                    if (list != null) { _highlightRules.Clear(); foreach (var item in list) _highlightRules[item.Word] = (Color.FromArgb(item.ForeArgb), Color.FromArgb(item.BackArgb)); }
                }
                catch { }
            }
            BuildHighlightRegex();
        }

        private void SaveHighlights()
        {
            var list = _highlightRules.Select(kv => new HighlightItem { Word = kv.Key, ForeArgb = kv.Value.Fore.ToArgb(), BackArgb = kv.Value.Back.ToArgb() }).ToList();
            File.WriteAllText(_highlightsPath, System.Text.Json.JsonSerializer.Serialize(list));
        }

        private void BuildHighlightRegex()
        {
            if (_highlightRules.Count == 0) { _highlightRegex = null; return; }
            string pattern = string.Join("|", _highlightRules.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            _highlightRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private (Color Fore, Color Back) GenerateRandomHighlightColor()
        {
            Color[] backColors = { Color.DarkRed, Color.DarkGreen, Color.DarkBlue, Color.OrangeRed, Color.Purple, Color.Teal, Color.Olive, Color.Brown, Color.DarkMagenta, Color.Indigo, Color.Maroon, Color.Crimson, Color.FromArgb(120, 80, 0), Color.FromArgb(0, 100, 150) };
            return (Color.White, backColors[_rnd.Next(backColors.Length)]);
        }

        private static Bitmap GenerateColorPreviewBitmap(Color fore, Color back)
        {
            Bitmap bmp = new Bitmap(52, 18);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush bgBrush = new SolidBrush(back))
                    g.FillRectangle(bgBrush, 0, 0, 52, 18);
                using (Pen borderPen = new Pen(Color.FromArgb(80, 80, 80), 1))
                    g.DrawRectangle(borderPen, 0, 0, 51, 17);
                using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (Brush b = new SolidBrush(fore))
                    g.DrawString("Abc", f, b, 4, 2);
            }
            return bmp;
        }

        private IEnumerable<ScrollAwareRichTextBox> GetAllRtbs()
        {
            return _mainLayout.Controls.OfType<GroupBox>()
                   .Concat(_minimizedBoxes.Values)
                   .Concat(_detachedForms.SelectMany(f => f.Controls.OfType<GroupBox>()))
                   .Select(g => g.Controls.Find("rtbOutput", true).FirstOrDefault())
                   .OfType<ScrollAwareRichTextBox>();
        }

        private void ApplyHighlightsToAllWindows()
        {
            if (_highlightRegex == null) return;
            foreach (var rtb in GetAllRtbs()) ApplyHighlightsToRTB(rtb);
        }

        private void ApplyHighlightsToRTB(ScrollAwareRichTextBox rtb)
        {
            if (_highlightRegex == null || rtb == null || rtb.IsDisposed || rtb.TextLength == 0) return;
            SendMessage(rtb.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            int savedStart = rtb.SelectionStart, savedLen = rtb.SelectionLength;
            try
            {
                var matches = _highlightRegex.Matches(rtb.Text);
                foreach (Match m in matches)
                {
                    rtb.SelectionStart = m.Index; rtb.SelectionLength = m.Length;
                    if (_highlightRules.TryGetValue(m.Value, out var colors)) { rtb.SelectionColor = colors.Fore; rtb.SelectionBackColor = colors.Back; rtb.SelectionFont = new Font(rtb.Font, FontStyle.Bold); }
                }
            }
            catch { }
            finally { rtb.SelectionStart = savedStart; rtb.SelectionLength = savedLen; SendMessage(rtb.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); rtb.Invalidate(); }
        }

        private void RemoveWordHighlightFromAllWindows(string removedWord)
        {
            if (string.IsNullOrEmpty(removedWord)) return;
            foreach (var rtb in GetAllRtbs())
            {
                if (rtb == null || rtb.IsDisposed || rtb.TextLength == 0) continue;
                SendMessage(rtb.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
                int savedStart = rtb.SelectionStart, savedLen = rtb.SelectionLength;
                try
                {
                    string textLower = rtb.Text.ToLower(), wordLower = removedWord.ToLower();
                    int idx = 0, wLen = removedWord.Length;
                    while ((idx = textLower.IndexOf(wordLower, idx)) != -1)
                    {
                        rtb.SelectionStart = idx; rtb.SelectionLength = wLen;
                        rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionColor = rtb.ForeColor; rtb.SelectionFont = new Font(rtb.Font, FontStyle.Regular);
                        if (_highlightRegex != null && _highlightRules.Count > 0)
                        {
                            string seg = rtb.Text.Substring(idx, wLen);
                            var m = _highlightRegex.Match(seg);
                            if (m.Success && m.Index == 0 && m.Length == wLen && _highlightRules.TryGetValue(m.Value, out var c2)) { rtb.SelectionColor = c2.Fore; rtb.SelectionBackColor = c2.Back; rtb.SelectionFont = new Font(rtb.Font, FontStyle.Bold); }
                        }
                        idx += wLen;
                    }
                }
                catch { }
                finally { rtb.SelectionStart = savedStart; rtb.SelectionLength = savedLen; SendMessage(rtb.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); rtb.Invalidate(); }
            }
        }

        private void ShowHighlightDialog()
        {
            using Form dlg = new Form
            {
                Text = Lang.Get("Kelime Vurgulama Yöneticisi", "Word Highlight Manager"),
                Size = new Size(760, 500),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            Label lblInfo = new Label
            {
                Text = Lang.Get(
                    "💡  Sol Tık → Ara   |   Sağ Tık → Renk / Sil   |   Metni seçip sağ tık → 🖍 Vurgula",
                    "💡  Left Click → Search   |   Right Click → Color / Delete   |   Select text, right-click → 🖍 Highlight"),
                Dock = DockStyle.Bottom,
                Height = 30,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(25, 25, 25),
                Padding = new Padding(5)
            };

            // FIX #6: Dışa/İçe aktar butonları
            Panel btnRow = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(5, 4, 5, 4) };
            Button btnClose = new Button { Text = Lang.Get("Kapat", "Close"), Width = 80, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, Dock = DockStyle.Right };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => dlg.Close();

            Button btnExport = new Button { Text = Lang.Get("⬆ Dışa Aktar", "⬆ Export"), Width = 110, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 80, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand, Dock = DockStyle.Left };
            btnExport.FlatAppearance.BorderSize = 0;
            btnExport.Click += (s, e) => ExportHighlights();

            Button btnImport = new Button { Text = Lang.Get("⬇ İçe Aktar", "⬇ Import"), Width = 110, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 60, 80), ForeColor = Color.White, Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand, Dock = DockStyle.Left };
            btnImport.FlatAppearance.BorderSize = 0;
            Action reloadListRef = null!;
            btnImport.Click += (s, e) => { ImportHighlights(); reloadListRef?.Invoke(); };

            btnRow.Controls.Add(btnClose);
            btnRow.Controls.Add(btnExport);
            btnRow.Controls.Add(btnImport);

            FlowLayoutPanel flp = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 30), AutoScroll = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(10) };

            Action loadList = null!;
            loadList = () =>
            {
                flp.SuspendLayout();
                flp.Controls.Clear();
                if (_highlightRules.Count == 0)
                {
                    Label lblEmpty = new Label { Text = Lang.Get("Henüz vurgulama kuralı eklenmemiş.\n\nBir kelime eklemek için:\nEkranda herhangi bir metni seçin → Sağ Tık → 🖍 Vurgula", "No highlight rules added yet.\n\nTo add a word:\nSelect any text on screen → Right-Click → 🖍 Highlight"), ForeColor = Color.FromArgb(120, 120, 120), Font = new Font("Segoe UI", 10f), AutoSize = false, Size = new Size(680, 120), Margin = new Padding(20), TextAlign = ContentAlignment.MiddleCenter };
                    flp.Controls.Add(lblEmpty);
                    flp.ResumeLayout();
                    return;
                }

                foreach (var kv in _highlightRules.ToList())
                {
                    string word = kv.Key; Color foreColor = kv.Value.Fore; Color backColor = kv.Value.Back;
                    int btnW = Math.Max(100, Math.Min(320, TextRenderer.MeasureText(word, new Font("Segoe UI", 9.5f, FontStyle.Bold)).Width + 28));
                    Button b = new Button { Text = word, BackColor = backColor, ForeColor = foreColor, AutoSize = false, Size = new Size(btnW, 36), FlatStyle = FlatStyle.Flat, Margin = new Padding(5), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Tag = word };
                    b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80); b.FlatAppearance.BorderSize = 1; b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.2f);
                    b.Click += (sender, args) => { foreach (var action in _windowSearchActions.Values.ToList()) action(word); dlg.Close(); };

                    ContextMenuStrip btnMenu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.5f) };
                    ToolStripMenuItem menuFore = new ToolStripMenuItem(Lang.Get("🎨  Yazı Rengini Belirle", "🎨  Set Text Color")) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
                    ToolStripMenuItem menuBack = new ToolStripMenuItem(Lang.Get("🎨  Dolgu Rengini Belirle", "🎨  Set Background Color")) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
                    ToolStripMenuItem menuDel = new ToolStripMenuItem(Lang.Get("🗑  Sil", "🗑  Delete")) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.OrangeRed };
                    btnMenu.Items.Add(menuFore); btnMenu.Items.Add(menuBack); btnMenu.Items.Add(new ToolStripSeparator()); btnMenu.Items.Add(menuDel);
                    menuFore.Click += (s, e) => { using ColorDialog cd = new ColorDialog { Color = foreColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; _highlightRules[word] = (cd.Color, backColor); SaveHighlights(); BuildHighlightRegex(); ApplyHighlightsToAllWindows(); loadList(); };
                    menuBack.Click += (s, e) => { using ColorDialog cd = new ColorDialog { Color = backColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; _highlightRules[word] = (foreColor, cd.Color); SaveHighlights(); BuildHighlightRegex(); ApplyHighlightsToAllWindows(); loadList(); };
                    menuDel.Click += (s, e) => { _highlightRules.Remove(word); SaveHighlights(); BuildHighlightRegex(); RemoveWordHighlightFromAllWindows(word); loadList(); };
                    b.MouseDown += (sender, args) => { if (args.Button == MouseButtons.Right) btnMenu.Show(b, args.Location); };
                    b.Disposed += (s, e) => btnMenu.Dispose();
                    flp.Controls.Add(b);
                }
                flp.ResumeLayout();
            };
            reloadListRef = loadList;

            loadList();
            dlg.Controls.Add(flp);
            dlg.Controls.Add(btnRow);
            dlg.Controls.Add(lblInfo);
            dlg.ShowDialog(this);
        }

        // ════════════════════════════════════════════════════════════════
        //  LOG BROWSER
        // ════════════════════════════════════════════════════════════════
        private void ShowLogBrowserDialog()
        {
            var dir = new DirectoryInfo(_logFolderPath);
            if (!dir.Exists) { MessageBox.Show(Lang.Get("Log klasörü bulunamadı.", "Log folder not found."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            var files = dir.GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).ToList();
            if (files.Count == 0) { MessageBox.Show(Lang.Get("Henüz kayıtlı log dosyası yok.\n\nLog kaydetmek için port penceresindeki '🔴 Log Kapalı' butonuna tıklayın.", "No saved log files yet.\n\nTo record logs, click the '🔴 Log Off' button in any port window."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            using Form dlg = new Form
            {
                Text = Lang.Get($"📂 Log Dosyaları — {files.Count} kayıt", $"📂 Log Files — {files.Count} records"),
                Size = new Size(760, 520),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(28, 28, 32),
                ForeColor = Color.White,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false,
                Icon = this.Icon
            };

            Panel titleStrip = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(60, 40, 0) };
            Label lblTitle = new Label { Text = Lang.Get("📂  Kayıtlı Log Dosyaları", "📂  Saved Log Files"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 200, 80), Font = new Font("Segoe UI", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            titleStrip.Controls.Add(lblTitle);

            ListView lv = new ListView { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 22, 28), ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f), FullRowSelect = true, GridLines = false, View = View.Details, BorderStyle = BorderStyle.None, MultiSelect = true, OwnerDraw = true };
            lv.Columns.Add(Lang.Get("Port", "Port"), 75);
            lv.Columns.Add(Lang.Get("Kayıt Tarihi", "Record Date"), 165);
            lv.Columns.Add(Lang.Get("Boyut", "Size"), 75);
            lv.Columns.Add(Lang.Get("Dosya Adı", "File Name"), 380);

            var logFileRegex = new Regex(@"^(.+?)_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})\.txt$");
            foreach (var fi in files)
            {
                string portPart = "?", datePart = fi.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss");
                var m = logFileRegex.Match(fi.Name);
                if (m.Success)
                {
                    portPart = m.Groups[1].Value;
                    string rawDate = m.Groups[2].Value + "_" + m.Groups[3].Value;
                    if (DateTime.TryParseExact(rawDate, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt)) datePart = dt.ToString("yyyy-MM-dd  HH:mm:ss");
                }
                string sizeStr = fi.Length < 1024 ? $"{fi.Length} B" : fi.Length < 1_048_576 ? $"{fi.Length / 1024.0:F1} KB" : $"{fi.Length / 1_048_576.0:F2} MB";
                var item = new ListViewItem(portPart);
                item.SubItems.Add(datePart); item.SubItems.Add(sizeStr); item.SubItems.Add(fi.Name);
                item.Tag = fi.FullName;
                lv.Items.Add(item);
            }

            lv.DrawColumnHeader += (s, e) => { using var bgBr = new SolidBrush(Color.FromArgb(38, 38, 50)); e.Graphics.FillRectangle(bgBr, e.Bounds); using var pen = new Pen(Color.FromArgb(60, 60, 70)); e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom); string headerText = e.Header?.Text ?? ""; using var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold); using var headerBr = new SolidBrush(Color.FromArgb(0, 210, 230)); e.Graphics.DrawString(headerText, headerFont, headerBr, e.Bounds.X + 8, e.Bounds.Y + 5); };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) => { if (e.Item == null || e.SubItem == null) return; bool selected = e.Item.Selected; Color bg = selected ? Color.FromArgb(0, 70, 140) : e.ItemIndex % 2 == 0 ? Color.FromArgb(22, 22, 28) : Color.FromArgb(28, 28, 36); using var bgBr = new SolidBrush(bg); e.Graphics.FillRectangle(bgBr, e.Bounds); Color fg = e.ColumnIndex == 0 ? Color.LimeGreen : e.ColumnIndex == 1 ? Color.FromArgb(0, 210, 230) : e.ColumnIndex == 2 ? Color.FromArgb(180, 180, 80) : Color.Silver; using var fgBr = new SolidBrush(fg); e.Graphics.DrawString(e.SubItem.Text ?? "", lv.Font, fgBr, e.Bounds.X + 5, e.Bounds.Y + 3); };

            Panel btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Color.FromArgb(20, 20, 25), Padding = new Padding(8, 6, 8, 6) };
            Button btnOpen = new Button { Text = Lang.Get("📂  Seçili Logu Aç", "📂  Open Selected Log"), Width = 200, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 60, 0), ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand, Dock = DockStyle.Right };
            btnOpen.FlatAppearance.BorderSize = 0; btnOpen.FlatAppearance.MouseOverBackColor = Color.FromArgb(120, 90, 10); btnOpen.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 45, 0);
            Button btnExplorer = new Button { Text = Lang.Get("🗂  Klasörü Aç", "🗂  Open Folder"), Width = 130, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 40, 50), ForeColor = Color.Silver, Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand, Dock = DockStyle.Right };
            btnExplorer.FlatAppearance.BorderSize = 0;
            btnExplorer.Click += (s, e) => System.Diagnostics.Process.Start("explorer.exe", _logFolderPath);
            Label lblHint = new Label { Text = Lang.Get("💡 Tek veya çoklu seçim yapabilirsiniz.", "💡 Single or multi-select supported."), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(130, 130, 130), Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft };

            lv.SelectedIndexChanged += (s, e) =>
            {
                int cnt = lv.SelectedItems.Count;
                if (cnt == 0) { btnOpen.Text = Lang.Get("📂  Seçili Logu Aç", "📂  Open Selected Log"); btnOpen.BackColor = Color.FromArgb(60, 45, 0); lblHint.ForeColor = Color.FromArgb(130, 130, 130); }
                else if (cnt == 1) { btnOpen.Text = Lang.Get("📂  1 Log Aç", "📂  Open 1 Log"); btnOpen.BackColor = Color.FromArgb(80, 60, 0); lblHint.ForeColor = Color.FromArgb(180, 180, 100); }
                else { btnOpen.Text = Lang.Get($"📂  {cnt} Log Aç", $"📂  Open {cnt} Logs"); btnOpen.BackColor = Color.FromArgb(0, 80, 140); lblHint.ForeColor = Color.FromArgb(100, 180, 255); }
            };

            btnPanel.Controls.Add(lblHint);
            btnPanel.Controls.Add(btnOpen);
            btnPanel.Controls.Add(btnExplorer);

            Action openSelected = () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var paths = lv.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag?.ToString() ?? "").Where(File.Exists).ToList();
                if (paths.Count == 0) return;
                dlg.Close();
                foreach (string path in paths) OpenLogViewerWindow(path);
            };

            btnOpen.Click += (s, e) => openSelected();
            lv.DoubleClick += (s, e) => openSelected();

            dlg.Controls.Add(lv);
            dlg.Controls.Add(btnPanel);
            dlg.Controls.Add(titleStrip);
            dlg.ShowDialog(this);
        }

        // ════════════════════════════════════════════════════════════════
        //  LOG VIEWER
        //  FIX #3: Async yükleme + progress bar
        //  FIX #4: Header sağ tarafındaki beyaz alan kaldırıldı
        //  FIX #5: Arşiv label genişliği azaltıldı
        // ════════════════════════════════════════════════════════════════
        private void OpenLogViewerWindow(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            string portPart = "LOG", sessionDate = "";
            var logRx = new Regex(@"^(.+?)_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})\.txt$");
            var lm = logRx.Match(fileName);
            if (lm.Success)
            {
                portPart = lm.Groups[1].Value;
                string rawDate = lm.Groups[2].Value + "_" + lm.Groups[3].Value;
                if (DateTime.TryParseExact(rawDate, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt))
                    sessionDate = dt.ToString("yyyy-MM-dd  HH:mm:ss");
            }

            FileInfo fi = new FileInfo(filePath);
            string sizeStr = fi.Length < 1024 ? $"{fi.Length} B" : fi.Length < 1_048_576 ? $"{fi.Length / 1024.0:F1} KB" : $"{fi.Length / 1_048_576.0:F2} MB";

            GroupBox groupBox = new GroupBox
            {
                Text = $"📂 {portPart}  ·  {sessionDate}",
                Size = new Size(200, 100),
                ForeColor = Color.FromArgb(200, 170, 80),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(40, 40, 40)
            };

            // Sürükle-bırak
            groupBox.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && e.Y <= 30) { _dragSource = groupBox; _originalDragSourceColor = groupBox.BackColor; groupBox.BackColor = Color.FromArgb(80, 80, 80); groupBox.BringToFront(); } };
            groupBox.MouseMove += (s, e) =>
            {
                if (_dragSource == null || e.Button != MouseButtons.Left) return;
                Point screenPt = groupBox.PointToScreen(e.Location);
                GroupBox? target = null;
                foreach (Control c in _mainLayout.Controls) { if (c is GroupBox gb && gb != _dragSource) { var gbScreen = new Rectangle(_mainLayout.PointToScreen(gb.Location), gb.Size); if (gbScreen.Contains(screenPt)) { target = gb; break; } } }
                if (target != null) { if (_dragTarget != null && _dragTarget != target) _dragTarget.BackColor = _originalDragTargetColor; if (_dragTarget != target) { _dragTarget = target; _originalDragTargetColor = target.BackColor; target.BackColor = Color.Orange; } var controls = _mainLayout.Controls.Cast<Control>().ToList(); int idxSrc = controls.IndexOf(_dragSource); int idxDst = controls.IndexOf(target); if (idxSrc >= 0 && idxDst >= 0 && idxSrc != idxDst) { _mainLayout.SuspendLayout(); var posSrc = _mainLayout.GetCellPosition(_dragSource); var posDst = _mainLayout.GetCellPosition(target); _mainLayout.SetCellPosition(_dragSource, posDst); _mainLayout.SetCellPosition(target, posSrc); _mainLayout.ResumeLayout(); } }
                else { if (_dragTarget != null) { _dragTarget.BackColor = _originalDragTargetColor; _dragTarget = null; } }
            };
            groupBox.MouseUp += (s, e) => { if (_dragSource != null) { _dragSource.BackColor = _originalDragSourceColor; _dragSource = null; } if (_dragTarget != null) { _dragTarget.BackColor = _originalDragTargetColor; _dragTarget = null; } };

            // RTB — FIX #4: RtbWrapper sağ kenar beyaz alan sorunu çözüldü
            Panel rtbWrapper = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            ScrollAwareRichTextBox rtbOutput = new ScrollAwareRichTextBox
            {
                Name = "rtbOutput",
                BackColor = Color.Black,
                ForeColor = Color.LimeGreen,
                // FIX #8: Emoji destekli font
                Font = IsFontAvailable("Segoe UI Emoji")
                    ? new Font("Segoe UI Emoji", 10f, FontStyle.Regular)
                    : new Font("Consolas", 10f, FontStyle.Regular),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                // FIX #4: RichTextBox tam genişlik, scrollbar sağda sabit
                Dock = DockStyle.Fill
            };
            rtbWrapper.Controls.Add(rtbOutput);

            Color logTsColor = Color.FromArgb(0, 200, 220);
            Font logBoldFont = new Font("Consolas", 10, FontStyle.Bold);
            Font logRegularFont = new Font("Consolas", 10, FontStyle.Regular);

            // Arama paneli
            Panel searchPanel = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.FromArgb(30, 30, 60), Padding = new Padding(2), Visible = false };
            TextBox txtSearch = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 40), ForeColor = Color.White, Font = new Font("Consolas", 10), BorderStyle = BorderStyle.FixedSingle };
            Button btnSearchNext = new Button { Text = "▼", Dock = DockStyle.Right, Width = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            Button btnSearchPrev = new Button { Text = "▲", Dock = DockStyle.Right, Width = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            Label lblSearchResult = new Label { Dock = DockStyle.Right, Width = 80, ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleCenter };
            searchPanel.Controls.AddRange(new Control[] { txtSearch, btnSearchNext, btnSearchPrev, lblSearchResult });

            int searchIndex = 0;
            List<int> FindAll(string term) { var res = new List<int>(); if (string.IsNullOrEmpty(term)) return res; string tl = rtbOutput.Text.ToLower(), trl = term.ToLower(); int idx = 0; while ((idx = tl.IndexOf(trl, idx)) != -1) { res.Add(idx); idx += trl.Length; } return res; }
            void ClearHL() { if (rtbOutput.IsDisposed) return; rtbOutput.SelectionStart = 0; rtbOutput.SelectionLength = rtbOutput.TextLength; rtbOutput.SelectionBackColor = rtbOutput.BackColor; rtbOutput.SelectionStart = rtbOutput.TextLength; rtbOutput.SelectionLength = 0; UpdateTimestampColors(rtbOutput, logTsColor); ApplyHighlightsToRTB(rtbOutput); }
            void HighlightAllSearch(List<int> hits, string term, int cur) { ClearHL(); foreach (int h in hits) { rtbOutput.SelectionStart = h; rtbOutput.SelectionLength = term.Length; rtbOutput.SelectionBackColor = Color.FromArgb(0, 60, 160); } if (cur >= 0 && cur < hits.Count) { rtbOutput.SelectionStart = hits[cur]; rtbOutput.SelectionLength = term.Length; rtbOutput.SelectionBackColor = Color.FromArgb(180, 80, 0); } }
            void DoSearch(bool forward)
            {
                string term = txtSearch.Text;
                if (string.IsNullOrEmpty(term)) { lblSearchResult.Text = ""; ClearHL(); return; }
                var hits = FindAll(term);
                if (hits.Count == 0) { lblSearchResult.Text = Lang.Get("Yok", "None"); lblSearchResult.ForeColor = Color.FromArgb(220, 80, 80); ClearHL(); return; }
                if (forward) searchIndex = (searchIndex + 1) % hits.Count; else searchIndex = (searchIndex - 1 + hits.Count) % hits.Count;
                HighlightAllSearch(hits, term, searchIndex);
                rtbOutput.SelectionStart = hits[searchIndex]; rtbOutput.SelectionLength = term.Length; rtbOutput.ScrollToCaret();
                lblSearchResult.Text = $"{searchIndex + 1} / {hits.Count}"; lblSearchResult.ForeColor = Color.Silver;
            }
            btnSearchNext.Click += (s, e) => DoSearch(true);
            btnSearchPrev.Click += (s, e) => DoSearch(false);
            txtSearch.TextChanged += (s, e) => { searchIndex = -1; DoSearch(true); };
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoSearch(true); e.Handled = true; e.SuppressKeyPress = true; } if (e.KeyCode == Keys.Escape) { ClearHL(); searchPanel.Visible = false; rtbOutput.Focus(); e.Handled = true; e.SuppressKeyPress = true; } };
            rtbOutput.CmdKeyInterceptor = keyData => { if (keyData == (Keys.Control | Keys.F)) { searchPanel.Visible = true; txtSearch.Focus(); txtSearch.SelectAll(); return true; } return false; };

            // Alt panel — FIX #5: Arşiv label genişliği azaltıldı
            Panel bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 33, BackColor = Color.FromArgb(55, 55, 58), Padding = new Padding(2, 5, 2, 5) };

            Button btnCopy = MakeButton(Lang.Get("📋 Kopyala", "📋 Copy"), 80);
            btnCopy.Click += (s, e) => { if (!string.IsNullOrEmpty(rtbOutput.Text)) Clipboard.SetText(rtbOutput.Text); };

            Button btnSaveAs = MakeButton(Lang.Get("💾 Dışa Aktar", "💾 Export"), 80);
            btnSaveAs.Click += (s, e) =>
            {
                using var sfd = new SaveFileDialog { Title = Lang.Get("Farklı Kaydet", "Save As"), Filter = "Text File (*.txt)|*.txt|All Files (*.*)|*.*", FileName = Path.GetFileNameWithoutExtension(filePath) + "_export", InitialDirectory = _logFolderPath };
                if (sfd.ShowDialog() == DialogResult.OK) try { File.WriteAllText(sfd.FileName, rtbOutput.Text); } catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
            };

            Button btnClear = MakeButton(Lang.Get("🗑 Temizle", "🗑 Clear"), 70);
            btnClear.Click += (s, e) => rtbOutput.Clear();

            Button btnSearchTrigger = MakeButton(Lang.Get("🔍 Ara", "🔍 Search"), 65);
            btnSearchTrigger.Click += (s, e) => { searchPanel.Visible = true; txtSearch.Focus(); txtSearch.SelectAll(); };

            // FIX #5: Label genişliği 320 → 200
            Label lblArchiveInfo = new Label
            {
                Text = Lang.Get($"  📂 {sizeStr}  ·  {sessionDate}", $"  📂 {sizeStr}  ·  {sessionDate}"),
                Dock = DockStyle.Left,
                AutoSize = false,
                Width = 200,
                ForeColor = Color.FromArgb(130, 110, 60),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft
            };

            Button btnSmartSaveLog = MakeButton(Lang.Get("💾 Kaydet", "💾 Save"), 80);
            btnSmartSaveLog.BackColor = Color.FromArgb(0, 100, 60);
            btnSmartSaveLog.Visible = false;

            Panel logSettingsRow1 = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(25, 25, 45), Padding = new Padding(4, 3, 4, 3) };

            Label lblFontL = new Label { Text = Lang.Get("Yazı:", "Font:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 32, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 22 };
            ComboBox cboFontL = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 8f), FlatStyle = FlatStyle.Flat, Dock = DockStyle.Left, Height = 22 };
            foreach (var f in new[] { "Consolas", "Courier New", "Lucida Console", "Segoe UI", "Segoe UI Emoji", "Arial", "Verdana" }) cboFontL.Items.Add(f);
            cboFontL.SelectedItem = "Consolas";

            Label lblSizeL = new Label { Text = Lang.Get("Boyut:", "Size:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 40, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 22 };
            ComboBox cboSizeL = new ComboBox { Width = 45, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 8f), FlatStyle = FlatStyle.Flat, Dock = DockStyle.Left, Height = 22 };
            foreach (var sz in new[] { 7, 8, 9, 10, 11, 12, 14, 16, 18, 20 }) cboSizeL.Items.Add(sz);
            cboSizeL.SelectedItem = 10;

            void CheckLogProfileState() { if (groupBox.IsDisposed) return; string cur = SerializeProfile(rtbOutput, cboFontL, cboSizeL, logTsColor); var profiles = LoadProfiles(); btnSmartSaveLog.Visible = !profiles.Values.Contains(cur); }
            void ApplyLogFont() { string fn = cboFontL.SelectedItem?.ToString() ?? "Consolas"; int fs = cboSizeL.SelectedItem is int sz ? sz : 10; logRegularFont = new Font(fn, fs, FontStyle.Regular); logBoldFont = new Font(fn, fs, FontStyle.Bold); rtbOutput.Font = logRegularFont; CheckLogProfileState(); }
            cboFontL.SelectedIndexChanged += (s, e) => ApplyLogFont();
            cboSizeL.SelectedIndexChanged += (s, e) => ApplyLogFont();

            Button btnTextColorL = new Button { Width = 20, Height = 20, BackColor = rtbOutput.ForeColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left };
            btnTextColorL.FlatAppearance.BorderColor = Color.Gray;
            btnTextColorL.Click += (s, e) => { using var cd = new ColorDialog { Color = rtbOutput.ForeColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; rtbOutput.ForeColor = cd.Color; btnTextColorL.BackColor = cd.Color; CheckLogProfileState(); };
            Label lblTcL = new Label { Text = Lang.Get("Yazı:", "Text:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 32, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };

            Button btnBgColorL = new Button { Width = 20, Height = 20, BackColor = rtbOutput.BackColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left };
            btnBgColorL.FlatAppearance.BorderColor = Color.Gray;
            btnBgColorL.Click += (s, e) => { using var cd = new ColorDialog { Color = rtbOutput.BackColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; rtbOutput.BackColor = cd.Color; btnBgColorL.BackColor = cd.Color; UpdateRTBBackgroundColor(rtbOutput, cd.Color); CheckLogProfileState(); };
            Label lblBgL = new Label { Text = Lang.Get("Zemin:", "Back:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 45, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };

            Button btnTsColorL = new Button { Width = 20, Height = 20, BackColor = logTsColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left };
            btnTsColorL.FlatAppearance.BorderColor = Color.Gray;
            btnTsColorL.Click += (s, e) => { using var cd = new ColorDialog { Color = logTsColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; logTsColor = cd.Color; btnTsColorL.BackColor = cd.Color; UpdateTimestampColors(rtbOutput, logTsColor); CheckLogProfileState(); };
            Label lblTsL = new Label { Text = Lang.Get("Tarih:", "Time:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 36, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };

            Panel spacerL = new Panel { Dock = DockStyle.Left, Width = 15 };

            Button btnDefaultL = new Button { Dock = DockStyle.Left, Width = 100, Height = 22, FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.LimeGreen, Cursor = Cursors.Hand };
            btnDefaultL.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
            btnDefaultL.Paint += (s, pe) =>
            {
                pe.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                string t1 = "[D] "; string t2 = Lang.Get("Varsayılan", "Default"); using Font fnt = new Font("Consolas", 9f, FontStyle.Regular);
                SizeF s1 = pe.Graphics.MeasureString(t1, fnt), s2 = pe.Graphics.MeasureString(t2, fnt);
                float x = (btnDefaultL.Width - (s1.Width + s2.Width)) / 2; if (x < 2) x = 2;
                float y = (btnDefaultL.Height - Math.Max(s1.Height, s2.Height)) / 2;
                using (Brush bTs = new SolidBrush(Color.FromArgb(0, 200, 220))) using (Brush bFg = new SolidBrush(Color.LimeGreen)) { pe.Graphics.DrawString(t1, fnt, bTs, x, y); pe.Graphics.DrawString(t2, fnt, bFg, x + s1.Width - 4, y); }
            };
            btnDefaultL.Click += (s, e) =>
            {
                rtbOutput.BackColor = Color.Black; rtbOutput.ForeColor = Color.LimeGreen; logTsColor = Color.FromArgb(0, 200, 220);
                btnTextColorL.BackColor = Color.LimeGreen; btnBgColorL.BackColor = Color.Black; btnTsColorL.BackColor = logTsColor;
                cboFontL.SelectedItem = "Consolas"; cboSizeL.SelectedItem = 10; ApplyLogFont();
                UpdateRTBBackgroundColor(rtbOutput, Color.Black); UpdateTimestampColors(rtbOutput, logTsColor); CheckLogProfileState();
            };

            logSettingsRow1.Controls.AddRange(new Control[] { cboFontL, lblFontL, cboSizeL, lblSizeL, btnTextColorL, lblTcL, btnBgColorL, lblBgL, btnTsColorL, lblTsL, spacerL, btnDefaultL, btnSmartSaveLog });

            btnSmartSaveLog.Click += (s, e) =>
            {
                using Form prompt = new Form { Text = Lang.Get("Profil Kaydet", "Save Profile"), Size = new Size(300, 150), FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent, BackColor = Color.FromArgb(40, 40, 40) };
                Label lbl = new Label { Text = Lang.Get("Yeni Profil Adı:", "New Profile Name:"), ForeColor = Color.White, Location = new Point(10, 15), AutoSize = true, Font = new Font("Segoe UI", 9f) };
                TextBox txt = new TextBox { Location = new Point(10, 35), Width = 260, BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f) };
                Button btnOkP = new Button { Text = Lang.Get("Kaydet", "Save"), Location = new Point(110, 75), Width = 75, BackColor = Color.FromArgb(0, 100, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                Button btnCancelP = new Button { Text = Lang.Get("İptal", "Cancel"), Location = new Point(195, 75), Width = 75, BackColor = Color.FromArgb(100, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                btnOkP.FlatAppearance.BorderSize = 0; btnCancelP.FlatAppearance.BorderSize = 0;
                prompt.Controls.Add(lbl); prompt.Controls.Add(txt); prompt.Controls.Add(btnOkP); prompt.Controls.Add(btnCancelP);
                btnOkP.Click += (sender, args) => prompt.DialogResult = DialogResult.OK;
                btnCancelP.Click += (sender, args) => prompt.DialogResult = DialogResult.Cancel;
                if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text)) { var profiles = LoadProfiles(); profiles[txt.Text.Trim()] = SerializeProfile(rtbOutput, cboFontL, cboSizeL, logTsColor); SaveProfiles(profiles); GlobalProfileListChanged?.Invoke(); }
            };

            Panel logSettingsRow2 = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(20, 20, 38), Padding = new Padding(4) };
            FlowLayoutPanel flpProfilesL = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(20, 20, 38) };
            logSettingsRow2.Controls.Add(flpProfilesL);
            flpProfilesL.BringToFront();

            void UpdateLogProfileButtons()
            {
                if (groupBox.IsDisposed) return;
                var profiles = LoadProfiles();
                var toRemove = flpProfilesL.Controls.Cast<Control>().Where(c => c.Tag?.ToString() == "profile").ToList();
                foreach (var c in toRemove) { flpProfilesL.Controls.Remove(c); c.Dispose(); }
                int maxBtnH = 24;
                foreach (var kv in profiles) { try { string[] p = kv.Value.Split('|'); int fs = int.Parse(p[4]); int h = Math.Max(24, fs * 2 + 6); if (h > maxBtnH) maxBtnH = h; } catch { } }
                foreach (var kv in profiles)
                {
                    string pName = kv.Key; string data = kv.Value;
                    Color fg = Color.White, bg = Color.FromArgb(0, 80, 140), ts2 = Color.Cyan; string fn = "Segoe UI"; int fs2 = 8;
                    try { string[] p = data.Split('|'); fg = Color.FromArgb(int.Parse(p[0])); bg = Color.FromArgb(int.Parse(p[1])); ts2 = Color.FromArgb(int.Parse(p[2])); fn = p[3]; fs2 = int.Parse(p[4]); } catch { }
                    Button btnP = new Button { Text = string.Empty, Width = 140, Height = maxBtnH, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = fg, Font = new Font(fn, fs2, FontStyle.Regular), Cursor = Cursors.Hand, Tag = "profile" };
                    btnP.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
                    btnP.Paint += (sender, pe) =>
                    {
                        pe.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        if (btnP.BackColor == Color.FromArgb(180, 40, 40)) { using (Brush b = new SolidBrush(Color.White)) { string dt = Lang.Get("Siliniyor...", "Deleting..."); SizeF sd = pe.Graphics.MeasureString(dt, btnP.Font); pe.Graphics.DrawString(dt, btnP.Font, b, (btnP.Width - sd.Width) / 2, (btnP.Height - sd.Height) / 2); } return; }
                        string t1 = "[D] "; string t2 = pName;
                        SizeF s1 = pe.Graphics.MeasureString(t1, btnP.Font), s2 = pe.Graphics.MeasureString(t2, btnP.Font);
                        float x = (btnP.Width - (s1.Width + s2.Width)) / 2; if (x < 2) x = 2; float y = (btnP.Height - Math.Max(s1.Height, s2.Height)) / 2;
                        using (Brush bTs2 = new SolidBrush(ts2)) using (Brush bFg2 = new SolidBrush(fg)) { pe.Graphics.DrawString(t1, btnP.Font, bTs2, x, y); pe.Graphics.DrawString(t2, btnP.Font, bFg2, x + s1.Width - 4, y); }
                    };
                    System.Windows.Forms.Timer lpTimer = new System.Windows.Forms.Timer { Interval = 800 };
                    bool isLp = false;
                    lpTimer.Tick += (s, e) => { lpTimer.Stop(); isLp = true; btnP.BackColor = Color.FromArgb(180, 40, 40); btnP.Invalidate(); btnP.Refresh(); var upd = LoadProfiles(); if (upd.Remove(pName)) { SaveProfiles(upd); GlobalProfileListChanged?.Invoke(); } };
                    btnP.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { isLp = false; lpTimer.Start(); } };
                    btnP.MouseUp += (s, e) => lpTimer.Stop();
                    btnP.MouseLeave += (s, e) => lpTimer.Stop();
                    btnP.Disposed += (s, e) => lpTimer.Dispose();
                    btnP.Click += (s, e) => { if (isLp) return; logTsColor = ApplyProfile(data, rtbOutput, cboFontL, cboSizeL, btnTextColorL, btnBgColorL, btnTsColorL, ref logRegularFont, ref logBoldFont); UpdateRTBBackgroundColor(rtbOutput, rtbOutput.BackColor); UpdateTimestampColors(rtbOutput, logTsColor); CheckLogProfileState(); };
                    flpProfilesL.Controls.Add(btnP);
                }
                CheckLogProfileState();
            }

            GlobalProfileListChanged += UpdateLogProfileButtons;
            UpdateLogProfileButtons();

            Panel logSettingsPanel = new Panel { Dock = DockStyle.Bottom, Height = 76, Visible = false, BackColor = Color.FromArgb(25, 25, 45) };
            logSettingsPanel.Controls.Add(logSettingsRow2);
            logSettingsPanel.Controls.Add(logSettingsRow1);

            Button btnSettings = MakeButton(Lang.Get("⚙ Görünüm", "⚙ Settings"), 85);
            btnSettings.Click += (s, e) =>
            {
                logSettingsPanel.Visible = !logSettingsPanel.Visible;
                if (logSettingsPanel.Visible) { btnSettings.BackColor = Color.FromArgb(60, 60, 100); }
                else { btnSettings.BackColor = Color.FromArgb(75, 75, 78); }
            };

            // FIX #5: Dock Left olan label, butonlar Dock Right — label dar tutuldu
            bottomPanel.Controls.Add(lblArchiveInfo);
            bottomPanel.Controls.Add(btnCopy);
            bottomPanel.Controls.Add(btnSaveAs);
            bottomPanel.Controls.Add(btnClear);
            bottomPanel.Controls.Add(btnSearchTrigger);
            bottomPanel.Controls.Add(btnSettings);

            // Sağ tık menüsü
            (Color Fore, Color Back) pendingHlColor = GenerateRandomHighlightColor();
            bool pendingIsRemove = false;
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            ToolStripMenuItem menuCopySelected = new ToolStripMenuItem("📋  Kopyala") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            menuCopySelected.Click += (s, e) => { if (rtbOutput.SelectionLength > 0) Clipboard.SetText(rtbOutput.SelectedText); else if (!string.IsNullOrEmpty(rtbOutput.Text)) Clipboard.SetText(rtbOutput.Text); };
            ToolStripMenuItem menuSearchThis = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            menuSearchThis.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; searchPanel.Visible = true; searchIndex = -1; if (txtSearch.Text != term) txtSearch.Text = term; else DoSearch(true); txtSearch.Focus(); txtSearch.SelectAll(); };
            ToolStripMenuItem menuSearchAll = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            menuSearchAll.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; foreach (var action in _windowSearchActions.Values.ToList()) action(term); };
            ToolStripMenuItem menuHighlight = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ImageScaling = ToolStripItemImageScaling.None };
            menuHighlight.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; if (pendingIsRemove) { _highlightRules.Remove(term); SaveHighlights(); BuildHighlightRegex(); RemoveWordHighlightFromAllWindows(term); } else { _highlightRules[term] = pendingHlColor; SaveHighlights(); BuildHighlightRegex(); ApplyHighlightsToAllWindows(); } };
            ToolStripMenuItem menuCloseLog = new ToolStripMenuItem(Lang.Get("Bu Pencereyi Kapat", "Close This Window")) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            menuCloseLog.Click += (s, e) => { var minKey = _minimizedBoxes.FirstOrDefault(kv => kv.Value == groupBox).Key; if (minKey != null) { _minimizedBoxes.Remove(minKey); UpdateMinimizedBarWithLogs(); } _mainLayout.Controls.Remove(groupBox); groupBox.Dispose(); RearrangeGrid(); };
            contextMenu.Opening += (s, e) =>
            {
                bool hasSel = rtbOutput.SelectionLength > 0;
                menuSearchThis.Enabled = hasSel; menuSearchAll.Enabled = hasSel; menuHighlight.Enabled = hasSel;
                if (hasSel) { string fullTerm = rtbOutput.SelectedText.Trim(); string preview = fullTerm.Length > 22 ? fullTerm.Substring(0, 22) + "..." : fullTerm; menuSearchThis.Text = Lang.Get($"🔍 Bu Pencerede Ara: \"{preview}\"", $"🔍 Search in This Window: \"{preview}\""); menuSearchAll.Text = Lang.Get($"🔍 Tüm Pencerelerde Ara: \"{preview}\"", $"🔍 Search in All Windows: \"{preview}\""); bool alreadyHL = !string.IsNullOrEmpty(fullTerm) && _highlightRules.ContainsKey(fullTerm); if (alreadyHL) { pendingIsRemove = true; var ec = _highlightRules[fullTerm]; menuHighlight.Text = Lang.Get($"🗑 Vurguyu Kaldır '{preview}'", $"🗑 Remove Highlight '{preview}'"); menuHighlight.Image = GenerateColorPreviewBitmap(ec.Fore, ec.Back); } else { pendingIsRemove = false; pendingHlColor = GenerateRandomHighlightColor(); menuHighlight.Text = Lang.Get($"🖍 Vurgula '{preview}'", $"🖍 Highlight '{preview}'"); menuHighlight.Image = GenerateColorPreviewBitmap(pendingHlColor.Fore, pendingHlColor.Back); } }
                else { pendingIsRemove = false; menuSearchThis.Text = Lang.Get("🔍 Bu Pencerede Ara (metin seçin)", "🔍 Search in This Window (select text first)"); menuSearchAll.Text = Lang.Get("🔍 Tüm Pencerelerde Ara (metin seçin)", "🔍 Search in All Windows (select text first)"); menuHighlight.Text = Lang.Get("🖍 Vurgula (metin seçin)", "🖍 Highlight (select text first)"); menuHighlight.Image = null; }
            };
            contextMenu.Items.Add(menuCopySelected); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuHighlight); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuSearchThis); contextMenu.Items.Add(menuSearchAll); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuCloseLog);
            groupBox.ContextMenuStrip = contextMenu;
            rtbOutput.ContextMenuStrip = contextMenu;

            groupBox.DoubleClick += (s, e) => ToggleZoom(groupBox);
            rtbOutput.CustomDoubleClick += (s, e) => ToggleZoom(groupBox);

            // Başlık butonları
            Button btnClose = new Button { Text = "✕", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(160, 60, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) =>
            {
                if (_zoomedBox == groupBox) ToggleZoom(groupBox);
                Form? df = _detachedForms.FirstOrDefault(f => f.Controls.Contains(groupBox));
                if (df != null) { _detachedForms.Remove(df); df.Controls.Remove(groupBox); df.Close(); }
                // Minimized bar'dan temizle
                var minKey = _minimizedBoxes.FirstOrDefault(kv => kv.Value == groupBox).Key;
                if (minKey != null) { _minimizedBoxes.Remove(minKey); UpdateMinimizedBarWithLogs(); }
                _mainLayout.Controls.Remove(groupBox);
                groupBox.Dispose();
                RearrangeGrid();
            };

            Button btnZoom = new Button { Text = "◻", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 80, 100), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false, Tag = "btnZoom" };
            btnZoom.FlatAppearance.BorderSize = 0;
            btnZoom.Click += (s, e) => ToggleZoom(groupBox);

            // FIX #1: Log penceresine de küçült butonu eklendi — log için farklı renk (teal)
            Button btnMinimize = new Button
            {
                Text = "─",
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 120),   // COM penceresinden farklı renk (teal)
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnMinimize.FlatAppearance.BorderSize = 0;
            btnMinimize.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 150, 150);
            btnMinimize.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 90, 90);
            string logWindowId = $"LOG_{filePath}";
            btnMinimize.Click += (s, e) =>
            {
                // Detach modundaysa host form'u Windows seviyesinde minimize et
                Form? hostForm = _detachedForms.FirstOrDefault(f => f.Controls.Contains(groupBox));
                if (hostForm != null)
                {
                    hostForm.WindowState = FormWindowState.Minimized;
                    return;
                }
                // Ana ekrandaysa minibar'a taşı
                MinimizeLogWindow(logWindowId, groupBox, portPart);
            };
            _toolTip.SetToolTip(btnMinimize, Lang.Get("Log penceresini küçült", "Minimize log window"));

            Button btnDetach = new Button { Text = "⧉", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 80, 50), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false, Tag = "btnDetach" };
            btnDetach.FlatAppearance.BorderSize = 0;
            btnDetach.Click += (s, e) => ToggleDetach(groupBox, $"📂 {portPart}  ·  {sessionDate}");

            groupBox.SizeChanged += (s, e) =>
            {
                btnClose.Location = new Point(groupBox.Width - 28, 1);
                btnZoom.Location = new Point(groupBox.Width - 54, 1);
                btnMinimize.Location = new Point(groupBox.Width - 80, 1);
                btnDetach.Location = new Point(groupBox.Width - 106, 1);
            };
            btnClose.Location = new Point(groupBox.Width - 28, 1);
            btnZoom.Location = new Point(groupBox.Width - 54, 1);
            btnMinimize.Location = new Point(groupBox.Width - 80, 1);
            btnDetach.Location = new Point(groupBox.Width - 106, 1);

            Panel topSpacer = new Panel { Dock = DockStyle.Top, Height = 5, BackColor = Color.Transparent };

            groupBox.Controls.Add(rtbWrapper);
            groupBox.Controls.Add(searchPanel);
            groupBox.Controls.Add(topSpacer);
            groupBox.Controls.Add(logSettingsPanel);
            groupBox.Controls.Add(bottomPanel);
            groupBox.Controls.Add(btnDetach);
            groupBox.Controls.Add(btnMinimize);
            groupBox.Controls.Add(btnZoom);
            groupBox.Controls.Add(btnClose);

            btnClose.BringToFront();
            btnZoom.BringToFront();
            btnMinimize.BringToFront();
            btnDetach.BringToFront();

            Action updateLogTexts = () =>
            {
                if (groupBox.IsDisposed) return;
                btnCopy.Text = Lang.Get("📋 Kopyala", "📋 Copy");
                btnSaveAs.Text = Lang.Get("💾 Dışa Aktar", "💾 Export");
                btnClear.Text = Lang.Get("🗑 Temizle", "🗑 Clear");
                btnSearchTrigger.Text = Lang.Get("🔍 Ara", "🔍 Search");
                btnSettings.Text = Lang.Get("⚙ Görünüm", "⚙ Settings");
                lblArchiveInfo.Text = Lang.Get($"  📂 {sizeStr}  ·  {sessionDate}", $"  📂 {sizeStr}  ·  {sessionDate}");
                menuCloseLog.Text = Lang.Get("Bu Pencereyi Kapat", "Close This Window");
            };
            updateLogTexts();
            Lang.Changed += updateLogTexts;
            groupBox.Disposed += (s, e) => { Lang.Changed -= updateLogTexts; GlobalProfileListChanged -= UpdateLogProfileButtons; };

            _mainLayout.Controls.Add(groupBox);
            RearrangeGrid();

            // FIX #3: Async log yükleme + progress bar
            LoadLogContentAsync(groupBox, rtbOutput, filePath, logBoldFont, logRegularFont, logTsColor);
        }

        // FIX #1: Log penceresi küçültme — sarı değil teal renk buton
        private void MinimizeLogWindow(string logId, GroupBox groupBox, string label)
        {
            if (_minimizedBoxes.ContainsKey(logId)) return;
            if (_zoomedBox == groupBox) ToggleZoom(groupBox);
            _mainLayout.Controls.Remove(groupBox);
            _minimizedBoxes[logId] = groupBox;
            RearrangeGrid();
            UpdateMinimizedBarWithLogs();
            UpdateStatusBar();
        }

        private void UpdateMinimizedBarWithLogs()
        {
            _minimizedBar.SuspendLayout();
            var old = _minimizedBar.Controls.Cast<Control>().ToList();
            _minimizedBar.Controls.Clear();
            foreach (var c in old) c.Dispose();

            var sorted = _minimizedBoxes.Keys
                .OrderByDescending(p => { var mx = Regex.Match(p, @"\d+"); return mx.Success ? int.Parse(mx.Value) : 0; })
                .ToList();

            foreach (string key in sorted)
            {
                bool isLog = key.StartsWith("LOG_");
                string label = isLog
                    ? "📂"
                    : GetMinimizedLabel(key);

                // FIX #1: Log pencereleri için farklı (teal) renk
                Color btnColor = isLog
                    ? Color.FromArgb(0, 120, 120)
                    : Color.FromArgb(200, 110, 0);
                Color btnBorder = isLog
                    ? Color.FromArgb(0, 180, 180)
                    : Color.FromArgb(255, 150, 30);

                string capturedKey = key;
                Button btn = new Button
                {
                    Text = label,
                    Width = isLog ? 36 : 42,
                    Height = 26,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = btnColor,
                    ForeColor = Color.White,
                    Font = new Font(isLog ? "Segoe UI" : "Consolas", isLog ? 10f : 8.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(2, 0, 2, 0),
                    Tag = capturedKey
                };
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = btnBorder;
                btn.FlatAppearance.MouseOverBackColor = isLog ? Color.FromArgb(0, 150, 150) : Color.FromArgb(230, 140, 20);
                btn.FlatAppearance.MouseDownBackColor = isLog ? Color.FromArgb(0, 90, 90) : Color.FromArgb(170, 85, 0);
                btn.Click += (s, e) => RestoreMinimizedWindow(capturedKey);

                string portName = isLog ? "Log" : capturedKey;
                _toolTip.SetToolTip(btn, Lang.Get($"{portName} — Geri getirmek için tıkla", $"{portName} — Click to restore"));
                _minimizedBar.Controls.Add(btn);
            }
            _minimizedBar.ResumeLayout();
        }

        private void RestoreMinimizedWindow(string key)
        {
            if (!_minimizedBoxes.TryGetValue(key, out GroupBox? groupBox)) return;
            _minimizedBoxes.Remove(key);
            _mainLayout.Controls.Add(groupBox);
            RearrangeGrid();
            UpdateMinimizedBarWithLogs();
            UpdateStatusBar();

            // COM port ise frozen buffer'ı boşalt
            if (!key.StartsWith("LOG_") && _frozenBuffer.ContainsKey(key) && _frozenBuffer[key].Length > 0 && !_scrollFrozen.GetValueOrDefault(key))
            {
                string buffered = _frozenBuffer[key].ToString();
                _frozenBuffer[key].Clear();
                var rtb = groupBox.Controls.Find("rtbOutput", true).FirstOrDefault() as ScrollAwareRichTextBox;
                if (rtb != null && !rtb.IsDisposed)
                {
                    Font boldF = new Font("Consolas", 10, FontStyle.Bold);
                    Font regularF = new Font("Consolas", 10, FontStyle.Regular);
                    AppendColoredText(rtb, buffered, key, boldF, regularF, Color.FromArgb(0, 200, 220));
                    rtb.SelectionStart = rtb.TextLength;
                    rtb.ScrollToCaret();
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  FIX #3: Async log yükleme + progress bar overlay
        // ════════════════════════════════════════════════════════════════
        private async void LoadLogContentAsync(GroupBox groupBox, ScrollAwareRichTextBox rtbOutput, string filePath,
            Font boldFont, Font regularFont, Color tsColor)
        {
            using var cts = new System.Threading.CancellationTokenSource();
            var token = cts.Token;
            bool cancelled = false;

            lock (_activeLoads) _activeLoads.Add(cts);

            // Progress overlay
            Panel progressOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(220, 30, 30, 35),
                Visible = true
            };

            Label lblLoading = new Label
            {
                Text = Lang.Get("⏳  Log dosyası yükleniyor...", "⏳  Loading log file..."),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 30)
            };

            ProgressBar progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25,
                Location = new Point(20, 60),
                Width = 260,
                Height = 18,
                ForeColor = Color.FromArgb(0, 200, 220),
                BackColor = Color.FromArgb(40, 40, 60)
            };

            Label lblPercent = new Label
            {
                Text = "0%",
                ForeColor = Color.FromArgb(0, 200, 220),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Location = new Point(290, 63)
            };

            Button btnCancel = new Button
            {
                Text = Lang.Get("✕  İptal", "✕  Cancel"),
                Location = new Point(20, 90),
                Width = 100,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(160, 40, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 60, 60);
            btnCancel.Click += (s, e) => { cancelled = true; cts.Cancel(); btnCancel.Enabled = false; btnCancel.Text = Lang.Get("İptal ediliyor...", "Cancelling..."); };

            progressOverlay.Controls.Add(lblLoading);
            progressOverlay.Controls.Add(progressBar);
            progressOverlay.Controls.Add(lblPercent);
            progressOverlay.Controls.Add(btnCancel);

            // Overlay RTB'nin üstüne yerleştir
            groupBox.Controls.Add(progressOverlay);
            progressOverlay.BringToFront();
            groupBox.Refresh();
            progressOverlay.Refresh();

            try
            {
                // Dosyayı arka planda oku
                string content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8), token);

                token.ThrowIfCancellationRequested();

                // Satırları arka planda böl
                string[] lines = await Task.Run(() =>
                    content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'), token);

                int totalLines = lines.Length;
                lblLoading.Text = Lang.Get($"⏳  {totalLines:N0} satır yükleniyor...", $"⏳  Loading {totalLines:N0} lines...");
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Maximum = 100;
                progressBar.Value = 0;

                var tsRegex = new Regex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] ", RegexOptions.Compiled);
                Color txColor = Color.Orange;   // TX mesajları için sabit turuncu
                Color textColor = rtbOutput.ForeColor;
                // tsColor parametresi — kullanıcının seçtiği timestamp rengi

                SendMessage(rtbOutput.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);

                int batchSize = 500;

                for (int i = 0; i < lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();

                    string rawLine = lines[i];

                    if (rawLine.Length == 0) { rtbOutput.AppendText("\n"); }
                    else
                    {
                        bool isTx = rawLine.Contains("[TX] ");
                        var tsMatch = tsRegex.Match(rawLine);
                        if (tsMatch.Success)
                        {
                            // Timestamp kısmı — tsColor ile renklendir
                            string timeOnly = "[" + tsMatch.Value.Substring(12, 12) + "] ";
                            rtbOutput.SelectionStart = rtbOutput.TextLength;
                            rtbOutput.SelectionLength = 0;
                            rtbOutput.SelectionColor = tsColor;
                            rtbOutput.SelectionBackColor = rtbOutput.BackColor;
                            rtbOutput.SelectionFont = boldFont;
                            rtbOutput.AppendText(timeOnly);
                            AppendLogSegment(rtbOutput, rawLine.Substring(tsMatch.Length), isTx, txColor, textColor, boldFont, regularFont);
                        }
                        else
                        {
                            AppendLogSegment(rtbOutput, rawLine, isTx, txColor, textColor, boldFont, regularFont);
                        }
                        rtbOutput.AppendText("\n");
                    }

                    // Her 500 satırda bir UI güncelle
                    if (i % batchSize == 0 && i > 0)
                    {
                        int pct = (int)((double)i / totalLines * 100);
                        if (progressBar.Value != pct) progressBar.Value = pct;
                        lblPercent.Text = $"{pct}%";
                        await Task.Delay(1, token); // UI'ın nefes almasına izin ver
                    }
                }

                // Vurgu kurallarını uygula
                if (_highlightRegex != null && _highlightRules.Count > 0)
                {
                    var matches = _highlightRegex.Matches(rtbOutput.Text);
                    foreach (Match m in matches)
                    {
                        rtbOutput.SelectionStart = m.Index;
                        rtbOutput.SelectionLength = m.Length;
                        if (_highlightRules.TryGetValue(m.Value, out var colors))
                        {
                            rtbOutput.SelectionColor = colors.Fore;
                            rtbOutput.SelectionBackColor = colors.Back;
                            rtbOutput.SelectionFont = boldFont;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // İptal edildi — RTB'yi temizle
                if (!rtbOutput.IsDisposed)
                {
                    rtbOutput.Clear();
                    rtbOutput.AppendText(Lang.Get("⚠  Yükleme iptal edildi.\n", "⚠  Loading cancelled.\n"));
                }
            }
            catch (Exception ex)
            {
                if (!rtbOutput.IsDisposed)
                    rtbOutput.AppendText($"\n[HATA / ERROR] {ex.Message}\n");
            }
            finally
            {
                lock (_activeLoads) _activeLoads.Remove(cts);

                try
                {
                    if (!rtbOutput.IsDisposed)
                    {
                        rtbOutput.SelectionStart = 0;
                        rtbOutput.SelectionLength = 0;
                        rtbOutput.ScrollToCaret();
                        SendMessage(rtbOutput.Handle, 0x000B, new IntPtr(1), IntPtr.Zero);
                        if (!cancelled) UpdateTimestampColors(rtbOutput, tsColor);
                        rtbOutput.Refresh();
                    }
                }
                catch { }

                try
                {
                    progressOverlay.Visible = false;
                    if (!groupBox.IsDisposed) groupBox.Controls.Remove(progressOverlay);
                    progressOverlay.Dispose();
                }
                catch { }
            }
        }

        // (Removed: LoadLogContent — artık async versiyonu kullanılıyor)

        private void AppendLogSegment(ScrollAwareRichTextBox rtb, string text, bool isTx, Color txColor, Color textColor, Font boldFont, Font regularFont)
        {
            if (string.IsNullOrEmpty(text)) return;
            Color baseColor = isTx ? txColor : textColor;
            Font baseFont = isTx ? boldFont : regularFont;
            if (_highlightRegex != null && _highlightRules.Count > 0 && !isTx)
            {
                var matches = _highlightRegex.Matches(text); int lastIdx = 0;
                foreach (Match m in matches)
                {
                    if (m.Index > lastIdx) { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = textColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText(text.Substring(lastIdx, m.Index - lastIdx)); }
                    rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0;
                    if (_highlightRules.TryGetValue(m.Value, out var cl)) { rtb.SelectionColor = cl.Fore; rtb.SelectionBackColor = cl.Back; } else { rtb.SelectionColor = textColor; rtb.SelectionBackColor = rtb.BackColor; }
                    rtb.SelectionFont = boldFont; rtb.AppendText(m.Value); lastIdx = m.Index + m.Length;
                }
                if (lastIdx < text.Length) { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = textColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText(text.Substring(lastIdx)); }
            }
            else { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = baseColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = baseFont; rtb.AppendText(text); }
        }

        // ════════════════════════════════════════════════════════════════
        //  PORT PENCERESİ
        //  FIX #2: Durdur/Devam Et butonu eklendi
        //  FIX #4: RtbWrapper beyaz alan düzeltmesi
        //  FIX #5: Status label genişliği azaltıldı
        //  FIX #8: Emoji font desteği
        // ════════════════════════════════════════════════════════════════
        private void StartScanner() { _portScanner = new System.Windows.Forms.Timer { Interval = 2000 }; _portScanner.Tick += CheckPorts; _portScanner.Start(); }

        private void CheckPorts(object? sender, EventArgs? e)
        {
            string[] systemPorts = SerialPort.GetPortNames();
            bool layoutChanged = false;
            foreach (string portName in systemPorts)
            {
                if (_portMap.Values.Any(p => p.PortName == portName)) { _busyPorts.Remove(portName); continue; }
                if (_ignoredPorts.Contains(portName)) { _busyPorts.Remove(portName); continue; }
                if (TryCreatePortWindow(portName)) { _busyPorts.Remove(portName); layoutChanged = true; } else _busyPorts.Add(portName);
            }
            List<GroupBox> toRemove = _portMap.Keys.Where(box => !systemPorts.Contains(_portMap[box].PortName)).ToList();
            foreach (var box in toRemove) { string pName = _portMap[box].PortName; if (_ignoredPorts.Contains(pName)) _ignoredPorts.Remove(pName); _busyPorts.Remove(pName); CloseSpecificPort(box, false, false); layoutChanged = true; }
            var missingBusy = _busyPorts.Where(p => !systemPorts.Contains(p)).ToList();
            foreach (var mb in missingBusy) _busyPorts.Remove(mb);
            if (layoutChanged) RearrangeGrid();
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            if (this.IsDisposed) return;
            string[] systemPorts = SerialPort.GetPortNames();
            var controlsToRemove = _statusBar.Controls.Cast<Control>().Where(c => !systemPorts.Contains(c.Text)).ToList();
            foreach (var c in controlsToRemove) { _statusBar.Controls.Remove(c); c.Dispose(); }
            foreach (string portName in systemPorts.OrderBy(p => p.Length).ThenBy(p => p))
            {
                Button? btn = _statusBar.Controls.Cast<Control>().FirstOrDefault(c => c.Text == portName) as Button;
                if (btn == null) { btn = new Button { Text = portName, Width = 65, Height = 26, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(2, 0, 2, 0) }; btn.FlatAppearance.BorderSize = 0; btn.Click += (s, e) => TogglePortState(portName); _statusBar.Controls.Add(btn); }
                bool isOpen = _portMap.Values.Any(port => port.PortName == portName), isBusy = _busyPorts.Contains(portName);
                if (isOpen) { btn.BackColor = Color.FromArgb(40, 140, 40); btn.ForeColor = Color.White; btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 160, 50); btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 110, 30); _toolTip.SetToolTip(btn, Lang.Get($"{portName} açık. Kapatmak/Gizlemek için tıkla.", $"{portName} is open. Click to close/ignore.")); }
                else if (isBusy) { btn.BackColor = Color.FromArgb(160, 40, 40); btn.ForeColor = Color.White; _toolTip.SetToolTip(btn, Lang.Get($"{portName} meşgul/kullanımda.", $"{portName} is busy/in use.")); }
                else { btn.BackColor = Color.FromArgb(60, 60, 65); btn.ForeColor = Color.Silver; _toolTip.SetToolTip(btn, Lang.Get($"{portName} kapalı (gizlendi). Açmak için tıkla.", $"{portName} is closed (ignored). Click to open.")); }
            }
        }

        private void TogglePortState(string portName)
        {
            GroupBox? targetBox = _portMap.FirstOrDefault(kvp => kvp.Value.PortName == portName).Key;
            if (targetBox != null) CloseSpecificPort(targetBox, true, true);
            else
            {
                if (_ignoredPorts.Contains(portName)) _ignoredPorts.Remove(portName);
                if (SerialPort.GetPortNames().Contains(portName))
                {
                    if (TryCreatePortWindow(portName)) { _busyPorts.Remove(portName); RearrangeGrid(); }
                    else { _busyPorts.Add(portName); MessageBox.Show(Lang.Get($"{portName} halen başka bir program tarafından kullanılıyor.", $"{portName} is still in use by another program."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    UpdateStatusBar();
                }
                else { MessageBox.Show(Lang.Get($"{portName} sistemde bulunamadı.", $"{portName} not found in system."), Lang.Get("Hata", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); UpdateStatusBar(); }
            }
        }

        private async void AnimateMinimizedButton(string key)
        {
            if (_isAnimating.TryGetValue(key, out bool animating) && animating) return;
            Button? btn = null;
            if (_minimizedBar.InvokeRequired)
                _minimizedBar.Invoke(new Action(() => { btn = _minimizedBar.Controls.Cast<Control>().FirstOrDefault(c => c.Tag?.ToString() == key) as Button; }));
            else
                btn = _minimizedBar.Controls.Cast<Control>().FirstOrDefault(c => c.Tag?.ToString() == key) as Button;

            if (btn == null || btn.IsDisposed) return;
            _isAnimating[key] = true;
            bool isLog = key.StartsWith("LOG_");
            Color originalColor = isLog ? Color.FromArgb(0, 120, 120) : Color.FromArgb(200, 110, 0);
            Color highlightColor = Color.FromArgb(255, 220, 80);
            try
            {
                if (btn.IsDisposed) return;
                btn.Invoke((MethodInvoker)(() => { if (!btn.IsDisposed) btn.BackColor = highlightColor; }));
                await Task.Delay(150);
                if (btn.IsDisposed) return;
                btn.Invoke((MethodInvoker)(() => { if (!btn.IsDisposed) btn.BackColor = originalColor; }));
            }
            catch { }
            finally { _isAnimating[key] = false; }
        }

        private bool TryCreatePortWindow(string portName)
        {
            SerialPort port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
            try { port.Open(); port.DiscardInBuffer(); } catch { port.Dispose(); return false; }

            _atLineStart[portName] = true;
            _scrollFrozen[portName] = false;
            _frozenBuffer[portName] = new StringBuilder();
            _logAtLineStart[portName] = true;
            _loggingEnabled[portName] = false;
            _receiveBuffer[portName] = new StringBuilder();
            _repeatCheck[portName] = (string.Empty, 0, DateTime.Now);
            _portPaused[portName] = false; // FIX #2

            var flushTmr = new System.Windows.Forms.Timer { Interval = 50 };
            _flushTimer[portName] = flushTmr;

            GroupBox groupBox = new GroupBox
            {
                Text = $"{portName} - {Lang.Get("AÇIK", "ONLINE")} (115200)",
                Size = new Size(200, 100),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(40, 40, 40)
            };

            groupBox.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && e.Y <= 30) { _dragSource = groupBox; _originalDragSourceColor = groupBox.BackColor; groupBox.BackColor = Color.FromArgb(80, 80, 80); groupBox.BringToFront(); } };
            groupBox.MouseMove += (s, e) =>
            {
                if (_dragSource == null || e.Button != MouseButtons.Left) return;
                Point screenPt = groupBox.PointToScreen(e.Location);
                GroupBox? target = null;
                foreach (Control c in _mainLayout.Controls) { if (c is GroupBox gb && gb != _dragSource) { var gbScreen = new Rectangle(_mainLayout.PointToScreen(gb.Location), gb.Size); if (gbScreen.Contains(screenPt)) { target = gb; break; } } }
                if (target != null) { if (_dragTarget != null && _dragTarget != target) _dragTarget.BackColor = _originalDragTargetColor; if (_dragTarget != target) { _dragTarget = target; _originalDragTargetColor = target.BackColor; target.BackColor = Color.Orange; } var controls = _mainLayout.Controls.Cast<Control>().ToList(); int idxSrc = controls.IndexOf(_dragSource); int idxDst = controls.IndexOf(target); if (idxSrc >= 0 && idxDst >= 0 && idxSrc != idxDst) { _mainLayout.SuspendLayout(); var posSrc = _mainLayout.GetCellPosition(_dragSource); var posDst = _mainLayout.GetCellPosition(target); _mainLayout.SetCellPosition(_dragSource, posDst); _mainLayout.SetCellPosition(target, posSrc); _mainLayout.ResumeLayout(); } }
                else { if (_dragTarget != null) { _dragTarget.BackColor = _originalDragTargetColor; _dragTarget = null; } }
            };
            groupBox.MouseUp += (s, e) => { if (_dragSource != null) { _dragSource.BackColor = _originalDragSourceColor; _dragSource = null; } if (_dragTarget != null) { _dragTarget.BackColor = _originalDragTargetColor; _dragTarget = null; } };

            // FIX #4: RTB Dock=Fill kullan, beyaz alan yok
            Panel rtbWrapper = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            ScrollAwareRichTextBox rtbOutput = new ScrollAwareRichTextBox
            {
                Name = "rtbOutput",
                BackColor = Color.Black,
                ForeColor = Color.LimeGreen,
                // FIX #8: Emoji destekli font
                Font = IsFontAvailable("Segoe UI Emoji")
                    ? new Font("Segoe UI Emoji", 10f, FontStyle.Regular)
                    : new Font("Consolas", 10f, FontStyle.Regular),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Dock = DockStyle.Fill  // FIX #4
            };
            rtbWrapper.Controls.Add(rtbOutput);

            Panel customScrollTrack = new Panel { Dock = DockStyle.Right, Width = 14, BackColor = Color.FromArgb(204, 30, 30, 35), Padding = new Padding(2) };
            Panel customScrollThumb = new Panel { Width = 10, Height = 30, BackColor = Color.FromArgb(100, 100, 110), Cursor = Cursors.Hand, Left = 2, Top = 2 };
            customScrollTrack.Controls.Add(customScrollThumb);

            bool isThumbDragging = false; int thumbDragStartY = 0, thumbStartTop = 0;
            Action updateScroll = () =>
            {
                if (rtbOutput.IsDisposed || customScrollTrack.IsDisposed) return;
                if (isThumbDragging) return;
                GetScrollRange(rtbOutput.Handle, 1, out _, out int max);
                int pos = GetScrollPos(rtbOutput.Handle, 1), clientH = rtbOutput.ClientSize.Height;
                if (max <= clientH || clientH == 0) { customScrollTrack.Visible = false; return; }
                customScrollTrack.Visible = true;
                float ratio = (float)clientH / max;
                int thumbH = Math.Max(20, (int)(customScrollTrack.Height * ratio));
                customScrollThumb.Height = thumbH;
                float scrollRatio = max > clientH ? (float)pos / (max - clientH) : 0;
                scrollRatio = Math.Max(0, Math.Min(1, scrollRatio));
                int maxTop = customScrollTrack.Height - thumbH - customScrollTrack.Padding.Bottom;
                customScrollThumb.Top = customScrollTrack.Padding.Top + (int)(scrollRatio * (maxTop - customScrollTrack.Padding.Top));
            };
            rtbOutput.Scrolled += (s, e) => updateScroll();
            rtbOutput.TextChanged += (s, e) => updateScroll();
            groupBox.SizeChanged += (s, e) => updateScroll();
            customScrollThumb.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { isThumbDragging = true; thumbDragStartY = Cursor.Position.Y; thumbStartTop = customScrollThumb.Top; customScrollThumb.BackColor = Color.FromArgb(140, 140, 150); } };
            customScrollThumb.MouseUp += (s, e) => { isThumbDragging = false; customScrollThumb.BackColor = Color.FromArgb(100, 100, 110); rtbOutput.Refresh(); };
            customScrollThumb.MouseMove += (s, e) =>
            {
                if (!isThumbDragging) return;
                int delta = Cursor.Position.Y - thumbDragStartY, newTop = thumbStartTop + delta, maxTop = customScrollTrack.Height - customScrollThumb.Height - customScrollTrack.Padding.Bottom;
                newTop = Math.Max(customScrollTrack.Padding.Top, Math.Min(newTop, maxTop));
                customScrollThumb.Top = newTop;
                float scrollRatio = (float)(newTop - customScrollTrack.Padding.Top) / Math.Max(1, maxTop - customScrollTrack.Padding.Top);
                if (rtbOutput.TextLength > 0) { int totalLines = rtbOutput.GetLineFromCharIndex(rtbOutput.TextLength - 1) + 1, targetLine = (int)(totalLines * scrollRatio); targetLine = Math.Max(0, Math.Min(targetLine, totalLines - 1)); int targetIndex = rtbOutput.GetFirstCharIndexFromLine(targetLine); if (targetIndex >= 0) { rtbOutput.SelectionStart = targetIndex; rtbOutput.SelectionLength = 0; rtbOutput.ScrollToCaret(); rtbOutput.Refresh(); } }
            };

            Font boldFont = new Font("Consolas", 10, FontStyle.Bold);
            Font regularFont = new Font("Consolas", 10, FontStyle.Regular);
            Color tsColor = Color.FromArgb(0, 200, 220);

            Panel searchPanel = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.FromArgb(30, 30, 60), Padding = new Padding(2), Visible = false };
            TextBox txtSearch = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 40), ForeColor = Color.White, Font = new Font("Consolas", 10), BorderStyle = BorderStyle.FixedSingle };
            Button btnSearchNext = new Button { Text = "▼", Dock = DockStyle.Right, Width = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            Button btnSearchPrev = new Button { Text = "▲", Dock = DockStyle.Right, Width = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            Label lblSearchResult = new Label { Dock = DockStyle.Right, Width = 80, ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleCenter };
            searchPanel.Controls.AddRange(new Control[] { txtSearch, btnSearchNext, btnSearchPrev, lblSearchResult });

            int searchIndex = 0;
            List<int> FindAll(string term) { var results = new List<int>(); if (string.IsNullOrEmpty(term)) return results; string textLower = rtbOutput.Text.ToLower(), termLower = term.ToLower(); int idx = 0; while ((idx = textLower.IndexOf(termLower, idx)) != -1) { results.Add(idx); idx += termLower.Length; } return results; }
            void ClearHighlights() { if (rtbOutput.IsDisposed) return; rtbOutput.SelectionStart = 0; rtbOutput.SelectionLength = rtbOutput.TextLength; rtbOutput.SelectionBackColor = rtbOutput.BackColor; rtbOutput.SelectionStart = rtbOutput.TextLength; rtbOutput.SelectionLength = 0; UpdateTimestampColors(rtbOutput, tsColor); ApplyHighlightsToRTB(rtbOutput); }
            void HighlightAll(List<int> hits, string term, int currentIdx) { ClearHighlights(); foreach (int hit in hits) { rtbOutput.SelectionStart = hit; rtbOutput.SelectionLength = term.Length; rtbOutput.SelectionBackColor = Color.FromArgb(0, 60, 160); } if (currentIdx >= 0 && currentIdx < hits.Count) { rtbOutput.SelectionStart = hits[currentIdx]; rtbOutput.SelectionLength = term.Length; rtbOutput.SelectionBackColor = Color.FromArgb(180, 80, 0); } }
            void DoSearch(bool forward)
            {
                string term = txtSearch.Text;
                if (string.IsNullOrEmpty(term)) { lblSearchResult.Text = ""; ClearHighlights(); return; }
                var hits = FindAll(term);
                if (hits.Count == 0) { lblSearchResult.Text = Lang.Get("Yok", "None"); lblSearchResult.ForeColor = Color.FromArgb(220, 80, 80); ClearHighlights(); return; }
                if (forward) searchIndex = (searchIndex + 1) % hits.Count; else searchIndex = (searchIndex - 1 + hits.Count) % hits.Count;
                HighlightAll(hits, term, searchIndex);
                rtbOutput.SelectionStart = hits[searchIndex]; rtbOutput.SelectionLength = term.Length; rtbOutput.ScrollToCaret();
                lblSearchResult.Text = $"{searchIndex + 1} / {hits.Count}"; lblSearchResult.ForeColor = Color.Silver;
            }
            btnSearchNext.Click += (s, e) => DoSearch(true); btnSearchPrev.Click += (s, e) => DoSearch(false);
            txtSearch.TextChanged += (s, e) => { searchIndex = -1; DoSearch(true); };
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoSearch(true); e.Handled = true; e.SuppressKeyPress = true; } if (e.KeyCode == Keys.Escape) { ClearHighlights(); searchPanel.Visible = false; rtbOutput.Focus(); e.Handled = true; e.SuppressKeyPress = true; } };
            rtbOutput.CmdKeyInterceptor = keyData => { if (keyData == (Keys.Control | Keys.F)) { searchPanel.Visible = true; txtSearch.Focus(); txtSearch.SelectAll(); return true; } return false; };
            rtbOutput.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.F) { searchPanel.Visible = true; txtSearch.Focus(); txtSearch.SelectAll(); e.Handled = true; } };
            groupBox.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.F) { searchPanel.Visible = true; txtSearch.Focus(); txtSearch.SelectAll(); e.Handled = true; } };

            _windowSearchActions[portName] = (term) =>
            {
                if (groupBox.IsDisposed || rtbOutput.IsDisposed) return;
                groupBox.BeginInvoke((MethodInvoker)(() =>
                {
                    if (groupBox.IsDisposed || searchPanel.IsDisposed) return;
                    searchPanel.Visible = true; searchIndex = -1;
                    if (txtSearch.Text != term) txtSearch.Text = term; else DoSearch(true);
                    txtSearch.Focus(); txtSearch.SelectAll();
                }));
            };

            Panel topSpacer = new Panel { Dock = DockStyle.Top, Height = 5, BackColor = Color.Transparent };
            Panel bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 33, BackColor = Color.FromArgb(55, 55, 58), Padding = new Padding(2, 5, 2, 5) };
            string sessionFileName = $"{portName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";

            Button btnCopy = MakeButton("📋 Kopyala", 80);
            btnCopy.Click += (s, e) => { if (!string.IsNullOrEmpty(rtbOutput.Text)) Clipboard.SetText(rtbOutput.Text); };
            Button btnSaveAs = MakeButton("💾 Kaydet", 70);
            btnSaveAs.Click += (s, e) => { using SaveFileDialog sfd = new SaveFileDialog { Title = Lang.Get($"{portName} - Farklı Kaydet", $"{portName} - Save As"), Filter = Lang.Get("Metin Dosyası (*.txt)|*.txt|Tüm Dosyalar (*.*)|*.*", "Text File (*.txt)|*.txt|All Files (*.*)|*.*"), FileName = $"{portName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}", InitialDirectory = _logFolderPath }; if (sfd.ShowDialog() == DialogResult.OK) { try { File.WriteAllText(sfd.FileName, rtbOutput.Text); MessageBox.Show(Lang.Get("Dosya kaydedildi.", "File saved."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception ex) { MessageBox.Show(Lang.Get($"Kayıt hatası: {ex.Message}", $"Save error: {ex.Message}"), Lang.Get("Hata", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error); } } };
            Button btnClear = MakeButton("🗑 Temizle", 70);
            btnClear.Click += (s, e) => { rtbOutput.Clear(); _atLineStart[portName] = true; if (_frozenBuffer.ContainsKey(portName)) _frozenBuffer[portName].Clear(); };
            Button btnFreeze = MakeButton("⏸ Dondur", 70);
            btnFreeze.Click += (s, e) =>
            {
                _scrollFrozen[portName] = !_scrollFrozen[portName];
                if (_scrollFrozen[portName]) { btnFreeze.Text = Lang.Get("▶ Devam", "▶ Resume"); btnFreeze.BackColor = Color.FromArgb(180, 120, 0); }
                else { btnFreeze.Text = Lang.Get("⏸ Dondur", "⏸ Freeze"); btnFreeze.BackColor = Color.FromArgb(75, 75, 78); FlushBuffer(); }
            };
            void FlushBuffer()
            {
                if (!_frozenBuffer.ContainsKey(portName)) return;
                string buffered = _frozenBuffer[portName].ToString(); _frozenBuffer[portName].Clear();
                if (buffered.Length > 0) AppendColoredText(rtbOutput, buffered, portName, boldFont, regularFont, tsColor);
                rtbOutput.SelectionStart = rtbOutput.TextLength; rtbOutput.ScrollToCaret();
            }

            // FIX #2: Durdur / Devam Et butonu
            Button btnPause = MakeButton(Lang.Get("⏹ Durdur", "⏹ Pause"), 80);
            btnPause.BackColor = Color.FromArgb(120, 50, 0);
            btnPause.FlatAppearance.MouseOverBackColor = Color.FromArgb(150, 70, 10);
            btnPause.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 30, 0);
            _toolTip.SetToolTip(btnPause, Lang.Get("COM verisi al/durdur — mevcut veri korunur", "Stop/Resume COM data — existing data preserved"));

            btnPause.Click += (s, e) =>
            {
                bool isPaused = _portPaused.GetValueOrDefault(portName, false);
                if (!isPaused)
                {
                    // DURDUR: Port bağlantısını kapat ama veriyi silme
                    _portPaused[portName] = true;
                    if (_portMap.TryGetValue(groupBox, out SerialPort? spToClose) && spToClose != null)
                    {
                        try { if (spToClose.IsOpen) spToClose.Close(); } catch { }
                    }
                    btnPause.Text = Lang.Get("▶ Devam Et", "▶ Resume");
                    btnPause.BackColor = Color.FromArgb(0, 100, 60);
                    btnPause.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 130, 80);
                    btnPause.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 70, 40);
                    groupBox.Text = $"{portName} - {Lang.Get("DURDURULDU", "PAUSED")}";
                    groupBox.ForeColor = Color.FromArgb(220, 180, 50);
                    _toolTip.SetToolTip(btnPause, Lang.Get("Devam Et — aynı porta yeniden bağlan", "Resume — reconnect to same port"));
                }
                else
                {
                    // DEVAM ET: Aynı porta yeniden bağlan
                    if (_portMap.TryGetValue(groupBox, out SerialPort? spOld) && spOld != null)
                    {
                        string pName = spOld.PortName;
                        int baud = spOld.BaudRate;
                        try
                        {
                            if (!spOld.IsOpen)
                            {
                                spOld.Open();
                                spOld.DiscardInBuffer();
                            }
                            _portPaused[portName] = false;
                            btnPause.Text = Lang.Get("⏹ Durdur", "⏹ Pause");
                            btnPause.BackColor = Color.FromArgb(120, 50, 0);
                            btnPause.FlatAppearance.MouseOverBackColor = Color.FromArgb(150, 70, 10);
                            btnPause.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 30, 0);
                            groupBox.Text = $"{pName} - {Lang.Get("AÇIK", "ONLINE")} ({baud})";
                            groupBox.ForeColor = Color.White;
                            _toolTip.SetToolTip(btnPause, Lang.Get("COM verisi al/durdur — mevcut veri korunur", "Stop/Resume COM data — existing data preserved"));
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                Lang.Get($"{pName} portuna yeniden bağlanılamadı:\n{ex.Message}", $"Could not reconnect to {pName}:\n{ex.Message}"),
                                Lang.Get("Hata", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            };

            Button btnLog = MakeButton("🔴 Log Kapalı", 95);
            btnLog.Click += (s, e) => { _loggingEnabled[portName] = !_loggingEnabled[portName]; if (_loggingEnabled[portName]) { btnLog.Text = Lang.Get("🟢 Log Aktif", "🟢 Log On"); btnLog.BackColor = Color.FromArgb(30, 130, 30); } else { btnLog.Text = Lang.Get("🔴 Log Kapalı", "🔴 Log Off"); btnLog.BackColor = Color.FromArgb(75, 75, 78); } };
            rtbOutput.ScrolledToBottom += (s, e) => { if (!_scrollFrozen.GetValueOrDefault(portName)) return; _scrollFrozen[portName] = false; btnFreeze.Text = Lang.Get("⏸ Dondur", "⏸ Freeze"); btnFreeze.BackColor = Color.FromArgb(70, 70, 30); FlushBuffer(); };

            ComboBox cboBaud = new ComboBox { Dock = DockStyle.Left, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White, Font = new Font("Segoe UI", 8f), FlatStyle = FlatStyle.Flat };
            foreach (int b in new[] { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }) cboBaud.Items.Add(b);
            cboBaud.SelectedItem = 115200;
            Label lblBaud = new Label { Text = "Baud:", Dock = DockStyle.Left, Width = 38, ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleRight, Height = 22 };
            cboBaud.SelectedIndexChanged += (s, e) =>
            {
                if (!_portMap.ContainsKey(groupBox)) return;
                SerialPort old = _portMap[groupBox]; string pName = old.PortName; int newBaud = (int)(cboBaud.SelectedItem ?? 115200);
                if (old.IsOpen) try { old.Close(); } catch { }
                old.Dispose(); _portMap.Remove(groupBox);
                try { SerialPort np = new SerialPort(pName, newBaud, Parity.None, 8, StopBits.One); np.DataReceived += (snd, ev) => { SerialPort sp2 = (SerialPort)snd; try { if (_portPaused.GetValueOrDefault(portName)) return; if (!sp2.IsOpen) return; string data = sp2.ReadExisting(); if (!string.IsNullOrEmpty(data)) lock (_receiveBuffer) _receiveBuffer[portName].Append(data); } catch { } }; np.Open(); _portMap[groupBox] = np; groupBox.BeginInvoke((MethodInvoker)delegate { groupBox.Text = $"{pName} - {Lang.Get("AÇIK", "ONLINE")} ({newBaud})"; rtbOutput.Clear(); _atLineStart[portName] = true; if (_frozenBuffer.ContainsKey(portName)) _frozenBuffer[portName].Clear(); }); }
                catch (Exception ex) { rtbOutput.BeginInvoke((MethodInvoker)delegate { rtbOutput.AppendText($"\n[HATA/ERR] : {ex.Message}\n"); groupBox.Text = $"{pName} - ERR"; }); }
            };

            Panel sendPanel = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(25, 25, 45), Padding = new Padding(4), Visible = false };
            TextBox txtCommand = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 40), ForeColor = Color.White, Font = new Font("Consolas", 10), BorderStyle = BorderStyle.FixedSingle };
            Button btnSend = new Button { Text = "Gönder", Dock = DockStyle.Right, Width = 70, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            btnSend.FlatAppearance.BorderSize = 0;
            Panel cmdSpacer = new Panel { Dock = DockStyle.Right, Width = 5, BackColor = Color.Transparent };
            sendPanel.Controls.Add(txtCommand); sendPanel.Controls.Add(cmdSpacer); sendPanel.Controls.Add(btnSend);

            List<string> cmdHistory = new List<string>(); int historyIdx = 0;
            Action sendAction = () =>
            {
                string text = txtCommand.Text; if (string.IsNullOrEmpty(text)) return;
                if (_portPaused.GetValueOrDefault(portName)) { MessageBox.Show(Lang.Get("Port durdurulmuş durumda. Önce 'Devam Et' butonuna basın.", "Port is paused. Press 'Resume' first."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (_portMap.TryGetValue(groupBox, out SerialPort? sp) && sp != null && sp.IsOpen)
                {
                    try { sp.Write(text + "\r\n"); rtbOutput.BeginInvoke((MethodInvoker)(() => { int sStart = rtbOutput.SelectionStart, sLen = rtbOutput.SelectionLength; bool hasSel = sLen > 0; if (hasSel) SendMessage(rtbOutput.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); rtbOutput.SelectionStart = rtbOutput.TextLength; rtbOutput.SelectionLength = 0; rtbOutput.SelectionColor = Color.Orange; rtbOutput.SelectionFont = boldFont; string ts2 = DateTime.Now.ToString("HH:mm:ss.fff"); rtbOutput.AppendText($"\n[{ts2}] [TX] {text}\n"); _atLineStart[portName] = true; if (hasSel) { rtbOutput.SelectionStart = sStart; rtbOutput.SelectionLength = sLen; SendMessage(rtbOutput.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); rtbOutput.Invalidate(); } else rtbOutput.ScrollToCaret(); })); if (cmdHistory.Count == 0 || cmdHistory.Last() != text) cmdHistory.Add(text); historyIdx = cmdHistory.Count; txtCommand.Clear(); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            };
            btnSend.Click += (s, e) => sendAction();
            txtCommand.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { sendAction(); e.Handled = true; e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Up) { if (cmdHistory.Count > 0 && historyIdx > 0) { historyIdx--; txtCommand.Text = cmdHistory[historyIdx]; txtCommand.SelectionStart = txtCommand.Text.Length; } e.Handled = true; } else if (e.KeyCode == Keys.Down) { if (cmdHistory.Count > 0 && historyIdx < cmdHistory.Count - 1) { historyIdx++; txtCommand.Text = cmdHistory[historyIdx]; txtCommand.SelectionStart = txtCommand.Text.Length; } else { historyIdx = cmdHistory.Count; txtCommand.Text = ""; } e.Handled = true; } };

            Button btnToggleSend = MakeButton("⌨ Komut", 75);
            btnToggleSend.Click += (s, e) => { sendPanel.Visible = !sendPanel.Visible; if (sendPanel.Visible) { btnToggleSend.BackColor = Color.FromArgb(60, 60, 100); txtCommand.Focus(); } else btnToggleSend.BackColor = Color.FromArgb(75, 75, 78); };

            // FIX #2: Durdur butonu bottomPanel'a eklendi
            bottomPanel.Controls.AddRange(new Control[] { btnCopy, btnSaveAs, btnClear, btnToggleSend, btnFreeze, btnPause, btnLog, cboBaud, lblBaud });

            Button btnSmartSave = MakeButton("💾 Kaydet", 80);
            btnSmartSave.BackColor = Color.FromArgb(0, 100, 60); btnSmartSave.Visible = false;

            Panel settingsRow1 = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(25, 25, 45), Padding = new Padding(4, 3, 4, 3) };
            Label lblFont = new Label { Text = "Yazı:", ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 32, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 22 };
            ComboBox cboFont = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 8f), FlatStyle = FlatStyle.Flat, Dock = DockStyle.Left, Height = 22 };
            foreach (var f in new[] { "Consolas", "Courier New", "Lucida Console", "Segoe UI", "Segoe UI Emoji", "Arial", "Verdana" }) cboFont.Items.Add(f);
            cboFont.SelectedItem = IsFontAvailable("Segoe UI Emoji") ? "Segoe UI Emoji" : "Consolas";
            Label lblSize = new Label { Text = "Boyut:", ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 40, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 22 };
            ComboBox cboSize = new ComboBox { Width = 45, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 40, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 8f), FlatStyle = FlatStyle.Flat, Dock = DockStyle.Left, Height = 22 };
            foreach (var sz in new[] { 7, 8, 9, 10, 11, 12, 14, 16, 18, 20 }) cboSize.Items.Add(sz);
            cboSize.SelectedItem = 10;

            void CheckProfileState() { if (groupBox.IsDisposed) return; string currentData = SerializeProfile(rtbOutput, cboFont, cboSize, tsColor); var profiles = LoadProfiles(); btnSmartSave.Visible = !profiles.Values.Contains(currentData); }
            void ApplyFont() { string fontName = cboFont.SelectedItem?.ToString() ?? "Consolas"; int fontSize = cboSize.SelectedItem is int sz ? sz : 10; regularFont = new Font(fontName, fontSize, FontStyle.Regular); boldFont = new Font(fontName, fontSize, FontStyle.Bold); rtbOutput.Font = regularFont; CheckProfileState(); }
            cboFont.SelectedIndexChanged += (s, e) => ApplyFont(); cboSize.SelectedIndexChanged += (s, e) => ApplyFont();

            Button btnTextColor2 = new Button { Width = 20, Height = 20, BackColor = rtbOutput.ForeColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left }; btnTextColor2.FlatAppearance.BorderColor = Color.Gray;
            btnTextColor2.Click += (s, e) => { using var cd = new ColorDialog { Color = rtbOutput.ForeColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; rtbOutput.ForeColor = cd.Color; btnTextColor2.BackColor = cd.Color; CheckProfileState(); };
            Label lblTc = new Label { Text = "Yazı:", ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 32, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };
            Button btnBgColor2 = new Button { Width = 20, Height = 20, BackColor = rtbOutput.BackColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left }; btnBgColor2.FlatAppearance.BorderColor = Color.Gray;
            btnBgColor2.Click += (s, e) => { using var cd = new ColorDialog { Color = rtbOutput.BackColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; rtbOutput.BackColor = cd.Color; btnBgColor2.BackColor = cd.Color; UpdateRTBBackgroundColor(rtbOutput, cd.Color); CheckProfileState(); };
            Label lblBg = new Label { Text = "Zemin:", ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 45, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };
            Button btnTsColor = new Button { Width = 20, Height = 20, BackColor = tsColor, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Dock = DockStyle.Left }; btnTsColor.FlatAppearance.BorderColor = Color.Gray;
            btnTsColor.Click += (s, e) => { using var cd = new ColorDialog { Color = tsColor, FullOpen = true }; if (cd.ShowDialog() != DialogResult.OK) return; tsColor = cd.Color; btnTsColor.BackColor = cd.Color; UpdateTimestampColors(rtbOutput, tsColor); CheckProfileState(); };
            Label lblTs = new Label { Text = "Tarih:", ForeColor = Color.Silver, Font = new Font("Segoe UI", 8f), Width = 36, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleRight, Height = 20 };
            Panel spacer = new Panel { Dock = DockStyle.Left, Width = 15 };

            Button btnDefault = new Button { Dock = DockStyle.Left, Width = 100, Height = 22, FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.LimeGreen, Cursor = Cursors.Hand };
            btnDefault.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
            btnDefault.Paint += (s, pe) => { pe.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; string t1 = "[D] "; string t2 = Lang.Get("Varsayılan", "Default"); using Font f = new Font("Consolas", 9f, FontStyle.Regular); SizeF s1 = pe.Graphics.MeasureString(t1, f), s2 = pe.Graphics.MeasureString(t2, f); float x = (btnDefault.Width - (s1.Width + s2.Width)) / 2; if (x < 2) x = 2; float y = (btnDefault.Height - Math.Max(s1.Height, s2.Height)) / 2; using (Brush bTs = new SolidBrush(Color.FromArgb(0, 200, 220))) using (Brush bFg = new SolidBrush(Color.LimeGreen)) { pe.Graphics.DrawString(t1, f, bTs, x, y); pe.Graphics.DrawString(t2, f, bFg, x + s1.Width - 4, y); } };
            btnDefault.Click += (s, e) => { rtbOutput.BackColor = Color.Black; rtbOutput.ForeColor = Color.LimeGreen; tsColor = Color.FromArgb(0, 200, 220); btnTextColor2.BackColor = Color.LimeGreen; btnBgColor2.BackColor = Color.Black; btnTsColor.BackColor = tsColor; cboFont.SelectedItem = "Consolas"; cboSize.SelectedItem = 10; ApplyFont(); UpdateRTBBackgroundColor(rtbOutput, Color.Black); UpdateTimestampColors(rtbOutput, tsColor); CheckProfileState(); };

            settingsRow1.Controls.AddRange(new Control[] { cboFont, lblFont, cboSize, lblSize, btnTextColor2, lblTc, btnBgColor2, lblBg, btnTsColor, lblTs, spacer, btnDefault, btnSmartSave });

            btnSmartSave.Click += (s, e) =>
            {
                using Form prompt = new Form { Text = Lang.Get("Profil Kaydet", "Save Profile"), Size = new Size(300, 150), FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent, BackColor = Color.FromArgb(40, 40, 40) };
                Label lbl = new Label { Text = Lang.Get("Yeni Profil Adı:", "New Profile Name:"), ForeColor = Color.White, Location = new Point(10, 15), AutoSize = true, Font = new Font("Segoe UI", 9f) };
                TextBox txt = new TextBox { Location = new Point(10, 35), Width = 260, BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f) };
                Button btnOk = new Button { Text = Lang.Get("Kaydet", "Save"), Location = new Point(110, 75), Width = 75, BackColor = Color.FromArgb(0, 100, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                Button btnCancel = new Button { Text = Lang.Get("İptal", "Cancel"), Location = new Point(195, 75), Width = 75, BackColor = Color.FromArgb(100, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                btnOk.FlatAppearance.BorderSize = 0; btnCancel.FlatAppearance.BorderSize = 0;
                prompt.Controls.Add(lbl); prompt.Controls.Add(txt); prompt.Controls.Add(btnOk); prompt.Controls.Add(btnCancel);
                btnOk.Click += (sender, args) => prompt.DialogResult = DialogResult.OK;
                btnCancel.Click += (sender, args) => prompt.DialogResult = DialogResult.Cancel;
                if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text)) { var profiles = LoadProfiles(); string name = txt.Text.Trim(); profiles[name] = SerializeProfile(rtbOutput, cboFont, cboSize, tsColor); SaveProfiles(profiles); GlobalProfileListChanged?.Invoke(); }
            };

            Panel settingsRow2 = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(20, 20, 38), Padding = new Padding(4) };
            FlowLayoutPanel flpProfiles = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(20, 20, 38) };
            settingsRow2.Controls.Add(flpProfiles); flpProfiles.BringToFront();

            void UpdateProfileButtons()
            {
                if (groupBox.IsDisposed) return;
                var profiles = LoadProfiles();
                var toRemove = flpProfiles.Controls.Cast<Control>().Where(c => c.Tag?.ToString() == "profile").ToList();
                foreach (var c in toRemove) { flpProfiles.Controls.Remove(c); c.Dispose(); }
                int maxBtnHeight = 24;
                foreach (var kv in profiles) { try { string[] p = kv.Value.Split('|'); int fs = int.Parse(p[4]); int h = Math.Max(24, fs * 2 + 6); if (h > maxBtnHeight) maxBtnHeight = h; } catch { } }
                foreach (var kv in profiles)
                {
                    string profileName = kv.Key; string data = kv.Value;
                    Color fg = Color.White, bg = Color.FromArgb(0, 80, 140), ts2 = Color.Cyan; string fn = "Segoe UI"; int fs2 = 8;
                    try { string[] p = data.Split('|'); fg = Color.FromArgb(int.Parse(p[0])); bg = Color.FromArgb(int.Parse(p[1])); ts2 = Color.FromArgb(int.Parse(p[2])); fn = p[3]; fs2 = int.Parse(p[4]); } catch { }
                    Button btnProfile = new Button { Text = string.Empty, Width = 140, Height = maxBtnHeight, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = fg, Font = new Font(fn, fs2, FontStyle.Regular), Cursor = Cursors.Hand, Tag = "profile" };
                    btnProfile.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
                    btnProfile.Paint += (sender, pe) => { pe.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; if (btnProfile.BackColor == Color.FromArgb(180, 40, 40)) { using (Brush b = new SolidBrush(Color.White)) { string delTxt = Lang.Get("Siliniyor...", "Deleting..."); SizeF sDel = pe.Graphics.MeasureString(delTxt, btnProfile.Font); pe.Graphics.DrawString(delTxt, btnProfile.Font, b, (btnProfile.Width - sDel.Width) / 2, (btnProfile.Height - sDel.Height) / 2); } return; } string t1 = "[D] "; string t2 = profileName; SizeF s1 = pe.Graphics.MeasureString(t1, btnProfile.Font), s2 = pe.Graphics.MeasureString(t2, btnProfile.Font); float x = (btnProfile.Width - (s1.Width + s2.Width)) / 2; if (x < 2) x = 2; float y = (btnProfile.Height - Math.Max(s1.Height, s2.Height)) / 2; using (Brush bTs = new SolidBrush(ts2)) using (Brush bFg = new SolidBrush(fg)) { pe.Graphics.DrawString(t1, btnProfile.Font, bTs, x, y); pe.Graphics.DrawString(t2, btnProfile.Font, bFg, x + s1.Width - 4, y); } };
                    System.Windows.Forms.Timer longPressTimer = new System.Windows.Forms.Timer { Interval = 800 };
                    bool isLongPress = false;
                    longPressTimer.Tick += (s, e) => { longPressTimer.Stop(); isLongPress = true; btnProfile.BackColor = Color.FromArgb(180, 40, 40); btnProfile.Invalidate(); btnProfile.Refresh(); var updated = LoadProfiles(); if (updated.Remove(profileName)) { SaveProfiles(updated); GlobalProfileListChanged?.Invoke(); } };
                    btnProfile.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { isLongPress = false; longPressTimer.Start(); } };
                    btnProfile.MouseUp += (s, e) => longPressTimer.Stop(); btnProfile.MouseLeave += (s, e) => longPressTimer.Stop(); btnProfile.Disposed += (s, e) => longPressTimer.Dispose();
                    btnProfile.Click += (s, e) => { if (isLongPress) return; tsColor = ApplyProfile(data, rtbOutput, cboFont, cboSize, btnTextColor2, btnBgColor2, btnTsColor, ref regularFont, ref boldFont); UpdateRTBBackgroundColor(rtbOutput, rtbOutput.BackColor); UpdateTimestampColors(rtbOutput, tsColor); CheckProfileState(); };
                    flpProfiles.Controls.Add(btnProfile);
                }
                CheckProfileState();
            }
            GlobalProfileListChanged += UpdateProfileButtons;
            UpdateProfileButtons();

            Panel settingsPanel = new Panel { Dock = DockStyle.Bottom, Height = 76, Visible = false, BackColor = Color.FromArgb(25, 25, 45) };
            settingsPanel.Controls.Add(settingsRow2); settingsPanel.Controls.Add(settingsRow1);

            Button btnSettings = MakeButton("⚙ Görünüm", 85);
            btnSettings.Click += (s, e) => { settingsPanel.Visible = !settingsPanel.Visible; if (settingsPanel.Visible) btnSettings.BackColor = Color.FromArgb(60, 60, 100); else btnSettings.BackColor = Color.FromArgb(75, 75, 78); };
            bottomPanel.Controls.Add(btnSettings);

            (Color Fore, Color Back) pendingHlColor = GenerateRandomHighlightColor();
            bool pendingIsRemove = false;
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            ToolStripMenuItem menuCopySelected = new ToolStripMenuItem("📋  Kopyala") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.Black };
            menuCopySelected.Click += (s, e) => { if (rtbOutput.SelectionLength > 0) Clipboard.SetText(rtbOutput.SelectedText); else if (!string.IsNullOrEmpty(rtbOutput.Text)) Clipboard.SetText(rtbOutput.Text); };
            ToolStripMenuItem menuSearchThis = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.Black };
            menuSearchThis.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; searchPanel.Visible = true; searchIndex = -1; if (txtSearch.Text != term) txtSearch.Text = term; else DoSearch(true); txtSearch.Focus(); txtSearch.SelectAll(); };
            ToolStripMenuItem menuSearchAll = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.Black };
            menuSearchAll.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; foreach (var action in _windowSearchActions.Values.ToList()) action(term); };
            ToolStripMenuItem menuHighlight = new ToolStripMenuItem { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.Black, ImageScaling = ToolStripItemImageScaling.None };
            menuHighlight.Click += (s, e) => { string term = rtbOutput.SelectedText.Trim(); if (string.IsNullOrEmpty(term)) return; if (pendingIsRemove) { _highlightRules.Remove(term); SaveHighlights(); BuildHighlightRegex(); RemoveWordHighlightFromAllWindows(term); } else { _highlightRules[term] = pendingHlColor; SaveHighlights(); BuildHighlightRegex(); ApplyHighlightsToAllWindows(); } };
            contextMenu.Opening += (s, e) =>
            {
                bool hasSelection = rtbOutput.SelectionLength > 0; menuSearchThis.Enabled = hasSelection; menuSearchAll.Enabled = hasSelection; menuHighlight.Enabled = hasSelection;
                if (hasSelection) { string fullTerm = rtbOutput.SelectedText.Trim(); string preview = fullTerm.Length > 22 ? fullTerm.Substring(0, 22) + "..." : fullTerm; menuSearchThis.Text = Lang.Get($"🔍 Bu Pencerede Ara: \"{preview}\"", $"🔍 Search in This Window: \"{preview}\""); menuSearchAll.Text = Lang.Get($"🔍 Tüm Pencerelerde Ara: \"{preview}\"", $"🔍 Search in All Windows: \"{preview}\""); bool alreadyHighlighted = !string.IsNullOrEmpty(fullTerm) && _highlightRules.ContainsKey(fullTerm); if (alreadyHighlighted) { pendingIsRemove = true; var ec = _highlightRules[fullTerm]; menuHighlight.Text = Lang.Get($"🗑 Vurguyu Kaldır '{preview}'", $"🗑 Remove Highlight '{preview}'"); menuHighlight.Image = GenerateColorPreviewBitmap(ec.Fore, ec.Back); } else { pendingIsRemove = false; pendingHlColor = GenerateRandomHighlightColor(); menuHighlight.Text = Lang.Get($"🖍 Vurgula '{preview}'", $"🖍 Highlight '{preview}'"); menuHighlight.Image = GenerateColorPreviewBitmap(pendingHlColor.Fore, pendingHlColor.Back); } }
                else { pendingIsRemove = false; menuSearchThis.Text = Lang.Get("🔍 Bu Pencerede Ara (metin seçin)", "🔍 Search in This Window (select text first)"); menuSearchAll.Text = Lang.Get("🔍 Tüm Pencerelerde Ara (metin seçin)", "🔍 Search in All Windows (select text first)"); menuHighlight.Text = Lang.Get("🖍 Vurgula (metin seçin)", "🖍 Highlight (select text first)"); menuHighlight.Image = null; }
            };
            ToolStripMenuItem menuCloseIgnored = new ToolStripMenuItem();
            ToolStripMenuItem menuLogDir = new ToolStripMenuItem();
            contextMenu.Items.Add(menuCopySelected); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuHighlight); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuSearchThis); contextMenu.Items.Add(menuSearchAll); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuCloseIgnored); contextMenu.Items.Add(new ToolStripSeparator()); contextMenu.Items.Add(menuLogDir);
            menuCloseIgnored.Click += (s, e) => CloseSpecificPort(groupBox, true, true);
            menuLogDir.Click += (s, e) => System.Diagnostics.Process.Start("explorer.exe", _logFolderPath);
            groupBox.ContextMenuStrip = contextMenu; rtbOutput.ContextMenuStrip = contextMenu;
            groupBox.DoubleClick += (s, e) => ToggleZoom(groupBox);
            rtbOutput.CustomDoubleClick += (s, e) => ToggleZoom(groupBox);

            // Başlık butonları
            Button btnClose = new Button { Text = "✕", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(160, 60, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => CloseSpecificPort(groupBox, true, true);

            Button btnZoom = new Button { Text = "◻", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 100, 160), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false, Tag = "btnZoom" };
            btnZoom.FlatAppearance.BorderSize = 0;
            btnZoom.Click += (s, e) => ToggleZoom(groupBox);

            // FIX #1: COM penceresi için küçültme butonu — turuncu (farklı)
            Button btnMinimize = new Button
            {
                Text = "─",
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(160, 120, 20),   // turuncu — log (teal) rengiyle farklı
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnMinimize.FlatAppearance.BorderSize = 0;
            btnMinimize.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 150, 30);
            btnMinimize.FlatAppearance.MouseDownBackColor = Color.FromArgb(130, 90, 10);
            btnMinimize.Click += (s, e) =>
            {
                // Detach modundaysa host form'u Windows seviyesinde minimize et
                Form? hostForm = _detachedForms.FirstOrDefault(f => f.Controls.Contains(groupBox));
                if (hostForm != null)
                {
                    hostForm.WindowState = FormWindowState.Minimized;
                    return;
                }
                // Ana ekrandaysa minibar'a taşı
                MinimizePort(portName, groupBox);
            };
            _toolTip.SetToolTip(btnMinimize, Lang.Get("Pencereyi küçült (status bara taşı)", "Minimize window to status bar"));

            Button btnDetach = new Button { Text = "⧉", Size = new Size(24, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 80, 50), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = false, Tag = "btnDetach" };
            btnDetach.FlatAppearance.BorderSize = 0;
            btnDetach.Click += (s, e) => ToggleDetach(groupBox, $"{portName} - {Lang.Get("AÇIK", "ONLINE")}");

            groupBox.SizeChanged += (s, e) =>
            {
                btnClose.Location = new Point(groupBox.Width - 28, 1);
                btnZoom.Location = new Point(groupBox.Width - 54, 1);
                btnMinimize.Location = new Point(groupBox.Width - 80, 1);
                btnDetach.Location = new Point(groupBox.Width - 106, 1);
            };
            btnClose.Location = new Point(groupBox.Width - 28, 1);
            btnZoom.Location = new Point(groupBox.Width - 54, 1);
            btnMinimize.Location = new Point(groupBox.Width - 80, 1);
            btnDetach.Location = new Point(groupBox.Width - 106, 1);

            groupBox.Controls.Add(customScrollTrack);
            groupBox.Controls.Add(rtbWrapper);
            groupBox.Controls.Add(searchPanel);
            groupBox.Controls.Add(topSpacer);
            groupBox.Controls.Add(sendPanel);
            groupBox.Controls.Add(settingsPanel);
            groupBox.Controls.Add(bottomPanel);

            sendPanel.BringToFront(); settingsPanel.BringToFront(); bottomPanel.BringToFront();

            groupBox.Controls.Add(btnDetach);
            groupBox.Controls.Add(btnMinimize);
            groupBox.Controls.Add(btnZoom);
            groupBox.Controls.Add(btnClose);

            customScrollTrack.BringToFront();
            btnClose.BringToFront(); btnZoom.BringToFront(); btnMinimize.BringToFront(); btnDetach.BringToFront();

            port.DataReceived += (sender, e) =>
            {
                SerialPort sp = (SerialPort)sender;
                try
                {
                    // FIX #2: Port durdurulduysa veri alma
                    if (_portPaused.GetValueOrDefault(portName)) return;
                    if (!sp.IsOpen) return;
                    string data = sp.ReadExisting();
                    if (!string.IsNullOrEmpty(data)) lock (_receiveBuffer) _receiveBuffer[portName].Append(data);
                }
                catch { }
            };

            flushTmr.Tick += (s, e) =>
            {
                string chunk;
                lock (_receiveBuffer)
                {
                    if (!_receiveBuffer.ContainsKey(portName) || _receiveBuffer[portName].Length == 0) return;
                    chunk = _receiveBuffer[portName].ToString();
                    _receiveBuffer[portName].Clear();
                }
                if (rtbOutput.IsDisposed) return;
                if (chunk.Length > 0 && _minimizedBoxes.ContainsKey(portName)) AnimateMinimizedButton(portName);

                var (lastChunk, repeatCount, lastTime) = _repeatCheck.GetValueOrDefault(portName, (string.Empty, 0, DateTime.Now));
                if (chunk == lastChunk && chunk.Length > 0)
                {
                    if ((DateTime.Now - lastTime).TotalSeconds <= 1.0)
                    {
                        repeatCount++;
                        if (repeatCount >= 10)
                        {
                            if (_portMap.TryGetValue(groupBox, out SerialPort? sp) && sp != null && sp.IsOpen)
                            {
                                try { sp.Close(); System.Threading.Thread.Sleep(200); sp.Open(); sp.DiscardInBuffer(); sp.DiscardOutBuffer(); string errTxt = Lang.Get("\n[UYARI] Port kilitlenmesi önlendi.\n", "\n[WARN] Port freeze prevented.\n"); rtbOutput.BeginInvoke((MethodInvoker)(() => { if (!rtbOutput.IsDisposed) AppendColoredText(rtbOutput, errTxt, portName, boldFont, regularFont, tsColor); })); } catch { }
                            }
                            repeatCount = 0; lastTime = DateTime.Now;
                        }
                    }
                    else { repeatCount = 1; lastTime = DateTime.Now; }
                }
                else { repeatCount = 0; lastTime = DateTime.Now; }
                _repeatCheck[portName] = (chunk, repeatCount, lastTime);

                if (_loggingEnabled.GetValueOrDefault(portName))
                {
                    try
                    {
                        string logData = SanitizeData(chunk), fullPath = Path.Combine(_logFolderPath, sessionFileName);
                        Directory.CreateDirectory(_logFolderPath);
                        string cleaned = logData.Replace("\r", ""); string[] parts = cleaned.Split('\n');
                        StringBuilder logSb = new StringBuilder();
                        for (int i = 0; i < parts.Length; i++) { string part = parts[i]; if (part.Length > 0) { if (_logAtLineStart.GetValueOrDefault(portName, true)) { logSb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] "); _logAtLineStart[portName] = false; } logSb.Append(part); } if (i < parts.Length - 1) { logSb.Append(Environment.NewLine); _logAtLineStart[portName] = true; } }
                        if (logSb.Length > 0) File.AppendAllText(fullPath, logSb.ToString(), Encoding.UTF8);
                    }
                    catch { }
                }

                if (_scrollFrozen.GetValueOrDefault(portName) || _minimizedBoxes.ContainsKey(portName))
                {
                    if (_frozenBuffer.ContainsKey(portName)) _frozenBuffer[portName].Append(chunk);
                }
                else
                {
                    if (rtbOutput.IsDisposed) return;
                    int sStart = rtbOutput.SelectionStart, sLen = rtbOutput.SelectionLength;
                    bool hasSelection = sLen > 0;
                    if (hasSelection) SendMessage(rtbOutput.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
                    AppendColoredText(rtbOutput, chunk, portName, boldFont, regularFont, tsColor);
                    if (hasSelection) { rtbOutput.SelectionStart = sStart; rtbOutput.SelectionLength = sLen; SendMessage(rtbOutput.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); rtbOutput.Invalidate(); }
                    else rtbOutput.ScrollToCaret();
                }
            };

            _portMap.Add(groupBox, port);
            flushTmr.Start();
            _mainLayout.Controls.Add(groupBox);

            Action updateTexts = () =>
            {
                if (groupBox.IsDisposed) return;
                string currentBaud = cboBaud.SelectedItem?.ToString() ?? "115200";
                bool isPortOpen = _portMap.ContainsKey(groupBox) && _portMap[groupBox].IsOpen;
                bool isPaused = _portPaused.GetValueOrDefault(portName);
                if (isPaused) groupBox.Text = $"{portName} - {Lang.Get("DURDURULDU", "PAUSED")}";
                else groupBox.Text = $"{portName} - " + (isPortOpen ? $"{Lang.Get("AÇIK", "ONLINE")} ({currentBaud})" : Lang.Get("HATA", "ERR"));

                btnCopy.Text = Lang.Get("📋 Kopyala", "📋 Copy");
                btnSaveAs.Text = Lang.Get("💾 Kaydet", "💾 Save");
                btnClear.Text = Lang.Get("🗑 Temizle", "🗑 Clear");
                btnToggleSend.Text = Lang.Get("⌨ Komut", "⌨ Cmd");
                btnSend.Text = Lang.Get("Gönder", "Send");
                if (_scrollFrozen.GetValueOrDefault(portName)) btnFreeze.Text = Lang.Get("▶ Devam", "▶ Resume"); else btnFreeze.Text = Lang.Get("⏸ Dondur", "⏸ Freeze");
                if (isPaused) btnPause.Text = Lang.Get("▶ Devam Et", "▶ Resume"); else btnPause.Text = Lang.Get("⏹ Durdur", "⏹ Pause");
                if (_loggingEnabled.GetValueOrDefault(portName)) btnLog.Text = Lang.Get("🟢 Log Aktif", "🟢 Log On"); else btnLog.Text = Lang.Get("🔴 Log Kapalı", "🔴 Log Off");
                btnSettings.Text = Lang.Get("⚙ Görünüm", "⚙ Settings");
                lblBaud.Text = Lang.Get("Hız:", "Baud:");
                menuCopySelected.Text = Lang.Get("📋  Kopyala", "📋  Copy");
                menuCloseIgnored.Text = Lang.Get("Bu Ekranı Kapat (Yoksay)", "Close Window (Ignore)");
                menuLogDir.Text = Lang.Get("Log Klasörünü Aç", "Open Log Folder");
                bool isDetachedNow = _detachedForms.Any(f => f.Controls.Contains(groupBox));
                if (!isDetachedNow) { btnDetach.Text = "⧉"; }
            };
            updateTexts();
            Lang.Changed += updateTexts;
            groupBox.Disposed += (s, e) =>
            {
                Lang.Changed -= updateTexts;
                GlobalProfileListChanged -= UpdateProfileButtons;
                _portPaused.Remove(portName);
            };
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  YARDIMCI: Font var mı kontrol et
        // ════════════════════════════════════════════════════════════════
        private static bool IsFontAvailable(string fontName)
        {
            try
            {
                using var f = new Font(fontName, 10);
                return f.Name.Equals(fontName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void UpdateRTBBackgroundColor(RichTextBox rtb, Color newBack)
        {
            if (rtb == null || rtb.IsDisposed || rtb.TextLength == 0) return;
            SendMessage(rtb.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            int savedStart = rtb.SelectionStart, savedLen = rtb.SelectionLength;
            try
            {
                rtb.SelectionStart = 0; rtb.SelectionLength = rtb.TextLength; rtb.SelectionBackColor = newBack;
                if (_highlightRegex != null && _highlightRules.Count > 0) { var matches = _highlightRegex.Matches(rtb.Text); foreach (Match m in matches) { rtb.SelectionStart = m.Index; rtb.SelectionLength = m.Length; if (_highlightRules.TryGetValue(m.Value, out var colors)) { rtb.SelectionColor = colors.Fore; rtb.SelectionBackColor = colors.Back; rtb.SelectionFont = new Font(rtb.Font, FontStyle.Bold); } } }
            }
            catch { }
            finally { rtb.SelectionStart = savedStart; rtb.SelectionLength = savedLen; SendMessage(rtb.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); rtb.Refresh(); }
        }

        private void UpdateTimestampColors(RichTextBox rtb, Color newColor)
        {
            if (rtb == null || rtb.IsDisposed || rtb.TextLength == 0) return;
            rtb.SuspendLayout();
            int savedStart = rtb.SelectionStart, savedLength = rtb.SelectionLength;
            string pattern = @"\[\d{2}:\d{2}:\d{2}\.\d{3}\] ";
            var matches = Regex.Matches(rtb.Text, pattern);
            foreach (Match m in matches) { rtb.SelectionStart = m.Index; rtb.SelectionLength = m.Length; rtb.SelectionColor = newColor; rtb.SelectionFont = new Font(rtb.Font, FontStyle.Bold); }
            rtb.SelectionStart = savedStart; rtb.SelectionLength = savedLength; rtb.SelectionColor = rtb.ForeColor; rtb.ResumeLayout();
        }

        private Dictionary<string, string> LoadProfiles()
        {
            try
            {
                if (!File.Exists(_profilesPath)) return new();
                string json = File.ReadAllText(_profilesPath);
                var result = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return result ?? new();
            }
            catch { return new(); }
        }

        private void SaveProfiles(Dictionary<string, string> profiles) { File.WriteAllText(_profilesPath, System.Text.Json.JsonSerializer.Serialize(profiles)); }

        private static string SerializeProfile(RichTextBox rtb, ComboBox cboFont, ComboBox cboSize, Color tsColor)
            => $"{rtb.ForeColor.ToArgb()}|{rtb.BackColor.ToArgb()}|{tsColor.ToArgb()}|{cboFont.SelectedItem}|{cboSize.SelectedItem}";

        private static Color ApplyProfile(string data, RichTextBox rtb, ComboBox cboFont, ComboBox cboSize, Button btnTc, Button btnBg, Button btnTs, ref Font regular, ref Font bold)
        {
            try
            {
                string[] p = data.Split('|');
                Color fg = Color.FromArgb(int.Parse(p[0])), bg = Color.FromArgb(int.Parse(p[1])), ts = Color.FromArgb(int.Parse(p[2]));
                string fn = p[3]; int fs = int.Parse(p[4]);
                rtb.ForeColor = fg; btnTc.BackColor = fg; rtb.BackColor = bg; btnBg.BackColor = bg; btnTs.BackColor = ts;
                if (cboFont.Items.Contains(fn)) cboFont.SelectedItem = fn;
                if (cboSize.Items.Contains(fs)) cboSize.SelectedItem = fs;
                regular = new Font(fn, fs, FontStyle.Regular); bold = new Font(fn, fs, FontStyle.Bold); rtb.Font = regular;
                return ts;
            }
            catch { return Color.FromArgb(0, 200, 220); }
        }

        private static readonly Regex _ansiRegex = new Regex(@"\x1B(\[[0-9;]*[A-Za-z]|\][^\x07]*\x07|[()][0-9A-Za-z]|[^[()#])", RegexOptions.Compiled);

        // FIX #8: Emoji ve Unicode karakterleri SanitizeData'da filtrelenmemeli
        private static string SanitizeData(string data)
        {
            data = _ansiRegex.Replace(data, "");
            var sb = new StringBuilder(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                char c = data[i];
                // Emoji için surrogate pair desteği
                if (char.IsHighSurrogate(c) && i + 1 < data.Length && char.IsLowSurrogate(data[i + 1]))
                {
                    sb.Append(c); sb.Append(data[i + 1]); i++;
                    continue;
                }
                // Yalnız kalan surrogate'ları filtrele (bozuk Unicode)
                if (char.IsSurrogate(c)) continue;
                if (c == '\n' || c == '\t' || c == '\r' || (c >= ' ' && c != '\x7F'))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private void AppendColoredText(RichTextBox rtb, string data, string portName, Font boldFont, Font regularFont, Color tsColor)
        {
            data = SanitizeData(data); if (string.IsNullOrEmpty(data)) return;
            string cleaned = data.Replace("\r", ""); string[] parts = cleaned.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                bool isLineEnd = i < parts.Length - 1; // bu parçadan sonra \n var mı

                if (part.Length > 0)
                {
                    // Satır tamamlandığında (yani \n gelince) tekrar kontrolü yap
                    if (isLineEnd && _atLineStart.GetValueOrDefault(portName, true))
                    {
                        // Tam bir satır: timestamp + içerik birlikte kontrol
                        if (ShouldSuppressLine(rtb, portName, part, boldFont, regularFont, tsColor))
                        {
                            // Satır bastırıldı — sadece satır sonunu işle
                            _atLineStart[portName] = true;
                            continue;
                        }
                    }

                    if (_atLineStart.GetValueOrDefault(portName, true))
                    {
                        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                        rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0;
                        rtb.SelectionColor = tsColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = boldFont;
                        rtb.AppendText($"[{ts}] "); _atLineStart[portName] = false;
                    }
                    if (_highlightRegex != null && _highlightRules.Count > 0)
                    {
                        var matches = _highlightRegex.Matches(part); int lastIdx = 0;
                        foreach (Match m in matches)
                        {
                            if (m.Index > lastIdx) { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = rtb.ForeColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText(part.Substring(lastIdx, m.Index - lastIdx)); }
                            rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0;
                            if (_highlightRules.TryGetValue(m.Value, out var colors)) { rtb.SelectionColor = colors.Fore; rtb.SelectionBackColor = colors.Back; } else { rtb.SelectionColor = rtb.ForeColor; rtb.SelectionBackColor = rtb.BackColor; }
                            rtb.SelectionFont = boldFont; rtb.AppendText(m.Value); lastIdx = m.Index + m.Length;
                        }
                        if (lastIdx < part.Length) { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = rtb.ForeColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText(part.Substring(lastIdx)); }
                    }
                    else { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionColor = rtb.ForeColor; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText(part); }
                }
                if (isLineEnd) { rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0; rtb.SelectionBackColor = rtb.BackColor; rtb.SelectionFont = regularFont; rtb.AppendText("\n"); _atLineStart[portName] = true; }
            }
            // Bellek taşmasını önle
            if (rtb.Text.Length > 5_000_000) { int cutAt = rtb.Text.IndexOf('\n', rtb.TextLength / 5); if (cutAt > 0) { rtb.SelectionStart = 0; rtb.SelectionLength = cutAt + 1; rtb.SelectedText = ""; } _atLineStart[portName] = true; }
        }

        // Satır bazlı tekrar bastırma: 1 sn içinde aynı satır > 5 kez gelirse ekrana yazma
        // true döndürürse satır bastırıldı demektir
        private bool ShouldSuppressLine(RichTextBox rtb, string portName, string line, Font boldFont, Font regularFont, Color tsColor)
        {
            const int Threshold = 5;

            var now = DateTime.Now;
            if (!_lineRepeat.TryGetValue(portName, out LineRepeatState state))
                state = new LineRepeatState { Line = string.Empty, Count = 0, WindowStart = now, SummaryPending = false };

            bool sameLine = state.Line == line;
            bool inWindow = (now - state.WindowStart).TotalSeconds <= 1.0;

            if (sameLine && inWindow)
            {
                state.Count++;
                if (state.Count > Threshold)
                {
                    // Bastır — özet satırını güncelle
                    state.SummaryPending = true;
                    _lineRepeat[portName] = state;
                    FlushRepeatSummary(rtb, portName, line, state.Count, boldFont, regularFont, tsColor, updateInPlace: true);
                    return true;
                }
                _lineRepeat[portName] = state;
                return false;
            }
            else
            {
                // Farklı satır veya zaman penceresi geçti — önceki özeti temizle
                if (state.SummaryPending)
                    _lineRepeat[portName] = new LineRepeatState { Line = state.Line, Count = state.Count, WindowStart = state.WindowStart, SummaryPending = false };

                // Yeni satır için sayacı sıfırla
                _lineRepeat[portName] = new LineRepeatState { Line = line, Count = 1, WindowStart = now, SummaryPending = false };
                return false;
            }
        }

        // Ekranda en son "⚠ × N kez" özet satırını güncelle (son satırı sil, yenisini yaz)
        private void FlushRepeatSummary(RichTextBox rtb, string portName, string line, int count, Font boldFont, Font regularFont, Color tsColor, bool updateInPlace)
        {
            string summary = Lang.Get($"⚠  \"{line}\"  ×{count} kez tekrar — bastırıldı", $"⚠  \"{line}\"  ×{count} repeats — suppressed");

            if (updateInPlace && rtb.TextLength > 0)
            {
                // Son satırı bul ve güncelle
                string text = rtb.Text;
                int lastNl = text.LastIndexOf('\n', Math.Max(0, text.Length - 2));
                if (lastNl >= 0 && text.Substring(lastNl + 1).Contains("⚠"))
                {
                    rtb.SelectionStart = lastNl + 1;
                    rtb.SelectionLength = rtb.TextLength - (lastNl + 1);
                    rtb.SelectionColor = Color.FromArgb(255, 180, 0);
                    rtb.SelectionBackColor = rtb.BackColor;
                    rtb.SelectionFont = boldFont;
                    rtb.SelectedText = summary;
                    return;
                }
            }

            // Yeni özet satırı ekle
            rtb.SelectionStart = rtb.TextLength; rtb.SelectionLength = 0;
            rtb.SelectionColor = Color.FromArgb(255, 180, 0);
            rtb.SelectionBackColor = rtb.BackColor;
            rtb.SelectionFont = boldFont;
            rtb.AppendText(summary + "\n");
        }

        private static Button MakeButton(string text, int width)
        {
            Color normalColor = Color.FromArgb(75, 75, 78);
            Button btn = new Button { Text = text, Dock = DockStyle.Right, Width = width, Height = 22, FlatStyle = FlatStyle.Flat, BackColor = normalColor, ForeColor = Color.White, Font = new Font("Segoe UI", 8f, FontStyle.Regular), Cursor = Cursors.Hand };
            btn.FlatAppearance.BorderSize = 0; btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 100, 105); btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 50, 53);
            return btn;
        }

        private void ResetIgnoredPorts()
        {
            _ignoredPorts.Clear();
            MessageBox.Show(Lang.Get("Gizlenen port listesi temizlendi.", "Ignored port list cleared."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateStatusBar();
        }

        private void ReopenIgnoredPort()
        {
            string[] systemPorts = SerialPort.GetPortNames();
            var available = _ignoredPorts.Where(p => systemPorts.Contains(p)).OrderBy(p => p).ToList();
            if (available.Count == 0) { MessageBox.Show(Lang.Get("Yeniden açılabilecek kapalı port yok.", "No closed ports to reopen."), Lang.Get("Bilgi", "Info"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using Form dlg = new Form { Text = Lang.Get("Port Seç", "Select Port"), Size = new Size(280, 180), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(40, 40, 40) };
            Label lbl = new Label { Text = Lang.Get("Açmak istediğiniz portu seçin:", "Select port to open:"), ForeColor = Color.Silver, Font = new Font("Segoe UI", 9f), Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
            ListBox lst = new ListBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.LimeGreen, Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None };
            foreach (var p in available) lst.Items.Add(p); lst.SelectedIndex = 0;
            Button btnOk = new Button { Text = Lang.Get("Aç", "Open"), Dock = DockStyle.Bottom, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnOk.FlatAppearance.BorderSize = 0; btnOk.Click += (s, e) => dlg.DialogResult = DialogResult.OK; lst.DoubleClick += (s, e) => dlg.DialogResult = DialogResult.OK;
            dlg.Controls.Add(lst); dlg.Controls.Add(lbl); dlg.Controls.Add(btnOk);
            if (dlg.ShowDialog(this) == DialogResult.OK && lst.SelectedItem is string selected) { _ignoredPorts.Remove(selected); if (TryCreatePortWindow(selected)) _busyPorts.Remove(selected); else _busyPorts.Add(selected); RearrangeGrid(); UpdateStatusBar(); }
        }

        private void CloseSpecificPort(GroupBox boxToRemove, bool refreshGrid, bool addToIgnoreList)
        {
            if (boxToRemove == null || boxToRemove.IsDisposed) return;
            if (_zoomedBox == boxToRemove) ToggleZoom(boxToRemove);

            Form? df = _detachedForms.FirstOrDefault(f => f.Controls.Contains(boxToRemove));
            if (df != null) { _detachedForms.Remove(df); df.Controls.Remove(boxToRemove); df.Close(); }

            if (_portMap.ContainsKey(boxToRemove))
            {
                SerialPort p = _portMap[boxToRemove]; string pName = p.PortName;
                if (addToIgnoreList) _ignoredPorts.Add(pName);
                _atLineStart.Remove(pName); _scrollFrozen.Remove(pName); _frozenBuffer.Remove(pName);
                _logAtLineStart.Remove(pName); _loggingEnabled.Remove(pName); _repeatCheck.Remove(pName);
                _portPaused.Remove(pName); // FIX #2
                _lineRepeat.Remove(pName); // FIX: LineRepeat temizliği
                if (_flushTimer.TryGetValue(pName, out var tmr)) { tmr.Stop(); tmr.Dispose(); _flushTimer.Remove(pName); }
                lock (_receiveBuffer) _receiveBuffer.Remove(pName);
                _windowSearchActions.Remove(pName);
                if (_minimizedBoxes.ContainsKey(pName)) { _minimizedBoxes.Remove(pName); UpdateMinimizedBarWithLogs(); }
                if (p.IsOpen) try { p.Close(); } catch { }
                p.Dispose(); _portMap.Remove(boxToRemove);
            }
            _mainLayout.Controls.Remove(boxToRemove);
            boxToRemove.Dispose();
            if (refreshGrid) RearrangeGrid();
            UpdateStatusBar();
        }

        private static string GetMinimizedLabel(string portName) { var m = Regex.Match(portName, @"\d+"); return m.Success ? "C" + int.Parse(m.Value).ToString("D2") : portName; }

        private void MinimizePort(string portName, GroupBox groupBox)
        {
            if (_minimizedBoxes.ContainsKey(portName)) return;
            if (_zoomedBox == groupBox) ToggleZoom(groupBox);
            _mainLayout.Controls.Remove(groupBox);
            _minimizedBoxes[portName] = groupBox;
            RearrangeGrid(); UpdateMinimizedBarWithLogs(); UpdateStatusBar();
        }

        private void RestoreMinimizedPort(string portName)
        {
            RestoreMinimizedWindow(portName);
        }

        private void UpdateMinimizedBar()
        {
            UpdateMinimizedBarWithLogs();
        }

        private void RearrangeGrid()
        {
            if (_isArranging || _zoomedBox != null) return;
            _isArranging = true;
            try
            {
                _mainLayout.SuspendLayout();

                int count = _mainLayout.Controls.Count;
                if (count == 0) { _mainLayout.ResumeLayout(); return; }

                int cols = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling((double)count / cols);

                // Sütun ve satır sayısını güncelle
                _mainLayout.ColumnCount = cols;
                _mainLayout.RowCount = rows;

                // Eşit genişlik dağılımı
                _mainLayout.ColumnStyles.Clear();
                for (int c = 0; c < cols; c++)
                    _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));

                // Eşit yükseklik dağılımı
                _mainLayout.RowStyles.Clear();
                for (int r = 0; r < rows; r++)
                    _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

                // Her kontrolü doğru hücreye yerleştir, Dock = Fill yap
                var controls = _mainLayout.Controls.Cast<Control>().ToList();
                for (int i = 0; i < controls.Count; i++)
                {
                    var ctrl = controls[i];
                    int col = i % cols;
                    int row = i / cols;
                    _mainLayout.SetCellPosition(ctrl, new TableLayoutPanelCellPosition(col, row));
                    ctrl.Dock = DockStyle.Fill;
                    ctrl.Margin = new Padding(4);
                }

                _mainLayout.ResumeLayout(true);
            }
            finally { _isArranging = false; }
        }

        private void ToggleZoom(GroupBox targetBox)
        {
            if (targetBox == null || targetBox.IsDisposed) return;

            string? minPort = _minimizedBoxes.FirstOrDefault(kv => kv.Value == targetBox).Key;
            if (minPort != null) RestoreMinimizedWindow(minPort);

            Form? df = _detachedForms.FirstOrDefault(f => f.Controls.Contains(targetBox));
            if (df != null) { df.Controls.Remove(targetBox); targetBox.Dock = DockStyle.None; _mainLayout.Controls.Add(targetBox); _detachedForms.Remove(df); df.Close(); }

            Button? btnZ = targetBox.Controls.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == "btnZoom");
            this.SuspendLayout();
            if (_zoomedBox == null)
            {
                _zoomedBox = targetBox;
                _mainLayout.Controls.Remove(targetBox);
                this.Controls.Add(targetBox);
                targetBox.BringToFront();
                targetBox.Dock = DockStyle.Fill;
                if (btnZ != null) btnZ.Text = "🗗";
            }
            else
            {
                if (_zoomedBox != targetBox) { this.ResumeLayout(); return; }
                this.Controls.Remove(targetBox);
                targetBox.Dock = DockStyle.None;
                _mainLayout.Controls.Add(targetBox);
                _zoomedBox = null;
                if (btnZ != null) btnZ.Text = "◻";
                RearrangeGrid();
            }
            this.ResumeLayout();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Tüm aktif log yükleme işlemlerini iptal et
            lock (_activeLoads)
            {
                foreach (var c in _activeLoads) try { c.Cancel(); } catch { }
                _activeLoads.Clear();
            }
            _portScanner?.Stop();
            foreach (var tmr in _flushTimer.Values) { try { tmr.Stop(); tmr.Dispose(); } catch { } }
            foreach (var p in _portMap.Values) if (p.IsOpen) try { p.Close(); } catch { }
            foreach (var gb in _minimizedBoxes.Values) if (gb != null && !gb.IsDisposed) gb.Dispose();
            foreach (var df in _detachedForms.ToList()) try { df.Close(); } catch { }
            base.OnFormClosing(e);
        }
    }
}