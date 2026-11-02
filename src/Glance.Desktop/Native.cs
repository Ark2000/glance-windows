using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace Glance.Desktop;

public static class KeyVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Encrypt(string key) => key.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(key), true));
    public static string Decrypt(string cipher) => cipher.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(cipher), false));
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "Glance API Key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Key 解密或加密失败，请在当前 Windows 账户下重新填写 Key。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}

public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private readonly HwndSource source;
    private readonly Action action;
    private int activeId;
    private int sequence = 400;
    public string Current { get; private set; } = "";
    public GlobalHotkey(IntPtr handle, Action action) { source = HwndSource.FromHwnd(handle)!; this.action = action; source.AddHook(Hook); }
    public void Set(string text)
    {
        var (modifiers, key) = Parse(text);
        if (Current.Equals(text, StringComparison.OrdinalIgnoreCase)) return;
        var nextId = ++sequence;
        if (!RegisterHotKey(source.Handle, nextId, modifiers | 0x4000, key)) throw new Win32Exception("快捷键已被占用，请换一组组合键。");
        if (activeId != 0) UnregisterHotKey(source.Handle, activeId);
        activeId = nextId; Current = text;
    }
    public static (uint, uint) Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("快捷键示例：Ctrl+Alt+Space 或 Ctrl+Shift+Q。");
        uint mod = 0;
        for (var i = 0; i < parts.Length - 1; i++) mod |= parts[i].ToLowerInvariant() switch { "ctrl" => 2u, "alt" => 1u, "shift" => 4u, _ => throw new ArgumentException("修饰键支持 Ctrl、Alt、Shift。") };
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            throw new ArgumentException("请使用有效按键名称，例如 Space、Q、F2。");
        if ((mod & 3) == 0) throw new ArgumentException("快捷键请包含 Ctrl 或 Alt。");
        return (mod, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
    { if (msg == 0x0312 && wparam.ToInt32() == activeId) { handled = true; action(); } return IntPtr.Zero; }
    public void Dispose() { if (activeId != 0) UnregisterHotKey(source.Handle, activeId); source.RemoveHook(Hook); }
}
