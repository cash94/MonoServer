// Отказ пиру в куске, которого у нас уже нет. Кэш — в памяти (MemoryStorage.cs):
// кусок, о котором пир узнал (bitfield или HAVE), потом вытесняется, а MonoTorrent
// 3.0.2 на запрос ставит его в очередь отправки, не спрашивая, есть ли он:
// ConnectionManager.TryProcessQueue читает блок из хранилища, результат чтения
// не смотрит и шлёт что прочиталось. Пир получает мусор, кусок у него не проходит
// проверку хэша, а libtorrent после нескольких таких кусков с одного адреса
// банит его — раздающие пиры потихоньку от нас отключались.
//
// Здесь: свой декодер запроса (сообщение 6) — RequestMessage, MonoTorrent
// обрабатывает его как обычно (HandleRequestMessage ставит кусок в очередь), а
// сразу после этого, в том же такте главного потока, освобождает сообщение —
// Releaser.Dispose зовёт Reset. В Reset и смотрим: куска нет — снимаем отправку
// с очереди штатной отменой (CancelMessage, как если бы пир сам отменил запрос),
// пиру с Fast Extension (BEP 6) — RejectRequest. Без Fast Extension отказать нечем:
// запрос просто остаётся без ответа (лучше, чем мусор — за него банят, за тишину нет).
// Очередь отправки разбирается в пуле потоков: если блок уже ушёл в сокет, снять
// его не успели — как раньше (счётчик Late).
using System;
using System.Reflection;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Messages.Peer;
using MonoTorrent.Messages.Peer.FastPeer;

namespace TorrStreamTorrServer
{
    public static class RequestGuard
    {
        const byte RequestId = 6;

        static readonly FieldInfo MessagesField = typeof(PeerMessage).GetField("messages", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly ConstructorInfo ReleaserCtor = typeof(PeerMessage.Releaser).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(PeerMessage), typeof(Action<PeerMessage>) }, null);
        static readonly PropertyInfo ModeProp = typeof(TorrentManager).GetProperty("Mode", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo HandleMessageMethod = ModeProp?.PropertyType.GetMethod("HandleMessage", new[] { typeof(PeerId), typeof(PeerMessage), typeof(PeerMessage.Releaser) });
        static readonly Action<PeerMessage> Nothing = _ => { };

        /// <summary>Есть ли кусок у нас (проверен и не вытеснен); задаёт TorrEngine</summary>
        public static Func<TorrentManager, int, bool> HasPiece = (m, p) => true;

        /// <summary>Декодер подменён — отказы работают</summary>
        public static bool Supported { get; private set; }

        // Счётчики на весь процесс (/version → load, /cache → Diag.Guard)
        static int rejected, silent, late;
        /// <summary>Запросов вытесненного куска, снятых с очереди, — пиру ушёл RejectRequest</summary>
        public static int Rejected => Volatile.Read(ref rejected);
        /// <summary>То же у пиров без Fast Extension — без ответа</summary>
        public static int Silent => Volatile.Read(ref silent);
        /// <summary>Блок уже уходил в сокет — снять не успели</summary>
        public static int Late => Volatile.Read(ref late);

        /// <summary>Один раз до создания ClientEngine (после HolePunch.Register — нужен его поиск пира по соединению)</summary>
        public static void Register()
        {
            if (Supported) return;
            if (MessagesField == null || ReleaserCtor == null || HandleMessageMethod == null || !HolePunch.InternalsFound) return;
            try
            {
                var creators = (Array)MessagesField.GetValue(null);
                if (creators == null || creators.Length <= RequestId) return;
                creators.SetValue(new Func<ITorrentManagerInfo, (PeerMessage, PeerMessage.Releaser)>(m =>
                {
                    var msg = new GuardedRequest { Manager = m as TorrentManager, Source = HolePunch.TakeReceiving() };
                    return (msg, (PeerMessage.Releaser)ReleaserCtor.Invoke(new object[] { msg, Nothing }));
                }), RequestId);
                Supported = true;
            }
            catch (Exception e)
            {
                Supported = false;
                Console.Error.WriteLine("RequestGuard: " + e.Message);
            }
        }

        sealed class GuardedRequest : RequestMessage
        {
            public TorrentManager Manager;
            public object Source;
            bool done;

            // Главный поток MonoTorrent, сразу после HandleRequestMessage (Mode.HandleMessage → releaser.Dispose)
            protected override void Reset()
            {
                base.Reset();
                if (done) return;
                done = true;
                var m = Manager;
                if (m == null || Source == null || !m.HasMetadata) return;
                try
                {
                    if (HasPiece(m, PieceIndex)) return;
                    var peer = HolePunch.FindByConnection(m, Source);
                    if (peer == null) return;
                    int queued = peer.IsRequestingPiecesCount;
                    // Штатная отмена: TryCancelRequest и счётчик запросов пира, как от CancelMessage
                    HandleMessageMethod.Invoke(ModeProp.GetValue(m), new object[] { peer, new CancelMessage(PieceIndex, StartOffset, RequestLength), default(PeerMessage.Releaser) });
                    if (peer.IsRequestingPiecesCount >= queued)
                    {
                        // Душим пира — MonoTorrent в очередь и не ставил (сам отказал или
                        // промолчал). Иначе блок уже взяли на отправку
                        if (!peer.AmChoking) Interlocked.Increment(ref late);
                        return;
                    }
                    if (peer.SupportsFastPeer)
                    {
                        HolePunch.Enqueue(m, peer, new RejectRequestMessage(PieceIndex, StartOffset, RequestLength));
                        Interlocked.Increment(ref rejected);
                    }
                    else Interlocked.Increment(ref silent);
                }
                catch { }
            }
        }
    }
}
