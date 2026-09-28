using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// ---------- 同伴：让局域网里多台 bedremote 互相知道对方存在 ----------
// 为什么用 UDP 广播而不是"扫一遍网段"：几十台机器里扫 254 个 IP 是噪音（而且很多公司网段是 /23、
// 还有 VLAN），而"自己喊一声"是零配置 —— 新机器插上电不用告诉任何人它的地址。
// 浏览器发不了 UDP，所以手机不掺和这件事：它只问**任意一台**电脑"你认识谁"，那台把听来的名单给它。
// 广播被交换机/路由挡掉的环境（跨网段、AP 隔离）靠 c=scan 那条 TCP 兜底，不在这文件里。
static class Mates
{
    // 发现端口固定，跟服务端口无关：一台机器上跑多份 bedremote（不同服务端口）也共用这一个听点。
    public const int DPort = 48765;
    const string Tag = "BR1";
    const int TtlMs = 30000;              // 三次心跳没听到就当它不在了

    public class Mate
    {
        public string Id, Name, Ip, Ver;
        public int Port, Mon;
        public bool Https;
        public int Last;                  // Environment.TickCount 意义下的最后 heard 时刻
        public bool Self;
    }

    static readonly Dictionary<string, Mate> Table = new Dictionary<string, Mate>();
    static readonly object Lk = new object();
    static readonly string SelfId = Guid.NewGuid().ToString("N").Substring(0, 8);

    static UdpClient ux;
    static volatile bool running;
    static int nextAnnounce;
    static Func<string> nameGet = delegate { return ""; };
    static Func<int> portGet = delegate { return 8765; };
    static Func<int> monGet = delegate { return 0; };
    static Func<string> verGet = delegate { return ""; };
    static Func<bool> httpsGet = delegate { return false; };

    static int Tk() { return Environment.TickCount; }

