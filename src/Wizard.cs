// 「往手机上加个软件」向导：把原来散在四个地方的动作（改 JSON、重启、界面里点名办证、
// 去网页编辑器加按钮）收进一个窗口。
//
// 最懒的路径是：列表里直接列**现在开着有窗口的程序**，双击一个 → 点「加到手机上」→ 完事。
// 要不要管理员由程序自己读清单/兼容性判断，需要的话顺手把通行证办了（那一下 UAC 必须人点，躲不掉）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

static class Wizard
{
    static Form w;
    static ListBox list;
    static TextBox nameBox;
    static ComboBox tabBox;
    static Label info;
    static Apps.Item picked;
    static bool busy;

    public static void Show(IWin32Window owner)
    {
        w = new Form();
        w.Text = "往手机上加个软件";
        w.ClientSize = new Size(560, 520);
        w.StartPosition = FormStartPosition.CenterParent;
        w.Font = new Font("Microsoft YaHei UI", 9f);
        w.ShowInTaskbar = false;
        // 拖放不是必需功能：注册失败（提权进程、异常桌面、某些精简系统）不能把整个向导带崩。
        w.Shown += delegate { try { w.AllowDrop = true; } catch { } };
        w.DragEnter += OnDragEnter;
        w.DragDrop += OnDragDrop;

        var tip = new Label();
        tip.Text = "点一个程序（下面列的就是现在开着的），或者把 exe / 快捷方式拖进来，然后点「加到手机上」。";
        tip.Location = new Point(12, 10);
        tip.Size = new Size(536, 20);
        w.Controls.Add(tip);

        list = new ListBox();
        list.Location = new Point(12, 34);
        list.Size = new Size(536, 220);
        list.IntegralHeight = false;
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        list.SelectedIndexChanged += delegate { Choose(list.SelectedItem as Apps.Item); };
        list.DoubleClick += delegate { Add(); };
        w.Controls.Add(list);

        var l2 = new Label();
        l2.Text = "手机上叫什么";
        l2.Location = new Point(12, 264);
        l2.AutoSize = true;
        l2.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        w.Controls.Add(l2);
        nameBox = new TextBox();
        nameBox.Location = new Point(100, 260);
        nameBox.Size = new Size(200, 26);
        nameBox.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        w.Controls.Add(nameBox);

        var l3 = new Label();
        l3.Text = "放进页签";
        l3.Location = new Point(316, 264);
        l3.AutoSize = true;
        l3.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        w.Controls.Add(l3);
        tabBox = new ComboBox();
        tabBox.DropDownStyle = ComboBoxStyle.DropDown;
        tabBox.Location = new Point(386, 260);
        tabBox.Size = new Size(162, 26);
        tabBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        w.Controls.Add(tabBox);

        info = new Label();
        info.Location = new Point(12, 294);
        info.Size = new Size(536, 52);
        info.ForeColor = Color.Gray;
        info.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        w.Controls.Add(info);

        var bBrowse = new Button();
        bBrowse.Text = "浏览别的程序…";
        bBrowse.Size = new Size(120, 30);
        bBrowse.Location = new Point(12, 356);
        bBrowse.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        bBrowse.Click += delegate { Browse(); };
        w.Controls.Add(bBrowse);

        var bRefresh = new Button();
        bRefresh.Text = "刷新列表";
        bRefresh.Size = new Size(90, 30);
        bRefresh.Location = new Point(140, 356);
        bRefresh.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        bRefresh.Click += delegate { Fill(); };
        w.Controls.Add(bRefresh);

        var bOk = new Button();
        bOk.Text = "加到手机上";
        bOk.Size = new Size(140, 30);
        bOk.Location = new Point(280, 356);
        bOk.BackColor = Color.FromArgb(224, 240, 230);
        bOk.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        bOk.Click += delegate { Add(); };
        w.Controls.Add(bOk);

        var bCancel = new Button();
        bCancel.Text = "关闭";
        bCancel.Size = new Size(80, 30);
        bCancel.Location = new Point(428, 356);
        bCancel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        bCancel.Click += delegate { w.Close(); };
        w.Controls.Add(bCancel);

        var note = new Label();
        note.Text = "它会自动：写进白名单 → 不用重启就生效 → 在手机上长出这个按钮；" +
                    "如果这个软件要管理员权限，会顺手办一张通行证（那时弹的 UAC 要你点「是」）。";
        note.ForeColor = Color.Gray;
        note.Location = new Point(12, 396);
        note.Size = new Size(536, 110);
        note.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        w.Controls.Add(note);

        Fill();
        tabBox.Items.Add("我的软件");
        foreach (var kv in Config.Panels)
        {
            if (kv.Key != "tabs") continue;
            foreach (var to in Json.Arr(kv.Value))
            {
                var t = Json.Obj(to);
                string nm = Json.Str(Json.Get(t, "name"), "");
                if (nm.Length > 0) tabBox.Items.Add(nm);
            }
        }
        tabBox.Text = "我的软件";
        w.ShowDialog(owner);
    }

