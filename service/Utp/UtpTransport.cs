// uTP в MonoTorrent 3.0.2 — через его фабрики (TorrEngine.CreateEngine):
//  - входящие: CompositePeerListener — обычный TCP-listener и uTP на общем
//    UDP-сокете (SharedUdpSocket) того же порта;
//  - исходящие: HybridPeerConnection вместо SocketPeerConnection. 3.0.2 на
//    попытку подключения создаёт одно соединение, поэтому выбор транспорта —
//    внутри него: сначала uTP (как у libtorrent), и если пир за UtpHeadStartMs
//    не ответил, параллельно TCP; что подключится первым, то и остаётся.
//    Пиры, у которых uTP не ответил, а TCP подключился, запоминаются — к ним
//    дальше сразу по TCP, без задержки.
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
        /// <summary>Сколько ждать ответа по uTP, прежде чем пробовать и TCP</summary>
        public const int UtpHeadStartMs = 700;
        /// <summary>
        /// Предел попытки uTP: MonoTorrent закрывает незавершённые подключения
        /// старше EngineSettings.ConnectionTimeout (у нас 6 с), а повтор SYN у uTP — через 3 с
        /// </summary>
        public const int UtpConnectTimeoutMs = 5000;
        const int MaxRemembered = 4096;

        // Пиры, к которым uTP не подключился, а TCP — да (адрес:порт → когда)
        static readonly ConcurrentDictionary<string, long> TcpOnly = new ConcurrentDictionary<string, long>();
        const long ForgetTcpOnlyMs = 30 * 60 * 1000;
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        readonly UtpPeerConnectionListener utp;
        readonly Func<IPeerConnection> createTcp;
        readonly IPEndPoint endPoint;
        readonly string key;
        IPeerConnection active;
        UtpPeerConnection utpConnection;
        IPeerConnection tcpConnection;
        int disposed;

        public HybridPeerConnection(Uri uri, UtpPeerConnectionListener utp, Func<IPeerConnection> createTcp)
        {
            Uri = uri;
            this.utp = utp;
            this.createTcp = createTcp;
            endPoint = new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            AddressBytes = endPoint.Address.GetAddressBytes();
            key = endPoint.ToString();
        }

        public ReadOnlyMemory<byte> AddressBytes { get; }
        public bool CanReconnect => true;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public IPEndPoint EndPoint => endPoint;
        public bool IsIncoming => false;
        public Uri Uri { get; }

        /// <summary>Подключились по uTP (до подключения — false)</summary>
        public bool IsUtp => active is UtpPeerConnection;

        bool UtpUsable
        {
            get
            {
                if (utp == null || !utp.Enabled || utp.Status != ListenerStatus.Listening)
                    return false;
                if (TcpOnly.TryGetValue(key, out var since) && Clock.ElapsedMilliseconds - since < ForgetTcpOnlyMs)
                    return false;
                return true;
            }
        }

        public async ReusableTask ConnectAsync()
        {
            if (!UtpUsable)
            {
                var tcp = tcpConnection = createTcp();
                if (Disposed) { tcp.Dispose(); throw new ObjectDisposedException(nameof(HybridPeerConnection)); }
                await tcp.ConnectAsync();
                active = tcp;
                return;
            }

            var u = utpConnection = new UtpPeerConnection(utp, endPoint, (ushort)UtpRandom.Next(1, ushort.MaxValue));
            if (Disposed) { u.Dispose(); throw new ObjectDisposedException(nameof(HybridPeerConnection)); }
            var utpTask = ConnectUtp(u);
            if (await Task.WhenAny(utpTask, Task.Delay(UtpHeadStartMs)).ConfigureAwait(false) == utpTask && utpTask.Result)
            {
                Won(u, true);
                return;
            }

            Task<bool> tcpTask;
            if (Disposed)
                tcpTask = Task.FromResult(false);
            else
            {
                var tcp = tcpConnection = createTcp();
                tcpTask = ConnectTcp(tcp);
            }

            while (true)
            {
                if (utpTask.IsCompleted && utpTask.Result)
                {
                    Won(u, true);
                    return;
                }
                if (tcpTask.IsCompleted && tcpTask.Result)
                {
                    // uTP не ответил и за время подключения по TCP — пир его, видимо,
                    // не умеет: дальше к нему сразу по TCP
                    Remember();
                    Won(tcpConnection, false);
                    return;
                }
                if (utpTask.IsCompleted && tcpTask.IsCompleted)
                    throw new SocketException((int)SocketError.TimedOut);
                var pending = new List<Task>(2);
                if (!utpTask.IsCompleted) pending.Add(utpTask);
                if (!tcpTask.IsCompleted) pending.Add(tcpTask);
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

        void Remember()
        {
            if (TcpOnly.Count >= MaxRemembered)
                TcpOnly.Clear();
            TcpOnly[key] = Clock.ElapsedMilliseconds;
        }

        void Won(IPeerConnection winner, bool isUtp)
        {
            if (isUtp)
            {
                TcpOnly.TryRemove(key, out _);
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

        public ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
        {
            var c = active ?? throw new InvalidOperationException("Не подключено");
            return c.ReceiveAsync(buffer);
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
            tcp.ConnectionReceived += (s, e) => ConnectionReceived?.Invoke(this, e);
            tcp.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
            if (utp != null)
                utp.ConnectionReceived += (s, e) => ConnectionReceived?.Invoke(this, e);
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
