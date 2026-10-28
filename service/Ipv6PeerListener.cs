// Приём пиров по IPv6 — как PeerConnectionListener из MonoTorrent 3.0.2, но сокет
// только IPv6 (IPV6_V6ONLY). У .NET сокет, созданный как InterNetworkV6, по
// умолчанию берёт настройку системы, а в Linux она «двухстековый»: [::]:порт
// занимал и IPv4 того же порта — слушатель IPv4 уже стоит, и IPv6 не открывался бы.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;

namespace TorrStreamTorrServer
{
    public sealed class Ipv6PeerListener : SocketListener, IPeerConnectionListener
    {
        public event EventHandler<PeerConnectionEventArgs> ConnectionReceived;

        public Ipv6PeerListener(IPEndPoint endpoint)
            : base(endpoint)
        {
        }

        protected override void Start(CancellationToken token)
        {
            base.Start(token);

            var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            var connectArgs = new SocketAsyncEventArgs();
            token.Register(() =>
            {
                listener.Close();
                connectArgs.Dispose();
            });

            listener.DualMode = false;
            listener.Bind(PreferredLocalEndPoint);
            LocalEndPoint = (IPEndPoint)listener.LocalEndPoint;
            listener.Listen(6);

            connectArgs.Completed += OnSocketReceived;
            if (!listener.AcceptAsync(connectArgs))
                OnSocketReceived(listener, connectArgs);
        }

        void OnSocketReceived(object sender, SocketAsyncEventArgs e)
        {
            Socket socket = null;
            try
            {
                socket = e.AcceptSocket;
                e.AcceptSocket = null;
                if (e.SocketError != SocketError.Success)
                    throw new SocketException((int)e.SocketError);
                ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(new SocketPeerConnection(socket, true), null));
            }
            catch
            {
                socket?.Close();
            }

            try
            {
                if (!((Socket)sender).AcceptAsync(e))
                    OnSocketReceived(sender, e);
            }
            catch
            {
            }
        }
    }
}
