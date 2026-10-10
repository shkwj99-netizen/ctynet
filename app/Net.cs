using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CtyNet
{
    public static class Net
    {
        public static readonly GeoDb Geo = GeoDb.LoadEmbedded();

        // 측정 대상 목록 (나중에 쉽게 추가 가능)
        public sealed class Target
        {
            public string Id;
            public string Label;   // 화면에 보이는 이름
            public string Host;    // 실제로 핑 보낼 주소
            public string Kind;    // site / dns / game
        }

        public static readonly List<Target> Targets = new()
        {
            new Target{ Id="naver",  Label="네이버",          Host="www.naver.com",      Kind="site" },
            new Target{ Id="kakao",  Label="카카오",          Host="www.daum.net",       Kind="site" },
            new Target{ Id="cf",     Label="클라우드플레어",   Host="1.1.1.1",            Kind="dns"  },
            new Target{ Id="google", Label="구글",            Host="www.google.com",     Kind="site" },
            new Target{ Id="gdns",   Label="구글 DNS",        Host="8.8.8.8",            Kind="dns"  },
            new Target{ Id="github", Label="깃허브",          Host="github.com",         Kind="site" },
            new Target{ Id="yahoojp",Label="일본 야후",       Host="www.yahoo.co.jp",    Kind="site" },
            new Target{ Id="aws_sg", Label="싱가포르 AWS",    Host="ec2.ap-southeast-1.amazonaws.com", Kind="site" },
            new Target{ Id="bbc",    Label="영국 BBC",        Host="www.bbc.co.uk",      Kind="site" },
            new Target{ Id="riotna", Label="라이엇 북미",     Host="104.160.141.3",      Kind="game" },
            new Target{ Id="steam",  Label="스팀",            Host="store.steampowered.com", Kind="site" },
        };

        public static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                // IPv4 우선
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetworkV6) return a;
            }
            catch { }
            return null;
        }

        public sealed class PingResult
        {
            public string ip;
            public double lat, lon;
            public string city = "", country = "", org = "";
            public bool located;
            public long rtt = -1;   // 평균 왕복시간(ms), -1=실패
            public int loss;        // 손실률 %
            public bool ok;
        }

        // 한 대상에 핑 4번
        public static PingResult PingHost(string host, int count = 4, int timeout = 1200)
        {
            var res = new PingResult();
            var ip = Resolve(host);
            if (ip == null) return res;
            res.ip = ip.ToString();
            var g = Geo.Lookup(ip);
            res.located = g.Located; res.lat = g.Lat; res.lon = g.Lon;
            res.city = g.City; res.country = g.Country; res.org = g.Org;

            var p = new Ping();
            long sum = 0; int good = 0; int sent = 0;
            byte[] buf = new byte[32];
            for (int i = 0; i < count; i++)
            {
                sent++;
                try
                {
                    var r = p.Send(ip, timeout, buf);
                    if (r.Status == IPStatus.Success) { sum += r.RoundtripTime; good++; }
                }
                catch { }
            }
            res.loss = sent == 0 ? 100 : (int)Math.Round((sent - good) * 100.0 / sent);
            if (good > 0) { res.rtt = (long)Math.Round((double)sum / good); res.ok = true; }
            return res;
        }

        public sealed class Hop
        {
            public int ttl;
            public string ip;
            public long rtt = -1;
            public double lat, lon;
            public string city = "", country = "", org = "";
            public bool located;
            public bool isPrivate;
            public bool done;   // 목적지 도달
            public bool timeout;
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            if (b[0] == 127) return true;
            return false;
        }

        // 경로 추적: TTL을 1부터 올리며 중간 지점을 찾음. 홉이 나올 때마다 콜백
        public static void Trace(string host, Action<Hop> onHop, int maxHops = 30, int timeout = 1200)
        {
            var dest = Resolve(host);
            if (dest == null) { onHop(new Hop { ttl = 0, done = true, timeout = true }); return; }
            var p = new Ping();
            byte[] buf = new byte[32];
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                var hop = new Hop { ttl = ttl };
                try
                {
                    var opt = new PingOptions(ttl, true);
                    var sw = Stopwatch.StartNew();
                    var r = p.Send(dest, timeout, buf, opt);
                    sw.Stop();
                    if (r.Status == IPStatus.TimedOut || r.Address == null || r.Address.Equals(IPAddress.Any))
                    {
                        hop.timeout = true;
                    }
                    else
                    {
                        var hip = r.Address;
                        hop.ip = hip.ToString();
                        hop.rtt = r.RoundtripTime > 0 ? r.RoundtripTime : sw.ElapsedMilliseconds;
                        if (IsPrivate(hip)) hop.isPrivate = true;
                        else
                        {
                            var g = Geo.Lookup(hip);
                            hop.located = g.Located; hop.lat = g.Lat; hop.lon = g.Lon;
                            hop.city = g.City; hop.country = g.Country; hop.org = g.Org;
                        }
                        if (r.Status == IPStatus.Success) hop.done = true;
                    }
                }
                catch { hop.timeout = true; }
                onHop(hop);
                if (hop.done) break;
            }
        }

        // 내 공인 IP와 위치
        public sealed class MeInfo
        {
            public string ip;
            public double lat, lon;
            public string city = "", country = "", org = "";
            public bool located;
        }

        public static async Task<MeInfo> WhoAmI()
        {
            var me = new MeInfo();
            string ipText = await FetchPublicIp();
            if (ipText != null && IPAddress.TryParse(ipText, out var ip))
            {
                me.ip = ip.ToString();
                var g = Geo.Lookup(ip);
                me.located = g.Located; me.lat = g.Lat; me.lon = g.Lon;
                me.city = g.City; me.country = g.Country; me.org = g.Org;
            }
            return me;
        }

        private static async Task<string> FetchPublicIp()
        {
            string[] urls = {
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://ifconfig.me/ip",
                "https://ipv4.icanhazip.com"
            };
            using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            hc.DefaultRequestHeaders.Add("User-Agent", "ctyNet");
            foreach (var u in urls)
            {
                try
                {
                    var s = (await hc.GetStringAsync(u)).Trim();
                    if (IPAddress.TryParse(s, out _)) return s;
                }
                catch { }
            }
            return null;
        }
    }
}
