using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SoundpadHUD;

/// <summary>
/// 只读监听级别的键盘钩子（WH_KEYBOARD_LL，安装在本进程内，不注入 Soundpad）。
/// 用于发现用户按下了 Soundpad 自己的「上一个/下一个」选曲热键，
/// 从而让悬浮窗的选中项跟随 Soundpad。
/// 热键组合直接从 Soundpad 的注册表配置读取。
/// </summary>
public sealed class SoundpadHotkeyMirror : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private readonly HookProc _proc;
    private readonly Action<int> _onMove;
    private IntPtr _hook = IntPtr.Zero;
    private uint _lastVk;

    public uint PrevKey, PrevMods, NextKey, NextMods;
    public bool Enabled = true;

    public SoundpadHotkeyMirror(Action<int> onMove)
    {
        _onMove = onMove;
        _proc = Hook;
    }

    public string Describe()
    {
        return "Soundpad 选曲热键: 上一个 vk=0x" + PrevKey.ToString("X") + " mods=" + PrevMods
             + " / 下一个 vk=0x" + NextKey.ToString("X") + " mods=" + NextMods;
    }

    public void LoadSoundpadHotkeys()
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Leppsoft\Soundpad\MainFrame");
            if (k == null) return;
            PrevKey = ToU(k.GetValue("selectPreviousKey"));
            PrevMods = ToU(k.GetValue("selectPreviousKeyModifiers"));
            NextKey = ToU(k.GetValue("selectNextKey"));
            NextMods = ToU(k.GetValue("selectNextKeyModifiers"));
        }
        catch { }
    }

    private static uint ToU(object v)
    {
        try { return v == null ? 0u : unchecked((uint)Convert.ToInt32(v)); }
        catch { return 0u; }
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>wxWidgets 风格的修饰键位掩码：Alt=1, Ctrl=2, Shift=4, Win=8。</summary>
    private static uint CurrentMods()
    {
        uint m = 0;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) m |= 0x0001;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) m |= 0x0002;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) m |= 0x0004;
        if (((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0) m |= 0x0008;
        return m;
    }

    private IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    uint vk = data.vkCode;
                    if (vk != _lastVk)
                    {
                        _lastVk = vk;
                        uint mods = CurrentMods();
                        if (NextKey != 0 && vk == NextKey && mods == NextMods) _onMove(1);
                        else if (PrevKey != 0 && vk == PrevKey && mods == PrevMods) _onMove(-1);
                    }
                }
                else
                {
                    _lastVk = 0;
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
