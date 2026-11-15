using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Lueur;

/// <summary>Raccourcis clavier globaux : Ctrl+Alt+Debut (tout allumer), Ctrl+Alt+Fin (tout eteindre).</summary>
static class Hotkeys
{
    public const int AllOnId = 0x4C01;
    public const int AllOffId = 0x4C02;

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const uint VK_END = 0x23, VK_HOME = 0x24;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static void Register(Window window, Action<int> onHotkey)
    {
        var helper = new WindowInteropHelper(window);
        RegisterHotKey(helper.Handle, AllOnId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_HOME);
        RegisterHotKey(helper.Handle, AllOffId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_END);
        HwndSource.FromHwnd(helper.Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY)
            {
                onHotkey(wParam.ToInt32());
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    public static void Unregister(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        UnregisterHotKey(handle, AllOnId);
        UnregisterHotKey(handle, AllOffId);
    }
}

/// <summary>Demarrage avec Windows via HKCU\...\Run (pas besoin des droits admin).</summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Lueur";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Raccourcis .lnk sur le Bureau qui appellent Lueur.exe --on / --off (un double-clic suffit).</summary>
static class DesktopShortcuts
{
    public static void Create()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Make(Path.Combine(desktop, "Lueur - Tout allumer.lnk"), "--on", "Allume la chambre et le PC");
        Make(Path.Combine(desktop, "Lueur - Tout eteindre.lnk"), "--off", "Eteint la chambre et le PC");
    }

    static void Make(string path, string args, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponible");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = Environment.ProcessPath;
            link.Arguments = args;
            link.Description = description;
            link.IconLocation = Environment.ProcessPath + ",0";
            link.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