    public static void Start(Func<string> machineName, Func<int> servicePort, Func<int> monitorCount, Func<string> version, Func<bool> https)
    {
        nameGet = machineName; portGet = servicePort; monGet = monitorCount; verGet = version; httpsGet = https;
        if (running) return;
        try
        {
            ux = new UdpClient(AddressFamily.InterNetwork);
            try { ux.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
            ux.Client.Bind(new IPEndPoint(IPAddress.Any, DPort));
            ux.Client.EnableBroadcast = true;
            running = true;
            Thread t = new Thread(ReceiveLoop);
            t.IsBackground = true;
            t.Start();
            // 开机头几秒连发三次带"问一下你们都是谁"的包：让新机器几秒内就拿到整份名单，
            // 不用等别人的下一个 5 秒心跳。
            Announce(true); Announce(true); Announce(true);
            nextAnnounce = Tk() + 5000;
        }
        catch (Exception ex)
        {
            running = false; ux = null;
            Program.Log("[发现] UDP 听点起不来（" + ex.Message + "）：手机只能靠手动加地址");
        }
    }

    public static void Stop()
    {
        running = false;
        var c = ux; ux = null;
        if (c != null) { try { c.Close(); } catch { } }
    }

    public static bool Listening { get { return running; } }

    static string Enc(string s) { return Uri.EscapeDataString(s ?? ""); }
    static string Dec(string s) { try { return Uri.UnescapeDataString(s ?? ""); } catch { return s ?? ""; } }

    static string Packet(bool ask)
    {
        // s=1 告诉手机"这台的 https 是活的"：切过去要用 https:// 打开，否则陀螺仪/剪贴板会莫名没了
        return Tag + " id=" + SelfId + " n=" + Enc(nameGet()) + " ip=" + MyPrimaryIp() +
               " port=" + portGet() + " mon=" + monGet() + " s=" + (httpsGet() ? "1" : "0") +
               " v=" + Enc(verGet()) + " q=" + (ask ? "1" : "0");
    }

    static string MyPrimaryIp()
    {
        var ips = Program.LocalIPv4();
        return ips.Count > 0 ? ips[0] : "127.0.0.1";
    }

    // 255.255.255.255 是"有限广播"，有些交换机/路由不往前转；每张网卡再补一发"定向广播"
    // （192.168.1.255 这种），家里和一般公司网段两种总有一种能到。
    static void Announce(bool ask)
    {
        var c = ux;
        if (c == null || !running) return;
        string p = Packet(ask);
        byte[] b = Encoding.ASCII.GetBytes(p);
        var targets = new List<IPEndPoint>();
        try { targets.Add(new IPEndPoint(IPAddress.Broadcast, DPort)); } catch { }
        foreach (var ep in DirectedBroadcasts()) targets.Add(ep);
        foreach (var ep in targets)
        {
            try { c.Send(b, b.Length, ep); }
            catch { }   // 没插网线/网卡禁用/多网卡路由奇怪，发不出去就算了，听还在听
        }
    }

    static List<IPEndPoint> DirectedBroadcasts()
    {
        var outList = new List<IPEndPoint>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    uint ip = ToUInt(ua.Address), mask = ToUInt(ua.IPv4Mask);
                    if (mask == 0) continue;
                    uint bcast = ip | ~mask;
                    if (bcast == 0xFFFFFFFF) continue;   // 已经发过有限广播了
                    outList.Add(new IPEndPoint(FromUInt(bcast), DPort));
                }
            }
        }
        catch { }
        return outList;
    }

    static uint ToUInt(IPAddress a)
    {
        byte[] b = a.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    static IPAddress FromUInt(uint v)
    {
        return new IPAddress(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }

    static void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            byte[] b;
            try { b = ux.Receive(ref any); }
            catch { break; }
            string txt = Encoding.ASCII.GetString(b ?? new byte[0]);
            var m = Parse(txt, any.Address);
            if (m == null) continue;
            bool ask = LastField(txt, "q") == "1";
            if (m.Id == SelfId) continue;           // 自己发的那份会绕回来，别把自己当别人
            bool isNew = false;
            lock (Lk)
            {
                string key = m.Ip + ":" + m.Port;
                Mate prev;
                Table.TryGetValue(key, out prev);
                isNew = prev == null;
                if (prev != null && prev.Name != m.Name) Program.Log("[发现] " + m.Ip + " 改名了：" + prev.Name + " → " + m.Name);
                Table[key] = m;
                SweepLocked();
            }
            if (isNew) Program.Log("[发现] 看到一台：" + m.Name + "  " + m.Ip + ":" + m.Port);
            // 有人刚起来问话：单播回它一次，它立刻就知道有我，不用等下一个心跳
            if (ask)
            {
                var c = ux;
                if (c != null) { try { byte[] rb = Encoding.ASCII.GetBytes(Packet(false)); c.Send(rb, rb.Length, any); } catch { } }
            }
            any = new IPEndPoint(IPAddress.Any, 0);
        }
    }

    static string LastField(string txt, string key)
    {
        foreach (var kv in txt.Split(' '))
        {
            int e = kv.IndexOf('=');
            if (e > 0 && kv.Substring(0, e) == key) return kv.Substring(e + 1);
        }
        return "";
    }

    static Mate Parse(string txt, IPAddress from)
    {
        if (string.IsNullOrEmpty(txt) || !txt.StartsWith(Tag + " ")) return null;
        var m = new Mate();
        foreach (var kv in txt.Split(' '))
        {
            int e = kv.IndexOf('=');
            if (e <= 0) continue;
            string k = kv.Substring(0, e), v = kv.Substring(e + 1);
            switch (k)
            {
                case "id": m.Id = v; break;
                case "n": m.Name = Dec(v); break;
                case "ip": m.Ip = v; break;
                case "port": int.TryParse(v, out m.Port); break;
                case "mon": int.TryParse(v, out m.Mon); break;
                case "s": m.Https = v == "1"; break;
                case "v": m.Ver = Dec(v); break;
            }
        }
        if (string.IsNullOrEmpty(m.Id) || m.Port <= 0) return null;
        // 包里的 ip 是它自己网卡上的地址，可能因为多网卡对不上；以 UDP 源地址为准
        if (from != null) m.Ip = from.ToString();
        m.Last = Tk();
        return m;
    }

    static void SweepLocked()
    {
        int now = Tk();
        List<string> dead = null;
        foreach (var kv in Table)
            if ((int)(now - kv.Value.Last) > TtlMs) { if (dead == null) dead = new List<string>(); dead.Add(kv.Key); }
        if (dead != null) foreach (var k in dead) Table.Remove(k);
    }

    // 心跳：放在这里而不是单开线程，省一个常驻线程；没在听就什么都不做。
    public static void Tick()
    {
        if (!running) return;
        int now = Tk();
        if ((int)(nextAnnounce - now) > 0) return;
        nextAnnounce = now + 5000;
        Announce(false);
    }

    public static string ListJson()
    {
        var sb = new StringBuilder();
        sb.Append('[');
        Append(sb, SelfMate(), true);       // 自己永远在名单里，手机不用另外拼一条
        List<Mate> all;
        lock (Lk) { SweepLocked(); all = new List<Mate>(Table.Values); }
        all.Sort(delegate(Mate a, Mate b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        foreach (var m in all) { sb.Append(','); Append(sb, m, false); }
        sb.Append(']');
        return sb.ToString();
    }

    static Mate SelfMate()
    {
        var m = new Mate();
        m.Id = SelfId; m.Name = nameGet(); m.Ip = MyPrimaryIp(); m.Port = portGet();
        m.Mon = monGet(); m.Ver = verGet(); m.Https = httpsGet(); m.Last = Tk(); m.Self = true;
        return m;
    }

    static void Append(StringBuilder sb, Mate m, bool self)
    {
        sb.Append("{\"id\":\"").Append(Program.Json(m.Id))
          .Append("\",\"name\":\"").Append(Program.Json(m.Name ?? ""))
          .Append("\",\"ip\":\"").Append(Program.Json(m.Ip ?? ""))
          .Append("\",\"port\":").Append(m.Port)
          .Append(",\"mon\":").Append(m.Mon)
          .Append(",\"https\":").Append(m.Https ? "true" : "false")
          .Append(",\"ver\":\"").Append(Program.Json(m.Ver ?? ""))
          .Append("\",\"seen\":").Append(Math.Max(0, (Tk() - m.Last) / 1000))
          .Append(",\"self\":").Append(self ? "true" : "false")
          .Append('}');
    }

    // c=scan 用：往一个 IP 的某个端口发一次"问一下"，等它回 —— 比 TCP 连接更准（只有真 bedremote 会答），
    // 而且不需要它开任何额外端口（它就在 DPort 上听）。
    public static void Poke(IPEndPoint target)
    {
        var c = ux;
        if (c == null) return;
        try { byte[] b = Encoding.ASCII.GetBytes(Packet(true)); c.Send(b, b.Length, target); } catch { }
    }

    public static int Count()
    {
        lock (Lk) { SweepLocked(); return Table.Count; }
    }

    // 界面上一行就够：「同网段还有 3 台：7号机、12号机、渲染那台」
    public static string NamesBrief(int max)
    {
        List<Mate> all;
        lock (Lk) { SweepLocked(); all = new List<Mate>(Table.Values); }
        all.Sort(delegate(Mate a, Mate b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        var sb = new StringBuilder();
        for (int i = 0; i < all.Count && i < max; i++)
        {
            if (i > 0) sb.Append('、');
            sb.Append(all[i].Name).Append('(').Append(all[i].Ip).Append(')');
        }
        if (all.Count > max) sb.Append("…");
        return sb.ToString();
    }

    // 推送配置前拿这个卡一下目标：只准写给**发现到的同伴**，不能是任意 URL，
    // 否则这条命令就成了"带着我们的令牌去改别人的服务器"。
    public static bool IsKnown(string ip, int port)
    {
        lock (Lk)
        {
            SweepLocked();
            Mate m;
            if (Table.TryGetValue(ip + ":" + port, out m)) return true;
            return port == portGet() && ip == MyPrimaryIp();     // 自己也算（"推给包括自己在内的全部"）
        }
    }
}
