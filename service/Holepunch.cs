// BEP 55 (ut_holepunch) — пробивка NAT через третьего пира. Два пира за NAT
// друг к другу не подключатся: входящие у обоих режет роутер. Если оба
// подключены к общему пиру-посреднику, инициатор шлёт ему rendezvous с адресом
// цели, посредник обоим шлёт connect с адресом другого, и оба сразу начинают
// uTP-подключение: свой SYN открывает у своего роутера дорогу встречному.
//
// В MonoTorrent 3.0.2 своего ut_holepunch нет, а обработчик чужих расширений
// (Mode.HandleGenericExtensionMessage) пустой и внутренний. Поэтому:
//  - сообщение регистрируется публичным ExtensionMessage.Register, а в «m»
//    рукопожатия попадает через внутренний список SupportedMessages;
//  - от кого пришло сообщение, ядро в Decode не передаёт. Но декодирует оно
//    синхронно, в том же потоке, сразу после последнего ReceiveAsync
//    соединения (PeerIO.ReceiveMessageAsync, ConfigureAwait(false), продолжения
//    ReusableTask на пуле — без перескоков). Наши соединения (HybridPeerConnection
//    исходящих, TrackedPeerConnection входящих) после каждого чтения пишут себя в
//    [ThreadStatic] — сообщение забирает его при создании. Пир по соединению
//    ищется среди подключённых к торренту — чужое сообщение не пройдёт;
//  - кто кого прислал в PEX, ядро тоже не говорит, а без этого не знать, кого
//    просить в посредники: PEX у нас свой (PeerExchange.cs), он отдаёт сюда
//    адреса с флагом 0x08 «умеет ut_holepunch» (BEP 11) и источник.
// Инициатор — мы, когда HybridPeerConnection не достучался до адреса из PEX;
// цель — по connect: SYN сразу (UtpPeerConnection), а ядру — пир первым в
// очередь, его попытку HybridPeerConnection отдаёт уже начатому подключению.
// Встречный SYN цели приходит входящим uTP — его ядро принимает само.
// Без uTP (EnableUTP выключен) только посредничаем: для него uTP не нужен.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Messages;
using MonoTorrent.Messages.Peer;
using MonoTorrent.Messages.Peer.Libtorrent;
using ReusableTasks;
using TorrStreamTorrServer.Utp;

namespace TorrStreamTorrServer
{
    /// <summary>Сообщение ut_holepunch: тип, адрес (IPv4/IPv6), порт, код ошибки — всё big-endian</summary>
    public sealed class HolepunchMessage : ExtensionMessage
    {
        public const string Name = "ut_holepunch";
        public const byte Rendezvous = 0, Connect = 1, Error = 2;
        public const uint NoSuchPeer = 1, NotConnected = 2, NoSupport = 3, NoSelf = 4;

        public byte Type { get; private set; }
        public IPEndPoint EndPoint { get; private set; }
        public uint ErrorCode { get; private set; }
        /// <summary>Разобрано без ошибок (иначе — молча пропустить)</summary>
        public bool Valid { get; private set; }

        /// <summary>Соединение, с которого пришло (см. HolePunch.Received)</summary>
        internal object Source;
        internal ITorrentManagerInfo Manager;

        /// <summary>Входящее: id расширения тут не важен</summary>
        internal HolepunchMessage(byte extensionId) : base(extensionId) { }

        /// <summary>Исходящее: extensionId — номер ut_holepunch из рукопожатия получателя</summary>
        internal HolepunchMessage(byte extensionId, byte type, IPEndPoint endPoint, uint error) : base(extensionId)
        {
            Type = type;
            EndPoint = endPoint;
            ErrorCode = error;
            Valid = true;
        }

        int AddressLength => EndPoint != null && EndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? 16 : 4;

        // длина, id 20, id расширения, тип, тип адреса, адрес, порт, код ошибки
        public override int ByteLength => 4 + 1 + 1 + 1 + 1 + AddressLength + 2 + 4;

