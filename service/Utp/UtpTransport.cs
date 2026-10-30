// uTP в MonoTorrent 3.0.2 — через его фабрики (TorrEngine.CreateEngine):
//  - входящие: CompositePeerListener — обычный TCP-listener и uTP на общем
//    UDP-сокете (SharedUdpSocket) того же порта;
//  - исходящие: HybridPeerConnection вместо SocketPeerConnection. 3.0.2 на
//    попытку подключения создаёт одно соединение, поэтому выбор транспорта —
//    внутри него: TCP и uTP пробуются сразу оба, и TCP главнее — uTP остаётся,
//    только если TCP за TcpGraceMs после uTP не подключился (или не смог).
//    В 1.0.20 было наоборот — сначала uTP, как у libtorrent, — и стало хуже:
//    по uTP отдающий пир сам сбавляет скорость (LEDBAT уступает другому
//    трафику) и сильно проседает на потерях, так что быстрые TCP-соединения
//    заменились медленными (раздача Ubuntu: 20 uTP-пиров из 31 давали вместе
//    1,5 МБ/с, всё вместе — 11–18 МБ/с против 23 у 1.0.19; libtorrent с
//    libtorrent по uTP при 2 % потерь — 0,2 МБ/с). Теперь uTP только добавляет
//    пиров, до которых по TCP не достучаться.
// Настройка EnableUTP выключает оба направления сразу, без перезапуска.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace TorrStreamTorrServer.Utp
{
    public sealed class HybridPeerConnection : IPeerConnection
    {
        /// <summary>uTP уже подключился — сколько ещё ждать TCP, прежде чем оставить uTP</summary>
        public const int TcpGraceMs = 1000;
        /// <summary>
        /// Предел попытки uTP: MonoTorrent закрывает незавершённые подключения
        /// старше EngineSettings.ConnectionTimeout (у нас 6 с), а повтор SYN у uTP — через 3 с
        /// </summary>
        public const int UtpConnectTimeoutMs = 5000;

        readonly UtpPeerConnectionListener utp;
        readonly Func<IPeerConnection> createTcp;
        readonly IPEndPoint endPoint;
        IPeerConnection active;
        UtpPeerConnection utpConnection;
        IPeerConnection tcpConnection;
        int disposed;

        public HybridPeerConnection(Uri uri, UtpPeerConnectionListener utp, Func<IPeerConnection> createTcp)
        {
            Uri = uri;
            this.utp = utp;
            this.createTcp = createTcp;
            // У ipv6:// Host в квадратных скобках
            endPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
            AddressBytes = endPoint.Address.GetAddressBytes();
        }

        public ReadOnlyMemory<byte> AddressBytes { get; }
        public bool CanReconnect => true;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public IPEndPoint EndPoint => endPoint;
        public bool IsIncoming => false;
        public Uri Uri { get; }

        /// <summary>uTP-соединение, если подключились по uTP</summary>
        public UtpPeerConnection Utp => active as UtpPeerConnection;

        /// <summary>Подключились пробивкой NAT (BEP 55, Holepunch.cs)</summary>
        public bool Punched { get; private set; }

        // UDP-сокет uTP — на IPv4: IPv6-пиры — только по TCP
        bool UtpUsable => utp != null && utp.Enabled && utp.Status == ListenerStatus.Listening
            && endPoint.AddressFamily == AddressFamily.InterNetwork;

        public async ReusableTask ConnectAsync()
        {
            // К адресу идёт пробивка NAT (ut_holepunch connect): uTP уже начат,
            // TCP за NAT не пройдёт — ждём только его
            var punch = HolePunch.TakePending(endPoint);
            if (punch != null)
            {
                utpConnection = punch.Out;
                if (Disposed) { punch.Out.Dispose(); throw new ObjectDisposedException(nameof(HybridPeerConnection)); }
                bool ok;
                // Подключилось — ещё решить с встречным входящим (близнецом), кто остаётся
                try { ok = await punch.OutConnect.ConfigureAwait(false) && await HolePunch.KeepOutgoing(punch).ConfigureAwait(false); }
                catch { ok = false; }
                if (!ok) throw new SocketException((int)SocketError.TimedOut);
                Won(punch.Out, true);
                Punched = true;
                return;
            }
            try
            {
                await ConnectDirectAsync();
            }
            catch
            {
                // Не достучались — может, пир за NAT: попросить пира, приславшего адрес по PEX, свести нас
                if (!Disposed) HolePunch.ConnectFailed(endPoint);
                throw;
            }
        }

        async ReusableTask ConnectDirectAsync()
        {
            if (!UtpUsable)
            {
                var tcp = tcpConnection = createTcp();
                if (Disposed) { tcp.Dispose(); throw new ObjectDisposedException(nameof(HybridPeerConnection)); }
                await tcp.ConnectAsync();
                active = tcp;
                return;
            }

            var tcpConn = tcpConnection = createTcp();
            var u = utpConnection = new UtpPeerConnection(utp, endPoint, (ushort)UtpRandom.Next(1, ushort.MaxValue));
            if (Disposed)
            {
                tcpConn.Dispose();
                u.Dispose();
                throw new ObjectDisposedException(nameof(HybridPeerConnection));
            }
            var tcpTask = ConnectTcp(tcpConn);
            var utpTask = ConnectUtp(u);
            Task grace = null;
            while (true)
            {
                if (tcpTask.IsCompleted && tcpTask.Result)
                {
                    Won(tcpConn, false);
                    return;
                }
                if (utpTask.IsCompleted && utpTask.Result)
                {
                    // TCP не смог или не успел за TcpGraceMs — остаётся uTP
                    if (tcpTask.IsCompleted || grace != null && grace.IsCompleted)
                    {
                        Won(u, true);
                        return;
                    }
                    if (grace == null)
                        grace = Task.Delay(TcpGraceMs);
                }
                if (utpTask.IsCompleted && !utpTask.Result && tcpTask.IsCompleted)
                    throw new SocketException((int)SocketError.TimedOut);
                var pending = new List<Task>(3);
                if (!tcpTask.IsCompleted) pending.Add(tcpTask);
                if (!utpTask.IsCompleted) pending.Add(utpTask);
                if (grace != null) pending.Add(grace);
                await Task.WhenAny(pending).ConfigureAwait(false);
            }
        }

        static async Task<bool> ConnectUtp(UtpPeerConnection connection)
        {
            try
            {
                var connect = AsTask(connection);
                if (await Task.WhenAny(connect, Task.Delay(UtpConnectTimeoutMs)).ConfigureAwait(false) != connect)
                    return false;
                return await connect.ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        static async Task<bool> AsTask(UtpPeerConnection connection) => await connection.ConnectAsync();

        static async Task<bool> ConnectTcp(IPeerConnection connection)
        {
            try
            {
                await connection.ConnectAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        void Won(IPeerConnection winner, bool isUtp)
        {
            if (isUtp)
            {
                try { tcpConnection?.Dispose(); } catch { }
            }
            else
            {
                try { utpConnection?.Dispose(); } catch { }
            }
            active = winner;
            if (Disposed)
            {
                try { winner.Dispose(); } catch { }
                throw new ObjectDisposedException(nameof(HybridPeerConnection));
            }
        }

        public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
        {
            var c = active ?? throw new InvalidOperationException("Не подключено");
            int n = await c.ReceiveAsync(buffer).ConfigureAwait(false);
            // Пир закрыл сам (FIN, а не RST) — для повтора без шифрования (PlainTextRetry.cs)
            if (n == 0) NoteClosedByPeer(Uri);
            // Следом ядро декодирует сообщение в этом же потоке — так ut_holepunch узнаёт отправителя
            HolePunch.Received(this);
            return n;
        }

        // Адрес → когда пир закрыл исходящее соединение сам. Не прошло рукопожатие
        // шифрования (MSE), и пир закрыл вежливо — у него, скорее всего, шифрование
        // выключено, а MonoTorrent 3.0.2 без шифрования не повторяет. Фейковые пиры
        // (принимают TCP и сразу RST) сюда не попадают
        static readonly ConcurrentDictionary<string, long> closedByPeer = new ConcurrentDictionary<string, long>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        const long ClosedTtlMs = 30000;

        static void NoteClosedByPeer(Uri uri)
        {
            long now = Clock.ElapsedMilliseconds;
            if (closedByPeer.Count > 4096)
                foreach (var kv in closedByPeer)
                    if (now - kv.Value > ClosedTtlMs) closedByPeer.TryRemove(kv.Key, out _);
            closedByPeer[uri.ToString()] = now;
        }

        /// <summary>Пир по этому адресу только что сам закрыл наше соединение (FIN); запись снимается</summary>
        public static bool TakeClosedByPeer(Uri uri)
        {
            return uri != null && closedByPeer.TryRemove(uri.ToString(), out long at) && Clock.ElapsedMilliseconds - at <= ClosedTtlMs;
        }

        public ReusableTask<int> SendAsync(Memory<byte> buffer)
        {
            var c = active ?? throw new InvalidOperationException("Не подключено");
            return c.SendAsync(buffer);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try { utpConnection?.Dispose(); } catch { }
            try { tcpConnection?.Dispose(); } catch { }
        }
    }

    /// <summary>Входящие пиры: TCP и uTP на одном порту — для MonoTorrent один listener</summary>
    public sealed class CompositePeerListener : IPeerConnectionListener
    {
        readonly IPeerConnectionListener tcp;
        readonly UtpPeerConnectionListener utp;

        public CompositePeerListener(IPeerConnectionListener tcp, UtpPeerConnectionListener utp)
        {
            this.tcp = tcp;
            this.utp = utp;
            // Входящие — в TrackedPeerConnection: ut_holepunch узнаёт по ним отправителя (Holepunch.cs)
            tcp.ConnectionReceived += (s, e) => Deliver(new TrackedPeerConnection(e.Connection, false), e.InfoHash);
            tcp.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
            if (utp != null)
                utp.ConnectionReceived += (s, e) =>
                {
                    // Встречный SYN пира, которого мы пробиваем: сначала решить с нашим исходящим
                    if (!HolePunch.HoldIncoming(e.Connection as UtpPeerConnection, c => Deliver(c, e.InfoHash)))
                        Deliver(new TrackedPeerConnection(e.Connection, false), e.InfoHash);
                };
        }

        void Deliver(IPeerConnection connection, MonoTorrent.InfoHash infoHash)
        {
            ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(connection, infoHash));
        }

        public event EventHandler<PeerConnectionEventArgs> ConnectionReceived;
        public event EventHandler<EventArgs> StatusChanged;

        public IPEndPoint LocalEndPoint => tcp.LocalEndPoint;
        public IPEndPoint PreferredLocalEndPoint => tcp.PreferredLocalEndPoint;
        public ListenerStatus Status => tcp.Status;

        public void Start()
        {
            tcp.Start();
            utp?.Start();
        }

        public void Stop()
        {
            tcp.Stop();
            utp?.Stop();
        }
    }

}
