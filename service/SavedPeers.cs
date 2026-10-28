// Сохранённые «жирные» пиры торрента, как peers в resume-данных libtorrent: при
// следующем открытии подключаемся к ним сразу, не дожидаясь трекеров, DHT и PEX
// (трекеры отвечают за 0,5–3 с, DHT — за 5–30 с, PEX — после первых подключений).
// Больше всего это даёт сериалам (торрент закрылся по простою, назавтра открыт
// ради следующей серии — пиры почти те же) и торрентам с медленными трекерами.
//  - Что: до Keep исходящих подключений, приславших за сессию не меньше MinBytes, —
//    больше всего данных первыми. Входящие — нет: у них в адресе случайный порт.
//  - Когда: при закрытии торрента и раз в SnapshotMs (не потерять при падении);
//    в счёт идут и отключившиеся за сессию (их байты — в момент отключения).
//  - Сессия, в которой докачали меньше RealSessionBytes (только метаданные, пара
//    секунд), прежний список не вытесняет: новые — вперёд, прежние остаются.
//  - Не подключился (Unreachable, HandshakeFailed) — из списка вон: адрес сменился.
//    EncryptionNegiotiationFailed — нет: MonoTorrent сразу пробует без шифрования.
// При открытии адреса идут в начало очереди MonoTorrent (AddPeers с prioritise) —
// он берёт из неё по порядку. Файл — peers.json в данных службы.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrStreamTorrServer
{
    public class SavedPeers
    {
        const int Keep = 30;
        const int MaxTorrents = 300;
        const long MinBytes = 4L << 20;
        const long RealSessionBytes = 32L << 20;
        public const long SnapshotMs = 5 * 60 * 1000;

        static readonly SynchronizationContext Loop =
            typeof(ClientEngine).GetField("MainLoop", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SynchronizationContext;
        static readonly MethodInfo AddPeersMethod = typeof(TorrentManager).GetMethod("AddPeers", BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(IEnumerable<PeerInfo>), typeof(InfoHash), typeof(bool), typeof(bool) }, null);
        static readonly MethodInfo TryConnectMethod = typeof(ConnectionManager).GetMethod("TryConnect", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

        sealed class Item
        {
            public string Uri;
            public long Bytes;
        }

        sealed class Entry
        {
            public List<Item> Peers = new List<Item>();
            public long Time;   // unix-время последнего обновления — старые вытесняются
        }

        readonly string path;
        readonly Action<string> log;
        readonly object sync = new object();
        readonly object fileSync = new object();
        readonly Dictionary<string, Entry> saved = new Dictionary<string, Entry>();
        // Кого видели в эту сессию: hash → адрес → байт от него
        readonly Dictionary<string, Dictionary<string, Item>> session = new Dictionary<string, Dictionary<string, Item>>();
        // Сессия кончилась (снимок при закрытии), а MonoTorrent ещё шлёт отключения —
        // их не копить: до следующего открытия (Feed)
        readonly HashSet<string> closed = new HashSet<string>();
        bool dirty;

        public SavedPeers(string path, Action<string> log)
        {
            this.path = path;
            this.log = log;
            try
            {
                if (!File.Exists(path)) return;
                var root = Json.ParseObject(File.ReadAllText(path));
                foreach (var kv in root)
                {
                    if (!(kv.Value is Dictionary<string, object> o)) continue;
                    var e = new Entry { Time = o.Num("t") ?? 0 };
                    if (o.TryGetValue("peers", out var pv) && pv is List<object> list)
                        foreach (var p in list.OfType<Dictionary<string, object>>())
                        {
                            string u = p.Str("u");
                            if (Parse(u) != null) e.Peers.Add(new Item { Uri = u, Bytes = p.Num("b") ?? 0 });
                        }
                    if (e.Peers.Count > 0) saved[kv.Key] = e;
                }
            }
            catch (Exception e) { log("peers.json: " + e.Message); }
        }

        static Uri Parse(string u)
        {
            return !string.IsNullOrEmpty(u) && Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "ipv4" || uri.Scheme == "ipv6") && uri.Port > 0 ? uri : null;
        }

        /// <summary>Адрес — из сохранённых этого торрента (для отчёта: сколько подключилось)</summary>
        public bool IsSaved(string hash, Uri uri)
        {
            if (uri == null) return false;
            string u = uri.ToString();
            lock (sync) return saved.TryGetValue(hash, out var e) && e.Peers.Any(p => p.Uri == u);
        }

        /// <summary>Торрент открыт: сохранённые адреса — в начало очереди MonoTorrent</summary>
        public int Feed(ClientEngine engine, TorrentManager m, string hash)
        {
            List<PeerInfo> peers;
            lock (sync)
            {
                closed.Remove(hash);
                if (Loop == null || AddPeersMethod == null || engine == null || m == null) return 0;
                if (!saved.TryGetValue(hash, out var e)) return 0;
                peers = e.Peers.Select(p => Parse(p.Uri)).Where(u => u != null).Select(u => new PeerInfo(u)).ToList();
            }
            if (peers.Count == 0) return 0;
            // prioritise вставляет по одному в начало — задом наперёд, чтобы лучший был первым.
            // fromTracker: у приватных торрентов MonoTorrent иначе не берёт адреса, а эти
            // от того же трекера и пришли (или подключались к нам сами)
            peers.Reverse();
            Loop.Post(_ =>
            {
                try
                {
                    AddPeersMethod.Invoke(m, new object[] { peers, m.InfoHashes.V1OrV2, true, true });
                    TryConnectMethod?.Invoke(engine.ConnectionManager, null);
                }
                catch (Exception ex) { log(hash + ": сохранённые пиры — " + ex.GetBaseException().Message); }
            }, null);
            return peers.Count;
        }

        /// <summary>Подключиться не вышло — адрес из списка</summary>
        public void Failed(string hash, Uri uri, ConnectionFailureReason reason)
        {
            if (uri == null || reason == ConnectionFailureReason.EncryptionNegiotiationFailed) return;
            string u = uri.ToString();
            lock (sync)
                if (saved.TryGetValue(hash, out var e) && e.Peers.RemoveAll(p => p.Uri == u) > 0)
                    dirty = true;
        }

        /// <summary>Пир отключился или снимок подключённых: запомнить, сколько он дал</summary>
        public void Seen(string hash, PeerId p)
        {
            if (p == null || p.ConnectionDirection != Direction.Outgoing || p.Uri == null) return;
            if (p.Uri.Scheme != "ipv4" && p.Uri.Scheme != "ipv6") return;
            long bytes = p.Monitor.DataBytesReceived;
            string u = p.Uri.ToString();
            lock (sync)
            {
                if (closed.Contains(hash)) return;
                if (!session.TryGetValue(hash, out var s)) session[hash] = s = new Dictionary<string, Item>();
                if (s.TryGetValue(u, out var it)) it.Bytes = Math.Max(it.Bytes, bytes);
                else s[u] = new Item { Uri = u, Bytes = bytes };
            }
        }

        /// <summary>
        /// Новый список из сессии: её лучшие — вперёд, прежние, кого в этот раз не
        /// пробовали, — следом. Настоящая сессия (докачали RealSessionBytes) вытесняет
        /// тех прежних, кто в этот раз подключился, но дал мало. closing — сессия кончилась
        /// </summary>
        public void Commit(string hash, bool closing)
        {
            lock (sync)
            {
                session.TryGetValue(hash, out var s);
                if (closing) { session.Remove(hash); closed.Add(hash); }
                if (s == null || s.Count == 0) return;
                bool real = s.Values.Sum(i => i.Bytes) >= RealSessionBytes;
                var fresh = s.Values.Where(i => i.Bytes >= MinBytes).OrderByDescending(i => i.Bytes).ToList();
                var old = saved.TryGetValue(hash, out var e) ? e.Peers : new List<Item>();
                var rest = old.Where(o => fresh.All(f => f.Uri != o.Uri) && (!real || !s.ContainsKey(o.Uri)));
                var list = fresh.Select(f => new Item { Uri = f.Uri, Bytes = f.Bytes }).Concat(rest).Take(Keep).ToList();
                if (list.Count == 0 && old.Count == 0) return;
                if (list.Count == 0) saved.Remove(hash);
                else saved[hash] = new Entry { Peers = list, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                // Лишние торренты — самые давние
                if (saved.Count > MaxTorrents)
                    foreach (var k in saved.OrderBy(kv => kv.Value.Time).Take(saved.Count - MaxTorrents).Select(kv => kv.Key).ToList())
                        saved.Remove(k);
                dirty = true;
            }
        }

        /// <summary>Торрент удалён совсем</summary>
        public void Forget(string hash)
        {
            lock (sync)
            {
                session.Remove(hash);
                closed.Remove(hash);
                if (saved.Remove(hash)) dirty = true;
            }
        }

        public void Save()
        {
            string text;
            lock (sync)
            {
                if (!dirty) return;
                dirty = false;
                var w = new JsonWriter().Obj();
                foreach (var kv in saved)
                {
                    w.Key(kv.Key).Obj().Prop("t", kv.Value.Time).Key("peers").Arr();
                    foreach (var p in kv.Value.Peers) w.Obj().Prop("u", p.Uri).Prop("b", p.Bytes).EndObj();
                    w.EndArr().EndObj();
                }
                text = w.EndObj().ToString();
            }
            // Снимки торрентов пишут из разных потоков (StopAll — все сразу)
            lock (fileSync)
            {
                try
                {
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, text);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception e) { log("peers.json: " + e.Message); }
            }
        }
    }
}