        public override void Decode(ReadOnlySpan<byte> buffer)
        {
            Valid = false;
            if (buffer.Length < 2) return;
            Type = Message.ReadByte(ref buffer);
            byte addrType = Message.ReadByte(ref buffer);
            int len = addrType == 0 ? 4 : addrType == 1 ? 16 : 0;
            if (len == 0 || buffer.Length < len + 2) return;
            var address = new IPAddress(buffer.Slice(0, len).ToArray());
            buffer = buffer.Slice(len);
            int port = Message.ReadUShort(ref buffer);
            // err_code по BEP 55 есть всегда (0 без ошибки), но libtorrent пишет его только в error
            ErrorCode = buffer.Length >= 4 ? Message.ReadUInt(ref buffer) : 0;
            EndPoint = new IPEndPoint(address, port);
            Valid = Type <= Error && port != 0;
            HolePunch.Dispatch(this);
        }

        public override int Encode(Span<byte> buffer)
        {
            int length = buffer.Length;
            Message.Write(ref buffer, ByteLength - 4);
            Message.Write(ref buffer, (byte)20);
            Message.Write(ref buffer, ExtensionId);
            Message.Write(ref buffer, Type);
            Message.Write(ref buffer, (byte)(AddressLength == 16 ? 1 : 0));
            EndPoint.Address.GetAddressBytes().AsSpan().CopyTo(buffer);
            buffer = buffer.Slice(AddressLength);
            Message.Write(ref buffer, (ushort)EndPoint.Port);
            Message.Write(ref buffer, ErrorCode);
            return length - buffer.Length;
        }

        public override string ToString()
        {
            return Name + " " + (Type == Rendezvous ? "rendezvous" : Type == Connect ? "connect" : "error " + ErrorCode) + " " + EndPoint;
        }
    }

    /// <summary>Входящее соединение (TCP или uTP) — отмечает себя после каждого чтения (HolePunch.Received)</summary>
    public sealed class TrackedPeerConnection : IPeerConnection
    {
        public TrackedPeerConnection(IPeerConnection inner, bool punched)
        {
            Inner = inner;
            Punched = punched;
        }

        public IPeerConnection Inner { get; }
        /// <summary>Пришло с адреса, который мы как раз пробивали (BEP 55)</summary>
        public bool Punched { get; }

        public ReadOnlyMemory<byte> AddressBytes => Inner.AddressBytes;
        public bool CanReconnect => Inner.CanReconnect;
        public bool Disposed => Inner.Disposed;
        public IPEndPoint EndPoint => Inner.EndPoint;
        public bool IsIncoming => Inner.IsIncoming;
        public Uri Uri => Inner.Uri;

        public ReusableTask ConnectAsync() => Inner.ConnectAsync();
        public ReusableTask<int> SendAsync(Memory<byte> buffer) => Inner.SendAsync(buffer);
        public void Dispose() => Inner.Dispose();

        public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
        {
            int n = await Inner.ReceiveAsync(buffer).ConfigureAwait(false);
            HolePunch.Received(this);
            return n;
        }
    }

    public static class HolePunch
    {
        // Флаг BEP 11 в added.f: пир умеет ut_holepunch
        const byte PexHolepunchFlag = 0x08;
        const int PexBookMax = 4000;
        const long PexBookTtlMs = 30 * 60 * 1000;
        // Одну цель через посредника — не чаще; connect с одного адреса — не чаще
        const long RendezvousEveryMs = 10 * 60 * 1000, PunchEveryMs = 30 * 1000;
        // Начатое по connect подключение ждёт попытку ядра; не взяли — закрыть
        const int PendingTtlMs = 15000;
        // Сколько ждать близнеца, прежде чем отдать соединение ядру
        const int TwinWaitMs = 1000;

