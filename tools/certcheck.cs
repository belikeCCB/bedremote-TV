// 证书自检：Chrome 是严格的验证器（basicConstraints/keyUsage/EKU/SAN/有效期一个不对就"不受信任"），
// 而我自己手拼过 CA 的 basicConstraints 和 keyUsage 的 DER —— 所以先验自己，别赖手机。
// 用法: certcheck.exe <ca.crt> <leaf.pfx> <期望的IP>
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

static class CertCheck
{
    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.WriteLine("用法: certcheck <ca.crt> <leaf.pfx> <ip>"); return 2; }
        var ca = new X509Certificate2(a[0]);
        var leaf = new X509Certificate2(a[1], "bedremote-local");
        Dump("CA", ca);
        Dump("叶子", leaf);
        Console.WriteLine("--- CA 是不是真的能当根（自签 + 是否带私钥不影响） ---");
        Console.WriteLine("  Subject == Issuer : " + (ca.Subject == ca.Issuer));
        Console.WriteLine("  叶子 Issuer == CA Subject : " + (leaf.Issuer == ca.Subject));
        Console.WriteLine("--- 链一次：只看「除了根不受信之外还有没有别的毛病」 ---");
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ExtraStore.Add(ca);       // 把 CA 放进中间存储，让它能拼出链
        bool ok = chain.Build(leaf);
        Console.WriteLine("  build ok: " + ok + "（False 是预期的：这台电脑没装这张根证书）");
        bool onlyUntrusted = true;
        foreach (var st in chain.ChainStatus)
        {
            Console.WriteLine("   status: " + st.Status + "  " + st.StatusInformation.Trim());
            if (st.Status != X509ChainStatusFlags.UntrustedRoot) onlyUntrusted = false;
        }
        Console.WriteLine("  只有 UntrustedRoot 这一条: " + onlyUntrusted);
        if (!onlyUntrusted) Console.WriteLine("  ⚠ 还有别的错 —— 那就是证书本身的问题，装到手机上也好不了");
        Console.WriteLine("  链里拼出 " + chain.ChainElements.Count + " 层：");
        foreach (X509ChainElement el in chain.ChainElements) Console.WriteLine("    " + el.Certificate.Subject);
        Console.WriteLine("--- 叶子里的 SAN 有没有你要用的那个 IP ---");
        string want = a[2];
        bool ipFound = false;
        var sanExt = leaf.Extensions["2.5.29.17"];
        if (sanExt != null)
        {
            string s = sanExt.Format(false);
            ipFound = s.IndexOf(want, StringComparison.Ordinal) >= 0;
            Console.WriteLine("  SAN 原文: " + s.Replace("\n", " "));
        }
        Console.WriteLine("  含 " + want + " : " + ipFound);
        Console.WriteLine("--- 有效期（Chrome 对「私信任的根」不套 398 天限制，但先说清楚） ---");
        Console.WriteLine("  CA   " + ca.NotBefore.ToString("yyyy-MM-dd") + " → " + ca.NotAfter.ToString("yyyy-MM-dd"));
        Console.WriteLine("  leaf " + leaf.NotBefore.ToString("yyyy-MM-dd") + " → " + leaf.NotAfter.ToString("yyyy-MM-dd") + "  天数=" + (leaf.NotAfter - leaf.NotBefore).TotalDays);
        Console.WriteLine("  leaf 到期的剩余天数: " + (int)(leaf.NotAfter - DateTime.Now).TotalDays);
        return 0;
    }

    static void Dump(string who, X509Certificate2 c)
    {
        Console.WriteLine("=== " + who + " ===");
        Console.WriteLine("  Subject : " + c.Subject);
        Console.WriteLine("  Issuer  : " + c.Issuer);
        Console.WriteLine("  Serial  : " + c.SerialNumber);
        Console.WriteLine("  签名算法 : " + c.SignatureAlgorithm.FriendlyName);
        Console.WriteLine("  公钥     : " + c.PublicKey.Oid.FriendlyName + " " + c.PublicKey.Key.KeySize + "bit");
        Console.WriteLine("  HasPrivateKey: " + c.HasPrivateKey);
        Console.WriteLine("  扩展 " + c.Extensions.Count + " 个：");
        foreach (var e in c.Extensions)
            Console.WriteLine("    " + e.Oid.Value + "  critical=" + e.Critical + "  " + e.Oid.FriendlyName + "  " + Short(e.Format(false)));
    }

    static string Short(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
