// tools\cfgprobe.cs —— 把一份 bedremote.json 拿来让**真的 Config.Load 读一遍**，打印读到了什么。
// 存在的理由：配置读失败时服务端是"退回内置默认"，界面上一切正常，只是你的改动没生效 ——
// 这种失败从手机页面上根本看不出来。想知道"我这份到底被不被认"，跑这个比猜快。
//
// 编译（零依赖，用系统自带的 csc；要把 src 全带上，因为按钮动作那张表在服务端代码里）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -codepage:65001 -platform:x64 `
//     -r:System.Windows.Forms.dll -r:System.Drawing.dll -main:CfgProbe -out:cfgprobe.exe `
//     src\Apps.cs src\Config.cs src\Display.cs src\Gui.cs src\Mates.cs src\Passes.cs src\Tls.cs `
//     src\Wizard.cs src\BedRemote.cs tools\cfgprobe.cs
// -main 是必需的：Program 自己也有 Main，不指定会报"多个入口点"。
// 用法：cfgprobe.exe <那个 json 所在的目录>     （目录里放的文件名必须是 bedremote.json）
// 输出是中文的：cmd 里先 `chcp 65001` 才不乱码（乱码不代表失败，看退出码：0=读通了，1=有问题）。
// 例子：把 bedremote.example.json 复制成临时目录里的 bedremote.json 再指过去 ——
//       仓库里这份示例能不能被程序读懂，就是这么验的。
static class CfgProbe
{
    static void Line(string k, string v) { System.Console.WriteLine(k.PadRight(16) + " = " + v); }

    static int Main(string[] args)
    {
        if (args.Length < 1) { System.Console.WriteLine("用法：cfgprobe <目录>（目录里要有 bedremote.json）"); return 2; }
        string dir = args[0];
        if (!System.IO.Directory.Exists(dir)) { System.Console.WriteLine("目录不存在：" + dir); return 2; }

        Config.Load(dir);
        Line("Path", Config.Path);
        Line("Loaded", Config.Loaded.ToString());
        Line("Error", Config.Error == null ? "(无)" : Config.Error);
        Line("name", Config.NameOrMachine());
        Line("port", Config.Port.ToString());
        Line("token", string.IsNullOrEmpty(Config.Token) ? "(空=不设口令)" : "已设（" + Config.Token.Length + " 位）");
        Line("https/ca", Config.Https + " / " + Config.Ca);
        Line("keepAwake", Config.KeepAwake.ToString());
        Line("denyWhenLocked", Config.DenyWhenLocked.ToString());
        Line("allowOpen", Config.AllowOpen.ToString());
        Line("ddcOff", Config.AllowDdcOff.ToString());
        Line("autoWakeSec", Config.AutoWakeSec.ToString());
        Line("run 条目", Config.Run == null ? "0" : Config.Run.Count.ToString());
        Line("elevatedRun", Config.ElevatedRun == null ? "0" : string.Join(",", Config.ElevatedRun.ToArray()));
        Line("screenNames", Config.ScreenNames == null ? "0" : Config.ScreenNames.Count.ToString());
        Line("gamepad", Config.Gamepad == null ? "(无)" : "键 " + Config.Gamepad.Count + " 个");
        int mc = Config.Macros == null ? 0 : Config.Macros.Count;
        Line("macros", mc.ToString());

        // panels 是"改了没生效"最容易发生的一段：结构错了服务端会整段忽略，只剩内置那页。
        int tabs = 0, groups = 0, buttons = 0;
        string err = "";
        try
        {
            var p = Config.Panels;
            var arr = Json.Arr(Json.Get(p, "tabs"));
            tabs = arr.Count;
            foreach (object to in arr)
            {
                var gs = Json.Arr(Json.Get(Json.Obj(to), "groups"));
                groups += gs.Count;
                foreach (object go in gs)
                {
                    var bs = Json.Arr(Json.Get(Json.Obj(go), "buttons"));
                    buttons += bs.Count;
                    foreach (object bo in bs)
                    {
                        var b = Json.Obj(bo);
                        string act = Json.Str(Json.Get(b, "act"), "");
                        // 面板按钮的动作必须是服务端认识的，写错就是"点了没反应"
                        if (act.Length == 0) err += "  有个按钮没写 act\n";
                        // tab / clearmods / macro 是手机页自己解释的（切页签、清修饰键、发起一个组合动作），
                        // 不走输入指令那条路，所以 IsInputCmd 不认它们。
                        else if (!Program.IsInputCmd(act) && act != "reload" && act != "awake" &&
                                 act != "confirm" && act != "notify" && act != "open" && act != "screen" &&
                                 act != "tab" && act != "clearmods" && act != "macro")
                            err += "  act=\"" + act + "\" 服务端没这个动作\n";
                    }
                }
            }
        }
        catch (System.Exception e) { err += "panels 结构读不动：" + e.Message + "\n"; }

        Line("panels", tabs + " 页 / " + groups + " 组 / " + buttons + " 个按钮");

        // 组合动作：一步 = 一句 /cmd 查询串（"c=key&k=esc"）或一句等待（"wait=400"）。
        // 写歪了不会崩，只会"那一步什么也没做"——这种静默失败最难查，所以这里替人验形状。
        if (Config.Macros != null)
            foreach (var mkv in Config.Macros)
                foreach (string s in mkv.Value)
                {
                    bool okStep = s.StartsWith("wait=", System.StringComparison.OrdinalIgnoreCase) ||
                                  s.StartsWith("c=", System.StringComparison.OrdinalIgnoreCase);
                    if (!okStep) err += "  组合动作「" + mkv.Key + "」有一步既不是 c=... 也不是 wait=...：" + s + "\n";
                }

        System.Console.WriteLine(err.Length == 0 ? "按钮动作都认。" : err.TrimEnd());
        // 配置没读成功却又"看起来正常"（退回默认）是最坑的一种，给它个非零退出码，脚本能接住
        return Config.Loaded && err.Length == 0 ? 0 : 1;
    }
}
