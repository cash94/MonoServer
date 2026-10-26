// Свой PEX (BEP 11, ut_pex) вместо PeerExchangeManager из MonoTorrent 3.0.2.
// У того четыре беды:
//  - шлёт с НАШИМ номером ut_pex (3), а не с номером получателя из его «m». У
//    libtorrent (qBittorrent и др.) 3 — upload_only: наш PEX для него «я только
//    раздаю», и раздающие пиры на libtorrent могут отключать нас как бесполезных;
//  - пир узнаёт только о тех, кто подключился после него — начального списка нет;
//    dropped не шлётся никогда, а IPv6 берётся из очередей IPv4;
//  - у входящих пиров отдаёт порт соединения (случайный), а не порт приёма;
//  - флаги только 0x01/0x02 — без 0x04 (uTP), 0x08 (ut_holepunch), 0x10 (доступен).
//    Без 0x08 клиенты libtorrent не выбирают нас посредником для BEP 55.
// Поэтому у торрентов AllowPeerExchange = false: его OnTick тогда ничего не шлёт.
// Сами его экземпляры ядро всё равно создаёт на каждого пира (рукопожатие), и они
// копили бы в очередях всех подключившихся — снимаем их (PeerId.PeerExchangeManager).
// Приём с AllowPeerExchange = false ядро тоже бросает — разбираем сами (PexMessage):
// адреса — в ядро, как делал MonoTorrent, и в запас торрента (PeerPool), флаг 0x08 —
// в HolePunch. Отправка — раз в минуту каждому пиру с ut_pex: разница с тем, что ему
// уже сообщили (первый раз — все), не больше 50 добавленных и 50 ушедших.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Messages.Peer;
using MonoTorrent.Messages.Peer.Libtorrent;

namespace TorrStreamTorrServer
{
    /// <summary>Входящий ut_pex: разбор как у MonoTorrent плюс отправитель (HolePunch.TakeReceiving)</summary>
    sealed class PexMessage : PeerExchangeMessage
    {
        internal object Source;
        internal ITorrentManagerInfo Manager;

        public override void Decode(ReadOnlySpan<byte> buffer)
        {
            base.Decode(buffer);
            try { PeerExchange.OnReceived(this); } catch { }
        }
    }

    public static class PeerExchange
    {
        // BEP 11: не чаще раза в минуту и не больше 50 добавленных и 50 ушедших в сообщении
        const long EveryMs = 60000;
        const int MaxPeers = 50;
        const byte FlagEncryption = 0x01, FlagSeed = 0x02, FlagUtp = 0x04, FlagHolepunch = 0x08, FlagReachable = 0x10;