        internal static readonly SynchronizationContext Loop =
            typeof(ClientEngine).GetField("MainLoop", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SynchronizationContext;
        static readonly FieldInfo SupportedField = typeof(ExtensionMessage).GetField("SupportedMessages", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly PropertyInfo ConnectionProp = typeof(PeerId).GetProperty("Connection", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo SupportsProp = typeof(PeerId).GetProperty("ExtensionSupports", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo QueueProp = typeof(PeerId).GetProperty("MessageQueue", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo EnqueueMethod = QueueProp?.PropertyType.GetMethod("Enqueue", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PeerMessage) }, null);
        static readonly PropertyInfo PeerProp = typeof(PeerId).GetProperty("Peer", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo LocalPortProp = PeerProp?.PropertyType.GetProperty("LocalPort", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo PeerInfoProp = PeerProp?.PropertyType.GetProperty("Info", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        static readonly MethodInfo TryProcessQueueMethod = typeof(ConnectionManager).GetMethod("TryProcessQueue", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo TryConnectMethod = typeof(ConnectionManager).GetMethod("TryConnect", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        static readonly FieldInfo AvailableField = typeof(PeerManager).GetField("AvailablePeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectingField = typeof(PeerManager).GetField("ConnectingToPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectedField = typeof(PeerManager).GetField("ConnectedPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo InactiveManagerProp = typeof(TorrentManager).GetProperty("InactivePeerManager", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo InactiveListProp = InactiveManagerProp?.PropertyType.GetProperty("InactivePeerList", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo AddPeersMethod = typeof(TorrentManager).GetMethod("AddPeers", BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(IEnumerable<PeerInfo>), typeof(InfoHash), typeof(bool), typeof(bool) }, null);

        [ThreadStatic] static object receiving;

        sealed class PexEntry
        {
            public object Source;
            public ITorrentManagerInfo Manager;
            public long Ms;
        }

        /// <summary>
        /// Пробивка к одному адресу. Оба SYN часто проходят — выходят два uTP-соединения
        /// («близнецы»: наше исходящее и встречное входящее). Дай оба ядру — каждая
        /// сторона отбросит как «Already connected» то, что пришло к ней вторым, а это
        /// у сторон разные соединения: умирают оба. Поэтому выбираем до рукопожатия
        /// BitTorrent, одинаково на обеих сторонах: остаётся соединение с меньшим id
        /// SYN инициатора (оба id знают обе стороны), опоздавший близнец — лишний
        /// </summary>
        internal sealed class PunchState
        {
            public UtpPeerConnection Out;
            public Task<bool> OutConnect;
            public long Ms;
            public bool Taken;
            public UtpPeerConnection InCandidate, OutCandidate;
            public int Winner;   // 0 — не решено, 1 — входящее, 2 — исходящее
            public readonly TaskCompletionSource<int> Decided = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        static readonly object sync = new object();
        static readonly Dictionary<string, PexEntry> pexBook = new Dictionary<string, PexEntry>();
        static readonly Dictionary<string, long> rendezvousAt = new Dictionary<string, long>();
        static readonly Dictionary<string, long> punchAt = new Dictionary<string, long>();
        static readonly Dictionary<string, PunchState> punches = new Dictionary<string, PunchState>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static HashSet<IPAddress> localAddresses;
        static long localAddressesMs = -1;

        static ClientEngine engine;
        static UtpPeerConnectionListener utp;
        static Func<int> listenPort;
        static Action<string> log;

        /// <summary>ut_holepunch есть в рукопожатии и сообщения доходят (иначе — внутренности MonoTorrent не те)</summary>
        public static bool Supported { get; private set; }

        // Счётчики для /cache (Diag.HolePunch)
        public static int RendezvousSent, ConnectReceived, Relayed, RelayFailed, ErrorsReceived, Punched;

        /// <summary>Внутренности MonoTorrent для отправки сообщений пирам и поиска их по соединению нашлись</summary>
        internal static bool InternalsFound => Loop != null && ConnectionProp != null && SupportsProp != null
            && EnqueueMethod != null && TryProcessQueueMethod != null && ConnectedField != null;

        /// <summary>
        /// Один раз до создания ClientEngine: ut_holepunch в рукопожатие (ut_pex — PeerExchange.Register).
        /// Список расширений в MonoTorrent общий на процесс — второй вызов ничего не делает
        /// </summary>
        public static void Register()
        {
            lock (sync)
            {
                if (Supported) return;
                if (!InternalsFound || SupportedField == null) return;
                try
                {
                    // ExtensionMessage.Register — публичный: id для «m» в рукопожатии
                    byte id = ExtensionMessage.Register(m => (new HolepunchMessage(0) { Source = TakeReceiving(), Manager = m }, default(PeerMessage.Releaser)));
                    var list = (List<ExtensionSupport>)SupportedField.GetValue(null);
                    if (!list.Any(s => s.Name == HolepunchMessage.Name))
                        list.Add(new ExtensionSupport(HolepunchMessage.Name, id));
                    Supported = true;
                }
                catch (Exception e)
                {
                    Supported = false;
                    Console.Error.WriteLine("ut_holepunch: " + e.Message);
                }
            }
        }

        /// <summary>Движок создан — с ним и шлём; utp может быть null (без uTP — только посредник)</summary>
        public static void Attach(ClientEngine e, UtpPeerConnectionListener u, Func<int> port, Action<string> logSink)
        {
            engine = e;
            utp = u;
            listenPort = port;
            log = logSink;
        }

        /// <summary>Наше соединение только что дочитало данные — следом ядро декодирует сообщение (в этом же потоке)</summary>
        public static void Received(object connection) => receiving = connection;

        /// <summary>Соединение, с которого декодируется сообщение (из фабрики сообщения, до Decode)</summary>
        internal static object TakeReceiving()
        {
            var c = receiving;
            receiving = null;
            return c;
        }

        static long Now => clock.ElapsedMilliseconds;

        static string Key(IPEndPoint ep) => ep?.ToString();

        static bool UtpUsable => utp != null && utp.Enabled && utp.Status == ListenerStatus.Listening;

        // ---------- PEX: кто кого прислал ----------

        /// <summary>Адреса из входящего PEX (разбирает PeerExchange): запомнить, кто прислал умеющих ut_holepunch</summary>
        internal static void OnPex(object source, ITorrentManagerInfo manager, List<(IPEndPoint ep, byte flags)> peers)
        {
            if (!Supported || source == null) return;
            long now = Now;
            lock (sync)
            {
                foreach (var (ep, flags) in peers)
                    if ((flags & PexHolepunchFlag) != 0)
                        pexBook[Key(ep)] = new PexEntry { Source = source, Manager = manager, Ms = now };
                if (pexBook.Count > PexBookMax)
                    foreach (var k in pexBook.Where(kv => now - kv.Value.Ms > PexBookTtlMs).Select(kv => kv.Key).ToList())
                        pexBook.Remove(k);
                if (pexBook.Count > PexBookMax)
                    foreach (var k in pexBook.OrderBy(kv => kv.Value.Ms).Take(pexBook.Count - PexBookMax).Select(kv => kv.Key).ToList())
                        pexBook.Remove(k);
            }
        }

        /// <summary>
        /// Торрент закрыт — забыть его адреса: запись держит TorrentManager и соединение
        /// источника, а чистилась книга только при переполнении. Сколько забыто
        /// </summary>
        internal static int Forget(Func<object, bool> closedManager)
        {
            lock (sync)
            {
                var gone = pexBook.Where(kv => closedManager(kv.Value.Manager)).Select(kv => kv.Key).ToList();
                foreach (var k in gone) pexBook.Remove(k);
                return gone.Count;
            }
        }

        // ---------- Инициатор ----------

        /// <summary>
        /// HybridPeerConnection не подключился ни по TCP, ни по uTP. Если адрес пришёл
        /// по PEX от пира с ut_holepunch — просим его свести нас (rendezvous)
        /// </summary>
        public static void ConnectFailed(IPEndPoint ep)
        {
            if (!Supported || ep == null || !UtpUsable) return;
            string k = Key(ep);
            PexEntry entry;
            long now = Now;
            lock (sync)
            {
                if (!pexBook.TryGetValue(k, out entry) || now - entry.Ms > PexBookTtlMs) return;
                if (rendezvousAt.TryGetValue(k, out long at) && now - at < RendezvousEveryMs) return;
                rendezvousAt[k] = now;
                if (rendezvousAt.Count > PexBookMax)
                    foreach (var old in rendezvousAt.Where(kv => now - kv.Value > RendezvousEveryMs).Select(kv => kv.Key).ToList())
                        rendezvousAt.Remove(old);
            }
            Post(() =>
            {
                var m = entry.Manager as TorrentManager;
                if (!Usable(m)) return;
                var relay = FindByConnection(m, entry.Source);
                if (relay == null || FindByEndPoint(m, ep) != null) return;
                if (Send(m, relay, HolepunchMessage.Rendezvous, ep, 0))
                    Interlocked.Increment(ref RendezvousSent);
            });
        }

        // ---------- Входящие ut_holepunch ----------

        static void OnMessage(HolepunchMessage msg)
        {
            var m = msg.Manager as TorrentManager;
            if (!Usable(m)) return;
            var from = FindByConnection(m, msg.Source);
            // BEP 55: от пира без ut_holepunch в рукопожатии — молча пропустить
            if (from == null || ExtensionId(from) == null) return;
            switch (msg.Type)
            {
                case HolepunchMessage.Rendezvous: Relay(m, from, msg.EndPoint); break;
                case HolepunchMessage.Connect: Interlocked.Increment(ref ConnectReceived); Punch(m, msg.EndPoint); break;
                case HolepunchMessage.Error: Interlocked.Increment(ref ErrorsReceived); break;
            }
        }

        // ---------- Посредник ----------

        static void Relay(TorrentManager m, PeerId from, IPEndPoint target)
        {
            uint error = 0;
            PeerId to = null;
            if (IsSelf(target)) error = HolepunchMessage.NoSelf;
            else if ((to = FindByEndPoint(m, target)) == null) error = HolepunchMessage.NotConnected;
            else if (ExtensionId(to) == null) error = HolepunchMessage.NoSupport;
            if (error != 0 || to == from)
            {
                Interlocked.Increment(ref RelayFailed);
                // Адрес в ошибке — тот же, что в rendezvous
                Send(m, from, HolepunchMessage.Error, target, error != 0 ? error : HolepunchMessage.NoSelf);
                return;
            }
            var fromEp = ListenEndPoint(from);
            if (fromEp == null) return;
            Send(m, to, HolepunchMessage.Connect, fromEp, 0);
            Send(m, from, HolepunchMessage.Connect, target, 0);
            Interlocked.Increment(ref Relayed);
        }

        // ---------- Цель (и инициатор — на ответный connect) ----------

        static void Punch(TorrentManager m, IPEndPoint ep)
        {
            // BEP 55: не хотим или уже подключены — молча
            if (!UtpUsable || IsSelf(ep) || FindByEndPoint(m, ep) != null) return;
            string k = Key(ep);
            long now = Now;
            PunchState p;
            lock (sync)
            {
                if (punchAt.TryGetValue(k, out long at) && now - at < PunchEveryMs) return;
                punchAt[k] = now;
                if (punchAt.Count > 1000)
                    foreach (var old in punchAt.Where(kv => now - kv.Value > PunchEveryMs).Select(kv => kv.Key).ToList())
                        punchAt.Remove(old);
                if (punches.ContainsKey(k)) return;
                var c = new UtpPeerConnection(utp, ep, (ushort)UtpRandom.Next(1, ushort.MaxValue));
                p = new PunchState { Out = c, OutConnect = ConnectUtp(c), Ms = now };
                punches[k] = p;
            }
            // Срок пробивки: не взятое ядром исходящее закрыть, чтобы не висело в uTP
            _ = Task.Delay(PendingTtlMs).ContinueWith(_ =>
            {
                bool drop = false;
                lock (sync)
                {
                    if (punches.TryGetValue(k, out var cur) && cur == p)
                    {
                        punches.Remove(k);
                        drop = !p.Taken;
                    }
                }
                if (drop) try { p.Out.Dispose(); } catch { }
            });
            Prioritise(m, ep);
        }

        static async Task<bool> ConnectUtp(UtpPeerConnection c)
        {
            try
            {
                var connect = Bool(c);
                if (await Task.WhenAny(connect, Task.Delay(HybridPeerConnection.UtpConnectTimeoutMs * 2)).ConfigureAwait(false) != connect)
                    return false;
                return await connect.ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        static async Task<bool> Bool(UtpPeerConnection c) => await c.ConnectAsync();

        /// <summary>
        /// Попытка ядра к адресу, к которому идёт пробивка: отдать начатое uTP-подключение
        /// (null — пробивки нет, подключаться как обычно)
        /// </summary>
        internal static PunchState TakePending(IPEndPoint ep)
        {
            if (ep == null) return null;
            lock (sync)
            {
                if (!punches.TryGetValue(Key(ep), out var p) || p.Taken) return null;
                p.Taken = true;
                return p;
            }
        }

        /// <summary>Исходящее пробивки подключилось: оставлять ли его (или остаётся встречное)</summary>
        internal static async Task<bool> KeepOutgoing(PunchState p)
        {
            bool keep = await Offer(p, p.Out, false).ConfigureAwait(false) == 2;
            if (keep) CountPunched(p.Out.EndPoint);
            return keep;
        }

        /// <summary>
        /// Входящий uTP с адреса, который мы пробиваем: решить с близнецом и отдать ядру
        /// (deliver) или закрыть. false — пробивки к адресу нет, принимать как обычно
        /// </summary>
        internal static bool HoldIncoming(UtpPeerConnection c, Action<IPeerConnection> deliver)
        {
            PunchState p;
            lock (sync)
                if (c == null || !punches.TryGetValue(Key(c.EndPoint), out p)) return false;
            _ = Offer(p, c, true).ContinueWith(t =>
            {
                if (t.Result == 1)
                {
                    CountPunched(c.EndPoint);
                    deliver(new TrackedPeerConnection(c, true));
                }
                else
                {
                    try { c.Dispose(); } catch { }
                }
            }, TaskScheduler.Default);
            return true;
        }

        static Task<int> Offer(PunchState p, UtpPeerConnection c, bool incoming)
        {
            lock (sync)
            {
                if (incoming) p.InCandidate = c;
                else p.OutCandidate = c;
                if (p.Winner == 0 && p.InCandidate != null && p.OutCandidate != null) Decide(p);
            }
            if (!p.Decided.Task.IsCompleted)
                _ = Task.Delay(TwinWaitMs).ContinueWith(_ => { lock (sync) if (p.Winner == 0) Decide(p); });
            return p.Decided.Task;
        }

        /// <summary>Под sync. Оба есть — меньший id SYN инициатора; один — он; опоздавшему достаётся решённое</summary>
        static void Decide(PunchState p)
        {
            if (p.InCandidate != null && p.OutCandidate != null)
            {
                // Входящее: id SYN — его ConnectionIdSend; наше исходящее: id SYN — ConnectionIdReceive
                p.Winner = p.InCandidate.ConnectionIdSend < p.OutCandidate.ConnectionIdReceive ? 1 : 2;
            }
            else p.Winner = p.InCandidate != null ? 1 : 2;
            p.Decided.TrySetResult(p.Winner);
        }

        static void CountPunched(IPEndPoint ep)
        {
            Interlocked.Increment(ref Punched);
            log?.Invoke("ut_holepunch: подключились через NAT к " + ep);
        }

        /// <summary>Ядру — подключаться к адресу первым делом (на главном потоке MonoTorrent)</summary>
        static void Prioritise(TorrentManager m, IPEndPoint ep)
        {
            try
            {
                var uri = new Uri((ep.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6://" : "ipv4://") + ep);
                if (ConnectingField?.GetValue(m.Peers) is IList connecting)
                    foreach (var peer in connecting)
                        if (SameUri(peer, uri)) return;   // ядро уже подключается
                (InactiveListProp?.GetValue(InactiveManagerProp.GetValue(m)) as IList)?.Remove(uri);
                bool queued = false;
                if (AvailableField?.GetValue(m.Peers) is IList available)
                    for (int i = 0; i < available.Count; i++)
                        if (SameUri(available[i], uri))
                        {
                            var peer = available[i];
                            available.RemoveAt(i);
                            available.Insert(0, peer);
                            queued = true;
                            break;
                        }
                if (!queued && AddPeersMethod != null)
                    AddPeersMethod.Invoke(m, new object[] { new[] { new PeerInfo(uri) }, m.InfoHashes.V1OrV2, true, false });
                TryConnectMethod?.Invoke(engine.ConnectionManager, null);
            }
            catch { }
        }

        static bool SameUri(object peer, Uri uri)
        {
            var info = PeerInfoProp?.GetValue(peer) as PeerInfo;
            return info != null && info.ConnectionUri == uri;
        }

        // ---------- Общее ----------

        static bool Usable(TorrentManager m)
        {
            return engine != null && m != null && !(m.Torrent != null && m.Torrent.IsPrivate);
        }

        /// <summary>На главный поток MonoTorrent (там и только там трогаем PeerId)</summary>
        internal static void Post(Action a)
        {
            Loop?.Post(_ => { try { a(); } catch { } }, null);
        }

        internal static void Dispatch(HolepunchMessage msg)
        {
            if (msg.Valid && msg.Source != null) Post(() => OnMessage(msg));
        }

        internal static IEnumerable<PeerId> Connected(TorrentManager m)
        {
            return (ConnectedField.GetValue(m.Peers) as IList)?.Cast<PeerId>().Where(p => p.IsConnected) ?? Enumerable.Empty<PeerId>();
        }

        internal static PeerId FindByConnection(TorrentManager m, object connection)
        {
            if (connection == null) return null;
            foreach (var p in Connected(m))
                if (ReferenceEquals(ConnectionProp.GetValue(p), connection)) return p;
            return null;
        }

        /// <summary>Подключённый пир с этим адресом: порт соединения или объявленный порт (входящие)</summary>
        static PeerId FindByEndPoint(TorrentManager m, IPEndPoint ep)
        {
            foreach (var p in Connected(m))
            {
                var uri = p.Uri;
                if (uri == null || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) continue;
                if (!Same(ip, ep.Address)) continue;
                if (uri.Port == ep.Port || ListenPort(p) == ep.Port) return p;
            }
            return null;
        }

        static bool Same(IPAddress a, IPAddress b)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
            return a.Equals(b);
        }

        static int ListenPort(PeerId p) => KnownListenPort(p) ?? p.Uri.Port;

        /// <summary>
        /// Порт, на котором пир принимает: у исходящих — порт соединения, у входящих uTP —
        /// тоже (uTP шлёт с сокета, на котором принимает), у входящих TCP — «p» из рукопожатия
        /// </summary>
        static int? KnownListenPort(PeerId p)
        {
            if (p.ConnectionDirection == Direction.Outgoing || IsUtp(p)) return p.Uri.Port;
            try
            {
                var peer = PeerProp?.GetValue(p);
                if (peer != null && LocalPortProp?.GetValue(peer) is int port && port > 0 && port < 65536) return port;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Куда подключаться к пиру: его адрес и порт, на котором он принимает.
        /// knownOnly — null, если порт приёма не известен (входящий TCP без «p»)
        /// </summary>
        internal static IPEndPoint ListenEndPoint(PeerId p, bool knownOnly = false)
        {
            var uri = p.Uri;
            if (uri == null || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            int? port = knownOnly ? KnownListenPort(p) : ListenPort(p);
            return port == null ? null : new IPEndPoint(ip, port.Value);
        }

        /// <summary>uTP-соединение пира или null — TCP (соединение у PeerId закрытое — через отражение)</summary>
        internal static UtpPeerConnection Utp(PeerId p)
        {
            object c = null;
            try { c = ConnectionProp?.GetValue(p); } catch { }
            if (c is TrackedPeerConnection t) c = t.Inner;
            return c as UtpPeerConnection ?? (c as HybridPeerConnection)?.Utp;
        }

        static bool IsUtp(PeerId p) => Utp(p) != null;

        /// <summary>Расширения из рукопожатия пира (null — рукопожатия LTEP не было)</summary>
        internal static ExtensionSupports Supports(PeerId p)
        {
            try { return SupportsProp.GetValue(p) as ExtensionSupports; }
            catch { return null; }
        }

        /// <summary>Номер расширения name у пира (из его «m»), null — не умеет</summary>
        internal static byte? ExtensionId(PeerId p, string name = HolepunchMessage.Name)
        {
            var s = Supports(p);
            if (s != null)
                foreach (var e in s)
                    if (e.Name == name) return e.MessageId;
            return null;
        }

        static bool Send(TorrentManager m, PeerId to, byte type, IPEndPoint ep, uint error)
        {
            var id = ExtensionId(to);
            if (id == null || ep == null) return false;
            return Enqueue(m, to, new HolepunchMessage(id.Value, type, ep, error));
        }

        /// <summary>Сообщение пиру в очередь и отправить (главный поток MonoTorrent)</summary>
        internal static bool Enqueue(TorrentManager m, PeerId to, PeerMessage msg)
        {
            try
            {
                EnqueueMethod.Invoke(QueueProp.GetValue(to), new object[] { msg });
                TryProcessQueueMethod.Invoke(engine.ConnectionManager, new object[] { m, to });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Адрес — наш: свой порт пиров на одном из своих интерфейсов</summary>
        static bool IsSelf(IPEndPoint ep)
        {
            if (listenPort == null || ep.Port != listenPort()) return false;
            var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            if (IPAddress.IsLoopback(a)) return true;
            long now = Now;
            if (localAddresses == null || now - localAddressesMs > 60000)
            {
                var set = new HashSet<IPAddress>();
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                            set.Add(ua.Address);
                }
                catch { }
                localAddresses = set;
                localAddressesMs = now;
            }
            return localAddresses.Contains(a);
        }
    }
}
