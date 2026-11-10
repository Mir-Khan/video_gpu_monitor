using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuMonitor
{
    // Hidden window that receives display mode changes, monitor connect and
    // disconnect notifications, and the flicker hotkey, so that none of these
    // need to be polled.
    class MessageWindow : NativeWindow, IDisposable
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DEVICECHANGE = 0x0219;
        const int WM_HOTKEY = 0x0312;
        const int DBT_DEVICEARRIVAL = 0x8000;
        const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        const int DBT_DEVTYP_DEVICEINTERFACE = 5;

        static readonly Guid MonitorInterface = new Guid("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

        [StructLayout(LayoutKind.Sequential)]
        struct DevBroadcastDeviceInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
            public short Name;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient, ref DevBroadcastDeviceInterface filter, int flags);
        [DllImport("user32.dll")]
        static extern bool UnregisterDeviceNotification(IntPtr handle);

        public event Action DisplayChanged;
        public event Action<bool, string> MonitorDevice;
        public event Action HotkeyPressed;

        IntPtr deviceNotify = IntPtr.Zero;
        readonly Hotkey hotkey = new Hotkey();

        public MessageWindow()
        {
            // A normal (not message-only) top-level window is needed, because
            // WM_DISPLAYCHANGE is only broadcast to top-level windows. It is
            // never shown.
            var cp = new CreateParams();
            cp.Caption = "GpuMonitorMessageWindow";
            CreateHandle(cp);

            var filter = new DevBroadcastDeviceInterface();
            filter.Size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterface));
            filter.DeviceType = DBT_DEVTYP_DEVICEINTERFACE;
            filter.ClassGuid = MonitorInterface;
            deviceNotify = RegisterDeviceNotificationW(Handle, ref filter, 0);
        }

        public bool RegisterFlickerHotkey()
        {
            return hotkey.Register(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == WM_DISPLAYCHANGE)
                {
                    var h = DisplayChanged;
                    if (h != null) h();
                }
                else if (m.Msg == WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
                {
                    int wp = m.WParam.ToInt32();
                    if ((wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE) &&
                        Marshal.ReadInt32(m.LParam, 4) == DBT_DEVTYP_DEVICEINTERFACE)
                    {
                        string name = Marshal.PtrToStringUni(new IntPtr(m.LParam.ToInt64() + 28)) ?? "";
                        var h = MonitorDevice;
                        if (h != null) h(wp == DBT_DEVICEARRIVAL, name);
                    }
                }
                else if (m.Msg == WM_HOTKEY && hotkey.Matches(m.WParam))
                {
                    var h = HotkeyPressed;
                    if (h != null) h();
                }
            }
            catch (Exception ex) { Program.LogError("MessageWindow", ex); }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            hotkey.Unregister(Handle);
            if (deviceNotify != IntPtr.Zero) { UnregisterDeviceNotification(deviceNotify); deviceNotify = IntPtr.Zero; }
            DestroyHandle();
        }
    }

    // Describes the current mode of every monitor attached to the desktop.
    static class DisplayInfo
    {
        const int ENUM_CURRENT_SETTINGS = -1;
        const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode dm);

        public class Mode
        {
            public string Device;
            public string Monitor;
            public string MonitorId;
            public int Width;
            public int Height;
            public int Hz;
            public int Bits;
            public bool Primary;
            public int X;
            public int Y;
        }

        public static List<Mode> Modes()
        {
            var list = new List<Mode>();
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new DisplayDevice();
                    dd.cb = Marshal.SizeOf(typeof(DisplayDevice));
                    if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                    var dm = new DevMode();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
                    if (!EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;

                    var mon = new DisplayDevice();
                    mon.cb = Marshal.SizeOf(typeof(DisplayDevice));
                    string monName = "", monId = "";
                    if (EnumDisplayDevicesW(dd.DeviceName, 0, ref mon, 0))
                    {
                        monName = mon.DeviceString;
                        monId = MonitorModel(mon.DeviceID);
                    }

                    var mode = new Mode();
                    mode.Device = dd.DeviceName;
                    mode.Monitor = monName;
                    mode.MonitorId = monId;
                    mode.Width = dm.dmPelsWidth;
                    mode.Height = dm.dmPelsHeight;
                    mode.Hz = dm.dmDisplayFrequency;
                    mode.Bits = dm.dmBitsPerPel;
                    mode.Primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                    mode.X = dm.dmPositionX;
                    mode.Y = dm.dmPositionY;
                    list.Add(mode);
                }
            }
            catch (Exception ex) { Program.LogError("DisplayInfo", ex); }
            return list;
        }

        // "MONITOR\LEN65FE\{...}\0001" becomes "LEN65FE".
        static string MonitorModel(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            var parts = deviceId.Split('\\');
            return parts.Length > 1 ? parts[1] : deviceId;
        }

        public static List<string> Describe()
        {
            var result = new List<string>();
            foreach (var m in Modes())
            {
                result.Add(string.Format("{0} {1} [{2}] {3}x{4} @ {5} Hz{6}",
                    m.Device.Replace(@"\\.\", ""), m.Monitor, m.MonitorId, m.Width, m.Height, m.Hz,
                    m.Primary ? " (primary)" : ""));
            }
            return result;
        }

        public static string DescribeJoined()
        {
            return string.Join("; ", Describe().ToArray());
        }
    }
}
