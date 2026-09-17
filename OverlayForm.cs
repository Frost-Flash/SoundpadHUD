using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SoundpadHUD;

public sealed class OverlayForm : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int HK_PREV = 1;
    private const int HK_NEXT = 2;
    private const int HK_PLAY = 3;
    private const int HK_STOP = 4;
    private const int HK_TOGGLE = 5;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class UiState
    {
        public bool Connected;
        public string Version = "";
        public string Message = "正在连接 Soundpad…";
        public List<SoundInfo> Sounds = new List<SoundInfo>();
        public int Cursor = -1;
        public PlayStatus Play = PlayStatus.Unknown;
        public long PosMs;
        public long DurMs;
        public string PlayingTitle = "";
        public int PlayingIndex = -1;
    }

    private readonly AppConfig _cfg;
    private readonly UiState _ui = new UiState();
    private readonly object _stateLock = new object();
    private readonly ConcurrentQueue<string> _intents = new ConcurrentQueue<string>();
    private readonly SoundpadHotkeyMirror _mirror;
    private readonly Color _accent;
    private readonly List<string> _hotkeyErrors = new List<string>();

    private NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private ToolStripMenuItem _miTop, _miLock, _miThrough, _miHints, _miProgress, _miNext, _miMirror, _miMirrorKeys, _miShowHide, _miAutoStart;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SoundpadHUD";
    private Thread _thread;
    private volatile bool _closing;
    private DateTime _lastListUtc = DateTime.MinValue;
    private bool _categoryLocked;
    private int _allSoundsCategoryIndex = -1;
    private bool _dragging;
    private Point _dragOffset;

    private readonly bool _demo;

    public OverlayForm(AppConfig cfg, bool demo = false)
    {
        _cfg = cfg;
        _demo = demo;
        _accent = Palette.Parse(cfg.Accent, Color.FromArgb(255, 76, 141, 255));

        AutoScaleMode = AutoScaleMode.None;

        // 双缓冲 + 不擦背景，避免重绘时闪烁
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = cfg.AlwaysOnTop;
        Opacity = Math.Max(0.25, Math.Min(1.0, cfg.Opacity));
        BackColor = Color.FromArgb(15, 17, 21);
        KeyPreview = true;
        Text = "Soundpad HUD";

        var wa = SystemInformation.VirtualScreen;
        int x = Math.Max(wa.Left + 4, Math.Min(cfg.X, wa.Right - 120));
        int y = Math.Max(wa.Top + 4, Math.Min(cfg.Y, wa.Bottom - 60));
        Location = new Point(x, y);

        BuildMenu();
        BuildTray();

        _mirror = new SoundpadHotkeyMirror(OnSoundpadHotkey)
        {
            Enabled = cfg.MirrorSoundpadHotkeys
        };
        _mirror.LoadSoundpadHotkeys();

        UpdateLayout();
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
        if (_cfg.MirrorSoundpadHotkeys) _mirror.Install();

        _thread = new Thread(PollLoop) { IsBackground = true, Name = "SoundpadPoll" };
        _thread.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        for (int id = HK_PREV; id <= HK_TOGGLE; id++)
            try { UnregisterHotKey(Handle, id); } catch { }
        try { _mirror.Dispose(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        _cfg.X = Location.X;
        _cfg.Y = Location.Y;
        _cfg.Save();
        Program.Log("paints=" + _paintCount + " regionSets=" + _regionSets);
        base.OnFormClosing(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            // 注意：基类 Form 构造函数期间就会读取 CreateParams，此时 _cfg 尚未赋值
            if (_cfg == null || _cfg.AlwaysOnTop) cp.ExStyle |= WS_EX_TOPMOST;
            if (_cfg != null && _cfg.ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    private void ApplyStyles()
    {
        TopMost = _cfg.AlwaysOnTop;
        UpdateStyles();
        Invalidate();
    }

    private void RegisterHotkeys()
    {
        _hotkeyErrors.Clear();
        Register(HK_PREV, _cfg.Hotkeys.Prev);
        Register(HK_NEXT, _cfg.Hotkeys.Next);
        Register(HK_PLAY, _cfg.Hotkeys.PlaySelected);
        Register(HK_STOP, _cfg.Hotkeys.Stop);
        Register(HK_TOGGLE, _cfg.Hotkeys.ToggleVisible);
        if (_tray != null)
        {
            string text = _hotkeyErrors.Count == 0
                ? "Soundpad HUD"
                : "Soundpad HUD - 热键被占用: " + string.Join(", ", _hotkeyErrors);
            _tray.Text = text.Length > 62 ? text.Substring(0, 62) : text;
        }
        Program.Log("hotkeys registered, errors=" + _hotkeyErrors.Count + " hook=" + _mirror.Install());
    }

    private void Register(int id, string spec)
    {
        if (!HotkeyUtil.TryParse(spec, out uint mods, out Keys key))
        {
            _hotkeyErrors.Add(spec + "(写法无法识别)");
            Program.Log("hotkey parse FAILED: [" + spec + "]");
            return;
        }
        if (!RegisterHotKey(Handle, id, mods, (uint)key))
        {
            _hotkeyErrors.Add(spec);
            Program.Log("hotkey register FAILED (被占用): [" + spec + "]");
        }
        else
        {
            Program.Log("hotkey ok: [" + spec + "] -> key=" + key + " mods=" + mods);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HK_PREV: _intents.Enqueue("prev"); break;
                case HK_NEXT: _intents.Enqueue("next"); break;
                case HK_PLAY: _intents.Enqueue("play"); break;
                case HK_STOP: _intents.Enqueue("stop"); break;
                case HK_TOGGLE: ToggleVisible(); break;
            }
        }
        base.WndProc(ref m);
    }

    private void OnSoundpadHotkey(int delta)
    {
        if (delta > 0) _intents.Enqueue("sd-next");
        else if (delta < 0) _intents.Enqueue("sd-prev");
    }

    private void ToggleVisible()
    {
        if (Visible) Hide();
        else { Show(); TopMost = _cfg.AlwaysOnTop; }
        UpdateMenuChecks();
    }

    // ---------------------------------------------------------------- tray + menu

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Soundpad HUD",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (s, e) => ToggleVisible();
    }

    private ToolStripMenuItem Item(string text, bool check, Action onClick)
    {
        var it = new ToolStripMenuItem(text) { CheckOnClick = false, Checked = check };
        it.Click += (s, e) => onClick();
        return it;
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();
        _menu.Opening += (s, e) => UpdateMenuChecks();

        _miTop = Item("始终置顶", _cfg.AlwaysOnTop, () =>
        {
            _cfg.AlwaysOnTop = !_cfg.AlwaysOnTop;
            ApplyStyles(); _cfg.Save();
        });
        _miLock = Item("锁定位置", _cfg.Locked, () => { _cfg.Locked = !_cfg.Locked; _cfg.Save(); });
        _miThrough = Item("鼠标穿透", _cfg.ClickThrough, () =>
        {
            _cfg.ClickThrough = !_cfg.ClickThrough;
            ApplyStyles(); _cfg.Save();
        });
        _miHints = Item("显示快捷键提示", _cfg.ShowHints, () => { _cfg.ShowHints = !_cfg.ShowHints; RefreshUi(); _cfg.Save(); });
        _miProgress = Item("显示播放进度", _cfg.ShowProgress, () => { _cfg.ShowProgress = !_cfg.ShowProgress; RefreshUi(); _cfg.Save(); });
        _miNext = Item("显示下一个音频", _cfg.ShowNextSounds, () => { _cfg.ShowNextSounds = !_cfg.ShowNextSounds; RefreshUi(); _cfg.Save(); });
        _miMirror = Item("选中同步到 Soundpad", _cfg.MirrorSelection, () => { _cfg.MirrorSelection = !_cfg.MirrorSelection; _cfg.Save(); });
        _miMirrorKeys = Item("跟随 Soundpad 选曲热键", _cfg.MirrorSoundpadHotkeys, () =>
        {
            _cfg.MirrorSoundpadHotkeys = !_cfg.MirrorSoundpadHotkeys;
            _mirror.Enabled = _cfg.MirrorSoundpadHotkeys;
            if (_cfg.MirrorSoundpadHotkeys) _mirror.Install(); else _mirror.Uninstall();
            _cfg.Save();
        });

        _menu.Items.Add(_miTop);
        _menu.Items.Add(_miLock);
        _menu.Items.Add(_miThrough);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miHints);
        _menu.Items.Add(_miProgress);
        _menu.Items.Add(_miNext);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miMirror);
        _menu.Items.Add(_miMirrorKeys);

        var opacity = new ToolStripMenuItem("不透明度");
        foreach (double v in new[] { 1.0, 0.92, 0.85, 0.75, 0.6, 0.45 })
        {
            double val = v;
            var oit = new ToolStripMenuItem(((int)(val * 100)) + "%")
            {
                Checked = Math.Abs(_cfg.Opacity - val) < 0.01,
            };
            oit.Click += (s, e) =>
            {
                _cfg.Opacity = val;
                Opacity = val;
                foreach (ToolStripMenuItem o in opacity.DropDownItems) o.Checked = false;
                ((ToolStripMenuItem)s).Checked = true;
                _cfg.Save();
            };
            opacity.DropDownItems.Add(oit);
        }
        _menu.Items.Add(opacity);

        var font = new ToolStripMenuItem("字号");
        foreach (int v in new[] { 14, 18, 22, 26, 32 })
        {
            int val = v;
            var it = new ToolStripMenuItem(val + "px") { Checked = _cfg.FontSize == val };
            it.Click += (s, e) =>
            {
                _cfg.FontSize = val;
                foreach (ToolStripMenuItem o in font.DropDownItems) o.Checked = false;
                it.Checked = true;
                RefreshUi(); _cfg.Save();
            };
            font.DropDownItems.Add(it);
        }
        _menu.Items.Add(font);

        var width = new ToolStripMenuItem("宽度");
        foreach (int v in new[] { 320, 440, 560, 720 })
        {
            int val = v;
            var it = new ToolStripMenuItem(val + "px") { Checked = _cfg.Width == val };
            it.Click += (s, e) =>
            {
                _cfg.Width = val;
                foreach (ToolStripMenuItem o in width.DropDownItems) o.Checked = false;
                it.Checked = true;
                RefreshUi(); _cfg.Save();
            };
            width.DropDownItems.Add(it);
        }
        _menu.Items.Add(width);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("刷新声音列表", null, (s, e) => _intents.Enqueue("refresh")));
        _menu.Items.Add(new ToolStripMenuItem("重新连接 Soundpad", null, (s, e) => _intents.Enqueue("reconnect")));
        _menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo(_cfg.FilePath) { UseShellExecute = true }); } catch { }
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _miAutoStart = Item("开机自动启动", IsAutoStart(), () =>
        {
            SetAutoStart(!IsAutoStart());
            UpdateMenuChecks();
        });
        _menu.Items.Add(_miAutoStart);
        _menu.Items.Add(new ToolStripSeparator());
        _miShowHide = new ToolStripMenuItem("隐藏悬浮窗", null, (s, e) => ToggleVisible());
        _menu.Items.Add(_miShowHide);
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Close()));
    }

    private static bool IsAutoStart()
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k != null && k.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool on)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (k == null) return;
            if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    private void UpdateMenuChecks()
    {
        if (_menu == null) return;
        _miTop.Checked = _cfg.AlwaysOnTop;
        _miLock.Checked = _cfg.Locked;
        _miThrough.Checked = _cfg.ClickThrough;
        _miHints.Checked = _cfg.ShowHints;
        _miProgress.Checked = _cfg.ShowProgress;
        _miNext.Checked = _cfg.ShowNextSounds;
        _miMirror.Checked = _cfg.MirrorSelection;
        _miMirrorKeys.Checked = _cfg.MirrorSoundpadHotkeys;
        _miAutoStart.Checked = IsAutoStart();
        _miShowHide.Text = Visible ? "隐藏悬浮窗" : "显示悬浮窗";
    }

    // ---------------------------------------------------------------- drawing

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private int _progBlock;

    /// <summary>按显示器 DPI 缩放（96 DPI = 1.0）。</summary>
    private float DpiScale => Math.Max(0.5f, DeviceDpi / 96f);
    private int Sc(float v) => (int)Math.Round(v * DpiScale);

    private static bool ShowProgressBlock(AppConfig cfg, PlayStatus st, long dur)
    {
        return cfg.ShowProgress && dur > 0 &&
               (st == PlayStatus.Playing || st == PlayStatus.Paused || st == PlayStatus.Seeking);
    }
    private int _hintBlock;

    private void RefreshUi()
    {
        UpdateLayout();
        Invalidate();
        _lastSignature = BuildSignature();
    }

    private void UpdateLayout()
    {
        if (IsDisposed) return;
        int pad = Sc(16);
        int titleH = (int)Math.Round(_cfg.FontSize * DpiScale * 1.4) + Sc(6);
        int subH = Sc(20);
        lock (_stateLock)
        {
            _progBlock = ShowProgressBlock(_cfg, _ui.Play, _ui.DurMs) ? Sc(30) : 0;
        }
        _hintBlock = _cfg.ShowHints ? Sc(18) : 0;
        int h = pad * 2 + titleH + subH + _progBlock + _hintBlock;
        if (h < Sc(76)) h = Sc(76);
        int w = Math.Max(Sc(220), Sc(_cfg.Width));
        // 关键：SetWindowRgn 会让整个窗口重建，只有尺寸真的变了才动它，
        // 否则每次轮询都设一遍 Region 就会持续闪烁。
        if (ClientSize.Width != w || ClientSize.Height != h)
        {
            ClientSize = new Size(w, h);
            ApplyRegion();
        }
    }

    private void ApplyRegion()
    {
        try
        {
            var old = Region;
            Region = new Region(Rounded(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Sc(12)));
            _regionSets++;
            old?.Dispose();
        }
        catch { }
    }

    private int _paintCount;
    private int _regionSets;

    protected override void OnPaint(PaintEventArgs e)
    {
        _paintCount++;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool connected;
        string title, sub;
        PlayStatus st;
        long pos, dur;
        int cursor;
        int count;
        SoundInfo cur = null, next = null;
        lock (_stateLock)
        {
            connected = _ui.Connected;
            cursor = _ui.Cursor;
            count = _ui.Sounds.Count;
            st = _ui.Play;
            pos = _ui.PosMs;
            dur = _ui.DurMs;
            if (cursor >= 0 && cursor < count) cur = _ui.Sounds[cursor];
            if (cursor + 1 >= 0 && cursor + 1 < count) next = _ui.Sounds[cursor + 1];
            title = !_ui.Connected ? "Soundpad 未运行"
                  : cur != null ? cur.Title
                  : "（未选择音频）";
            sub = BuildSubtitle(connected, cursor, count, next);
        }

        var bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        using (var path = Rounded(bounds, 12))
        {
            using var bg = new SolidBrush(Color.FromArgb(255, 15, 17, 21));
            g.FillPath(bg, path);
            using var pen = new Pen(connected ? Color.FromArgb(255, 44, 50, 60) : Color.FromArgb(255, 102, 46, 46), 1f);
            g.DrawPath(pen, path);
        }
        using (var ab = new SolidBrush(connected ? _accent : Color.FromArgb(255, 150, 70, 70)))
            g.FillRectangle(ab, Sc(1), Sc(12), Sc(4), Math.Max(1, ClientSize.Height - Sc(24)));

        int pad = Sc(16);
        int x = pad + Sc(4);
        int w = ClientSize.Width - x - Sc(12);
        int y = pad;
        int titleH = (int)Math.Round(_cfg.FontSize * DpiScale * 1.4) + Sc(6);

        using (var f = Fonts.Title((int)Math.Round(_cfg.FontSize * DpiScale)))
            TextRenderer.DrawText(g, title, f, new Rectangle(x, y, w, titleH),
                connected ? Color.FromArgb(255, 240, 243, 248) : Color.FromArgb(255, 214, 140, 140),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += titleH;

        using (var f = Fonts.Small(13f * DpiScale))
            TextRenderer.DrawText(g, sub, f, new Rectangle(x, y, w, Sc(20)), Color.FromArgb(255, 148, 156, 168),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += Sc(20);

        int progBlock = ShowProgressBlock(_cfg, st, dur) ? Sc(30) : 0;
        if (progBlock > 0)
        {
            double frac = dur > 0 ? Math.Max(0, Math.Min(1, (double)pos / dur)) : 0;
            var bar = new Rectangle(x, y + Sc(4), w, Sc(5));
            using (var b = new SolidBrush(Color.FromArgb(255, 38, 42, 50))) g.FillRectangle(b, bar);
            using (var b = new SolidBrush(_accent))
                g.FillRectangle(b, new Rectangle(bar.X, bar.Y, Math.Max(1, (int)(bar.Width * frac)), bar.Height));
            string pt = (st == PlayStatus.Paused ? "⏸ 已暂停  " : "▶ 正在播放  ")
                        + SoundpadClient.FormatMs(pos) + " / " + SoundpadClient.FormatMs(dur);
            using var f = Fonts.Small(12f * DpiScale);
            TextRenderer.DrawText(g, pt, f, new Rectangle(x, y + Sc(11), w, Sc(18)), Color.FromArgb(255, 140, 200, 255),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += Sc(30);
        }

        if (_hintBlock > 0)
        {
            using var f = Fonts.Small(11.5f * DpiScale);
            TextRenderer.DrawText(g, HintText(), f, new Rectangle(x, y, w, Sc(18)), Color.FromArgb(255, 98, 106, 118),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private string BuildSubtitle(bool connected, int cursor, int count, SoundInfo next)
    {
        if (!connected) return "等待 Soundpad 启动（从 Steam 启动即可）";
        if (count == 0) return "声音列表为空";
        var parts = new List<string>();
        if (cursor >= 0 && cursor < count)
        {
            var s = _ui.Sounds[cursor];
            parts.Add("#" + s.Index + " / 共 " + count);
            if (!string.IsNullOrEmpty(s.Duration)) parts.Add(s.Duration);
            parts.Add("播放 " + s.PlayCount + " 次");
        }
        else parts.Add("共 " + count + " 个音频");
        if (_cfg.ShowNextSounds && next != null) parts.Add("下一个: " + next.Title);
        return string.Join("  ·  ", parts);
    }

    private string HintText()
    {
        var h = _cfg.Hotkeys;
        return HotkeyUtil.Text(h.Prev) + " / " + HotkeyUtil.Text(h.Next) + " 选曲   ·   "
             + HotkeyUtil.Text(h.PlaySelected) + " 播放选中的   ·   "
             + HotkeyUtil.Text(h.ToggleVisible) + " 隐藏";
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RefreshUi();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !_cfg.Locked)
        {
            _dragging = true;
            _dragOffset = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            _cfg.X = Location.X;
            _cfg.Y = Location.Y;
            _cfg.Save();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right)
        {
            UpdateMenuChecks();
            _menu.Show(this, e.Location);
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        _cfg.Locked = !_cfg.Locked;
        _cfg.Save();
        UpdateMenuChecks();
    }

    // ---------------------------------------------------------------- polling

    private void RequestRepaint()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(RefreshUi));
        }
        catch { }
    }

    private string _lastSignature;

    /// <summary>
    /// 只有画面内容真的变了才请求重绘。空闲时签名不变，一次重绘都不会发生，
    /// 这样就不会出现"不停闪烁"。
    /// </summary>
    private void RequestRepaintIfChanged()
    {
        string sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        RequestRepaint();
    }

    private string BuildSignature()
    {
        var sb = new System.Text.StringBuilder(128);
        lock (_stateLock)
        {
            sb.Append(_ui.Connected ? '1' : '0').Append('|')
              .Append(_ui.Cursor).Append('|')
              .Append(_ui.Sounds.Count).Append('|')
              .Append((int)_ui.Play).Append('|')
              .Append(_ui.PosMs / 250).Append('|')
              .Append(_ui.DurMs).Append('|');
            if (_ui.Cursor >= 0 && _ui.Cursor < _ui.Sounds.Count)
            {
                var s = _ui.Sounds[_ui.Cursor];
                sb.Append(s.Title).Append('|').Append(s.Duration).Append('|').Append(s.PlayCount).Append('|');
            }
            if (_cfg.ShowNextSounds && _ui.Cursor >= 0 && _ui.Cursor + 1 < _ui.Sounds.Count)
                sb.Append(_ui.Sounds[_ui.Cursor + 1].Title);
        }
        sb.Append('|').Append(_cfg.FontSize).Append('|').Append(_cfg.Width)
          .Append('|').Append(_cfg.ShowHints).Append('|').Append(_cfg.ShowProgress)
          .Append('|').Append(_cfg.ShowNextSounds).Append('|').Append(_cfg.Opacity)
          .Append('|').Append(_cfg.AlwaysOnTop).Append('|').Append(_cfg.Locked)
          .Append('|').Append(_cfg.ClickThrough).Append('|').Append(_cfg.Accent);
        return sb.ToString();
    }

    /// <summary>--demo：不连接 Soundpad，用假数据预览悬浮窗外观（含播放进度）。</summary>
    private void DemoLoop()
    {
        var sounds = new List<SoundInfo>
        {
            new SoundInfo { Index = 1, Title = "演示音频 - 悬浮窗预览", Duration = "1:39", DurationSeconds = 99, PlayCount = 7 },
            new SoundInfo { Index = 2, Title = "第二个音频", Duration = "0:49", DurationSeconds = 49, PlayCount = 3 },
        };
        lock (_stateLock)
        {
            _ui.Connected = true;
            _ui.Version = "demo";
            _ui.Sounds = sounds;
            _ui.Cursor = 0;
            _ui.Play = PlayStatus.Playing;
            _ui.DurMs = 99000;
        }
        long t0 = Environment.TickCount64;
        while (!_closing)
        {
            long elapsed = (Environment.TickCount64 - t0) % 99000;
            lock (_stateLock) { _ui.PosMs = elapsed; }
            RequestRepaintIfChanged();
            Thread.Sleep(200);
        }
    }

    private void PollLoop()
    {
        if (_demo) { DemoLoop(); return; }
        using var client = new SoundpadClient();
        while (!_closing)
        {
            try
            {
                if (!client.Connected)
                {
                    _categoryLocked = false;
                    _allSoundsCategoryIndex = -1;
                    string v = client.GetVersion();
                    if (string.IsNullOrEmpty(v))
                    {
                        lock (_stateLock) { _ui.Connected = false; _ui.Sounds = new List<SoundInfo>(); _ui.Cursor = -1; }
                        RequestRepaintIfChanged();
                        Thread.Sleep(1000);
                        continue;
                    }
                    lock (_stateLock) { _ui.Connected = true; _ui.Version = v; }
                    _lastListUtc = DateTime.MinValue;
                }

                bool needMirror = false;
                while (_intents.TryDequeue(out string intent))
                {
                    switch (intent)
                    {
                        case "prev": MoveCursor(-1); needMirror = true; break;
                        case "next": MoveCursor(1); needMirror = true; break;
                        case "sd-prev": MoveCursor(-1); break;
                        case "sd-next": MoveCursor(1); break;
                        case "play": needMirror = PlaySelected(client); break;
                        case "stop": client.Stop(); lock (_stateLock) _ui.Play = PlayStatus.Stopped; break;
                        case "refresh": RefreshList(client); break;
                        case "reconnect": client.Reset(); _categoryLocked = false; _allSoundsCategoryIndex = -1; break;
                    }
                }

                if (!_categoryLocked && _cfg.LockToAllSoundsCategory)
                {
                    var cats = client.GetCategories();
                    var all = cats.FirstOrDefault(c => c.Type == 1) ?? cats.FirstOrDefault(c => c.Hidden);
                    if (all != null && SoundpadClient.Ok(client.SelectCategory(all.Index)))
                    {
                        _allSoundsCategoryIndex = all.Index;
                        _categoryLocked = true;
                    }
                }

                if (needMirror && _cfg.MirrorSelection)
                {
                    int cur;
                    lock (_stateLock) cur = _ui.Cursor;
                    if (cur >= 0)
                    {
                        EnsureAllSoundsCategory(client);
                        client.SelectIndex(cur);
                    }
                }

                if ((DateTime.UtcNow - _lastListUtc).TotalMilliseconds > 1600 || _lastListUtc == DateTime.MinValue)
                    RefreshList(client);

                PollStatus(client);
                RequestRepaintIfChanged();
            }
            catch
            {
                client.Reset();
            }

            int sleep = Math.Max(120, _cfg.PollIntervalMs);
            for (int i = 0; i < sleep && !_closing; i += 40) Thread.Sleep(40);
        }
    }

    private void MoveCursor(int delta)
    {
        lock (_stateLock)
        {
            int n = _ui.Sounds.Count;
            if (n == 0) return;
            if (_ui.Cursor < 0) _ui.Cursor = delta > 0 ? 0 : n - 1;
            else _ui.Cursor = Math.Max(0, Math.Min(n - 1, _ui.Cursor + delta));
        }
    }

    private void RefreshList(SoundpadClient client)
    {
        var sounds = client.GetSoundList();
        _lastListUtc = DateTime.UtcNow;
        if (sounds.Count == 0) return;
        lock (_stateLock)
        {
            _ui.Sounds = sounds;
            if (_ui.Cursor >= sounds.Count) _ui.Cursor = sounds.Count - 1;
            if (_ui.Cursor < 0) _ui.Cursor = 0;
        }
    }

    private bool PlaySelected(SoundpadClient client)
    {
        int row, index;
        string title;
        lock (_stateLock)
        {
            if (_ui.Cursor < 0 || _ui.Cursor >= _ui.Sounds.Count) return false;
            row = _ui.Cursor;
            index = _ui.Sounds[row].Index;   // GetSoundlist 里的全局索引（1 起）
            title = _ui.Sounds[row].Title;
        }

        // 关键：播放「明确指定的这一条」，而不是 DoPlaySelectedSound()。
        // 后者播放的是 Soundpad 自己认定的“当前选中项”，一旦高亮同步慢一拍
        // （或失败）就会播出别的文件。这里先同步高亮，再按索引播放。
        EnsureAllSoundsCategory(client);
        if (_cfg.MirrorSelection) client.SelectIndex(row);
        client.Play(index);

        lock (_stateLock)
        {
            _ui.PlayingIndex = index;
            _ui.PlayingTitle = title;
            _ui.Play = PlayStatus.Playing;
        }
        return false;
    }

    /// <summary>
    /// DoSelectIndex 用的是“当前类别里的行号”，而 GetSoundlist 给的是「所有声音」的索引；
    /// 只有把 Soundpad 的类别切到「所有声音」，行号 0..N-1 才恰好等于索引 1..N。
    /// 「所有声音」是隐藏类别，Soundpad 刚启动时可能还没建立（会返回 R-204），
    /// 所以这里每次动作前都重新断言一次，失败也不影响播放（播放用的是索引）。
    /// </summary>
    private void EnsureAllSoundsCategory(SoundpadClient client)
    {
        if (!_cfg.LockToAllSoundsCategory) return;
        if (_allSoundsCategoryIndex < 0)
        {
            var cats = client.GetCategories();
            var all = cats.FirstOrDefault(c => c.Type == 1) ?? cats.FirstOrDefault(c => c.Hidden);
            if (all == null) return;
            _allSoundsCategoryIndex = all.Index;
        }
        client.SelectCategory(_allSoundsCategoryIndex);
    }

    private void PollStatus(SoundpadClient client)
    {
        var st = client.GetPlayStatus();
        long pos = 0, dur = 0;
        if (st == PlayStatus.Playing || st == PlayStatus.Paused || st == PlayStatus.Seeking)
        {
            pos = client.GetPlaybackPositionMs();
            dur = client.GetPlaybackDurationMs();
            if (pos < 0) pos = 0;
            if (dur < 0) dur = 0;
        }

        bool justStarted;
        lock (_stateLock)
        {
            justStarted = st == PlayStatus.Playing && _ui.Play != PlayStatus.Playing;
            _ui.Play = st;
            _ui.PosMs = pos;
            _ui.DurMs = dur;
        }

        if (justStarted && dur > 500)
        {
            int best = -1;
            double bestDiff = 3.0;
            lock (_stateLock)
            {
                for (int i = 0; i < _ui.Sounds.Count; i++)
                {
                    double diff = Math.Abs(_ui.Sounds[i].DurationSeconds - dur / 1000.0);
                    if (_ui.Sounds[i].DurationSeconds > 0 && diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = i;
                    }
                }
                if (best >= 0)
                {
                    _ui.Cursor = best;
                    _ui.PlayingIndex = _ui.Sounds[best].Index;
                    _ui.PlayingTitle = _ui.Sounds[best].Title;
                }
            }
        }
    }
}
