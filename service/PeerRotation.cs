// Замена медленных пиров, как у TorrServer (anacrolix: при полном наборе
// соединений худшее отключается ради нового адреса). В лимит соединений
// торрента MonoTorrent считает и незавершённые подключения: при 80 %
// недоступных адресов часть мест всё время занята попытками, и при лимите 25
// подключено было меньше. Поэтому MonoTorrent разрешено подключать с запасом
// (Headroom, TorrEngine.TorrentSettingsFor), а лишних сверх ConnectionsLimit
// здесь отключаем — самых медленных: из 30 подключившихся остаются 25 лучших. MonoTorrent 3.0.2 сам
// отключает только пиров, не приславших ни байта (InactivePeerManager), и
// набор из ConnectionsLimit пиров оставался тем, что подключилось первым, —
// даже если в запасе ждали десятки адресов.
// Раз в 15 с, когда есть что качать (окно читателя не заполнено), соединений —
// под лимит и в запасе есть адреса: самые медленные за эти 15 с (не больше
// 10 % и не больше запаса; медленнее четверти медианы; подключённые хотя бы
// 30 с) отключаются, их адреса — в InactivePeerList MonoTorrent, чтобы
// к ним не подключиться снова. Освободившееся место MonoTorrent занимает сам
// следующим адресом из запаса. Всё — в главном цикле MonoTorrent (его списки
// не потокобезопасны); внутренности — через отражение, их нет — ничего не делаем.
//
// Там же — уборка закрытых пиров, оставшихся в ConnectedPeers. CleanupSocket
// MonoTorrent 3.0.2 сначала отменяет запросы, потом убирает пира из списков,
// а закрывает (Dispose) в любом случае: если отмена бросила исключение, закрытый
// пир остаётся в ConnectedPeers. Тогда каждый такт главного цикла бросает
// ObjectDisposedException (MessageQueue) на Have/KeepAlive и обрывается, а через
// 180 с без сообщений от пира PostLogicTick зовёт CleanupSocket, тот у закрытого
// ничего не делает, и цикл «CleanupSocket; i--» крутится вечно — движок встаёт.
// Уборка — и у раздающих торрентов: этот такт MonoTorrent у них тот же.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MonoTorrent.Client;

namespace TorrStreamTorrServer
{
    public class PeerRotation
    {
        const long EveryMs = 15000, MinAgeMs = 30000;
        const int MaxInactive = 500;

