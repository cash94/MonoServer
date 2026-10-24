// Порядок и номера файлов — как у TorrServer (server/torr/torrent.go, Status):
// файлы сортируются по пути (utils.CompareStrings — числа как числа, «Серия 2»
// раньше «Серия 10»), id = место в этом списке, с 1. По этому id /stream и
// /play находят файл. Раньше здесь был порядок словаря info, а он бывает
// любым: встречаются раздачи, где серии лежат по размеру (2, 10, 3, 1…), —
// так их и показывали TorrStream и Lampa, а номера файлов расходились с
// TorrServer. Сравнение — по байтам UTF-8, как строки в Go.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TorrStreamTorrServer
{
    static class FileOrder
    {
        static bool IsDigit(byte c) { return c >= '0' && c <= '9'; }

        // Число из цифр в начале; цифр нет или не влезает в int — нет (strconv.Atoi)
        static bool NumberPrefix(byte[] s, int from, out long n)
        {
            n = 0;
            int i = from;
            while (i < s.Length && IsDigit(s[i]))
            {
                n = n * 10 + (s[i] - '0');
                if (n > long.MaxValue / 10) return false;
                i++;
            }
            return i > from;
        }

        static int CompareBytes(byte[] a, int ai, byte[] b, int bi)
        {
            while (ai < a.Length && bi < b.Length)
            {
                if (a[ai] != b[bi]) return a[ai] - b[bi];
                ai++; bi++;
            }
            return (a.Length - ai) - (b.Length - bi);
        }

        /// <summary>utils.CompareStrings из TorrServer: true — first раньше second</summary>
        public static bool Less(string first, string second)
        {
            var a = Encoding.UTF8.GetBytes(first ?? "");
            var b = Encoding.UTF8.GetBytes(second ?? "");
            int p = 0;
            while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
            // Число, на котором кончается общее начало, сравниваем целиком («1» и «10»)
            while (p > 0 && IsDigit(a[p - 1])) p--;
            bool ok1 = NumberPrefix(a, p, out long n1);
            bool ok2 = NumberPrefix(b, p, out long n2);
            if (ok1 && ok2)
            {
                if (n1 == n2) return CompareBytes(a, p, b, p) < 0;
                return n1 < n2;
            }
            if (ok1) return true;
            if (ok2) return false;
            return CompareBytes(a, p, b, p) < 0;
        }

        static int Compare(FileStat x, FileStat y)
        {
            if (Less(x.Path, y.Path)) return -1;
            if (Less(y.Path, x.Path)) return 1;
            return 0;
        }

        /// <summary>Файлы по пути, номера заново — 1, 2, 3… Новый список</summary>
        public static List<FileStat> Apply(IEnumerable<FileStat> files)
        {
            var list = files.ToList();
            list.Sort(Compare);
            for (int i = 0; i < list.Count; i++)
                list[i] = new FileStat { Id = i + 1, Path = list[i].Path, Length = list[i].Length, File = list[i].File };
            return list;
        }
    }
}
