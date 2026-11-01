// Samsung и AVI с XviD/DivX — как «samsung tv fix xvid/divx» у TorrServer
// (server/torr/storage/torrstor/reader.go): FourCC видеопотока XVID / DIVX / DX50
// подменяется на MP4V — тот же MPEG-4 Part 2, но телевизоры Samsung без
// лицензии DivX такие файлы не открывают. У TorrServer правка по постоянным
// смещениям 112 и 188 и только для чтения с нуля (и сейчас закомментирована);
// здесь — по разбору заголовка hdrl (strh.fccHandler и strf.biCompression
// каждого видеопотока) и в любом запросе Range, который задевает эти байты.
// DIV3 и прочие MPEG-4 v3 не трогаем: это другой кодек.
using System;
using System.Collections.Generic;
using System.Text;

namespace TorrStreamTorrServer
{
    static class AviFix
    {
        // Сколько начала файла разбираем: hdrl с потоками — первые килобайты, до movi
        public const int HeadSize = 64 * 1024;

        static readonly byte[] Mp4v = Encoding.ASCII.GetBytes("MP4V");
        static readonly string[] Codecs = { "XVID", "DIVX", "DX50" };
        static readonly string[] Clients = { "tizen", "smart-tv", "samsung", "sec_hhp", "maple" };

        public static bool IsAvi(string path)
        {
            return path != null && path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);
        }

        // Плеер телевизора Samsung: AVPlay на Tizen, Orsay (Maple), DLNA (SEC_HHP)
        public static bool ForClient(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return false;
            string ua = userAgent.ToLowerInvariant();
            foreach (var c in Clients) if (ua.Contains(c)) return true;
            return false;
        }

        /// <summary>Смещения FourCC, которые подменить, в первых n байтах файла</summary>
        public static long[] Find(byte[] h, int n)
        {
            var at = new List<long>();
            if (n >= 12 && Tag(h, 0) == "RIFF" && Tag(h, 8) == "AVI ") Walk(h, 12, n, at);
            return at.ToArray();
        }

        /// <summary>Подменить в buf (n байт с позиции pos файла) найденные FourCC</summary>
        public static void Patch(byte[] buf, int n, long pos, long[] at)
        {
            foreach (long o in at)
                for (int i = 0; i < 4; i++)
                {
                    long p = o + i;
                    if (p >= pos && p < pos + n) buf[p - pos] = Mp4v[i];
                }
        }

        // Куски с from до to; false — дошли до movi, дальше заголовков нет
        static bool Walk(byte[] h, int from, int to, List<long> at)
        {
            bool video = false;   // в этом strl strh — видеопоток
            int pos = from;
            while (pos + 8 <= to)
            {
                string id = Tag(h, pos);
                long size = BitConverter.ToUInt32(h, pos + 4);
                int body = pos + 8;
                if (id == "LIST" && body + 4 <= to)
                {
                    string type = Tag(h, body);
                    if (type == "movi") return false;
                    if ((type == "hdrl" || type == "strl") && !Walk(h, body + 4, (int)Math.Min(body + size, to), at)) return false;
                }
                else if (id == "strh" && size >= 8 && body + 8 <= to)
                {
                    video = Tag(h, body) == "vids";
                    if (video && IsCodec(h, body + 4)) at.Add(body + 4);
                }
                else if (id == "strf" && video && size >= 20 && body + 20 <= to)
                {
                    // BITMAPINFOHEADER: biSize, biWidth, biHeight, biPlanes + biBitCount, biCompression
                    if (IsCodec(h, body + 16)) at.Add(body + 16);
                }
                long next = body + size + (size & 1);
                if (next > to) break;
                pos = (int)next;
            }
            return true;
        }

        static bool IsCodec(byte[] h, int at)
        {
            string fcc = Tag(h, at).ToUpperInvariant();
            return Array.IndexOf(Codecs, fcc) >= 0;
        }

        static string Tag(byte[] h, int at)
        {
            return Encoding.ASCII.GetString(h, at, 4);
        }
    }
}