        static readonly SynchronizationContext Loop =
            typeof(ClientEngine).GetField("MainLoop", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SynchronizationContext;
        static readonly PropertyInfo InactiveManagerProp =
            typeof(TorrentManager).GetProperty("InactivePeerManager", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo InactiveListProp =
            InactiveManagerProp?.PropertyType.GetProperty("InactivePeerList", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectedField =
            typeof(PeerManager).GetField("ConnectedPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ActiveField =
            typeof(PeerManager).GetField("ActivePeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo PeerProp =
            typeof(PeerId).GetProperty("Peer", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo OpenConnectionsField =
            typeof(ConnectionManager).GetField("openConnections", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo CleanupMethod =
            typeof(ConnectionManager).GetMethod("CleanupSocket", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo CancelRequestsMethod =
            typeof(PieceManager).GetMethod("CancelRequests", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(PeerId) }, null);

        /// <summary>Сколько соединений сверх ConnectionsLimit разрешить MonoTorrent (их место — попыткам)</summary>
        public static int Headroom(int limit) { return Supported ? Math.Max(5, limit / 4) : 0; }

        /// <summary>Внутренности MonoTorrent нашлись — замена работает</summary>
        public static bool Supported => Loop != null && InactiveListProp != null && ConnectedField != null && CleanupMethod != null;

        // Сколько байт прислал пир к прошлой проверке и когда его увидели впервые
        readonly Dictionary<Uri, long> lastBytes = new Dictionary<Uri, long>();
        readonly Dictionary<Uri, long> firstSeen = new Dictionary<Uri, long>();
        long lastMs = -1;

        /// <summary>Всего отключено медленных пиров (для /cache)</summary>
        public int Rotated { get; private set; }

        /// <summary>Раз в 2 с из TorrEngine (качающие и раздающие торренты); строка для журнала — если кого-то отключили</summary>
        public string Tick(ClientEngine engine, TorrentManager m, int limit, bool demand, long now)
        {
            if (!Supported || engine == null || m == null) return null;
            if (lastMs >= 0 && now - lastMs < EveryMs) return null;
            long interval = lastMs < 0 ? 0 : now - lastMs;
            lastMs = now;
            bool rotate = m.State == TorrentState.Downloading;
            string report = null;
            try
            {
                Loop.Send(_ =>
                {
                    int removed = RemoveDisposed(engine, m);
                    if (rotate) report = RotateOnLoop(engine, m, limit, demand, now, interval);
                    // Скорости — только за интервал, когда качали
                    else lastBytes.Clear();
                    if (removed > 0)
                        report = "закрытые пиры убраны из подключённых: " + removed + (report != null ? "; " + report : "");
                }, null);
            }
            catch (Exception e) { report = "замена пиров: " + e.GetBaseException().Message; }
            return report;
        }

        /// <summary>Убрать из ConnectedPeers пиров, закрытых оборвавшимся CleanupSocket (в начале файла)</summary>
        static int RemoveDisposed(ClientEngine engine, TorrentManager m)
        {
            if (!(ConnectedField.GetValue(m.Peers) is IList connected)) return 0;
            int removed = 0;
            for (int i = connected.Count - 1; i >= 0; i--)
            {
                if (!(connected[i] is PeerId p) || p.IsConnected) continue;
                connected.RemoveAt(i);
                removed++;
                // Его блоки — снова свободны (на отмене CleanupSocket и оборвался). Сама
                // отмена пиру не уйдёт: очередь закрыта — ObjectDisposedException, блоки к
                // тому времени уже отменены
                try { CancelRequestsMethod?.Invoke(m.PieceManager, new object[] { p }); } catch { }
                if (PeerProp?.GetValue(p) is object peer && ActiveField?.GetValue(m.Peers) is IList active)
                    active.Remove(peer);
                // Счётчик соединений движка CleanupSocket уменьшает вместе с удалением из списка
                if (OpenConnectionsField != null)
                    OpenConnectionsField.SetValue(engine.ConnectionManager, Math.Max(0, (int)OpenConnectionsField.GetValue(engine.ConnectionManager) - 1));
            }
            return removed;
        }

        string RotateOnLoop(ClientEngine engine, TorrentManager m, int limit, bool demand, long now, long interval)
        {
            var connected = (ConnectedField.GetValue(m.Peers) as IList)?.Cast<PeerId>().Where(p => p.IsConnected).ToList();
            if (connected == null) return null;

            // Скорость каждого за прошедший интервал; новых — запомнить
            var rates = new List<KeyValuePair<PeerId, long>>();
            var alive = new HashSet<Uri>();
            foreach (var p in connected)
            {
                var uri = p.Uri;
                alive.Add(uri);
                long bytes = p.Monitor.DataBytesReceived;
                if (!firstSeen.ContainsKey(uri)) firstSeen[uri] = now;
                if (interval > 0 && lastBytes.TryGetValue(uri, out long prev))
                    rates.Add(new KeyValuePair<PeerId, long>(p, Math.Max(0, bytes - prev) * 1000 / interval));
                lastBytes[uri] = bytes;
            }
            foreach (var gone in lastBytes.Keys.Where(u => !alive.Contains(u)).ToList()) { lastBytes.Remove(gone); firstSeen.Remove(gone); }

            if (interval <= 0 || rates.Count < 4) return null;
            int spare = m.Peers.Available;
            var sorted = rates.Select(r => r.Value).OrderBy(v => v).ToList();
            long median = sorted[sorted.Count / 2];
            var eligible = rates.Where(r => now - firstSeen[r.Key.Uri] >= MinAgeMs).ToList();
            List<KeyValuePair<PeerId, long>> victims;
            string why;
            int over = connected.Count - limit;
            if (over > 0)
            {
                // Сверх лимита — самые медленные; окно скачано (скорость упирается в
                // нас) — те, кто прислал меньше всего за всё время
                victims = (demand ? eligible.OrderBy(r => r.Value) : eligible.OrderBy(r => r.Key.Monitor.DataBytesReceived)).Take(over).ToList();
                why = "сверх лимита " + limit;
            }
            else if (demand && spare > 0 && connected.Count >= limit - 1)
            {
                long threshold = median / 4;
                int max = Math.Min(spare, Math.Max(1, connected.Count / 10));
                victims = eligible.Where(r => r.Value < threshold).OrderBy(r => r.Value).Take(max).ToList();
                why = "медленнее четверти медианы";
            }
            else return null;
            if (victims.Count == 0) return null;

            var inactive = InactiveListProp.GetValue(InactiveManagerProp.GetValue(m)) as IList;
            foreach (var v in victims)
            {
                if (inactive != null && !inactive.Contains(v.Key.Uri))
                {
                    inactive.Add(v.Key.Uri);
                    while (inactive.Count > MaxInactive) inactive.RemoveAt(0);
                }
                CleanupMethod.Invoke(engine.ConnectionManager, new object[] { m, v.Key });
                lastBytes.Remove(v.Key.Uri);
                firstSeen.Remove(v.Key.Uri);
            }
            Rotated += victims.Count;
            return "медленные пиры отключены (" + why + "): " + victims.Count + " (" + string.Join(", ", victims.Select(v => v.Value / 1024 + " КБ/с")) +
                "; медиана " + median / 1024 + " КБ/с), подключено " + (connected.Count - victims.Count) + ", в запасе " + spare;
        }
    }
}
