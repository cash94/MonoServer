// Подключение к пирам — асинхронное и с таймаутом. На Tizen работает сборка
// MonoTorrent для netstandard2.0, а в ней SocketConnector подключается
// блокирующим socket.Connect на потоке пула. К недоступному пиру (за NAT — таких
// с трекеров большинство) ядро Linux ждёт ~2 минуты, и закрытие сокета из
// другого потока такой Connect не прерывает. MonoTorrent держит не больше
// MaximumHalfOpenConnections подключений сразу — они забивались зависшими
// попытками, и новых не было: на Tizen 5.5 «подключено 5, ждут 83» полторы
// минуты, метаданные не приходили. Здесь — ConnectAsync (на Linux — epoll, его
// прерывает Dispose) и свой таймаут. В сборках .NET 6+ MonoTorrent и сам
// подключается асинхронно — там этот класс просто то же самое.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using ReusableTasks;

namespace TorrStreamTorrServer
{
    public class PeerConnector : ISocketConnector
    {
        // Как в master MonoTorrent (первая попытка — 3 с): недоступных пиров
        // большинство, и чем быстрее отпускаем попытку, тем быстрее доходим до живых
        public const int TimeoutMs = 4000;

        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            var endPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);   // у ipv6:// — в скобках
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeoutMs);
                using (cts.Token.Register(() => { try { socket.Dispose(); } catch { } }))
                {
                    try
                    {
                        await socket.ConnectAsync(endPoint).ConfigureAwait(false);
                        cts.Token.ThrowIfCancellationRequested();
                        return socket;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Закрыли по таймауту или отмене — для MonoTorrent это «недоступен»
                        throw new SocketException((int)SocketError.TimedOut);
                    }
                    catch
                    {
                        try { socket.Dispose(); } catch { }
                        throw;
                    }
                }
            }
        }
    }
}
