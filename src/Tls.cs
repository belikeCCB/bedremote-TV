// HTTPS：自签证书（或自建小 CA）+ 同一个端口上同时吃 http 和 https。
//
// 为什么要 HTTPS：手机浏览器把传感器和剪贴板当敏感能力 —— DeviceOrientation / DeviceMotion /
// clipboard.readText / Screen Wake Lock 在非安全上下文里**根本不给你数据**（不是弹权限框，是事件不触发）。
// 所以"陀螺仪控鼠标""一键读剪贴板""装成 App"这几件事，HTTPS 是前置条件，不是加分项。
//
// 两种模式（配置里 `ca` 决定）：
//   ca=false（默认）  一张自签叶子证书。安卓 Chrome 会警告，点"高级 → 继续访问"就能用，
//                     传感器/剪贴板照常（它们只看 isSecureContext，不看证书是否被信任）。
//                     但**装不了 App**：Chrome 注册 Service Worker 要求证书是被信任的，
//                     绕过警告页不算信任 —— 所以 Web Share Target（分享面板里出现 bedremote）走不通。
//   ca=true           首启生成一张根 CA，再用它签叶子。你把 `http://<电脑>:8765/ca.crt`
//                     这个文件在手机上装成 CA 证书（设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书），
//                     Chrome 就完全信任这个地址：不再警告，SW 能注册，能"添加到主屏幕"当 App，
//                     分享面板里也会出现它。代价是你手机上多信任一个自己造的根证书。
//
// 为什么同一个端口：手机书签、二维码、防火墙规则都只认一个端口。
// 明文 HTTP 请求的第一个字节一定是方法名的字母，TLS 的第一个字节固定是 0x16（handshake），
// peek 一个字节就能分开 —— 于是 http:// 和 https:// 都能用，老的书签不会突然坏掉。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

static class Tls
{
    const string PfxName = "bedremote.pfx";
    const string CaPfxName = "bedremote-ca.pfx";
    const string CaPemName = "bedremote-ca.crt";
    const string CaCN = "bedremote-ca";
    static readonly string Pw = "bedremote-local";   // 私钥本来就躺在用户目录里，这个密码只是让 PFX 能被加载

    public static string Status = "";               // 给日志/界面看的一句话结论
    public static bool UsingCa;                     // 当前这张叶子是不是 CA 签的（决定 /ca.crt 有没有意义）

    static string Join(IList<string> a) { var s = new StringBuilder(); for (int i = 0; i < a.Count; i++) { if (i > 0) s.Append(','); s.Append(a[i]); } return s.ToString(); }

    // 拿证书（必要时重签）。失败返回 null = 只跑明文，服务不会因此起不来。
    public static X509Certificate2 Ensure(string dir, IList<string> ips, Action<string> log, bool useCa)
    {
        try
        {
            Directory.CreateDirectory(dir);
            X509Certificate2 ca = null;
            if (useCa)
            {
                ca = LoadOrMakeCa(dir, log);
                if (ca == null) { useCa = false; if (log != null) log("[https] 根 CA 没弄成，退回自签叶子"); }
            }
            UsingCa = useCa;

            string pfx = Path.Combine(dir, PfxName);
            if (File.Exists(pfx))
            {
                try
                {
                    var c = Load(pfx);
                    string why = NeedResign(c, ips, useCa);
                    if (why == null)
                    {
                        Status = (useCa ? "CA 签的证书可用，到 " : "自签证书可用，到 ") + c.NotAfter.ToString("yyyy-MM-dd") + " 过期";
                        return c;
                    }
                    if (log != null) log("[https] 重新签发叶子证书：" + why);
                }
                catch (Exception ex) { if (log != null) log("[https] 旧证书读不出来，重签：" + ex.Message); }
            }
            var nc = useCa ? NewLeaf(ca, ips) : NewSelfSigned(ips);
            try { File.WriteAllBytes(pfx, nc.Export(X509ContentType.Pfx, Pw)); }
            catch (Exception ex) { if (log != null) log("[https] 证书存盘失败（这次启动只用内存里那份）：" + ex.Message); }
            Status = (useCa ? "已用自建 CA 签发叶子证书" : "已签发自签证书") + "，SAN=" + Join(ips);
            if (log != null) log("[https] " + Status + (useCa ? "；手机装 " + CaPemName + " 后不再警告" : ""));
            return nc;
        }
        catch (Exception ex)
        {
            Status = "证书生成失败：" + ex.Message;
            if (log != null) log("[https] " + Status + " —— 只跑 http://，陀螺仪/剪贴板用不了");
            return null;
        }
    }

