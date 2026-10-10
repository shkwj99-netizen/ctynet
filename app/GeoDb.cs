using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace CtyNet
{
    // 공개 IP 위치 데이터(DB-IP Lite, IPtoASN)를 읽어
    // IP 주소를 나라 / 도시 / 통신사로 바꿔 주는 조회기
    public sealed class GeoHit
    {
        public double Lat;
        public double Lon;
        public string Country = "";
        public string City = "";
        public int Asn;
        public string Org = "";
        public bool Located;
    }

    public sealed class GeoDb
    {
        private const uint NONE = 0xFFFFFFFF;

        private string[] _cc = Array.Empty<string>();
        private short[] _la = Array.Empty<short>();
        private short[] _lo = Array.Empty<short>();
        private string[] _city = Array.Empty<string>();

        private uint[] _v4s = Array.Empty<uint>();
        private uint[] _v4i = Array.Empty<uint>();
        private ulong[] _v6s = Array.Empty<ulong>();
        private uint[] _v6i = Array.Empty<uint>();

        private int[] _asn = Array.Empty<int>();
        private string[] _org = Array.Empty<string>();

        private uint[] _a4s = Array.Empty<uint>();
        private uint[] _a4i = Array.Empty<uint>();
        private ulong[] _a6s = Array.Empty<ulong>();
        private uint[] _a6i = Array.Empty<uint>();

        public bool Ready { get; private set; }

        public static GeoDb LoadEmbedded()
        {
            var db = new GeoDb();
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("geo.bin.gz", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                if (name == null) return db;
                using var rs = asm.GetManifestResourceStream(name);
                using var gz = new GZipStream(rs, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                db.Parse(ms.ToArray());
                db.Ready = true;
            }
            catch { db.Ready = false; }
            return db;
        }

        private void Parse(byte[] d)
        {
            int p = 0;
            // magic + version
            p += 4;
            uint ver = ReadU32(d, ref p);
            _ = ver;

            uint nLoc = ReadU32(d, ref p);
            _cc = new string[nLoc];
            _la = new short[nLoc];
            _lo = new short[nLoc];
            _city = new string[nLoc];
            for (uint i = 0; i < nLoc; i++)
            {
                _cc[i] = Encoding.ASCII.GetString(d, p, 2); p += 2;
                _la[i] = (short)ReadU16(d, ref p);
                _lo[i] = (short)ReadU16(d, ref p);
                int len = d[p]; p += 1;
                _city[i] = Encoding.UTF8.GetString(d, p, len); p += len;
            }

            ReadRanges32(d, ref p, out _v4s, out _v4i);
            ReadRanges64(d, ref p, out _v6s, out _v6i);

            uint nName = ReadU32(d, ref p);
            _asn = new int[nName];
            _org = new string[nName];
            for (uint i = 0; i < nName; i++)
            {
                _asn[i] = (int)ReadU32(d, ref p);
                int len = (int)ReadU16(d, ref p);
                _org[i] = Encoding.UTF8.GetString(d, p, len); p += len;
            }

            ReadRanges32(d, ref p, out _a4s, out _a4i);
            ReadRanges64(d, ref p, out _a6s, out _a6i);
        }

        private static void ReadRanges32(byte[] d, ref int p, out uint[] starts, out uint[] ids)
        {
            uint n = ReadU32(d, ref p);
            starts = new uint[n];
            for (uint i = 0; i < n; i++) { starts[i] = ReadU32Fast(d, p); p += 4; }
            ids = new uint[n];
            for (uint i = 0; i < n; i++) { ids[i] = ReadU32Fast(d, p); p += 4; }
        }

        private static void ReadRanges64(byte[] d, ref int p, out ulong[] starts, out uint[] ids)
        {
            uint n = ReadU32(d, ref p);
            starts = new ulong[n];
            for (uint i = 0; i < n; i++) { starts[i] = ReadU64Fast(d, p); p += 8; }
            ids = new uint[n];
            for (uint i = 0; i < n; i++) { ids[i] = ReadU32Fast(d, p); p += 4; }
        }

        private static uint ReadU16(byte[] d, ref int p) { uint v = (uint)(d[p] | (d[p + 1] << 8)); p += 2; return v; }
        private static uint ReadU32(byte[] d, ref int p) { uint v = ReadU32Fast(d, p); p += 4; return v; }
        private static uint ReadU32Fast(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        private static ulong ReadU64Fast(byte[] d, int p)
        {
            ulong lo = ReadU32Fast(d, p);
            ulong hi = ReadU32Fast(d, p + 4);
            return lo | (hi << 32);
        }

        // 마지막 start <= key 인 구간을 찾음 (upper_bound - 1)
        private static int UpperMinusOne(uint[] a, uint key)
        {
            int lo = 0, hi = a.Length; // first index > key
            while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] <= key) lo = m + 1; else hi = m; }
            return lo - 1;
        }
        private static int UpperMinusOne(ulong[] a, ulong key)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] <= key) lo = m + 1; else hi = m; }
            return lo - 1;
        }

        public GeoHit Lookup(IPAddress ip)
        {
            var hit = new GeoHit();
            if (!Ready || ip == null) return hit;
            try
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    uint x = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
                    int k = UpperMinusOne(_v4s, x);
                    if (k >= 0) ApplyLoc(_v4i[k], hit);
                    int ak = UpperMinusOne(_a4s, x);
                    if (ak >= 0) ApplyAsn(_a4i[ak], hit);
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    byte[] b = ip.GetAddressBytes(); // 16 bytes, big-endian
                    ulong top = 0;
                    for (int i = 0; i < 8; i++) top = (top << 8) | b[i];
                    int k = UpperMinusOne(_v6s, top);
                    if (k >= 0) ApplyLoc(_v6i[k], hit);
                    int ak = UpperMinusOne(_a6s, top);
                    if (ak >= 0) ApplyAsn(_a6i[ak], hit);
                }
            }
            catch { }
            return hit;
        }

        private void ApplyLoc(uint id, GeoHit hit)
        {
            if (id == NONE || id >= _cc.Length) return;
            hit.Country = _cc[id];
            hit.City = _city[id];
            hit.Lat = _la[id] / 100.0;
            hit.Lon = _lo[id] / 100.0;
            hit.Located = true;
        }

        private void ApplyAsn(uint id, GeoHit hit)
        {
            if (id == NONE || id >= _asn.Length) return;
            hit.Asn = _asn[id];
            hit.Org = _org[id];
        }
    }
}
