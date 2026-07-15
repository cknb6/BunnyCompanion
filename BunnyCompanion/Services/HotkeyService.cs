using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BunnyCompanion.Services;

/// <summary>
/// 全局快捷键（对标经典桌宠：托盘之外的可发现热键）。
/// Ctrl+Shift+S 显隐 / C 聊天 / P 穿透 / Oem 设置 / H 帮助气泡
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    public const int IdToggleVisible = 1;
    public const int IdOpenChat = 2;
    public const int IdClickThrough = 3;
    public const int IdSettings = 4;
    public const int IdHelp = 5;

    private HwndSource? _source;
    private bool _registered;
    private Action<int>? _onHotkey;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Attach(Window window, Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(Hook);

        var mods = ModControl | ModShift | ModNoRepeat;
        // S / C / P / OemComma(,) / H
        RegisterHotKey(helper.Handle, IdToggleVisible, mods, 0x53);
        RegisterHotKey(helper.Handle, IdOpenChat, mods, 0x43);
        RegisterHotKey(helper.Handle, IdClickThrough, mods, 0x50);
        RegisterHotKey(helper.Handle, IdSettings, mods, 0xBC); // VK_OEM_COMMA
        RegisterHotKey(helper.Handle, IdHelp, mods, 0x48);
        _registered = true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            _onHotkey?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered || _source is null)
            return;
        var hwnd = _source.Handle;
        for (var id = IdToggleVisible; id <= IdHelp; id++)
        {
            try { UnregisterHotKey(hwnd, id); } catch { /* ignore */ }
        }
        _source.RemoveHook(Hook);
        _registered = false;
    }
}
