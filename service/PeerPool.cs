// Свой запас адресов пиров, как у TorrServer (anacrolix держит до 500 известных
// пиров). MonoTorrent 3.0.2 хранит не больше «лимит соединений + 50» адресов
// (TorrentSettings.MaximumPeerDetails): из 300+ адресов от трекеров он брал ~80,
// остальные выбрасывал, а неудачные попытки забывал — и пиры кончались.
// Здесь копятся все адреса от трекеров и DHT; когда очередь MonoTorrent пуста,
// а соединений меньше его лимита, подкладываем ещё не опробованные — понемногу:
// пока очередь MonoTorrent не пуста, он отбрасывает адреса от PEX (самые свежие).
// Опробованные (подключились или нет) второй раз не даём — их снова пришлют трекеры.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrStreamTorrServer
{
    public class PeerPool
    {
        const int Max = 500;
        const int Batch = 15;

        static readonly SynchronizationContext Loop =
            typeof(ClientEngine).GetField("MainLoop", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SynchronizationContext;
        static readonly FieldInfo AvailableField = typeof(PeerManager).GetField("AvailablePeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectingField = typeof(PeerManager).GetField("ConnectingToPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectedField = typeof(PeerManager).GetField("ConnectedPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo PeerInfoProp = AvailableField?.FieldType.GetGenericArguments().FirstOrDefault()?.GetProperty("Info", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        readonly object sync = new object();
        readonly Dictionary<string, PeerInfo> untried = new Dictionary<string, PeerInfo>();
        readonly Queue<string> order = new Queue<string>();          // в порядке поступления
        readonly HashSet<string> tried = new HashSet<string>();

        /// <summary>Не опробованных адресов, которых нет в очереди MonoTorrent (для total_peers)</summary>
        public int Extra { get; private set; }
        /// <summary>Сколько адресов подложено в MonoTorrent</summary>
        public int Fed { get; private set; }
        public int Known { get { lock (sync) return untried.Count + tried.Count; } }
        /// <summary>Сколько IPv6-адресов пришло (от трекеров, DHT, PEX) — для отчёта</summary>
        public int V6Known { get; private set; }

        static string Key(Uri u) { return u == null ? null : u.Host + ":" + u.Port; }

        public void Add(IEnumerable<PeerInfo> peers)
        {
            if (peers == null) return;
            lock (sync)
                foreach (var p in peers)
                {
                    string k = Key(p?.ConnectionUri);
                    if (k == null || tried.Contains(k) || untried.ContainsKey(k)) continue;
                    if (untried.Count >= Max)
                    {
                        // Самый старый — вон: свежие адреса полезнее
                        while (order.Count > 0 && !untried.Remove(order.Dequeue())) { }
                    }
                    untried[k] = p;
                    order.Enqueue(k);
                    if (p.ConnectionUri.Scheme == "ipv6") V6Known++;
                }
        }

        /// <summary>MonoTorrent подключился к пиру или не смог — больше не предлагать</summary>
        public void MarkTried(Uri uri)
        {
            string k = Key(uri);
            if (k == null) return;
            lock (sync)
            {
                untried.Remove(k);
                if (tried.Count < Max * 4) tried.Add(k);
            }
        }

        /// <summary>
        /// Раз в 2 с из TorrEngine (вне его блокировки): посчитать запас и, если
        /// очередь MonoTorrent пуста и место под соединения есть, подложить порцию
        /// </summary>
        public void Tick(TorrentManager m)
        {
            if (m == null || !m.HasMetadata || (m.Torrent != null && m.Torrent.IsPrivate)) return;
            if (Loop == null || AvailableField == null || PeerInfoProp == null) return;
            List<PeerInfo> batch = null;
            try
            {
                Loop.Send(_ =>
                {
                    // Что MonoTorrent уже знает: очередь, подключающиеся, подключённые
                    var inEngine = new HashSet<string>();
                    foreach (var p in (AvailableField.GetValue(m.Peers) as IList)?.Cast<object>() ?? Enumerable.Empty<object>())
                        inEngine.Add(Key((PeerInfoProp.GetValue(p) as PeerInfo)?.ConnectionUri));
                    foreach (var p in (ConnectingField?.GetValue(m.Peers) as IList)?.Cast<object>() ?? Enumerable.Empty<object>())
                        inEngine.Add(Key((PeerInfoProp.GetValue(p) as PeerInfo)?.ConnectionUri));
                    foreach (var id in (ConnectedField?.GetValue(m.Peers) as IList)?.Cast<PeerId>() ?? Enumerable.Empty<PeerId>())
                        inEngine.Add(Key(id.Uri));
                    bool hungry = m.Peers.Available == 0 && m.OpenConnections < m.Settings.MaximumConnections;
                    lock (sync)
                    {
                        var fresh = untried.Where(kv => !inEngine.Contains(kv.Key)).ToList();
                        Extra = fresh.Count;
                        if (!hungry || fresh.Count == 0) return;
                        batch = new List<PeerInfo>();
                        foreach (var kv in fresh.Take(Batch))
                        {
                            batch.Add(kv.Value);
                            untried.Remove(kv.Key);
                            tried.Add(kv.Key);
                        }
                        Extra -= batch.Count;
                    }
                }, null);
            }
            catch { return; }
            if (batch == null || batch.Count == 0) return;
            Fed += batch.Count;
            _ = m.AddPeersAsync(batch);
        }
    }
}
