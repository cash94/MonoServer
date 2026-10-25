// Один UDP-сокет на порту пиров — для DHT и uTP сразу, как у libtorrent: пиры
// пробуют uTP на тот же порт, что объявлен для TCP, а DHT у нас и так на нём
// (DhtEndPoint = порт пиров). Два сокета на один порт не открыть, поэтому
// сокет общий, а пакеты делим по первому байту: у uTP младшие 4 бита —
// версия 1, старшие — тип 0…4; сообщение DHT — bencode-словарь, начинается с
// 'd' (0x64), его младшие биты — 4, с uTP не спутать.
//
// Приём — в своём потоке блокирующим ReceiveFrom: на телевизоре это дешевле,
// чем асинхронный приём (в netstandard2.0 — через APM, с выделением памяти на
// каждый пакет), а пакетов при загрузке по uTP — тысячи в секунду. Остановка —
// закрытием сокета; на случай, если закрытие не прервёт ReceiveFrom (Mono),
// у сокета таймаут приёма — поток всё равно выйдет.
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;

namespace TorrStreamTorrServer.Utp
{
    public sealed class SharedUdpSocket
    {
        public delegate void DatagramHandler(ReadOnlySpan<byte> datagram, IPEndPoint remote);

        const int ReceiveTimeoutMs = 1000;

        readonly object locker = new object();
        readonly object sendLocker = new object();
        Socket socket;
        Thread thread;
        int users;

        public SharedUdpSocket(IPEndPoint preferredLocalEndPoint)
        {
            PreferredLocalEndPoint = preferredLocalEndPoint;
        }

        public IPEndPoint PreferredLocalEndPoint { get; }
        public IPEndPoint LocalEndPoint { get; private set; }
        public ListenerStatus Status { get; private set; } = ListenerStatus.NotListening;
        public event EventHandler<EventArgs> StatusChanged;

        public int ReceiveBufferSize { get; set; } = 2 * 1024 * 1024;
        public int SendBufferSize { get; set; } = 1024 * 1024;

        /// <summary>Пакеты uTP — синхронно, в потоке приёма; буфер действителен только во время вызова</summary>
        public DatagramHandler UtpReceived;

        /// <summary>Остальные пакеты (DHT) — копией</summary>
        public event Action<ReadOnlyMemory<byte>, IPEndPoint> OtherReceived;

        public static bool IsUtpDatagram(ReadOnlySpan<byte> d)
            => d.Length >= UtpPacket.HeaderSize && (d[0] & 0x0F) == UtpPeerConnectionListener.UTP_VERSION && (d[0] >> 4) <= (int)PacketType.Syn;

        /// <summary>Пользователь сокета (DHT, uTP) начал работу — открыть сокет, если ещё закрыт</summary>
        public void Acquire()
        {
            lock (locker)
            {
                users++;
                if (socket != null)
                    return;
                Open();
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Пользователь закончил — последний закрывает сокет</summary>
        public void Release()
        {
            lock (locker)
            {
                if (users == 0)
                    return;
                if (--users > 0)
                    return;
                Close();
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        void Open()
        {
            var s = new Socket(PreferredLocalEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                try { s.ReceiveBufferSize = ReceiveBufferSize; } catch { }
                try { s.SendBufferSize = SendBufferSize; } catch { }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Иначе ICMP «порт недоступен» на отправленный пакет рвёт приём (WSAECONNRESET)
                    const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                    try { s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
                }
                s.ReceiveTimeout = ReceiveTimeoutMs;
                s.Bind(PreferredLocalEndPoint);
            }
            catch (SocketException)
            {
                s.Dispose();
                Status = ListenerStatus.PortNotFree;
                return;
            }
            socket = s;
            LocalEndPoint = (IPEndPoint)s.LocalEndPoint;
            Status = ListenerStatus.Listening;
            thread = new Thread(() => ReceiveLoop(s)) { IsBackground = true, Name = "udp " + LocalEndPoint.Port };
            thread.Start();
        }

        void Close()
        {
            var s = socket;
            socket = null;
            thread = null;
            Status = ListenerStatus.NotListening;
            try { s?.Dispose(); } catch { }
        }

        void ReceiveLoop(Socket s)
        {
            var buffer = new byte[65536];
            EndPoint any = new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (Volatile.Read(ref socket) == s)
            {
                int length;
                EndPoint from = any;
                try
                {
                    length = s.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                }
                catch (SocketException)
                {
                    // Таймаут приёма, ICMP-ошибки прошлых отправок — продолжаем, пока сокет наш
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (length <= 0)
                    continue;
                var remote = (IPEndPoint)from;
                var datagram = new ReadOnlySpan<byte>(buffer, 0, length);
                try
                {
                    if (IsUtpDatagram(datagram))
                        UtpReceived?.Invoke(datagram, remote);
                    else
                        OtherReceived?.Invoke(datagram.ToArray(), remote);
                }
                catch (Exception e)
                {
                    UtpLog.Error("ошибка обработки пакета от " + remote + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Отправить пакет. UDP-отправка не ждёт сети (только место в буфере сокета),
        /// поэтому синхронно. dontFragment — для проверки MTU по IPv4.
        /// </summary>
        public void Send(ReadOnlyMemory<byte> data, IPEndPoint remote, bool dontFragment = false)
        {
            var s = Volatile.Read(ref socket);
            if (s == null)
                throw new InvalidOperationException("UDP-сокет не открыт");
            ArraySegment<byte> segment;
            if (!MemoryMarshal.TryGetArray(data, out segment))
                segment = new ArraySegment<byte>(data.ToArray());
            lock (sendLocker)
            {
                if (!dontFragment)
                {
                    s.SendTo(segment.Array, segment.Offset, segment.Count, SocketFlags.None, remote);
                    return;
                }
                bool restore = false;
                try
                {
                    try
                    {
                        if (!s.DontFragment) { s.DontFragment = true; restore = true; }
                    }
                    catch (Exception e) when (e is SocketException || e is NotSupportedException || e is NotImplementedException)
                    {
                    }
                    s.SendTo(segment.Array, segment.Offset, segment.Count, SocketFlags.None, remote);
                }
                finally
                {
                    if (restore)
                        try { s.DontFragment = false; } catch { }
                }
            }
        }
    }

    /// <summary>DHT MonoTorrent на общем сокете (Factories.WithDhtListenerCreator)</summary>
    public sealed class SharedUdpDhtListener : IDhtListener
    {
        readonly SharedUdpSocket udp;
        bool started;

        public SharedUdpDhtListener(SharedUdpSocket udp)
        {
            this.udp = udp;
            udp.OtherReceived += (data, remote) => { if (started) MessageReceived?.Invoke(data, remote); };
            udp.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
        }

        public event Action<ReadOnlyMemory<byte>, IPEndPoint> MessageReceived;
        public event EventHandler<EventArgs> StatusChanged;

        public IPEndPoint LocalEndPoint => started ? udp.LocalEndPoint : null;
        public ListenerStatus Status => started ? udp.Status : ListenerStatus.NotListening;

        public Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
        {
            try
            {
                udp.Send(buffer, endpoint);
            }
            catch (Exception e) when (e is SocketException || e is InvalidOperationException || e is ObjectDisposedException)
            {
                // Как потеря пакета: DHT ждёт ответ и сам повторяет запрос
            }
            return Task.CompletedTask;
        }

        public void Start()
        {
            if (started)
                return;
            started = true;
            udp.Acquire();
        }

        public void Stop()
        {
            if (!started)
                return;
            started = false;
            udp.Release();
        }
    }
}