        static readonly FieldInfo CreatorsField = typeof(ExtensionMessage).GetField("messages", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly PropertyInfo ManagerProp = typeof(PeerId).GetProperty("PeerExchangeManager", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo RaisePeersFoundMethod = typeof(TorrentManager).GetMethod("RaisePeersFound", BindingFlags.NonPublic | BindingFlags.Instance);

        sealed class PeerState
        {
            public long LastMs = -1;
            /// <summary>Что этот пир от нас уже знает (адрес → адрес)</summary>
            public readonly Dictionary<string, IPEndPoint> Sent = new Dictionary<string, IPEndPoint>();
        }

        // Только на главном потоке MonoTorrent
        static readonly Dictionary<PeerId, PeerState> states = new Dictionary<PeerId, PeerState>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static ClientEngine engine;
        static Action<TorrentManager, IList<PeerInfo>> found;
        static Timer timer;
        static readonly object sync = new object();

        /// <summary>Свой PEX работает (иначе — внутренности MonoTorrent не те, остаётся его PEX)</summary>
        public static bool Supported { get; private set; }

        // Для /cache (Diag.Pex)
        public static int MessagesSent, MessagesReceived;

        /// <summary>Один раз до создания ClientEngine: ut_pex разбирает наш PexMessage</summary>
        public static void Register()
        {
            lock (sync)
            {
                if (Supported) return;
                if (!HolePunch.InternalsFound || CreatorsField == null || ManagerProp == null || ManagerProp.GetSetMethod(true) == null) return;
                try
                {
                    var creators = (Array)CreatorsField.GetValue(null);
                    int pex = PeerExchangeMessage.Support.MessageId;
                    if (pex <= 0 || pex >= creators.Length) return;
                    creators.SetValue(new Func<ITorrentManagerInfo, (PeerMessage, PeerMessage.Releaser)>(
                        m => (new PexMessage { Source = HolePunch.TakeReceiving(), Manager = m }, default(PeerMessage.Releaser))), pex);
                    Supported = true;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("ut_pex: " + e.Message);
                }
            }
        }

        /// <summary>Движок создан: раз в секунду — рассылка; peersFound — адреса из PEX в запас торрента</summary>
        public static void Attach(ClientEngine e, Action<TorrentManager, IList<PeerInfo>> peersFound)
        {
            if (!Supported) return;
            engine = e;
            found = peersFound;
            timer = new Timer(_ => HolePunch.Post(Tick), null, 1000, 1000);
        }

        static long Now => clock.ElapsedMilliseconds;

        static bool Private(TorrentManager m) => m.Torrent != null && m.Torrent.IsPrivate;

        // ---------- Отправка ----------

        static void Tick()
        {
            if (engine == null) return;
            long now = Now;
            var alive = new HashSet<PeerId>();
            foreach (var m in engine.Torrents)
            {
                var connected = HolePunch.Connected(m).ToList();
                foreach (var p in connected)
                {
                    alive.Add(p);
                    DropEngineManager(p);
                }
                if (Private(m) || connected.Count == 0) continue;

                // Что сейчас можно сообщить о каждом: адрес приёма (известный) и флаги
                var current = new Dictionary<PeerId, (string key, IPEndPoint ep, byte flags)>();
                foreach (var q in connected)
                {
                    var ep = HolePunch.ListenEndPoint(q, true);
                    if (ep != null) current[q] = (ep.ToString(), ep, Flags(q));
                }

                foreach (var p in connected)
                {
                    var supports = HolePunch.Supports(p);
                    if (supports == null || !supports.Supports(PeerExchangeMessage.Support.Name)) continue;
                    if (!states.TryGetValue(p, out var st)) states[p] = st = new PeerState();
                    if (st.LastMs >= 0 && now - st.LastMs < EveryMs) continue;

                    var own = current.TryGetValue(p, out var self) ? self.key : null;
                    // Кроме него самого
                    var visible = new Dictionary<string, (IPEndPoint ep, byte flags)>();
                    foreach (var kv in current)
                        if (kv.Key != p && kv.Value.key != own) visible[kv.Value.key] = (kv.Value.ep, kv.Value.flags);
                    var added = visible.Where(kv => !st.Sent.ContainsKey(kv.Key)).Take(MaxPeers).ToList();
                    var dropped = st.Sent.Where(kv => !visible.ContainsKey(kv.Key)).Take(MaxPeers).ToList();
                    if (added.Count == 0 && dropped.Count == 0) continue;

                    var msg = Build(supports, added.Select(kv => kv.Value).ToList(), dropped.Select(kv => kv.Value).ToList());
                    if (!HolePunch.Enqueue(m, p, msg)) continue;
                    st.LastMs = now;
                    foreach (var kv in added) st.Sent[kv.Key] = kv.Value.ep;
                    foreach (var kv in dropped) st.Sent.Remove(kv.Key);
                    Interlocked.Increment(ref MessagesSent);
                }
            }
            foreach (var gone in states.Keys.Where(p => !alive.Contains(p)).ToList())
                states.Remove(gone);
        }

        static byte Flags(PeerId q)
        {
            byte f = 0;
            if (q.EncryptionType == EncryptionType.RC4Header || q.EncryptionType == EncryptionType.RC4Full) f |= FlagEncryption;
            if (q.IsSeeder) f |= FlagSeed;
            if (HolePunch.Utp(q) != null) f |= FlagUtp;
            if (HolePunch.ExtensionId(q) != null) f |= FlagHolepunch;
            // Мы к нему подключились сами — значит, он принимает входящие
            if (q.ConnectionDirection == Direction.Outgoing) f |= FlagReachable;
            return f;
        }

        /// <summary>
        /// PeerExchangeMessage из MonoTorrent — ради его кодирования; номер ut_pex Initialize
        /// берёт из переданного списка расширений — даём список получателя
        /// </summary>
        static PeerExchangeMessage Build(ExtensionSupports recipient, List<(IPEndPoint ep, byte flags)> added, List<IPEndPoint> dropped)
        {
            var a4 = added.Where(x => x.ep.AddressFamily == AddressFamily.InterNetwork).ToList();
            var a6 = added.Where(x => x.ep.AddressFamily == AddressFamily.InterNetworkV6).ToList();
            var msg = new PeerExchangeMessage();
            msg.Initialize(recipient,
                Compact(a4.Select(x => x.ep)), a4.Select(x => x.flags).ToArray(),
                Compact(dropped.Where(ep => ep.AddressFamily == AddressFamily.InterNetwork)),
                Compact(a6.Select(x => x.ep)), a6.Select(x => x.flags).ToArray(),
                Compact(dropped.Where(ep => ep.AddressFamily == AddressFamily.InterNetworkV6)),
                default(ByteBufferPool.Releaser));
            return msg;
        }

        static byte[] Compact(IEnumerable<IPEndPoint> eps)
        {
            var bytes = new List<byte>();
            foreach (var ep in eps)
            {
                bytes.AddRange(ep.Address.GetAddressBytes());
                bytes.Add((byte)(ep.Port >> 8));
                bytes.Add((byte)ep.Port);
            }
            return bytes.ToArray();
        }

        /// <summary>
        /// Снять PeerExchangeManager ядра с пира: с AllowPeerExchange = false он молчит,
        /// но копит в очередях всех подключившихся, пока пир не отключится
        /// </summary>
        static void DropEngineManager(PeerId p)
        {
            try
            {
                if (ManagerProp.GetValue(p) is IDisposable x)
                {
                    x.Dispose();
                    ManagerProp.SetValue(p, null);
                }
            }
            catch { }
        }

        // ---------- Приём ----------

        internal static void OnReceived(PexMessage msg)
        {
            if (!Supported) return;
            Interlocked.Increment(ref MessagesReceived);
            var peers = new List<(IPEndPoint ep, byte flags)>();
            try { Parse(peers, msg.Added, msg.AddedDotF, 4); } catch { }    // ключа нет — пропуск
            try { Parse(peers, msg.Added6, msg.Added6DotF, 16); } catch { }
            if (peers.Count == 0) return;
            HolePunch.OnPex(msg.Source, msg.Manager, peers);
            var source = msg.Source;
            var manager = msg.Manager as TorrentManager;
            HolePunch.Post(() => AddPeers(manager, source, peers));
        }

        static void Parse(List<(IPEndPoint ep, byte flags)> into, ReadOnlyMemory<byte> added, ReadOnlyMemory<byte> flags, int alen)
        {
            int size = alen + 2;
            var a = added.Span;
            var f = flags.Span;
            for (int i = 0; (i + 1) * size <= a.Length; i++)
            {
                var p = a.Slice(i * size, size);
                int port = (p[alen] << 8) | p[alen + 1];
                if (port == 0) continue;
                into.Add((new IPEndPoint(new IPAddress(p.Slice(0, alen).ToArray()), port), i < f.Length ? f[i] : (byte)0));
            }
        }

        /// <summary>Главный поток: как HandlePeerExchangeMessage у MonoTorrent, плюс запас торрента</summary>
        static async void AddPeers(TorrentManager m, object source, List<(IPEndPoint ep, byte flags)> peers)
        {
            try
            {
                if (m == null || Private(m)) return;
                var infos = peers.Select(x => new PeerInfo(
                    new Uri((x.ep.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6://" : "ipv4://") + x.ep),
                    BEncodedString.Empty, (x.flags & FlagSeed) != 0)).ToList();
                found?.Invoke(m, infos);
                var from = HolePunch.FindByConnection(m, source);
                // Как у MonoTorrent: очередь и соединения уже под потолок — в ядро не класть
                if (m.Peers.Available + m.OpenConnections >= m.Settings.MaximumConnections) return;
                int count = await m.AddPeersAsync(infos);
                if (from != null)
                    RaisePeersFoundMethod?.Invoke(m, new object[] { new PeerExchangePeersAdded(m, count, infos.Count, from) });
            }
            catch { }
        }
    }
}
