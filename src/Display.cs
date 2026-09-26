// 按屏控制显示器。
// 两套机制，因为两类屏的能力完全不同（本机实测）：
//   1) 拓扑法（SetDisplayConfig）：把某块屏从显示配置里摘掉 → 该屏真的收不到信号（电视会显示"无信号"并待机），
//      恢复 = 把它的 path 重新置为 ACTIVE。通用、唤醒可靠，代价是桌面会重排、窗口挪回主屏。
//   2) DDC/CI（VCP 0xD6）：不动桌面布局，只让那块屏的面板自己熄灭。只有部分屏支持
//      （本机主屏支持，电视不支持），而且软关后能不能再命令它亮，取决于显示器关掉后是否还保留 DDC 通道。
//
// 两个踩过的坑，别再犯：
//   - 显示器**不能用 targetInfo.id 当身份**：同一块屏在"扩展"和"克隆"两种拓扑下 target id 会变
//     （实测电视扩展时 268，被克隆后变成 50331916）。要用设备路径里的 UID（= EDID+UID，屏不变它就不变）。
//   - 唤醒时**不能挑"已经被别的活跃屏占用的 source"**：那会变成克隆（两块屏显示同一画面，
//     而且帧率会被拉齐）。实测就是照 source id 从小到大取，结果取到克隆。
//     正确顺序：先试数据库里存的那条 path（用户原来的布局）→ 再试"空闲 source"的候选 → 最后整组扩展。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Disp
{
    const uint QDC_ALL_PATHS = 1;
    const uint QDC_DATABASE_CURRENT = 4;
    const uint PATH_ACTIVE = 0x1;

    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    const uint SDC_VALIDATE = 0x40;
    const uint SDC_APPLY = 0x80;
    const uint SDC_SAVE_TO_DATABASE = 0x200;
    const uint SDC_ALLOW_CHANGES = 0x400;
    const uint SDC_TOPOLOGY_EXTEND = 0x4;

    const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

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

    // 只声明到 union 里 SOURCE_MODE 的那几个字段（源模式 = 这块屏的桌面图像在虚拟桌面里的位置和大小）。
    // 桌面坐标原点 (0,0) 所在的那块屏就是 Windows 眼里的"主屏" —— 切主屏就是改这里的 posX/posY。
    // 后面的 TARGET_MODE 内容我不读，但结构总大小必须还是 64（sizeof(DISPLAYCONFIG_MODE_INFO)）。
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct MODE_INFO
    {
        public uint infoType;      // 1 = SOURCE, 2 = TARGET, 3 = DESKTOP_IMAGE
        public uint id;
        public LUID adapterId;
        public uint width;         // 以下仅 infoType==1 时有意义
        public uint height;
        public uint pixelFormat;
        public int posX;
        public int posY;
    }

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
    static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths, uint numMode, [In] MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, ref uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetVCPFeature(IntPtr hMon, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMon, byte code, IntPtr type, out uint cur, out uint max);

    public const byte VCP_POWER = 0xD6;
    public const uint VCP_POWER_ON = 0x01;
    public const uint VCP_POWER_OFF = 0x04;

    public class Mon
    {
        public uint Uid;                 // 设备路径里的 UID：屏不变它就不变，一切动作都认它
        public string Name = "";         // 显示器自报的型号名
        public string Dev = "";          // \\.\DISPLAYn（只在激活时有，克隆态会指向共享的那个源）
        public bool Active;
        public bool Clone;               // 激活了但和别的屏共用一个源
        public int Index = -1;           // 激活时对应 Screen.AllScreens 的下标；未激活 = -1
        public bool Primary;
        public int Left, Top, Right, Bottom;
        public uint Tech, HzNum, HzDen;
        public IntPtr Hmon = IntPtr.Zero;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public string Hz { get { return HzDen == 0 ? "" : (HzNum / HzDen) + "Hz"; } }
    }

    static List<Mon> _cache;
    static DateTime _cacheAt = DateTime.MinValue;

    public static string TechName(uint t)
    {
        switch (t)
        {
            case 0: return "VGA";
            case 4: return "DVI";
            case 5: return "HDMI";
            case 6: return "LVDS";
            case 10: return "DisplayPort";
            case 11: return "eDP";
            case 0x80000000: return "内置屏";
            default: return "接口" + t;
        }
    }

    static bool Query(uint flags, out PATH_INFO[] paths, out uint n, out MODE_INFO[] modes, out uint nm)
    {
        paths = null; modes = null; n = 0; nm = 0;
        uint np, nmc;
        if (GetDisplayConfigBufferSizes(flags, out np, out nmc) != 0) return false;
        if (np == 0) return false;
        var pa = new PATH_INFO[np];
        var ma = new MODE_INFO[nmc == 0 ? 1 : nmc];
        uint np2 = np, nm2 = nmc;
        if (QueryDisplayConfig(flags, ref np2, pa, ref nm2, ma, IntPtr.Zero) != 0) return false;
        paths = pa; n = np2; modes = ma; nm = nm2;
        return true;
    }

    static void Describe(PATH_INFO p, out uint uid, out string name, out string devPath)
    {
        uid = p.targetInfo.id; name = ""; devPath = "";
        var tn = new TARGET_DEVICE_NAME();
        tn.header.type = GET_TARGET_NAME;
        tn.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
        tn.header.adapterId = p.targetInfo.adapterId;
        tn.header.id = p.targetInfo.id;
        if (DisplayConfigGetDeviceInfo(ref tn) != 0) return;
        name = tn.monitorFriendlyDeviceName ?? "";
        devPath = tn.monitorDevicePath ?? "";
        // \\?\DISPLAY#SHP3884#7&2530182d&2&UID268#{...} -> 268
        int i = devPath.IndexOf("UID", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return;
        int j = i + 3, k = j;
        while (k < devPath.Length && devPath[k] >= '0' && devPath[k] <= '9') k++;
        uint v;
        if (k > j && uint.TryParse(devPath.Substring(j, k - j), out v)) uid = v;
    }

    static string GdiName(uint srcId, LUID adapter)
    {
        var sn = new SOURCE_DEVICE_NAME();
        sn.header.type = GET_SOURCE_NAME;
        sn.header.size = (uint)Marshal.SizeOf(typeof(SOURCE_DEVICE_NAME));
        sn.header.adapterId = adapter;
        sn.header.id = srcId;
        if (DisplayConfigGetDeviceInfo(ref sn) == 0) return sn.viewGdiDeviceName ?? "";
        return "";
    }

    static string SrcKey(PATH_INFO p) { return p.sourceInfo.adapterId.LowPart + ":" + p.sourceInfo.id; }

    // 列"物理上插着的"每一块屏：激活的 + 插着但当前没驱动的（比如刚被摘掉的电视）。
    // targetAvailable=0 的是驱动虚拟出来的组合，没用，跳过。
    public static List<Mon> All(bool refresh)
    {
        if (!refresh && _cache != null && (DateTime.Now - _cacheAt).TotalMilliseconds < 1200) return _cache;

        var list = new List<Mon>();
        var byUid = new Dictionary<uint, Mon>();
        var usedSrc = new Dictionary<string, int>();
        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm))
        {
            _cache = list; _cacheAt = DateTime.Now; return list;
        }

        // 先统计每个源被几块活跃屏占着：>1 就是克隆
        for (int i = 0; i < n; i++)
        {
            if ((paths[i].flags & PATH_ACTIVE) == 0 || paths[i].targetInfo.targetAvailable == 0) continue;
            string k = SrcKey(paths[i]);
            int c;
            usedSrc.TryGetValue(k, out c);
            usedSrc[k] = c + 1;
        }

        var screens = Screen.AllScreens;
        for (int i = 0; i < n; i++)
        {
            var p = paths[i];
            if (p.targetInfo.targetAvailable == 0) continue;
            uint uid; string nm2, dp;
            Describe(p, out uid, out nm2, out dp);
            bool act = (p.flags & PATH_ACTIVE) != 0;

            Mon m;
            if (!byUid.TryGetValue(uid, out m))
            {
                m = new Mon();
                m.Uid = uid; m.Name = nm2;
                byUid[uid] = m; list.Add(m);
            }
            if (!act || m.Active) continue;      // 有活跃 path 就够了，别被后面的非活跃 path 覆盖

            m.Active = true;
            m.Tech = p.targetInfo.outputTechnology;
            m.HzNum = p.targetInfo.refreshRate.Numerator;
            m.HzDen = p.targetInfo.refreshRate.Denominator;
            int cnt; usedSrc.TryGetValue(SrcKey(p), out cnt);
            m.Clone = cnt > 1;
            m.Dev = GdiName(p.sourceInfo.id, p.sourceInfo.adapterId);
            if (m.Clone) continue;               // 克隆态没有自己的桌面区域，别去匹配 Screen
            for (int s = 0; s < screens.Length; s++)
            {
                if (!string.Equals(screens[s].DeviceName, m.Dev, StringComparison.OrdinalIgnoreCase)) continue;
                m.Index = s;
                m.Primary = screens[s].Primary;
                m.Left = screens[s].Bounds.Left; m.Top = screens[s].Bounds.Top;
                m.Right = screens[s].Bounds.Right; m.Bottom = screens[s].Bounds.Bottom;
                var pt = new POINT();
                pt.X = m.Left + m.Width / 2; pt.Y = m.Top + m.Height / 2;
                m.Hmon = MonitorFromPoint(pt, 2);
                break;
            }
        }
        list.Sort(delegate (Mon a, Mon b)
        {
            if (a.Active != b.Active) return a.Active ? -1 : 1;
            if (a.Active && b.Active && a.Left != b.Left) return a.Left - b.Left;
            return a.Uid.CompareTo(b.Uid);
        });
        _cache = list; _cacheAt = DateTime.Now;
        return list;
    }

    public static Mon Find(uint uid)
    {
        foreach (var m in All(false)) if (m.Uid == uid) return m;
        return null;
    }

    // ---------- 拓扑法 ----------

    static bool Apply(List<PATH_INFO> keep, MODE_INFO[] modes, uint nm, out int lastErr)
    {
        var arr = keep.ToArray();
        // 先 VALIDATE：不合法就别去动真实显示配置，免得出现"关了就回不来"
        int e = SetDisplayConfig((uint)arr.Length, arr, nm, modes,
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_VALIDATE | SDC_ALLOW_CHANGES);
        if (e != 0) { lastErr = e; return false; }
        // 故意不带 SDC_SAVE_TO_DATABASE：要的是"暂时"无信号，重启就回来，别写进系统布局
        e = SetDisplayConfig((uint)arr.Length, arr, nm, modes,
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_ALLOW_CHANGES);
        lastErr = e;
        return e == 0;
    }

    /// <summary>回读校验：目标是否真的到了想要的状态，以及有没有意外变成克隆。</summary>
    static bool Verify(uint uid, bool wantActive, out string detail)
    {
        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        detail = "";
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm)) { detail = "回读失败"; return false; }
        var used = new Dictionary<string, int>();
        bool any = false, clone = false;
        for (int i = 0; i < n; i++)
        {
            if ((paths[i].flags & PATH_ACTIVE) == 0 || paths[i].targetInfo.targetAvailable == 0) continue;
            string k = SrcKey(paths[i]);
            int c; used.TryGetValue(k, out c);
            used[k] = c + 1;
            if (used[k] > 1) clone = true;
            uint u; string ns, ds;
            Describe(paths[i], out u, out ns, out ds);
            if (u == uid) any = true;
        }
        if (clone) { detail = "结果变成了克隆（两块屏共用一个源）"; return false; }
        if (any != wantActive) { detail = wantActive ? "没接上" : "没摘掉"; return false; }
        detail = wantActive ? "已激活" : "已无信号";
        return true;
    }

    static bool ReApply(List<PATH_INFO> list, MODE_INFO[] modes, uint nm)
    {
        if (list.Count == 0) return false;          // 一块屏都不剩就别应用，宁可维持现状
        var arr = list.ToArray();
        return SetDisplayConfig((uint)arr.Length, arr, nm, modes,
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_ALLOW_CHANGES) == 0;
    }

    /// <summary>on=false 摘掉这块屏（真无信号）；on=true 接回来（唤醒）。how 说明走的哪条路。</summary>
    public static bool SetActive(uint uid, bool on, out string how)
    {
        how = "";
        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm)) { how = "查询显示配置失败"; return false; }

        var others = new List<PATH_INFO>();       // 其余活跃 path：改配置时要原样带上
        var mine = new List<PATH_INFO>();         // 这块屏的活跃 path（可能不止一条：克隆时会重复）
        var cands = new List<PATH_INFO>();        // 这块屏的候选 non-active path
        var usedSrc = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            if ((paths[i].flags & PATH_ACTIVE) != 0 && paths[i].targetInfo.targetAvailable != 0)
            {
                string k = SrcKey(paths[i]);
                int c; usedSrc.TryGetValue(k, out c);
                usedSrc[k] = c + 1;
            }
        }
        for (int i = 0; i < n; i++)
        {
            var p = paths[i];
            uint u; string ns, ds;
            Describe(p, out u, out ns, out ds);
            bool isMine = u == uid && p.targetInfo.targetAvailable != 0;
            bool act = (p.flags & PATH_ACTIVE) != 0;
            if (isMine && act) mine.Add(p);
            else if (isMine) cands.Add(p);
            else if (act) others.Add(p);
        }
        if (mine.Count == 0 && cands.Count == 0)
        {
            how = "找不到这块屏（线没插好？或它已经被拔掉了）";
            return false;
        }

        if (!on)
        {
            if (mine.Count == 0) { how = "本来就是无信号状态"; return true; }
            // 尝试一：留在配置里但清掉 ACTIVE 位（官方推荐的"连着但不驱动"表达）
            // 尝试二：干脆整条移出配置
            var tryA = new List<PATH_INFO>(others);
            foreach (var p in mine) { var q = p; q.flags &= ~PATH_ACTIVE; tryA.Add(q); }
            int ea = -1, eb = -1;
            string det;
            if (Apply(tryA, modes, nm, out ea) && Verify(uid, false, out det)) { how = "已摘掉"; return true; }
            if (Apply(others, modes, nm, out eb) && Verify(uid, false, out det)) { how = "已摘掉(移出配置)"; return true; }
            // 两个都没成：把原来的活跃配置放回去，别留个半死不活的显示状态
            var back = new List<PATH_INFO>(others);
            back.AddRange(mine);
            ReApply(back, modes, nm);
            how = "失败了 err=" + ea + "/" + eb + "（已回滚）";
            return false;
        }

        if (mine.Count > 0 && !Find(uid).Clone) { how = "本来就亮着"; return true; }

        // 唤醒顺序：数据库里存的那条（用户原来的布局）> 空闲 source 的候选 > 整组扩展
        PATH_INFO[] dbPaths; MODE_INFO[] dbModes; uint dbN, dbNm;
        int edb = -1;
        if (Query(QDC_DATABASE_CURRENT, out dbPaths, out dbN, out dbModes, out dbNm))
        {
            var keep = new List<PATH_INFO>();
            bool found = false;
            for (int i = 0; i < dbN; i++)
            {
                uint u; string ns, ds;
                Describe(dbPaths[i], out u, out ns, out ds);
                var q = dbPaths[i];
                if (u == uid) { q.flags |= PATH_ACTIVE; found = true; }
                keep.Add(q);
            }
            if (found)
            {
                var arr = keep.ToArray();
                edb = SetDisplayConfig((uint)arr.Length, arr, dbNm, dbModes,
                    SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_VALIDATE | SDC_ALLOW_CHANGES);
                if (edb == 0)
                    edb = SetDisplayConfig((uint)arr.Length, arr, dbNm, dbModes,
                        SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_ALLOW_CHANGES);
                string det;
                if (edb == 0 && Verify(uid, true, out det)) { how = "已接回(按原布局)"; return true; }
            }
            else edb = -2;
        }

        // 候选里挑"source 没被别的活跃屏占用"的（占用 = 会变克隆），逐个试
        int last = 0;
        foreach (var c in cands)
        {
            int cnt; usedSrc.TryGetValue(SrcKey(c), out cnt);
            if (cnt > 0) continue;
            var l = new List<PATH_INFO>(others);
            var cc = c; cc.flags |= PATH_ACTIVE;
            l.Add(cc);
            int e;
            if (Apply(l, modes, nm, out e))
            {
                string det;
                if (Verify(uid, true, out det)) { how = "已接回(空源 src" + c.sourceInfo.id + ")"; return true; }
            }
            last = e;
        }

        int et = SetDisplayConfig(0, null, 0, null, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
        if (et == 0)
        {
            string det;
            if (Verify(uid, true, out det)) { how = "已接回(整组扩展)"; return true; }
        }
        how = "没接上 err=" + edb + "/" + last + "/ext" + et;
        return false;
    }

    // ---------- 主屏切换 / 一键"只留这块屏" ----------

    static List<PATH_INFO> ActivePaths(PATH_INFO[] paths, uint n)
    {
        var l = new List<PATH_INFO>();
        for (int i = 0; i < n; i++) if ((paths[i].flags & PATH_ACTIVE) != 0) l.Add(paths[i]);
        return l;
    }

    // 撤销用的快照：动显示配置之前先存一份"当时活着的 path + 完整 mode 表"
    static PATH_INFO[] _snapPaths;
    static MODE_INFO[] _snapModes;
    static uint _snapNm;

    static void SaveSnapshot(List<PATH_INFO> paths, MODE_INFO[] modes, uint nm)
    {
        _snapPaths = paths.ToArray();
        _snapModes = (MODE_INFO[])modes.Clone();
        _snapNm = nm;
    }

    public static bool Undo(out string how)
    {
        how = "";
        if (_snapPaths == null) { how = "没有可撤销的上一步"; return false; }
        int e;
        if (Apply(new List<PATH_INFO>(_snapPaths), _snapModes, _snapNm, out e))
        {
            how = "已回到上一步的显示配置";
            _snapPaths = null;
            return true;
        }
        how = "撤销失败 err=" + e;
        return false;
    }

    static int SourceModeIndexOf(MODE_INFO[] modes, uint nm, PATH_INFO p)
    {
        for (int i = 0; i < nm; i++)
            if (modes[i].infoType == 1 && modes[i].id == p.sourceInfo.id
                && modes[i].adapterId.LowPart == p.sourceInfo.adapterId.LowPart) return i;
        return -1;
    }

    static bool VerifyPrimary(uint uid, out string detail)
    {
        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        detail = "";
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm)) { detail = "回读失败"; return false; }
        for (int i = 0; i < n; i++)
        {
            if ((paths[i].flags & PATH_ACTIVE) == 0) continue;
            uint u; string ns, ds;
            Describe(paths[i], out u, out ns, out ds);
            if (u != uid) continue;
            int mi = SourceModeIndexOf(modes, nm, paths[i]);
            if (mi < 0) { detail = "读不到它的桌面位置"; return false; }
            bool ok = modes[mi].posX == 0 && modes[mi].posY == 0;
            detail = ok ? "已在原点" : ("位置是 " + modes[mi].posX + "," + modes[mi].posY);
            return ok;
        }
        detail = "它当前没在工作";
        return false;
    }

    /// <summary>把某块屏变主屏（任务栏、新窗口、全屏游戏的默认落点都跟着走）。只改桌面坐标原点，不关任何屏。</summary>
    public static bool SetPrimary(uint uid, out string how)
    {
        how = "";
        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm)) { how = "查询显示配置失败"; return false; }

        int tgt = -1;
        for (int i = 0; i < n; i++)
        {
            if ((paths[i].flags & PATH_ACTIVE) == 0) continue;
            uint u; string ns, ds;
            Describe(paths[i], out u, out ns, out ds);
            if (u == uid) { tgt = i; break; }
        }
        if (tgt < 0) { how = "这块屏当前没在工作（先唤醒它）"; return false; }

        int tgtMode = SourceModeIndexOf(modes, nm, paths[tgt]);
        if (tgtMode < 0) { how = "找不到这块屏的桌面模式"; return false; }
        if (modes[tgtMode].posX == 0 && modes[tgtMode].posY == 0) { how = "它本来就是主屏"; return true; }

        var active = ActivePaths(paths, n);
        SaveSnapshot(active, modes, nm);          // 先存快照，任何情况下都能 Undo 回来

        var newModes = (MODE_INFO[])modes.Clone();
        int w = (int)newModes[tgtMode].width, h = (int)newModes[tgtMode].height;
        if (w <= 0 || h <= 0) { how = "它的桌面尺寸读不出来，不敢动"; return false; }
        int oldX = newModes[tgtMode].posX, oldY = newModes[tgtMode].posY;
        newModes[tgtMode].posX = 0; newModes[tgtMode].posY = 0;

        // 其余活着的屏顺着原来的左右次序排到右边，保证不重叠（原点那一块必须是唯一主屏）
        var rest = new List<int>();
        foreach (var p in active)
        {
            int mi = SourceModeIndexOf(modes, nm, p);
            if (mi >= 0 && mi != tgtMode) rest.Add(mi);
        }
        rest.Sort(delegate (int a, int b) { return newModes[a].posX.CompareTo(newModes[b].posX); });
        int cursor = w;
        foreach (int mi in rest) { newModes[mi].posX = cursor; newModes[mi].posY = 0; cursor += (int)newModes[mi].width; }

        int e;
        if (Apply(active, newModes, nm, out e))
        {
            string det;
            if (VerifyPrimary(uid, out det)) { how = "已把主屏换成它，其余屏排到它右边"; return true; }
            // 应用成功但结果不对：退回原样，别留个怪配置
            int e2; Apply(active, modes, nm, out e2);
            how = "切过去之后回读不对（" + det + "），已退回";
            return false;
        }
        int e3; Apply(active, modes, nm, out e3);
        how = "没切成功 err=" + e + "，已保持原样";
        return false;
    }

    /// <summary>一键"只用这块屏"：先把它叫起来，再切断其他所有屏的信号。电脑会给剩下的这块自动升成主屏，
    /// 没升就显式切一次。这是他躺在床上/坐回桌前的那个开关。</summary>
    public static bool OnlyThis(uint uid, out string how)
    {
        how = "";
        var list = All(true);
        var target = Find(uid);
        if (target == null) { how = "没有这块屏（线没插好？）"; return false; }

        var sb = new StringBuilder();
        if (!target.Active)
        {
            string h;
            if (!SetActive(uid, true, out h)) { how = "先把这块屏叫起来就失败了：" + h; return false; }
            Thread.Sleep(1200);
            list = All(true);
            sb.Append("先把它唤醒；");
        }

        PATH_INFO[] paths; MODE_INFO[] modes; uint n, nm;
        if (!Query(QDC_ALL_PATHS, out paths, out n, out modes, out nm)) { how = "查询显示配置失败"; return false; }
        SaveSnapshot(ActivePaths(paths, n), modes, nm);

        int off = 0; var fails = new List<string>();
        foreach (var m in list)
        {
            if (m.Uid == uid || !m.Active) continue;
            string h;
            if (SetActive(m.Uid, false, out h)) off++;
            else fails.Add(m.Name + "(" + h + ")");
        }

        string det;
        bool prim = VerifyPrimary(uid, out det);
        if (!prim)
        {
            // 自己关掉主屏时 Windows 通常会把剩下的自动升为主屏；没升就手动切
            if (SetPrimary(uid, out det)) sb.Append("并显式切了主屏；");
            else sb.Append("主屏没切过去（").Append(det).Append("）；");
        }
        if (fails.Count > 0) sb.Append("有 ").Append(fails.Count).Append(" 块没关掉：").Append(string.Join("、", fails.ToArray()));
        how = sb.ToString() + "现在只剩这块屏" + (prim ? "（它就是主屏）" : "");
        return true;
    }

    /// <summary>
    /// 轮换：不指定是哪块屏，就是"下一块"。两块屏的时候是花架子，三块四块才看得出用。
    /// 环按 uid 排（稳定，跟激活与否无关），起点是当前的主屏。
    /// onlyOne=true → 只留下一块屏（切主屏 + 关掉其他）；false → 只把主屏交给下一块，一块都不关。
    /// </summary>
    public static bool Cycle(bool onlyOne, out string how)
    {
        how = "";
        var ring = new List<uint>();
        var list = All(true);
        foreach (var m in list) ring.Add(m.Uid);
        ring.Sort();
        if (ring.Count < 2) { how = "只有一块屏，没什么可轮换的"; return false; }

        uint cur = 0;
        foreach (var m in list) { if (m.Active && m.Primary) { cur = m.Uid; break; } }
        if (cur == 0) foreach (var m in list) { if (m.Active) { cur = m.Uid; break; } }
        int idx = ring.IndexOf(cur);
        if (idx < 0) idx = 0;
        uint nxt = ring[(idx + 1) % ring.Count];
        string nm = "";
        foreach (var m in list) if (m.Uid == nxt) nm = m.Name;
        how = "从 " + cur + " 轮到 " + nxt + (nm.Length > 0 ? "（" + nm + "）" : "") + "：";
        string r;
        bool ok = onlyOne ? OnlyThis(nxt, out r) : SetPrimary(nxt, out r);
        how += r;
        return ok;
    }

    // ---------- DDC/CI ----------

    // 打开物理屏句柄干一件事，干完**一定**销毁。
    // 别漏掉 DestroyPhysicalMonitors：漏掉的句柄会攒着，攒几次之后这个进程里所有 DDC 调用都开始失败
    // （实测：一开始读得到亮度，几次之后连 0x10 都读不到；而另开一个新进程一读就正常 —— 就是这么被坑的）。
    static bool WithPhysical(Mon m, Func<IntPtr, bool> fn)
    {
        if (m == null || m.Hmon == IntPtr.Zero) return false;
        uint c = 0;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m.Hmon, ref c) || c == 0) return false;
        var arr = new PHYSICAL_MONITOR[c];
        if (!GetPhysicalMonitorsFromHMONITOR(m.Hmon, c, arr)) return false;
        bool r = false;
        try { r = fn(arr[0].hPhysicalMonitor); }
        catch { }
        finally { try { DestroyPhysicalMonitors(c, arr); } catch { } }
        return r;
    }

    // 探测一次记 30 秒：0x10（亮度）读得回来就认为这条 DDC 通道通。
    // 读通一次就永久记住通（面板偶尔一次 I2C 超时不该让手机上的按钮忽隐忽现）。
    static readonly Dictionary<uint, bool> _ddcCache = new Dictionary<uint, bool>();
    static readonly Dictionary<uint, DateTime> _ddcAt = new Dictionary<uint, DateTime>();

    public static bool DdcSupported(Mon m)
    {
        if (m == null || !m.Active || m.Clone) return false;
        lock (_ddcCache)
        {
            bool known;
            if (_ddcCache.TryGetValue(m.Uid, out known))
            {
                if (known) return true;
                DateTime at;
                if (_ddcAt.TryGetValue(m.Uid, out at) && (DateTime.Now - at).TotalSeconds < 30) return false;
            }
        }
        // 这块屏（ESWIN 面板）的 DDC 读大约 10% 会随机失败（实测 6/6 vs 5/6 vs 3/3 三种场景都出现过失败），
        // 所以单次失败不算数：重试 3 次，全失败才判"不支持"。否则手机上的按钮会忽隐忽现。
        bool ok = false;
        for (int i = 0; i < 3 && !ok; i++)
        {
            if (i > 0) Thread.Sleep(120);
            ok = WithPhysical(m, delegate(IntPtr h)
            {
                uint cur, max;
                return GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out cur, out max);
            });
        }
        lock (_ddcCache) { _ddcCache[m.Uid] = ok; _ddcAt[m.Uid] = DateTime.Now; }
        return ok;
    }

    // 诊断用："为什么我这块屏控制不了"——把 DDC 每一步的返回值原样吐出来
    public static string DdcDebug(uint uid)
    {
        var m = Find(uid);
        if (m == null) return "找不到这块屏 uid=" + uid;
        var sb = new StringBuilder();
        sb.Append("uid=").Append(m.Uid).Append(" name=\"").Append(m.Name).Append("\" active=").Append(m.Active)
          .Append(" clone=").Append(m.Clone).Append(" hmon=").Append(m.Hmon);
        if (m.Hmon == IntPtr.Zero) return sb.ToString();
        uint c = 0;
        bool got = GetNumberOfPhysicalMonitorsFromHMONITOR(m.Hmon, ref c);
        int e1 = Marshal.GetLastWin32Error();
        sb.Append(" | GetNum ok=").Append(got).Append(" count=").Append(c).Append(" err=").Append(e1);
        if (!got || c == 0) return sb.ToString();
        var arr = new PHYSICAL_MONITOR[c];
        bool got2 = GetPhysicalMonitorsFromHMONITOR(m.Hmon, c, arr);
        int e2 = Marshal.GetLastWin32Error();
        sb.Append(" | GetPhys ok=").Append(got2).Append(" err=").Append(e2);
        if (!got2) return sb.ToString();
        sb.Append(" | desc=\"").Append(arr[0].szPhysicalMonitorDescription).Append("\" handle=").Append(arr[0].hPhysicalMonitor);
        uint cur, max;
        bool r = GetVCPFeatureAndVCPFeatureReply(arr[0].hPhysicalMonitor, 0x10, IntPtr.Zero, out cur, out max);
        sb.Append(" | VCP0x10 ok=").Append(r).Append(" cur=").Append(cur).Append(" max=").Append(max)
          .Append(" err=").Append(Marshal.GetLastWin32Error());
        uint pc, pm;
        bool r2 = GetVCPFeatureAndVCPFeatureReply(arr[0].hPhysicalMonitor, VCP_POWER, IntPtr.Zero, out pc, out pm);
        sb.Append(" | VCP0xD6读 ok=").Append(r2).Append(" cur=").Append(pc).Append(" err=").Append(Marshal.GetLastWin32Error());
        DestroyPhysicalMonitors(c, arr);
        return sb.ToString();
    }

    // 面板还活着吗？0x10 能读回来就算活着（不走缓存，专门用来判定"叫不叫得醒"）
    public static bool DdcAlive(Mon m)
    {
        if (m == null) return false;
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) Thread.Sleep(150);
            if (WithPhysical(m, delegate(IntPtr h)
            {
                uint cur, max;
                return GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out cur, out max);
            })) return true;
        }
        return false;
    }

    // 软件版"拔数据线重插"：把这块屏摘掉再立刻接回，强制重新握一次手（HPD + 重新协商模式）。
    // 面板软关之后连 DDC 都不应答时，这是唯一不需要人起身的救命手段。
    public static bool Rescue(uint uid, out string how)
    {
        string h1, h2;
        bool a = SetActive(uid, false, out h1);
        Thread.Sleep(1200);
        bool b = SetActive(uid, true, out h2);
        how = "摘掉" + (a ? "成功" : "失败") + " → 接回" + (b ? "成功" : "失败") + "（" + h2 + "）";
        return a && b;
    }

    public static bool DdcPower(uint uid, bool on, out string info)
    {
        info = "";
        var m = Find(uid);
        if (m == null) { info = "没有这块屏"; return false; }
        if (!m.Active) { info = "这块屏当前无信号，DDC 通道也一起没了，只能用拓扑法接回来"; return false; }
        if (m.Clone) { info = "这块屏现在是克隆态，先接回它自己的源再谈 DDC"; return false; }

        // 写完立刻回读：SetVCPFeature 成功只说明"命令发出去了"，回读值才是面板到底听没听
        uint before = 0, after = 0;
        bool rb = false, ra = false, wrote = false;
        WithPhysical(m, delegate(IntPtr h)
        {
            uint bm, am;
            rb = GetVCPFeatureAndVCPFeatureReply(h, VCP_POWER, IntPtr.Zero, out before, out bm);
            wrote = SetVCPFeature(h, VCP_POWER, on ? VCP_POWER_ON : VCP_POWER_OFF);
            if (wrote) ra = GetVCPFeatureAndVCPFeatureReply(h, VCP_POWER, IntPtr.Zero, out after, out am);
            return true;
        });
        if (!wrote)
        {
            info = "SetVCPFeature 写不进去（面板可能不支持写，只支持读）";
            return false;
        }
        info = "写 0xD6=" + (on ? "01" : "04") + " 成功，回读 前=" + (rb ? "0x" + before.ToString("X2") : "读不到") +
               " 后=" + (ra ? "0x" + after.ToString("X2") : "读不到") + "（面板不回读不代表没执行）";
        return true;
    }
}
