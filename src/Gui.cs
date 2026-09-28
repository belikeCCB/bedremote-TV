// 图形界面：一个窗口 + 一个托盘图标，把"起服务 / 停服务 / 手机地址 / 配对二维码 /
// 谁在控制这台电脑 / 每块屏的开关 / 开机自启 / 日志"全部收在一起。
//
// 为什么不做成套的 .cmd 快捷方式：那对"躺在床上的用户"是负担（四个图标 + 黑窗口）。
// 一个 exe 既是服务也是界面：服务在后台线程里跑，界面直接读同进程的状态，不用自己跟自己开 HTTP。
//
// 命令行模式仍然保留：带任何参数（--console / --port / --token）就是原来的无界面服务模式，
// 脚本、SSH、开机自启那些用法不受影响。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Gui
{
    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string RunValue = "bedremote";

    static Form f;
    static NotifyIcon tray;
    static Label status, ver, addr;
    static Button btnStartStop, btnFirewall, btnTake;
    static CheckBox chkBoot, chkAwake, chkLock, chkHttps, chkCa;
    static ListView peers;
    static GroupBox gPeers;
    static Panel screens;
    static ComboBox wake;
    static TextBox logBox;
    static Label passInfo;
    static ComboBox elevCombo;
    static TextBox nameBox;
    static Label matesInfo;
    static string shownName = "";
    static int passTick = 99;
    static System.Windows.Forms.Timer tm;
    static bool loading = true;           // 搭界面时的赋值不该触发"改配置"，见各 CheckedChanged
    static readonly Queue<string> q = new Queue<string>();
    static string sigScreens = "", sigAddr = "", sigIps = "";
    static bool exiting, ballooned, hinted;

    [STAThread]
    public static int Run()
    {
        DropOrKeepConsole();              // 双击进来的是界面，不是黑窗口
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Build();
        Program.AddSink(OnLog);
        Program.InGui = true;
        Program.StartInBackground();          // 端口被占会置 StartError=3，界面如实显示"已在别处运行"
        if (Program.StartHidden) HideToTray();
        if (Program.OpenWizard)
        {
            // 等窗口先画出来再弹向导（ShowDialog 会起自己的模态循环，太早叫它主窗口还没影）
            var t0 = new System.Windows.Forms.Timer();
            t0.Interval = 500;
            t0.Tick += delegate { try { t0.Stop(); t0.Dispose(); } catch { } Wizard.Show(f); };
            t0.Start();
        }
        Application.Run(f);
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        return 0;
    }

    // 图形模式不该带一个控制台黑框。但也不能把"从他自己的终端里启动"的那个终端一起藏了。
    // 做法：先脱离当前控制台，再试"挂到父进程的控制台"。
    //   挂得上 = 那个框本来就是他的 cmd/PowerShell，原样留着，日志照样往里打；
    //   挂不上 = 双击启动、那个框是系统为我们新建的，FreeConsole 已经让它消失了。
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int nCmdShow);
    const int ATTACH_PARENT = -1;

    static void DropOrKeepConsole()
    {
        try
        {
            IntPtr h = GetConsoleWindow();
            if (h == IntPtr.Zero) return;             // 本来就没有控制台
            FreeConsole();
            if (AttachConsole(ATTACH_PARENT)) { Console.Out.Flush(); return; }
            ShowWindow(h, 0);
        }
        catch { }
    }

    // ---------- 搭界面 ----------
    static void Build()
    {
        f = new Form();
        f.Text = "bedremote · 床上遥控";
        f.ClientSize = new Size(800, 862);
        f.MinimumSize = new Size(720, 640);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Font = new Font("Microsoft YaHei UI", 9f);
        try { f.Icon = AppIcon(); } catch { }
        f.FormClosing += OnClosing;

        status = new Label();
        status.Text = "正在启动…";
        status.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        status.Location = new Point(14, 12);
        status.AutoSize = true;
        f.Controls.Add(status);

        ver = new Label();
        ver.Text = Program.Version;
        ver.ForeColor = Color.Gray;
        ver.Location = new Point(240, 20);
        ver.AutoSize = true;
        f.Controls.Add(ver);

        btnStartStop = new Button();
        btnStartStop.Text = "停止服务";
        btnStartStop.Size = new Size(110, 32);
        btnStartStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnStartStop.Location = new Point(f.ClientSize.Width - 124, 10);
        btnStartStop.Click += delegate { ToggleService(); };
        f.Controls.Add(btnStartStop);

        addr = new Label();
        addr.Font = new Font("Consolas", 11f);
        addr.Location = new Point(14, 44);
        addr.AutoSize = true;
        f.Controls.Add(addr);

        int bx = 14;
        bx = AddToolButton(bx, "往手机上加个软件", delegate { Wizard.Show(f); });
        bx = AddToolButton(bx, "复制地址", delegate { CopyAddr(); });
        bx = AddToolButton(bx, "配对二维码（全屏）", delegate { OpenUrl(LocalUrl("pair")); });
        bx = AddToolButton(bx, "手机页（本机）", delegate { OpenUrl(LocalUrl("")); });
        bx = AddToolButton(bx, "改按钮（编辑器）", delegate { OpenUrl(LocalUrl("edit")); });
        btnFirewall = new Button();
        btnFirewall.Text = "防火墙放行";
        btnFirewall.Size = new Size(110, 28);
        btnFirewall.Location = new Point(bx, 70);
        btnFirewall.Click += delegate { Firewall(); };
        f.Controls.Add(btnFirewall);
        // 端口被别的 bedremote（多半是旧的命令行实例）占着时才出现：一键交棒，不用他去任务管理器
        btnTake = new Button();
        btnTake.Text = "接管端口（关掉旧实例）";
        btnTake.Size = new Size(170, 28);
        btnTake.Location = new Point(bx + 118, 70);
        btnTake.ForeColor = Color.Firebrick;
        btnTake.Visible = false;
        btnTake.Click += delegate { Takeover(); };
        f.Controls.Add(btnTake);

        gPeers = new GroupBox();
        gPeers.Text = "正在控制这台电脑的设备";
        gPeers.Location = new Point(14, 106);
        gPeers.Size = new Size(386, 196);
        gPeers.Anchor = AnchorStyles.Top | AnchorStyles.Left;                 // 不挂 Bottom：只有日志框跟着窗口长高
        peers = new ListView();
        peers.View = View.Details;
        peers.FullRowSelect = true;
        peers.GridLines = true;
        peers.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        peers.Location = new Point(10, 20);
        peers.Size = new Size(366, 166);
        peers.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        peers.Columns.Add("IP", 120);
        peers.Columns.Add("状态", 96);
        peers.Columns.Add("指令", 46);
        peers.Columns.Add("最近", 82);
        gPeers.Controls.Add(peers);
        f.Controls.Add(gPeers);

        var gScr = new GroupBox();
        gScr.Text = "显示器";
        gScr.Location = new Point(410, 106);
        gScr.Size = new Size(376, 336);
        gScr.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;     // 只跟着变宽，不变高
        screens = new Panel();
        screens.Location = new Point(10, 20);
        screens.Size = new Size(356, 240);
        screens.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        screens.AutoScroll = true;
        gScr.Controls.Add(screens);

        int sx = 10;
        sx = AddScreenButton(gScr, sx, 268, "黑屏一下", delegate { W32.MonitorOff(); Log2("已让所有屏进待机 —— 动一下鼠标就回来（这个不会把窗口搬走）"); });
        sx = AddScreenButton(gScr, sx, 268, "撤销上一步", delegate { string h; bool ok = Disp.Undo(out h); Log2((ok ? "撤销：" : "撤销失败：") + h); });
        AddScreenButton(gScr, sx, 268, "恢复系统配置", delegate { string h; bool ok = Disp.Recover(out h); Log2((ok ? "恢复：" : "恢复失败：") + h); });

        var awLabel = new Label();
        awLabel.Text = "断掉的屏";
        awLabel.Location = new Point(10, 306);
        awLabel.AutoSize = true;
        awLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        gScr.Controls.Add(awLabel);
        wake = new ComboBox();
        wake.DropDownStyle = ComboBoxStyle.DropDownList;
        wake.Location = new Point(72, 302);
        wake.Size = new Size(170, 24);
        wake.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        wake.Items.AddRange(new object[] { "不自动接回（推荐）", "20 秒后自动接回", "60 秒后自动接回", "120 秒后自动接回" });
        wake.SelectedIndexChanged += delegate
        {
            if (loading) return;
            string err; int v = WakeSecOf(wake.SelectedIndex);
            if (!Config.SetNum("autoWakeSec", v, out err)) { Log2("改自动接回失败：" + err); RestoreWakeChoice(); }
            else Log2("断掉的屏：" + (v == 0 ? "不自动接回" : v + " 秒后自动接回") + "（已写进 bedremote.json）");
        };
        gScr.Controls.Add(wake);
        AddScreenButton(gScr, 250, 300, "修刷新率", delegate { Disp.FixRefreshOnly(); Log2("已按各屏最高档修过刷新率"); });
        f.Controls.Add(gScr);

        var gOpt = new GroupBox();
        gOpt.Text = "选项";
        gOpt.Location = new Point(14, 310);
        gOpt.Size = new Size(386, 132);
        gOpt.Anchor = AnchorStyles.Top | AnchorStyles.Left;                     // 挂 Bottom 会把这组撑短，下面几行直接被裁掉（就是这个 bug）
        chkBoot = new CheckBox();
        chkBoot.Text = "开机自动启动";
        chkBoot.Location = new Point(12, 22);
        chkBoot.AutoSize = true;
        chkBoot.CheckedChanged += delegate { if (!loading) SetBoot(chkBoot.Checked); };
        gOpt.Controls.Add(chkBoot);
        chkAwake = new CheckBox();
        chkAwake.Text = "防止电脑睡眠";
        chkAwake.Location = new Point(12, 48);
        chkAwake.AutoSize = true;
        chkAwake.CheckedChanged += delegate { if (loading) return; Program.SetAwake(chkAwake.Checked); Log2("防睡眠：" + (chkAwake.Checked ? "开" : "关")); };
        gOpt.Controls.Add(chkAwake);
        chkLock = new CheckBox();
        chkLock.Text = "锁屏时拒绝手机输入";
        chkLock.Location = new Point(186, 22);
        chkLock.AutoSize = true;
        chkLock.CheckedChanged += delegate { if (!loading) SetLockGuard(chkLock.Checked); };
        gOpt.Controls.Add(chkLock);
        chkHttps = new CheckBox();
        chkHttps.Text = "手机走 HTTPS";
        chkHttps.Location = new Point(12, 74);
        chkHttps.AutoSize = true;
        chkHttps.CheckedChanged += delegate { if (!loading) SetHttps(chkHttps.Checked); };
        gOpt.Controls.Add(chkHttps);
        chkCa = new CheckBox();
        chkCa.Text = "用自建 CA 签";
        chkCa.Location = new Point(190, 48);
        chkCa.AutoSize = true;
        chkCa.CheckedChanged += delegate { if (!loading) SetCa(chkCa.Checked); };
        gOpt.Controls.Add(chkCa);
        var btnElev = new Button();
        btnElev.Text = "以管理员身份重启";
        btnElev.Size = new Size(140, 26);
        btnElev.Location = new Point(190, 74);
        btnElev.Click += delegate { RelaunchElevated(); };
        gOpt.Controls.Add(btnElev);
        var hint = new Label();
        hint.Text = "勾了「用自建 CA」再让手机装一次 /ca.crt，才不弹警告、才能装成 App（安卓分享面板里才会有 bedremote）。";
        hint.ForeColor = Color.Gray;
        hint.Font = new Font("Microsoft YaHei UI", 8f);
        hint.Location = new Point(12, 100);
        hint.Size = new Size(366, 18);
        gOpt.Controls.Add(hint);
        var hint2 = new Label();
        hint2.Text = "没设令牌 = 局域网里任何设备都能控制；加令牌改 bedremote.json";
        hint2.ForeColor = Color.Gray;
        hint2.Font = new Font("Microsoft YaHei UI", 8f);
        hint2.Location = new Point(12, 116);
        hint2.Size = new Size(366, 16);
        gOpt.Controls.Add(hint2);
        f.Controls.Add(gOpt);

        // ---- 多台电脑：给这台起个名字 + 看看局域网里还有谁 ----
        var gNet = new GroupBox();
        gNet.Text = "多台电脑（同网段的 bedremote 会互相打招呼，手机上就能一键切过去）";
        gNet.Location = new Point(14, 450);
        gNet.Size = new Size(772, 64);
        gNet.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var lblName = new Label();
        lblName.Text = "这台电脑叫";
        lblName.Location = new Point(10, 27);
        lblName.Size = new Size(70, 18);
        gNet.Controls.Add(lblName);
        nameBox = new TextBox();
        nameBox.Location = new Point(84, 24);
        nameBox.Size = new Size(150, 23);
        gNet.Controls.Add(nameBox);
        var btnRename = new Button();
        btnRename.Text = "改名";
        btnRename.Size = new Size(56, 26);
        btnRename.Location = new Point(242, 23);
        btnRename.Click += delegate { RenameThis(); };
        gNet.Controls.Add(btnRename);
        var btnScan = new Button();
        btnScan.Text = "找同伴";
        btnScan.Size = new Size(76, 26);
        btnScan.Location = new Point(306, 23);
        btnScan.Click += delegate { ScanMates(); };
        gNet.Controls.Add(btnScan);
        matesInfo = new Label();
        matesInfo.Location = new Point(390, 27);
        matesInfo.Size = new Size(374, 18);
        matesInfo.ForeColor = Color.Gray;
        matesInfo.AutoEllipsis = true;
        gNet.Controls.Add(matesInfo);
        f.Controls.Add(gNet);

        var gPass = new GroupBox();
        gPass.Text = "提权通行证（给「要管理员权限」的软件免掉那个确认框：授权一次，以后不弹）";
        gPass.Location = new Point(14, 520);
        gPass.Size = new Size(772, 88);
        gPass.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        passInfo = new Label();
        passInfo.Location = new Point(10, 22);
        passInfo.Size = new Size(750, 18);
        passInfo.ForeColor = Color.Gray;
        gPass.Controls.Add(passInfo);
        elevCombo = new ComboBox();
        elevCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        elevCombo.Location = new Point(10, 50);
        elevCombo.Size = new Size(200, 24);
        gPass.Controls.Add(elevCombo);
        AddPassButton(gPass, 218, "点名", delegate { MarkElev(true); });
        AddPassButton(gPass, 302, "去掉点名", delegate { MarkElev(false); });
        AddPassButton(gPass, 412, "办证（全部）", delegate { IssuePasses(); });
        AddPassButton(gPass, 530, "撕掉全部", delegate { RevokePasses(); });
        AddPassButton(gPass, 626, "?", delegate { PassHelp(); });
        f.Controls.Add(gPass);

        var gLog = new GroupBox();
        gLog.Text = "日志（谁在动你的鼠标键盘，这里看得见）";
        gLog.Location = new Point(14, 616);
        gLog.Size = new Size(772, 234);
        gLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = Color.FromArgb(18, 18, 22);
        logBox.ForeColor = Color.Gainsboro;
        logBox.Font = new Font("Consolas", 9f);
        logBox.Location = new Point(10, 20);
        logBox.Size = new Size(752, 204);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        gLog.Controls.Add(logBox);
        f.Controls.Add(gLog);
        gLog.MinimumSize = new Size(300, 110);

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, delegate { ShowFromTray(); });
        menu.Items.Add("配对二维码", null, delegate { OpenUrl(LocalUrl("pair")); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { ExitApp(); });
        tray = new NotifyIcon();
        tray.Icon = AppIcon();
        tray.Text = "bedremote";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowFromTray(); };
        tray.Visible = true;

        tm = new System.Windows.Forms.Timer();
        tm.Interval = 1200;
        tm.Tick += delegate { Refresh(); };
        tm.Start();

        try { chkBoot.Checked = BootEnabled(); } catch { }
        try { chkLock.Checked = Config.DenyWhenLocked; } catch { }
        chkAwake.Checked = Program.AwakeOn;
        try { chkHttps.Checked = Config.Https; } catch { }
        try { chkCa.Checked = Config.Ca; } catch { }
        RestoreWakeChoice();
        loading = false;
        // 窗口句柄建好之后才知道"放得下所有东西"的外框尺寸是多少 —— 直接把它设成下限，
        // 省得拖小了以后各组互相压住（这次那个 bug 就是这么被拖出来的）。
        f.Shown += delegate
        {
            try { f.MinimumSize = new Size(f.Size.Width, f.Size.Height); } catch { }
        };
        Refresh();
    }

    // autoWakeSec 是"断掉的屏过多久自动接回"，界面上做成 4 档下拉，别让他去数秒
    static int WakeSecOf(int idx) { return idx == 1 ? 20 : idx == 2 ? 60 : idx == 3 ? 120 : 0; }
    static void RestoreWakeChoice()
    {
        int cur = Config.AutoWakeSec;
        int idx = cur == 20 ? 1 : cur == 60 ? 2 : cur == 120 ? 3 : 0;
        wake.SelectedIndex = idx;
    }

    static int AddToolButton(int x, string text, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.Size = new Size(0, 28);
        b.AutoSize = true;
        b.Location = new Point(x, 70);
        b.Click += onClick;
        f.Controls.Add(b);
        b.PerformLayout();
        return x + b.Width + 8;
    }

    static int AddScreenButton(GroupBox g, int x, int y, string text, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.Size = new Size(0, 24);
        b.Location = new Point(x, y);
        b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        b.Click += onClick;
        g.Controls.Add(b);
        b.PerformLayout();
        return x + b.Width + 6;
    }

    // ---------- 刷新 ----------
    static void Refresh()
    {
        DrainLog();
        if (passInfo != null && ++passTick >= 10) { passTick = 0; try { FillElevCombo(); passInfo.Text = PassText(); } catch { } }
        bool serving = Program.ServingNow;
        if (serving)
        {
            status.Text = "运行中 · 端口 " + Program.PortNow;
            status.ForeColor = Color.FromArgb(30, 160, 90);
        }
        else if (Program.StartError == 3)
        {
            status.Text = "已在别处运行（端口被占）";
            status.ForeColor = Color.DarkOrange;
        }
        else
        {
            status.Text = "已停止";
            status.ForeColor = Color.Firebrick;
        }
        btnStartStop.Text = serving ? "停止服务" : "启动服务";
        bool blocked = !serving && Program.StartError == 3;
        btnTake.Visible = blocked;
        if (blocked && !hinted)
        {
            hinted = true;
            Log2("端口被另一个 bedremote 占着。要这个界面来跑，点上面的「接管端口」；不想动旧的，这个窗口关掉就行。");
        }
        tray.Text = "bedremote · " + status.Text;

        // 换了网卡 / DHCP 重新分了 IP：证书 SAN 里必须有它，否则手机连"继续访问"的按钮都不会给。
        string ips = string.Join(",", Program.IpsNow());
        if (ips != sigIps) { sigIps = ips; if (Program.ServingNow) Program.RefreshCert(); }

        string a = LocalUrl("");
        if (a != sigAddr) { sigAddr = a; addr.Text = "手机打开：" + a + (Program.TokenNow.Length > 0 ? "  （带令牌 ?t=…）" : "  （未设令牌）"); }

        // 名字和同伴：屋里一排机器的时候，标题栏和这一行是唯一能认出"我开的是哪台"的东西
        string nm = Config.NameOrMachine();
        if (nm != shownName)
        {
            shownName = nm;
            f.Text = "bedremote · " + nm;
            if (nameBox != null && !nameBox.Focused) nameBox.Text = nm;
        }
        if (matesInfo != null)
        {
            int n = Mates.Count();
            matesInfo.Text = n == 0
                ? "还没听到别的 bedremote（新机器会自己喊；听不到点「找同伴」）"
                : "同网段还有 " + n + " 台：" + Mates.NamesBrief(5);
        }

        peers.BeginUpdate();
        peers.Items.Clear();
        var ps = Program.ExternalPeers();
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            long ago = (DateTime.Now.Ticks - p.Last) / TimeSpan.TicksPerSecond;
            if (ago < 0) ago = 0;
            var it = new ListViewItem(p.Ip);
            it.SubItems.Add(p.Sse > 0 ? "页面开着" : "只发过指令");
            it.SubItems.Add(p.Cmds.ToString());
            it.SubItems.Add(Human(ago));
            if (p.LastCmd.Length > 0) it.SubItems[1].Text += " · " + p.LastCmd;
            peers.Items.Add(it);
        }
        peers.EndUpdate();
        gPeers.Text = ps.Count == 0 ? "正在控制这台电脑的设备（没有别人，只有你自己）"
                                    : "正在控制这台电脑的设备（" + ps.Count + " 台）";

        RenderScreens();
    }

    static void RenderScreens()
    {
        var list = Disp.All(true);
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            sb.Append(m.Uid).Append('|').Append(m.Name).Append('|').Append(m.Active ? "1" : "0")
              .Append('|').Append(m.Width).Append('x').Append(m.Height).Append('|').Append(m.Hz).Append('|');
        }
        string sig = sb.ToString();
        if (sig == sigScreens) return;
        sigScreens = sig;

        screens.SuspendLayout();
        screens.Controls.Clear();
        int y = 2;
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            string rawName = string.IsNullOrEmpty(m.Name) ? m.Dev : m.Name;
            var t = new Label();
            t.Text = (m.Primary ? "★ " : "· ") + Config.ScreenName(m.Uid.ToString(), rawName) + "  " +
                     (m.Active ? (m.Width + "×" + m.Height + " " + m.Hz + (m.Clone ? " 克隆!" : "")) : "无信号") +
                     "  [" + Disp.TechName(m.Tech) + "]";
            t.Location = new Point(4, y + 3);
            t.Size = new Size(340, 18);
            t.AutoEllipsis = true;
            t.ForeColor = m.Active ? Color.Black : Color.Gray;
            screens.Controls.Add(t);

            uint uid = m.Uid;
            var b1 = new Button();
            b1.Text = m.Active ? "只留这块屏" : "切到这块屏";
            b1.Size = new Size(96, 24);
            b1.Location = new Point(4, y + 24);
            b1.Click += delegate { DoScreen("只留这块屏", delegate(out string h) { return Disp.OnlyThis(uid, out h); }); };
            screens.Controls.Add(b1);

            var b2 = new Button();
            b2.Text = m.Active ? "断这块信号" : "唤醒这块屏";
            b2.Size = new Size(96, 24);
            b2.Location = new Point(106, y + 24);
            b2.Click += delegate
            {
                bool want = m.Active;
                DoScreen(want ? "无信号" : "唤醒", delegate(out string h) { return Disp.SetActive(uid, !want, out h); });
            };
            screens.Controls.Add(b2);

            var b3 = new Button();
            b3.Text = "改名";
            b3.Size = new Size(56, 24);
            b3.Location = new Point(208, y + 24);
            string curName = Config.ScreenName(uid.ToString(), "");
            b3.Click += delegate { RenameScreen(uid, curName); };
            screens.Controls.Add(b3);
            y += 52;
        }
        var tip = new Label();
        tip.Text = "「只留这块屏」= 它当主屏 + 其他屏断信号。\n断掉的屏不会自己亮回来，按「唤醒这块屏」接回。";
        tip.ForeColor = Color.Gray;
        tip.Font = new Font("Microsoft YaHei UI", 8f);
        tip.Location = new Point(4, y + 2);
        tip.Size = new Size(340, 34);
        screens.Controls.Add(tip);
        screens.ResumeLayout();
    }

    static void RenameThis()
    {
        string err;
        string want = nameBox == null ? "" : nameBox.Text.Trim();
        if (!Config.SetName(want, out err)) { Log2("[改名] 失败：" + err); return; }
        shownName = "";            // 让下一次 Refresh 把标题/输入框/广播都按新名字走
        Log2("[改名] 这台电脑现在叫「" + Config.NameOrMachine() + "」。同网段的 bedremote 最迟 5 秒后就知道，手机上重新打开名单会看到新名字。");
        Refresh();
    }

    static void ScanMates()
    {
        if (matesInfo != null) matesInfo.Text = "在问 192.168.x.1~254…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string json = "";
            try { json = Program.MatesScan(); } catch (Exception ex) { json = "err:" + ex.Message; }
            try
            {
                f.BeginInvoke((Action)delegate
                {
                    Log2("[发现] 单播问了一圈：" + json.Replace("\n", " "));
                    Refresh();
                });
            }
            catch { }
        });
    }

    // 一个 20 行的小输入框，省得为这事引 Microsoft.VisualBasic.dll。
    // 屋里一排机器时"给这块屏起个名"这种输入一天要点好几次，不能让人去改 JSON。
    static string AskText(string title, string prompt, string def)
    {
        using (var d = new Form())
        {
            d.Text = title;
            d.FormBorderStyle = FormBorderStyle.FixedDialog;
            d.ShowInTaskbar = false;
            d.StartPosition = FormStartPosition.CenterParent;
            d.ClientSize = new Size(360, 128);
            d.MinimizeBox = false; d.MaximizeBox = false;
            var lbl = new Label();
            lbl.Text = prompt; lbl.Location = new Point(12, 12); lbl.Size = new Size(336, 34);
            var tb = new TextBox();
            tb.Text = def ?? ""; tb.Location = new Point(12, 50); tb.Size = new Size(336, 23);
            tb.SelectAll();
            var ok = new Button();
            ok.Text = "好"; ok.Location = new Point(186, 88); ok.Size = new Size(76, 28); ok.DialogResult = DialogResult.OK;
            var no = new Button();
            no.Text = "取消"; no.Location = new Point(272, 88); no.Size = new Size(76, 28); no.DialogResult = DialogResult.Cancel;
            d.AcceptButton = ok; d.CancelButton = no;
            d.Controls.Add(lbl); d.Controls.Add(tb); d.Controls.Add(ok); d.Controls.Add(no);
            return d.ShowDialog(f) == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }

    static void RenameScreen(uint uid, string current)
    {
        string want = AskText("给这块屏起个名字", "比如「左边竖屏」「床头那台」。留空 = 回到显示器自己的型号名。", current);
        if (want == null) return;
        string err;
        if (!Config.SetScreenName(uid.ToString(), want, out err)) { Log2("[改名] 失败：" + err); return; }
        Log2("[改名] 屏 " + uid + " → 「" + (want.Length == 0 ? "（型号名）" : want) + "」");
        sigScreens = "";
        Refresh();
    }

    static void DoScreen(string what, FuncOut fn)
    {
        string h;
        bool ok = fn(out h);
        Log2("[" + what + "] " + (ok ? "完成：" : "失败：") + h);
        sigScreens = "";      // 强制重画
        Refresh();
    }
    delegate bool FuncOut(out string how);

    // ---------- 动作 ----------
    static void ToggleService()
    {
        if (Program.ServingNow)
        {
            Program.StopServer();
            Log2("已停止服务（托盘图标右键可以退出，或在这里再点启动）");
        }
        else
        {
            Program.StartInBackground();
            Log2("正在启动服务…");
        }
        sigAddr = "";
        Refresh();
    }

    static void Takeover()
    {
        int pid = Program.PortOccupier(Program.PortNow);
        if (pid == 0)
        {
            Log2("查不到占着端口 " + Program.PortNow + " 的 bedremote 进程 —— 占着的可能是别的程序，没动它。");
            return;
        }
        Log2("准备关掉 pid " + pid + "（端口 " + Program.PortNow + " 上的旧 bedremote），然后自己起来…");
        string how;
        bool ok = Program.Takeover(out how);
        Log2((ok ? "接管成功：" : "接管失败：") + how);
        hinted = false;
        sigAddr = "";
        Refresh();
    }

    static string LocalUrl(string path)
    {
        var ips = Program.IpsNow();
        string ip = ips.Length > 0 ? ips[0] : "127.0.0.1";
        // 有 HTTPS 就优先给 https 的地址：手机只有从它进去才拿得到陀螺仪/剪贴板/屏幕常亮
        string u = (Program.HttpsOn ? "https://" : "http://") + ip + ":" + Program.PortNow + "/" + path;
        if (Program.TokenNow.Length > 0) u += "?t=" + Uri.EscapeDataString(Program.TokenNow);
        return u;
    }

    static void CopyAddr()
    {
        try { Clipboard.SetText(LocalUrl("")); Log2("已复制手机地址：" + LocalUrl("")); } catch { }
    }

    static void OpenUrl(string u)
    {
        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
        catch (Exception ex) { Log2("打不开 " + u + " ：" + ex.Message); }
    }

    static void Firewall()
    {
        try
        {
            string args = "advfirewall firewall add rule name=bedremote dir=in action=allow protocol=TCP localport=" +
                          Program.PortNow + " profile=private,domain enable=yes";
            var psi = new ProcessStartInfo("netsh.exe", args) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            Log2("已提交防火墙放行请求（要在弹窗里点“是”）。端口 " + Program.PortNow + "，仅专用/域网络。");
        }
        catch (Exception ex) { Log2("没提成（需要你点“是”）：" + ex.Message); }
    }

    static bool BootEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            if (k == null) return false;
            return k.GetValue(RunValue) != null;
        }
    }

    static void SetBoot(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) { Log2("打不开自启注册表项"); return; }
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue(RunValue, false);
            }
            Log2("开机自启：" + (on ? "已打开（开机缩到托盘，不抢你桌面窗口）" : "已关闭"));
        }
        catch (Exception ex) { Log2("改开机自启失败：" + ex.Message); }
    }

    static void SetLockGuard(bool on)
    {
        string err;
        if (Config.SetBool("denyWhenLocked", on, out err))
            Log2("锁屏守卫：" + (on ? "开" : "关") + "（已写进 bedremote.json）");
        else
        {
            Log2("改锁屏守卫失败：" + err + "（配置没动）");
            loading = true; chkLock.Checked = Config.DenyWhenLocked; loading = false;
        }
    }

    // HTTPS 是"启动时才读"的开关，改完要重启监听才生效。
    static void SetHttps(bool on)
    {
        string err;
        if (!Config.SetBool("https", on, out err))
        {
            Log2("改 HTTPS 开关失败：" + err + "（配置没动）");
            bool sv = loading; loading = true; chkHttps.Checked = Config.Https; loading = sv;
            return;
        }
        Log2("HTTPS：" + (on ? "开 —— 重启监听中，手机会自己重连" : "关 —— 只剩 http://，手机的陀螺仪/剪贴板/屏幕常亮都会拿不到数据"));
        Program.Restart();
        sigAddr = "";
        Refresh();
    }

    // 裸自签 vs 自建 CA：差别只在"手机信不信你"。信了才谈得上装 App 和分享面板。
    static void SetCa(bool on)
    {
        string err;
        if (!Config.SetBool("ca", on, out err))
        {
            Log2("改 CA 模式失败：" + err + "（配置没动）");
            bool sv = loading; loading = true; chkCa.Checked = Config.Ca; loading = sv;
            return;
        }
        if (on)
            Log2("改用自建根 CA 签证书，正在重启监听。下一步在手机上装根证书：浏览器打开 http://这台电脑的IP:"
                 + Program.PortNow + "/ca.crt 下载，然后 设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书。"
                 + "装完就不再弹警告，也能「添加到主屏幕」当 App 用，安卓分享面板里会出现 bedremote。");
        else
            Log2("改回裸自签：手机每次要点「继续访问」，而且装不了 App —— Chrome 注册 Service Worker 要求证书被**信任**，绕过警告页不算。");
        Program.Restart();
        sigAddr = "";
        Refresh();
    }

    // ---------- 提权通行证 ----------
    static int AddPassButton(GroupBox g, int x, string text, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.Size = new Size(0, 26);
        b.MinimumSize = new Size(0, 26);
        b.Location = new Point(x, 49);
        b.Click += onClick;
        g.Controls.Add(b);
        b.PerformLayout();
        return x + b.Width + 8;
    }

    // 下拉里列的是 run 白名单的全部条目名；选一个点「点名」就写进 elevatedRun。
    // 为什么要给界面做这个：不然用户得自己去改 JSON，那和"一堆快捷方式"是同一种麻烦。
    static void FillElevCombo()
    {
        var names = new List<string>();
        foreach (var kv in Config.Run) if (kv.Value.Trim().Length > 0) names.Add(kv.Key);
        names.Sort();
        string keep = elevCombo.SelectedItem == null ? "" : elevCombo.SelectedItem.ToString();
        var old = new StringBuilder();
        for (int i = 0; i < elevCombo.Items.Count; i++) old.Append(elevCombo.Items[i]).Append('|');
        var nowS = new StringBuilder();
        for (int i = 0; i < names.Count; i++) nowS.Append(names[i]).Append('|');
        if (old.ToString() == nowS.ToString()) return;         // 没变就别重画（重画会把选中项弄丢）
        elevCombo.BeginUpdate();
        elevCombo.Items.Clear();
        for (int i = 0; i < names.Count; i++) elevCombo.Items.Add(names[i]);
        elevCombo.EndUpdate();
        for (int i = 0; i < elevCombo.Items.Count; i++)
            if (string.Equals(elevCombo.Items[i].ToString(), keep, StringComparison.OrdinalIgnoreCase)) { elevCombo.SelectedIndex = i; break; }
        // 故意不自动选第一条：误点一下「点名」就会把根本没打算提权的条目点上名（这台机就中过一次）。
        if (elevCombo.SelectedIndex < 0 && keep.Length == 0) elevCombo.SelectedIndex = -1;
    }

    static void MarkElev(bool on)
    {
        if (elevCombo.SelectedItem == null) { Log2("先在下拉里选一个条目，再点「点名」（下拉里是空的说明 run 白名单还没东西）。"); return; }
        string name = elevCombo.SelectedItem.ToString();
        var list = new List<string>(Program.ElevatedNames);
        bool have = false;
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) { have = true; if (on) { Log2(name + " 已经点过名了"); return; } list.RemoveAt(i); break; }
        if (on) list.Add(name);
        string err;
        if (!Config.SetList("elevatedRun", list, out err)) { Log2("改点名列表失败：" + err); return; }
        Log2((on ? "已点名：" + name + "（现在点「办证（全部）」，会弹一次 UAC，点「是」）"
                 : "已去掉点名：" + name + (have ? "（证还留在系统里，要清干净点「撕掉全部」）" : "")));
        passTick = 99;
        Refresh();
    }

    // 每 12 秒才真去 schtasks 查一遍（每条一次进程调用，别在 1.2 秒的定时器里干这个）
    static string PassText()
    {
        var names = Program.ElevatedNames;
        if (names.Count == 0)
            return "还没点名：在 bedremote.json 里加 \"elevatedRun\": [\"某个run条目的名字\"]";
        var ps = Program.PassList();
        int ok = 0;
        var bad = new StringBuilder();
        for (int i = 0; i < ps.Count; i++)
        {
            if (ps[i].Cmd.Length == 0) { bad.Append(ps[i].Name).Append("(白名单里没这条) "); continue; }
            if (ps[i].Registered && !ps[i].Stale) { ok++; continue; }
            bad.Append(ps[i].Name).Append(ps[i].Stale ? "(命令改过,要重办) " : "(没办) ");
        }
        return "点名 " + names.Count + " 条，已办好 " + ok + (bad.Length == 0 ? "，都有效" : "；待办：" + bad.ToString());
    }

    static void IssuePasses()
    {
        var names = Program.ElevatedNames;
        if (names.Count == 0)
        {
            Log2("还没点名：先在 bedremote.json 里写 \"elevatedRun\": [\"某个run条目的名字\"]，再回来点办证。");
            return;
        }
        Log2("要给这几条办证：" + string.Join("、", names.ToArray())
             + " —— 马上弹一次 UAC，点「是」。这一步就是Windows 在确认你愿意让这几个程序免检启动。");
        string err = Program.PassIssue();
        Log2(err.Length == 0 ? "办证完成。以后从手机上点这些软件不会再弹确认框。撕掉用旁边那个按钮。"
                             : "办证没成：" + err);
        passTick = 99;
        Refresh();
    }

    static void RevokePasses()
    {
        if (MessageBox.Show("撕掉全部通行证？\n\n撕掉之后这些软件会重新弹管理员确认框（别的什么都不影响）。",
                            "bedremote", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string err = Program.PassRevoke();
        Log2(err.Length == 0 ? "已撕掉全部通行证。" : "撕证没成：" + err);
        passTick = 99;
        Refresh();
    }

    static void PassHelp()
    {
        MessageBox.Show(
            "有些软件启动时要管理员权限，Windows 会弹一个「要用管理员身份运行吗」的框。\n" +
            "那个框是高权限窗口，普通权限的 bedremote 送不进输入 —— 所以从手机上点这种软件，远程会当场「点不动」。\n\n" +
            "办证 = 在 Windows 里注册一个「以最高权限运行」的计划任务，执行人设成 Users 组，\n" +
            "于是普通权限也能免确认触发它。授权时要点一次「是」，以后每次启动都不弹。\n\n" +
            "代价（认真看）：这张证是一个**定向的 UAC 绕过**。能连上 bedremote 的人，\n" +
            "就能不弹框地以管理员身份启动证上写的那几个程序（只有那几个）。所以：\n" +
            "  · 只给确实需要的软件办，别给 shell、脚本解释器这类办；\n" +
            "  · 办了证就建议同时给 bedremote 设 token；\n" +
            "  · 不想要了就「撕掉全部」，或者在任务计划程序里删 bedremote-run-* 那几个任务。\n\n" +
            "另一条更省事的路：很多软件其实不需要管理员权限（是被兼容性勾选害的），\n" +
            "右键 → 属性 → 兼容性，把「以管理员身份运行此程序」去掉即可。",
            "提权通行证是什么", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // 为什么要有这个按钮：UAC 那个"要用管理员身份运行吗"的确认框是**高权限窗口**，
    // 普通权限的 bedremote 往里送输入会被 UIPI 直接丢掉 —— 于是手机上一点某个要提权的程序，
    // 远程就"点不动"了。bedremote 自己提权之后两件事同时解决：
    //   1) 它能操作 Consent 窗口（能替你把是/否点了）；
    //   2) 它启动的高权限程序根本不会再弹这个框（父进程已经有权限）。
    // 做法：让 cmd 晚一秒去拉新实例，我们这边先退出把端口让出来；他点「否」就什么都不发生。
    static void RelaunchElevated()
    {
        try
        {
            string tail = Program.StartHidden ? " --tray" : "";
            var psi = new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul && start \"\" \"" + Application.ExecutablePath + "\"" + tail)
            { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log2("提权没成（点了「否」或者系统不让）：" + ex.Message + " —— 现在这个实例照常跑，什么都没变。");
            return;
        }
        Log2("已请求提权：弹窗里点「是」，新实例会以管理员身份起来接管端口，这个旧实例现在退出。");
        exiting = true;
        try { Program.StopServer(); } catch { }
        try { tm.Stop(); } catch { }
        try { tray.Visible = false; } catch { }
        try { f.Close(); } catch { }
        Application.Exit();
    }

    // ---------- 托盘 / 日志 / 退出 ----------
    static void HideToTray()
    {
        f.Hide();
        if (!ballooned)
        {
            try { tray.ShowBalloonTip(2500, "bedremote 在托盘里", "双击图标打开窗口；手机地址在窗口里可复制。", ToolTipIcon.Info); } catch { }
            ballooned = true;
        }
    }

    static void ShowFromTray()
    {
        f.Show();
        f.WindowState = FormWindowState.Normal;
        f.Activate();
    }

    static void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        HideToTray();
    }

    static void ExitApp()
    {
        exiting = true;
        Program.StopServer();
        try { tm.Stop(); } catch { }
        tray.Visible = false;
        f.Close();
    }

    static void OnLog(string s) { lock (q) { q.Enqueue(s); while (q.Count > 600) q.Dequeue(); } }

    static void DrainLog()
    {
        string[] arr;
        lock (q) { if (q.Count == 0) return; arr = q.ToArray(); q.Clear(); }
        var sb = new StringBuilder(logBox.Text);
        for (int i = 0; i < arr.Length; i++) sb.Append(arr[i]).Append("\r\n");
        if (sb.Length > 60000) sb.Remove(0, sb.Length - 60000);
        logBox.Text = sb.ToString();
        logBox.SelectionStart = logBox.Text.Length;
        logBox.ScrollToCaret();
    }

    static void Log2(string s)
    {
        Program.Log(s);
        DrainLog();
    }

    static string Human(long sec)
    {
        if (sec < 60) return sec + " 秒前";
        if (sec < 3600) return (sec / 60) + " 分钟前";
        return (sec / 3600) + " 小时前";
    }

    // 图标是现画的：绿底一张床。托盘里一眼能认出来，不用去猜哪个图标是谁。
    // （不嵌 .ico 文件 —— 项目要保持"一个 exe、零外部资源"。）
    static Icon AppIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(24, 140, 90))) g.FillEllipse(bg, 0, 0, 32, 32);
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, 7, 15, 19, 5);       // 床垫
                g.FillRectangle(w, 8, 20, 3, 5);        // 前腿
                g.FillRectangle(w, 22, 20, 3, 5);       // 后腿
                g.FillRectangle(w, 8, 10, 6, 5);        // 枕头
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
