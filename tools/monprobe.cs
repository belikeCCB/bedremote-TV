// monprobe - 只读侦察工具（开发用，不随发布包跑）：
//   1) QueryDisplayConfig 列出所有 path，包括"接口在、但当前没启用"的屏（比如没接信号或刚被关掉的电视）
//   2) 每块屏的 GDI 名、显示器友好名、输出接口类型、分辨率、主/副
//   3) 对每块活跃屏读 DDC/CI：物理屏数量 + VCP 0x10/0x12/0x60/0xD6 + capabilities 字符串
// 只读，不改任何显示状态。用它回答"这台机到底有几块屏、每块支不支持单独熄灭"。
//
// 编译：csc.exe -nologo -target:exe -platform:x64 -r:System.Windows.Forms.dll -out:monprobe.exe tools\monprobe.cs
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class MonProbe
{
    const uint QDC_ALL_PATHS = 1;
    const uint PATH_ACTIVE = 0x1;
    const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;

    // ---- 显示配置（QueryDisplayConfig）用的结构，字段顺序/大小必须和 wingdi.h 一致 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }

    // 只当缓冲区用，不读内容：64 是 x64/x86 下 sizeof(DISPLAYCONFIG_MODE_INFO)
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags; public uint outputTechnology;
        public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths,
        ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME info);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, ref uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMon, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetCapabilitiesStringLength(IntPtr hMon, ref uint len);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMon, [Out] StringBuilder sb, uint len);

    static string Tech(uint t)
    {
        switch (t)
        {
            case 0: return "VGA";
            case 4: return "DVI";
            case 5: return "HDMI";
            case 6: return "LVDS";
            case 10: return "DisplayPort(external)";
            case 11: return "DisplayPort(embedded)";
            case 0x80000000: return "internal";
            default: return "tech=" + t;
        }
    }

    static int Main()
    {
        Console.WriteLine("=== struct sizes ===");
        Console.WriteLine("PATH_INFO=" + Marshal.SizeOf(typeof(PATH_INFO)) + " (expect 72)  MODE_INFO=" +
            Marshal.SizeOf(typeof(MODE_INFO)) + " (expect 64)  TARGET_DEVICE_NAME=" +
            Marshal.SizeOf(typeof(TARGET_DEVICE_NAME)) + " (expect 420)");

        uint np, nm;
        int err = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out np, out nm);
        Console.WriteLine("\n=== buffers: paths=" + np + " modes=" + nm + " (err=" + err + ") ===");
        if (err != 0) { Console.WriteLine("GetDisplayConfigBufferSizes failed " + err); return 1; }

        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        uint np2 = np, nm2 = nm;
        err = QueryDisplayConfig(QDC_ALL_PATHS, ref np2, paths, ref nm2, modes, IntPtr.Zero);
        Console.WriteLine("QueryDisplayConfig err=" + err + " paths=" + np2 + " modes=" + nm2);
        if (err != 0) return 1;

        for (int i = 0; i < np2; i++)
        {
            bool active = (paths[i].flags & PATH_ACTIVE) != 0;
            var sn = new SOURCE_DEVICE_NAME();
            sn.header.type = GET_SOURCE_NAME;
            sn.header.size = (uint)Marshal.SizeOf(typeof(SOURCE_DEVICE_NAME));
            sn.header.adapterId = paths[i].sourceInfo.adapterId;
            sn.header.id = paths[i].sourceInfo.id;
            int se = DisplayConfigGetDeviceInfo(ref sn);

            var tn = new TARGET_DEVICE_NAME();
            tn.header.type = GET_TARGET_NAME;
            tn.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
            tn.header.adapterId = paths[i].targetInfo.adapterId;
            tn.header.id = paths[i].targetInfo.id;
            int te = DisplayConfigGetDeviceInfo(ref tn);

            Console.WriteLine("\n--- path " + i + " ---");
            Console.WriteLine("  active=" + active + " targetAvailable=" + paths[i].targetInfo.targetAvailable +
                " tech=" + Tech(paths[i].targetInfo.outputTechnology) +
                " refresh=" + paths[i].targetInfo.refreshRate.Numerator + "/" + paths[i].targetInfo.refreshRate.Denominator);
            Console.WriteLine("  gdiName=" + (se == 0 ? sn.viewGdiDeviceName : "(err " + se + ")"));
            Console.WriteLine("  targetId=" + paths[i].targetInfo.id + "  name=" + (te == 0 ? tn.monitorFriendlyDeviceName : "(err " + te + ")"));
            if (te == 0) Console.WriteLine("  devicePath=" + tn.monitorDevicePath);
        }

        Console.WriteLine("\n=== .NET 看到的屏（Screen.AllScreens）===");
        var screens = Screen.AllScreens;
        foreach (var sc in screens)
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            var pt = new POINT();
            pt.X = sc.Bounds.Left + sc.Bounds.Width / 2;
            pt.Y = sc.Bounds.Top + sc.Bounds.Height / 2;
            IntPtr hm = MonitorFromPoint(pt, 2 /*NEAREST*/);
            bool ok = GetMonitorInfo(hm, ref mi);
            Console.WriteLine("\n--- screen " + sc.DeviceName + " primary=" + sc.Primary + " bounds=" +
                sc.Bounds.Left + "," + sc.Bounds.Top + "," + sc.Bounds.Width + "x" + sc.Bounds.Height +
                " hmon=" + hm + " infoOK=" + ok + " szDevice=" + (ok ? mi.szDevice : "?"));

            uint pc = 0;
            bool got = GetNumberOfPhysicalMonitorsFromHMONITOR(hm, ref pc);
            Console.WriteLine("  physical monitors: got=" + got + " count=" + pc + " lastErr=" + Marshal.GetLastWin32Error());
            if (!got || pc == 0) continue;

            var pm = new PHYSICAL_MONITOR[pc];
            if (!GetPhysicalMonitorsFromHMONITOR(hm, pc, pm))
            {
                Console.WriteLine("  GetPhysicalMonitorsFromHMONITOR failed lastErr=" + Marshal.GetLastWin32Error());
                continue;
            }
            for (int i = 0; i < pc; i++)
            {
                Console.WriteLine("  phys[" + i + "] desc=\"" + pm[i].szPhysicalMonitorDescription + "\" handle=" + pm[i].hPhysicalMonitor);
                byte[] codes = new byte[] { 0x10, 0x12, 0x60, 0xD6, 0xDF };
                string[] labels = new string[] { "brightness", "contrast", "input-source", "power-mode(DPMS)", "power-state" };
                for (int c = 0; c < codes.Length; c++)
                {
                    uint cur, max;
                    bool v = GetVCPFeatureAndVCPFeatureReply(pm[i].hPhysicalMonitor, codes[c], IntPtr.Zero, out cur, out max);
                    Console.WriteLine("    VCP 0x" + codes[c].ToString("X2") + " " + labels[c] + ": ok=" + v +
                        (v ? " cur=" + cur + " max=" + max : " lastErr=" + Marshal.GetLastWin32Error()));
                }
                uint clen = 0;
                if (GetCapabilitiesStringLength(pm[i].hPhysicalMonitor, ref clen) && clen > 0 && clen < 4096)
                {
                    var sb = new StringBuilder((int)clen);
                    if (CapabilitiesRequestAndCapabilitiesReply(pm[i].hPhysicalMonitor, sb, clen))
                    {
                        string caps = sb.ToString();
                        Console.WriteLine("    capabilities(" + clen + "): " + (caps.Length > 400 ? caps.Substring(0, 400) + "..." : caps));
                    }
                    else Console.WriteLine("    capabilities reply failed lastErr=" + Marshal.GetLastWin32Error());
                }
                else Console.WriteLine("    GetCapabilitiesStringLength failed/len=" + clen + " lastErr=" + Marshal.GetLastWin32Error());
            }
            // 一定要销毁：漏掉的物理屏句柄会攒着，之后这个进程里的 DDC 调用会一个个开始失败
            DestroyPhysicalMonitors(pc, pm);
            DestroyPhysicalMonitors(pc, pm);
        }
        return 0;
    }
}
