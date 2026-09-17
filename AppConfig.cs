using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundpadHUD;

public sealed class HotkeyConfig
{
    public string Prev { get; set; } = "Ctrl+Alt+PageUp";
    public string Next { get; set; } = "Ctrl+Alt+PageDown";
    public string PlaySelected { get; set; } = "Ctrl+Alt+Enter";
    public string Stop { get; set; } = "Ctrl+Alt+Backspace";
    public string ToggleVisible { get; set; } = "Ctrl+Alt+H";
}

public sealed class AppConfig
{
    public int X { get; set; } = 80;
    public int Y { get; set; } = 80;
    public int Width { get; set; } = 440;
    public double Opacity { get; set; } = 0.92;
    public int FontSize { get; set; } = 20;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool Locked { get; set; } = false;
    public bool ShowHints { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowNextSounds { get; set; } = true;

    /// <summary>把悬浮窗里选中的行同步给 Soundpad（DoSelectIndex）。</summary>
    public bool MirrorSelection { get; set; } = true;

    /// <summary>连接后把 Soundpad 的类别切到「所有声音」，保证行号与全局索引一致。</summary>
    public bool LockToAllSoundsCategory { get; set; } = true;

    /// <summary>监听 Soundpad 自己的选曲热键（Alt+PgUp/PgDn 等），让悬浮窗跟随。</summary>
    public bool MirrorSoundpadHotkeys { get; set; } = true;

    public string Accent { get; set; } = "#4C8DFF";
    public int PollIntervalMs { get; set; } = 400;
    public HotkeyConfig Hotkeys { get; set; } = new HotkeyConfig();

    [JsonIgnore] public string FilePath { get; set; } = "";

    private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.json");
        AppConfig cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opts) ?? new AppConfig()
                : new AppConfig();
        }
        catch
        {
            cfg = new AppConfig();
        }
        cfg.Hotkeys ??= new HotkeyConfig();
        cfg.FilePath = path;
        try { if (!File.Exists(path)) cfg.Save(); } catch { }
        return cfg;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); } catch { }
    }
}

public static class HotkeyUtil
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public static bool TryParse(string s, out uint mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var raw in s.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win":
                case "windows": mods |= MOD_WIN; break;
                default:
                    // Keys 枚举的成员名与常用写法不一致，这里补上别名
                    switch (part.ToLowerInvariant())
                    {
                        case "backspace": case "back": key = Keys.Back; break;
                        case "delete": case "del": key = Keys.Delete; break;
                        case "insert": case "ins": key = Keys.Insert; break;
                        case "enter": case "return": key = Keys.Enter; break;
                        case "escape": case "esc": key = Keys.Escape; break;
                        case "space": case "spacebar": key = Keys.Space; break;
                        case "pageup": case "pgup": key = Keys.PageUp; break;
                        case "pagedown": case "pgdn": key = Keys.PageDown; break;
                        case "up": key = Keys.Up; break;
                        case "down": key = Keys.Down; break;
                        case "left": key = Keys.Left; break;
                        case "right": key = Keys.Right; break;
                        default:
                            if (!Enum.TryParse(part, true, out Keys k)) return false;
                            key = k;
                            break;
                    }
                    break;
            }
        }
        return key != Keys.None;
    }

    public static string Text(string s) => string.IsNullOrWhiteSpace(s) ? "（未设置）" : s;
}

public static class Palette
{
    public static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var h = hex.Trim().TrimStart('#');
            if (h.Length == 6)
                return Color.FromArgb(255,
                    Convert.ToInt32(h.Substring(0, 2), 16),
                    Convert.ToInt32(h.Substring(2, 2), 16),
                    Convert.ToInt32(h.Substring(4, 2), 16));
        }
        catch { }
        return fallback;
    }
}

public static class Fonts
{
    private static readonly string Family = Pick("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");

    private static string Pick(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var f = new FontFamily(n);
                return n;
            }
            catch { }
        }
        return "Segoe UI";
    }

    public static Font Title(int size) => new Font(Family, size, FontStyle.Bold, GraphicsUnit.Pixel);
    public static Font Body(float size) => new Font(Family, size, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Small(float size) => new Font(Family, size, FontStyle.Regular, GraphicsUnit.Pixel);
}
