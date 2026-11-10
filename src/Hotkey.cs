using System;
using System.Runtime.InteropServices;

namespace GpuMonitor
{
    // Global Ctrl+Alt+F hotkey, which the user presses right after seeing a
    // flicker or black screen so that the moment is recorded as an incident.
    class Hotkey
    {
        const uint MOD_ALT = 0x1;
        const uint MOD_CONTROL = 0x2;
        const uint MOD_NOREPEAT = 0x4000;
        const uint VK_F = 0x46;
        const int Id = 0x4746;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        bool registered;

        public const string Label = "Ctrl+Alt+F";

        public bool Register(IntPtr hwnd)
        {
            registered = RegisterHotKey(hwnd, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F);
            return registered;
        }

        public bool Matches(IntPtr wParam)
        {
            return wParam.ToInt32() == Id;
        }

        public void Unregister(IntPtr hwnd)
        {
            if (registered) UnregisterHotKey(hwnd, Id);
            registered = false;
        }
    }
}