    static void Fill()
    {
        list.Items.Clear();
        List<Apps.Item> items;
        try { items = Apps.Running(); }
        catch (Exception ex) { info.Text = "列不出来：" + ex.Message; return; }
        for (int i = 0; i < items.Count; i++) list.Items.Add(items[i]);
        if (items.Count == 0) info.Text = "没找到开着的程序窗口 —— 用「浏览别的程序」或者直接拖进来。";
    }

    static void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    static void OnDragDrop(object sender, DragEventArgs e)
    {
        try
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            UsePath(files[0]);
        }
        catch (Exception ex) { info.Text = "拖进来的东西读不了：" + ex.Message; }
    }

    static void Browse()
    {
        var d = new OpenFileDialog();
        d.Title = "选程序的 exe 或快捷方式";
        d.Filter = "程序与快捷方式|*.exe;*.lnk;*.bat;*.cmd|所有文件|*.*";
        try
        {
            string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            if (Directory.Exists(start)) d.InitialDirectory = start;
        }
        catch { }
        if (d.ShowDialog(w) == DialogResult.OK) UsePath(d.FileName);
    }

    static void UsePath(string path)
    {
        string exe = Apps.Resolve(path);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            info.Text = "找不到这个文件：" + exe + "\n（快捷方式指向的东西可能已经删了）";
            return;
        }
        var it = new Apps.Item();
        it.Exe = exe;
        it.Name = Path.GetFileNameWithoutExtension(exe);
        it.Title = it.Name;
        it.Why = Apps.NeedsAdmin(exe);
        Choose(it);
    }

    static void Choose(Apps.Item it)
    {
        if (it == null) return;
        picked = it;
        nameBox.Text = it.Name;
        if (it.Why.Length == 0)
            info.ForeColor = Color.Gray;
        else
            info.ForeColor = Color.DarkOrange;
        info.Text = "选中：" + it.Exe + "\n" +
                    (it.Why.Length == 0 ? "不要管理员权限 —— 加完就能从手机上点，不用办证。"
                                        : "要管理员权限：" + it.Why + " —— 加完会顺手办张证（弹 UAC 时点「是」）。");
    }

    static void Add()
    {
        if (busy) return;
        if (picked == null) { info.Text = "先双击选一个程序（或拖进来）"; return; }
        string name = (nameBox.Text ?? "").Trim();
        if (name.Length == 0) { info.Text = "给它在手机上起个名字"; return; }
        string tab = (tabBox.Text ?? "").Trim();
        if (tab.Length == 0) tab = "我的软件";
        busy = true;
        try
        {
            string cmd = "start \"\" \"" + picked.Exe + "\"";
            string err;
            if (!Config.SetRun(name, cmd, out err)) { info.Text = "写白名单失败：" + err; return; }
            bool needAdmin = picked.Why.Length > 0;
            if (needAdmin)
            {
                var list2 = new List<string>(Config.ElevatedRun);
                bool have = false;
                for (int i = 0; i < list2.Count; i++)
                    if (string.Equals(list2[i], name, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
                if (!have) list2.Add(name);
                if (!Config.SetList("elevatedRun", list2, out err)) { info.Text = "点名失败：" + err; return; }
                Program.Log("[向导] 已点名：" + name + "（" + picked.Why + "）");
            }
            if (!Config.AddRunButton(tab, name, name, needAdmin ? "管理员" : "", out err))
            { info.Text = "写手机按钮失败：" + err + "\n（白名单已经加上了，按钮你可以自己去 /edit 里加）"; return; }
            Program.ReloadConfig();
            // 面板变更由 ReloadConfig 统一推给手机
            string tail;
            if (needAdmin)
            {
                Program.Log("[向导] 正在给 " + name + " 办通行证 —— 弹 UAC 时点「是」");
                info.Text = "已经加好了，手机上现在就有这个按钮。\n正在办通行证…弹出来的 UAC 请点「是」。";
                w.Refresh();
                tail = Program.PassIssue();
                info.Text = tail.Length == 0
                    ? "都好了：手机页「" + tab + "」里点「" + name + "」就能开。\n通行证已办好，以后不再弹确认框。"
                    : "手机按钮已经加好了；但通行证没办成：" + tail + "\n（不办证也能用，只是每次会弹确认框，得你去点）";
            }
            else
            {
                info.Text = "好了：手机页「" + tab + "」里点「" + name + "」就能开。\n这个软件不要管理员权限，不用办证。";
            }
            Program.Log("[向导] " + info.Text.Replace("\n", "　"));
        }
        catch (Exception ex) { info.Text = "出错了：" + ex.Message; }
        finally { busy = false; }
    }
}

