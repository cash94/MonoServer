// Заблокированные адреса — кому служба не отвечает совсем (веб-интерфейс, API,
// потоки). Правило — адрес (1.2.3.4, 2001:db8::1) или подсеть (1.2.3.0/24,
// 2001:db8::/32). Хранится в blocked-ips.txt в данных службы: строка — правило,
// после # — заметка; файл можно править руками (перечитывается при изменении).
//
// Проверка — дважды: при приёме соединения (адрес сокета — отказ без единого байта)
// и в Handle по адресу клиента (за nginx — из X-Forwarded-For, ClientIp): 403.
// Идущий поток с такого адреса обрывается на следующей порции (ServeFile смотрит
// Version). Loopback не блокируется никогда: приложение на этом же устройстве
// (Android, Tizen) ходит через 127.0.0.1, и блокировкой его было бы не вернуть.
//
// Заодно — недавние адреса клиентов (Seen): кто, когда и что спрашивал — по ним
// в веб-интерфейсе видно, кого блокировать.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TorrStreamTorrServer
{
    public class IpBlocklist
    {
        public sealed class Rule
        {
            public string Text;         // как в файле: адрес или подсеть
            public string Note = "";
            public long Added;          // unix
            public long Hits;           // отказов с запуска
            public long LastHit;        // unix
            internal byte[] Net;
            internal int Bits;
            internal long LoggedMs = -1;
        }

        public sealed class Client
        {
            public string Ip, Who = "", What = "";
            public long First, Last, Count;   // unix, unix, запросов
        }

        const int MaxSeen = 300;
        const long LogEveryMs = 10 * 60 * 1000;

        readonly string path;
        readonly Action<string> log;
        readonly object sync = new object();
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        List<Rule> rules = new List<Rule>();
        readonly Dictionary<string, Client> seen = new Dictionary<string, Client>(StringComparer.Ordinal);
        DateTime fileTime;
        long nextFileCheckMs;
        int version;

        /// <summary>Меняется при каждом изменении списка — ServeFile по нему перепроверяет свой адрес</summary>
        public int Version => Volatile.Read(ref version);

        public IpBlocklist(string dataDir, Action<string> log)
        {
            path = dataDir != null ? Path.Combine(dataDir, "blocked-ips.txt") : null;
            this.log = log ?? (_ => { });
            Load();
        }

        // ==================== правила ====================

        /// <summary>Правило из текста; null и error — не адрес и не подсеть</summary>
        public static Rule Parse(string text, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) { error = "пустой адрес"; return null; }
            string addr = text;
            int bits = -1;
            int slash = text.IndexOf('/');
            if (slash >= 0)
            {
                addr = text.Substring(0, slash).Trim();
                if (!int.TryParse(text.Substring(slash + 1).Trim(), out bits) || bits < 0) { error = "неверная маска: " + text; return null; }
            }
            if (!IPAddress.TryParse(addr, out var ip) || addr.IndexOf(':') < 0 && addr.Count(ch => ch == '.') != 3)
            {
                error = "не IP-адрес: " + text;
                return null;
            }
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            int max = bytes.Length * 8;
            if (bits < 0) bits = max;
            if (bits > max) { error = "маска больше /" + max + ": " + text; return null; }
            if (bits < (bytes.Length == 4 ? 8 : 16)) { error = "слишком широкая подсеть: " + text; return null; }
            // Подсеть — по её началу: 1.2.3.77/24 → 1.2.3.0/24
            for (int i = 0; i < bytes.Length; i++)
            {
                int keep = Math.Max(0, Math.Min(8, bits - i * 8));
                bytes[i] &= (byte)(0xff << (8 - keep));
            }
            string canon = new IPAddress(bytes).ToString() + (bits < max ? "/" + bits : "");
            return new Rule { Text = canon, Net = bytes, Bits = bits };
        }

        static bool Contains(Rule r, byte[] b)
        {
            if (b.Length != r.Net.Length) return false;
            int bits = r.Bits;
            for (int i = 0; i < b.Length && bits > 0; i++, bits -= 8)
            {
                int mask = bits >= 8 ? 0xff : (0xff << (8 - bits)) & 0xff;
                if ((b[i] & mask) != r.Net[i]) return false;
            }
            return true;
        }

        static byte[] Bytes(string ip)
        {
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var a)) return null;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return null;
            return a.GetAddressBytes();
        }

        /// <summary>Адрес, который можно заблокировать (IP и не loopback)</summary>
        public static bool Blockable(string ip) { return Bytes(ip) != null; }

        /// <summary>Правило, под которое попадает адрес; null — не заблокирован (и loopback — всегда null)</summary>
        public Rule Match(string ip)
        {
            CheckFile();
            var list = rules;
            if (list.Count == 0) return null;
            var b = Bytes(ip);
            if (b == null) return null;
            foreach (var r in list) if (Contains(r, b)) return r;
            return null;
        }

        /// <summary>Отказ адресу: счётчик правила и строка в журнал (с адреса — раз в 10 минут)</summary>
        public bool Refuse(string ip)
        {
            var r = Match(ip);
            if (r == null) return false;
            long now = clock.ElapsedMilliseconds;
            bool write;
            lock (sync)
            {
                r.Hits++;
                r.LastHit = Accounts.UnixNow();
                write = r.LoggedMs < 0 || now - r.LoggedMs >= LogEveryMs;
                if (write) r.LoggedMs = now;
            }
            if (write) log("блокировка: " + ip + (r.Text != ip ? " (" + r.Text + ")" : "") + " — не пускаем, отказов " + r.Hits);
            return true;
        }

        public List<Rule> Rules()
        {
            CheckFile();
            lock (sync) return rules.ToList();
        }

        /// <summary>Добавить правила; ошибка — текст для веб-интерфейса</summary>
        public string Add(IEnumerable<string> texts, string note, string selfIp)
        {
            var add = new List<Rule>();
            foreach (var t in texts)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                var r = Parse(t, out string err);
                if (r == null) return err;
                if (IPAddress.IsLoopback(new IPAddress(r.Net))) return r.Text + " — адрес этого устройства (127.0.0.1, ::1) не блокируется";
                var self = Bytes(selfIp);
                if (self != null && Contains(r, self)) return r.Text + " — это ваш адрес (" + selfIp + "): заблокировали бы себя";
                r.Note = (note ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                r.Added = Accounts.UnixNow();
                add.Add(r);
            }
            if (add.Count == 0) return "пустой адрес";
            lock (sync)
            {
                var list = rules.ToList();
                foreach (var r in add)
                {
                    int at = list.FindIndex(x => x.Text == r.Text);
                    if (at >= 0) { if (r.Note.Length > 0) list[at].Note = r.Note; }
                    else list.Add(r);
                }
                rules = list;
                Save();
            }
            log("блокировка: добавлено " + string.Join(", ", add.Select(r => r.Text)) + (add[0].Note.Length > 0 ? " — " + add[0].Note : ""));
            return null;
        }

        public int Remove(IEnumerable<string> texts)
        {
            var del = new HashSet<string>(texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Parse(t, out _)?.Text ?? t.Trim()), StringComparer.OrdinalIgnoreCase);
            int n;
            lock (sync)
            {
                var list = rules.Where(r => !del.Contains(r.Text)).ToList();
                n = rules.Count - list.Count;
                if (n == 0) return 0;
                rules = list;
                Save();
            }
            log("блокировка: снята с " + string.Join(", ", del));
            return n;
        }

        // ==================== файл ====================

        void Load()
        {
            if (path == null) return;
            var list = new List<Rule>();
            try
            {
                if (!File.Exists(path)) { lock (sync) { rules = list; fileTime = default; } return; }
                fileTime = File.GetLastWriteTimeUtc(path);
                var old = rules.ToDictionary(r => r.Text);
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string note = "";
                    int hash = line.IndexOf('#');
                    if (hash >= 0) { note = line.Substring(hash + 1).Trim(); line = line.Substring(0, hash).Trim(); }
                    // «правило  добавлено  заметка»: число после правила — когда добавлено
                    long added = 0;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (parts.Length > 1) long.TryParse(parts[1], out added);
                    var r = Parse(parts[0], out string err);
                    if (r == null) { log("блокировка: " + Path.GetFileName(path) + ": " + err); continue; }
                    if (IPAddress.IsLoopback(new IPAddress(r.Net))) { log("блокировка: " + r.Text + " — адрес этого устройства не блокируется, пропущен"); continue; }
                    if (list.Any(x => x.Text == r.Text)) continue;
                    r.Note = note;
                    r.Added = added;
                    if (old.TryGetValue(r.Text, out var was)) { r.Hits = was.Hits; r.LastHit = was.LastHit; r.LoggedMs = was.LoggedMs; }
                    list.Add(r);
                }
            }
            catch (Exception e) { log("блокировка: не прочитать " + path + ": " + e.Message); return; }
            lock (sync) rules = list;
            Interlocked.Increment(ref version);
        }

        // Файл правили руками — перечитать (проверка не чаще раза в 5 с)
        void CheckFile()
        {
            if (path == null) return;
            long now = clock.ElapsedMilliseconds;
            if (now < Volatile.Read(ref nextFileCheckMs)) return;
            Volatile.Write(ref nextFileCheckMs, now + 5000);
            try
            {
                var t = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                if (t != fileTime) Load();
            }
            catch { }
        }

        // Под sync
        void Save()
        {
            Interlocked.Increment(ref version);
            if (path == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("# MonoServer: заблокированные адреса. Строка: адрес или подсеть [время добавления] [# заметка]\n");
                foreach (var r in rules)
                    sb.Append(r.Text).Append(' ').Append(r.Added).Append(r.Note.Length > 0 ? " # " + r.Note : "").Append('\n');
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                fileTime = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception e) { log("блокировка: не записать " + path + ": " + e.Message); }
        }

        // ==================== недавние клиенты ====================

        /// <summary>Запрос с адреса: who — кто вошёл (или пусто), what — метод и путь</summary>
        public void Saw(string ip, string who, string what)
        {
            if (string.IsNullOrEmpty(ip) || ip == "?") return;
            long now = Accounts.UnixNow();
            lock (seen)
            {
                if (!seen.TryGetValue(ip, out var c))
                {
                    if (seen.Count >= MaxSeen)
                    {
                        var oldest = seen.Values.OrderBy(x => x.Last).Take(MaxSeen / 5).Select(x => x.Ip).ToList();
                        foreach (var k in oldest) seen.Remove(k);
                    }
                    seen[ip] = c = new Client { Ip = ip, First = now };
                }
                c.Last = now;
                c.Count++;
                if (!string.IsNullOrEmpty(who)) c.Who = who;
                if (!string.IsNullOrEmpty(what)) c.What = what.Length > 80 ? what.Substring(0, 80) + "…" : what;
            }
        }

        public List<Client> Seen()
        {
            lock (seen) return seen.Values.OrderByDescending(x => x.Last).Select(x => new Client { Ip = x.Ip, Who = x.Who, What = x.What, First = x.First, Last = x.Last, Count = x.Count }).ToList();
        }

        /// <summary>Адрес сокета без преобразований ::ffff:</summary>
        public static string SocketIp(TcpClient client)
        {
            try
            {
                var a = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
            }
            catch { return null; }
        }
    }
}