    static X509Certificate2 Load(string pfx)
    {
        var c = new X509Certificate2(File.ReadAllBytes(pfx), Pw, X509KeyStorageFlags.Exportable);
        if (c.HasPrivateKey) return c;
        return new X509Certificate2(File.ReadAllBytes(pfx), Pw,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    static X509Certificate2 LoadOrMakeCa(string dir, Action<string> log)
    {
        string pfx = Path.Combine(dir, CaPfxName), pem = Path.Combine(dir, CaPemName);
        if (File.Exists(pfx))
        {
            try
            {
                var c = Load(pfx);
                if (c.NotAfter - DateTime.Now > TimeSpan.FromDays(60))
                {
                    if (!File.Exists(pem)) File.WriteAllText(pem, ToPem(c.RawData));
                    return c;
                }
                if (log != null) log("[https] 根 CA 快过期了，重签（手机上装过的 CA 要重新装一次）");
            }
            catch (Exception ex) { if (log != null) log("[https] 根 CA 读不出来，重做：" + ex.Message); }
        }
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=" + CaCN, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            // basicConstraints: CA=TRUE（关键位，手机不认就当普通证书）
            req.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.19"),
                new byte[] { 0x30, 0x03, 0x01, 0x01, 0xFF }, true));
            // keyUsage: keyCertSign | cRLSign
            req.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.15"),
                new byte[] { 0x03, 0x02, 0x01, 0xC4 }, false));
            var ca = req.CreateSelfSigned(DateTime.Now.AddDays(-2), DateTime.Now.AddYears(10));
            // CreateSelfSigned 回来的证书**已经带着私钥**，再 CopyWithPrivateKey 会抛"该证书已有关联的私钥"；
            // 而 CA 签叶子（NewLeaf）回来的那张是公钥，才需要拼私钥。所以这里要看 HasPrivateKey。
            var withKey = ca.HasPrivateKey ? ca : ca.CopyWithPrivateKey(rsa);
            File.WriteAllBytes(pfx, withKey.Export(X509ContentType.Pfx, Pw));
            File.WriteAllText(pem, ToPem(withKey.RawData));
            if (log != null) log("[https] 已生成根 CA：" + pem + "（手机要装的就是这个文件）");
            return withKey;
        }
    }

    static string ToPem(byte[] der)
    {
        var sb = new StringBuilder();
        sb.Append("-----BEGIN CERTIFICATE-----\n");
        string b64 = Convert.ToBase64String(der);
        for (int i = 0; i < b64.Length; i += 64) sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        sb.Append("-----END CERTIFICATE-----\n");
        return sb.ToString();
    }

    // 给手机下载的那张根证书（PEM）。没开 ca 模式就没有。
    public static byte[] CaPem(string dir)
    {
        try
        {
            string pem = Path.Combine(dir, CaPemName);
            if (!UsingCa || !File.Exists(pem)) return null;
            return Encoding.ASCII.GetBytes(File.ReadAllText(pem));
        }
        catch { return null; }
    }

    // null = 不用重签；否则返回原因
    static string NeedResign(X509Certificate2 c, IList<string> ips, bool useCa)
    {
        bool selfSigned = c.Subject == c.Issuer;
        if (useCa && selfSigned) return "换成了 ca 模式（叶子要由根 CA 重新签一次）";
        if (!useCa && !selfSigned) return "换回了自签模式";
        if (c.NotAfter - DateTime.Now < TimeSpan.FromDays(30)) return "快过期（" + c.NotAfter.ToString("yyyy-MM-dd") + "）";
        var have = IpSan(c);
        var want = new List<string>();
        for (int i = 0; i < ips.Count; i++) want.Add(ips[i]);
        want.Sort();
        var missing = new List<string>();
        for (int i = 0; i < want.Count; i++) if (Array.IndexOf(have, want[i]) < 0) missing.Add(want[i]);
        if (missing.Count > 0) return "SAN 里缺 " + string.Join(",", missing.ToArray()) + "（换了网卡或 DHCP 重新分了 IP）";
        return null;
    }

    static string[] IpSan(X509Certificate2 c)
    {
        var list = new List<string>();
        foreach (var e in c.Extensions)
        {
            if (e.Oid.Value != "2.5.29.17") continue;
            // Format(true) 形如 "IP Address=192.0.2.10\r\nDNS Name=localhost"
            foreach (var raw in e.Format(true).Split('\n'))
            {
                var l = raw.Trim();
                if (l.StartsWith("IP Address=")) list.Add(l.Substring(11).Trim());
            }
        }
        list.Sort();
        return list.ToArray();
    }

    static void AddSans(CertificateRequest req, IList<string> ips)
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        for (int i = 0; i < ips.Count; i++)
        {
            IPAddress a;
            if (IPAddress.TryParse(ips[i], out a)) san.AddIpAddress(a);
        }
        req.CertificateExtensions.Add(san.Build());
        // keyUsage: digitalSignature | keyEncipherment（.NET Framework 没有 X509KeyUsageExtension，手搓 DER）
        req.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.15"),
            new byte[] { 0x03, 0x02, 0x05, 0xA0 }, false));
        // extKeyUsage: serverAuth(1.3.6.1.5.5.7.3.1)
        req.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.37"),
            new byte[] { 0x30, 0x0A, 0x06, 0x08, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01 }, false));
    }

    static X509Certificate2 NewSelfSigned(IList<string> ips)
    {
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=bedremote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            AddSans(req, ips);
            using (var cert = req.CreateSelfSigned(DateTime.Now.AddDays(-2), DateTime.Now.AddYears(10)))
                return new X509Certificate2(cert.Export(X509ContentType.Pfx, Pw), Pw, X509KeyStorageFlags.Exportable);
        }
    }

    // CA 签的叶子：有效期短一点无所谓，到期自己会重签，手机上装的始终是那张根 CA
    static X509Certificate2 NewLeaf(X509Certificate2 ca, IList<string> ips)
    {
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=bedremote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            AddSans(req, ips);
            var nb = DateTime.Now.AddDays(-2);
            using (var pub = req.Create(ca, new DateTimeOffset(nb), new DateTimeOffset(nb).AddYears(2), new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }))
            using (var full = pub.HasPrivateKey ? pub : pub.CopyWithPrivateKey(rsa))
                return new X509Certificate2(full.Export(X509ContentType.Pfx, Pw), Pw, X509KeyStorageFlags.Exportable);
        }
    }

    // peek 第一个字节：0x16 = TLS ClientHello，别的都当明文 HTTP。
    // 返回 null 表示这条连接直接关掉（握手失败/对端提前断开）。
    public static Stream Upgrade(TcpClient c, X509Certificate2 cert)
    {
        NetworkStream net = c.GetStream();
        if (cert == null) return net;
        int b;
        try { b = net.ReadByte(); }
        catch { return null; }
        if (b < 0) return null;
        var head = new Pushback(net, (byte)b);
        if (b != 0x16) return head;                       // 明文：把那个字节还回去，照旧走 http
        var ssl = new SslStream(head, false);
        try
        {
            ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
            return ssl;
        }
        catch (Exception)
        {
            try { ssl.Dispose(); } catch { }
            return null;
        }
    }

    // 只多缓存一个字节的最小流。SslStream 要看到完整的 ClientHello，第一个字节已经被我 peek 掉了。
    class Pushback : Stream
    {
        readonly Stream inner;
        readonly byte[] pre = new byte[1];
        int at = 0;                       // 0=还有存货，1=吃完了
        public Pushback(Stream inner, byte first) { this.inner = inner; pre[0] = first; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override void Flush() { inner.Flush(); }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; } }
        public override int Read(byte[] buf, int off, int cnt)
        {
            if (at == 0 && cnt > 0) { buf[off] = pre[0]; at = 1; return 1; }
            return inner.Read(buf, off, cnt);
        }
        public override int ReadByte()
        {
            if (at == 0) { at = 1; return pre[0]; }
            return inner.ReadByte();
        }
        public override void Write(byte[] buf, int off, int cnt) { inner.Write(buf, off, cnt); }
        public override void WriteByte(byte b) { inner.WriteByte(b); }
        public override long Seek(long o, SeekOrigin s) { return inner.Seek(o, s); }
        public override void SetLength(long v) { inner.SetLength(v); }
        protected override void Dispose(bool disposing) { try { if (disposing) inner.Dispose(); } catch { } base.Dispose(disposing); }
    }
}
