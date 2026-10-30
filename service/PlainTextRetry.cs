// Повтор подключения без шифрования. MonoTorrent 3.0.2 к исходящему пиру идёт
// сначала через MSE (шифрование), и если оно не прошло — хотел бы повторить
// открытым текстом: ставит пиру AllowedEncryption = PlainText и подключается
// снова, но только если пир ещё в очереди (AvailablePeers.Remove). А TryConnect
// убирает его из очереди до подключения — повтора не бывает, пир потерян. На
// раздаче Debian так терялось 4 из 77 живых пиров: у них шифрование выключено,
// на MSE они просто закрывают соединение.
//
// Здесь: MSE не прошло (EncryptionNegiotiationFailed), а пир закрыл соединение
// сам, вежливо (FIN — HybridPeerConnection.TakeClosedByPeer), — тот же адрес в
// начало очереди MonoTorrent, только без шифрования. Один раз на адрес. Фейковые
// пиры (принимают TCP и на первые данные — RST: на раздаче Ubuntu их 83 % адресов)
// так второй попытки не получают. Всё — в главном потоке MonoTorrent.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace TorrStreamTorrServer
{
    public static class PlainTextRetry
    {
        static readonly SynchronizationContext Loop =
            typeof(ClientEngine).GetField("MainLoop", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SynchronizationContext;
        static readonly FieldInfo AvailableField = typeof(PeerManager).GetField("AvailablePeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectingField = typeof(PeerManager).GetField("ConnectingToPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ConnectedField = typeof(PeerManager).GetField("ConnectedPeers", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly Type PeerType = AvailableField?.FieldType.GetGenericArguments().FirstOrDefault();
        static readonly ConstructorInfo PeerCtor = PeerType?.GetConstructor(new[] { typeof(PeerInfo), typeof(IList<EncryptionType>), typeof(InfoHash) });
        static readonly PropertyInfo PeerInfoProp = PeerType?.GetProperty("Info", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        static readonly MethodInfo TryConnectMethod = typeof(ConnectionManager).GetMethod("TryConnect", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

        public static bool Supported => Loop != null && AvailableField != null && ConnectingField != null && ConnectedField != null
            && PeerCtor != null && PeerInfoProp != null && TryConnectMethod != null;

        static int retried;
        /// <summary>Сколько адресов поставлено на повтор без шифрования (с запуска службы)</summary>
        public static int Retried => Volatile.Read(ref retried);

        /// <summary>Адрес — снова в начало очереди торрента, без шифрования (из обработчика ConnectionAttemptFailed)</summary>
        public static void Retry(ClientEngine engine, TorrentManager m, PeerInfo info)
        {
            if (!Supported || engine == null || m == null || info?.ConnectionUri == null) return;
            if (m.Torrent != null && m.Torrent.IsPrivate) return;
            Loop.Post(_ =>
            {
                try
                {
                    if (m.Engine == null || (m.State != TorrentState.Downloading && m.State != TorrentState.Metadata && m.State != TorrentState.Seeding)) return;
                    var uri = info.ConnectionUri;
                    // Уже снова в очереди, подключается или подключён (пришёл входящим) — не нужно
                    foreach (var p in (AvailableField.GetValue(m.Peers) as IList)?.Cast<object>() ?? Enumerable.Empty<object>())
                        if ((PeerInfoProp.GetValue(p) as PeerInfo)?.ConnectionUri == uri) return;
                    foreach (var p in (ConnectingField.GetValue(m.Peers) as IList)?.Cast<object>() ?? Enumerable.Empty<object>())
                        if ((PeerInfoProp.GetValue(p) as PeerInfo)?.ConnectionUri == uri) return;
                    foreach (var id in (ConnectedField.GetValue(m.Peers) as IList)?.Cast<PeerId>() ?? Enumerable.Empty<PeerId>())
                        if (id.Uri == uri) return;
                    var peer = PeerCtor.Invoke(new object[] { info, new List<EncryptionType> { EncryptionType.PlainText }, m.InfoHashes.V1OrV2 });
                    ((IList)AvailableField.GetValue(m.Peers)).Insert(0, peer);
                    Interlocked.Increment(ref retried);
                    TryConnectMethod.Invoke(engine.ConnectionManager, null);
                }
                catch { }
            }, null);
        }
    }
}
