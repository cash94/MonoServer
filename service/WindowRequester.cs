// Какие куски просить у пиров — только окно вокруг читателей, как
// ReaderReadAHead у TorrServer. Переделка StreamingPieceRequester из MonoTorrent
// 3.0.2 (MIT, лицензия ниже): тот, кроме окна у позиции,
// понемногу качает и весь торрент — в память на телевизоре это не влезет и
// только вытесняло бы нужное. Здесь:
//   - нет читателей — ничего не просим (торрент только держит пиров);
//   - главный читатель (последний открытый) — первые 2 куска срочно, дальше
//     его окно до конца файла или «Размера кэша × упреждение»;
//   - остальные читатели (плеер читает конец файла, пока играет начало) —
//     по 2 куска у своей позиции;
//   - окно качается по порядку от позиции (StandardPicker берёт первый
//     свободный кусок диапазона). У StreamingPieceRequester за двумя срочными
//     кусками шли «редкие первыми» вперемешку: дальние куски окна докачивались
//     раньше ближних, и буфер подряд от позиции (его и показывает плеер)
//     проваливался до остатка текущего куска — при кусках по 8 МБ до пары МБ;
//   - маска доступных кусков обрезается по окну: StandardPicker берёт куски
//     «allowed fast» и «suggested» без учёта диапазона, и каждый такой кусок —
//     целый буфер в памяти (у раздач с кусками по 8 МБ память так дошла до
//     152 МБ при кэше 64), а докачивается он от пира, который нас не пускает;
//   - очередь запросов у пира — сколько он сам тянет (MaxPendingRequests растёт
//     со скоростью, как в обычном режиме MonoTorrent). У StreamingPieceRequester
//     было 2–4 блока по 16 КБ: при задержке ~100 мс это ~0,5 МБ/с на пира, и
//     скорость упиралась в это, сколько бы пиров ни было.
// Позиции даёт TorrEngine (TorrEntry.Window), спрашиваем на каждом такте MonoTorrent.
//
// MIT License, Copyright (C) 2006 Alan McGovern, Olivier Dufour — Permission is
// hereby granted, free of charge, to any person obtaining a copy of this
// software... (полный текст — res/THIRD-PARTY-NOTICES.txt, он же в пакете).
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
using System;
using System.Collections.Generic;
using System.Linq;
using MonoTorrent;
using MonoTorrent.PiecePicking;

namespace TorrStreamTorrServer
{
    /// <summary>Куда читатели смотрят сейчас: номера кусков (−1 — читателей нет). Не меняется после создания</summary>
    public sealed class ReadWindow
    {
        public static readonly ReadWindow None = new ReadWindow { Primary = -1 };
        public int Primary;          // кусок главного читателя
        public int PrimaryEnd;       // последний кусок его окна (включительно)
        public int[] Secondary;      // куски остальных читателей
    }

    public class WindowRequester : IStreamingPieceRequester
    {
        const int HighPriorityCount = 15;
        // Без своего предела: Math.Min с peer.MaxPendingRequests ниже
        const int AsManyAsPeerTakes = int.MaxValue;
        // Сколько блоков выбирать за раз (буфер PickPiece)
        const int PickBatch = 64;

        readonly Func<ReadWindow> window;
        IMessageEnqueuer Enqueuer;
        IPieceRequesterData TorrentData;
        IReadOnlyList<ReadOnlyBitField> IgnoringBitfields;
        IPiecePicker LowPriorityPicker;
        IPiecePicker HighPriorityPicker;
        BitField Temp;
        BitField Range;
        int lastPrimary = -1;
        ReadOnlyBitField[] otherAvailableCache = Array.Empty<ReadOnlyBitField>();
        PieceSegment[] CancellationsCache = Array.Empty<PieceSegment>();

        public WindowRequester(Func<ReadWindow> window)
        {
            this.window = window;
        }

        public bool InEndgameMode => false;

        public void Initialise(IPieceRequesterData torrentData, IMessageEnqueuer enqueuer, ReadOnlySpan<ReadOnlyBitField> ignoringBitfields)
        {
            TorrentData = torrentData;
            Enqueuer = enqueuer;
            IgnoringBitfields = ignoringBitfields.ToArray();
            Temp = new BitField(TorrentData.PieceCount);
            Range = new BitField(TorrentData.PieceCount);

            var standardPicker = new StandardPicker();
            HighPriorityPicker = IgnoringPicker.Wrap(new PriorityPicker(standardPicker), IgnoringBitfields);
            // По порядку, без RandomisedPicker/RarestFirstPicker — см. выше
            LowPriorityPicker = IgnoringPicker.Wrap(new PriorityPicker(standardPicker), IgnoringBitfields);
            LowPriorityPicker.Initialise(torrentData);
            HighPriorityPicker.Initialise(torrentData);
        }

        ReadWindow Current()
        {
            ReadWindow w;
            try { w = window() ?? ReadWindow.None; }
            catch { w = ReadWindow.None; }
            if (TorrentData == null || w.Primary < 0 || w.Primary >= TorrentData.PieceCount) return ReadWindow.None;
            int end = Math.Max(w.Primary, Math.Min(TorrentData.PieceCount - 1, w.PrimaryEnd));
            if (end == w.PrimaryEnd) return w;
            return new ReadWindow { Primary = w.Primary, PrimaryEnd = end, Secondary = w.Secondary };
        }

        public void AddRequests(ReadOnlySpan<(IRequester Peer, ReadOnlyBitField Available)> peers)
        {
            if (TorrentData == null || Enqueuer == null) return;
            var w = Current();
            if (w.Primary < 0) { lastPrimary = -1; return; }

            // Главный читатель перескочил (перемотка) — отменяем запросы вне его окна
            // у пиров, которые это умеют, чтобы они не занимали очередь
            bool seeked = lastPrimary >= 0 && (w.Primary < lastPrimary || w.Primary > lastPrimary + HighPriorityCount);
            lastPrimary = w.Primary;

            var allPeers = peers.ToArray().OrderBy(t => -t.Peer.DownloadSpeed).ToArray();
            var bitfield = GenerateAlreadyHaves();
            if (seeked && bitfield.FirstFalse(w.Primary, Math.Min(w.PrimaryEnd, w.Primary + HighPriorityCount - 1)) != -1)
            {
                foreach (var peer in allPeers.Where(p => p.Peer.CanCancelRequests))
                {
                    if (w.Primary > 0)
                        CancelRequests(peer.Peer, 0, w.Primary - 1);
                    if (w.PrimaryEnd + 1 < bitfield.Length)
                        CancelRequests(peer.Peer, w.PrimaryEnd + 1, bitfield.Length - 1);
                }
            }

            if (otherAvailableCache.Length < peers.Length)
                otherAvailableCache = new ReadOnlyBitField[peers.Length];
            var otherAvailable = otherAvailableCache.AsSpan(0, peers.Length);
            for (int i = 0; i < otherAvailable.Length; i++)
                otherAvailable[i] = peers[i].Available;

            // Срочное (кусок под позицией) — сначала у быстрых пиров
            var fastestPeers = allPeers.Where(t => t.Peer.DownloadSpeed > 50 * 1024).ToArray();
            foreach (var supportsFastPeer in new[] { true, false })
                for (int i = 0; i < 4; i++)
                    foreach (var peer in fastestPeers.Where(p => p.Peer.SupportsFastPeer == supportsFastPeer))
                        AddRequests(peer.Peer, peer.Available, otherAvailable, w.Primary, Math.Min(w.Primary + 1, w.PrimaryEnd), 2, (i + 1) * 2, w);

            foreach (var peer in peers)
                AddRequests(peer.Peer, peer.Available, otherAvailable, w);
        }

        public void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers)
        {
            if (TorrentData == null) return;
            var w = Current();
            if (w.Primary < 0) return;
            AddRequests(peer, available, allPeers, w);
        }

        void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers, ReadWindow w)
        {
            AddRequests(peer, available, allPeers, w.Primary, Math.Min(w.Primary + 1, w.PrimaryEnd), 2, AsManyAsPeerTakes, w);
            if (w.Secondary != null)
                foreach (int s in w.Secondary)
                    if (s >= 0 && s < TorrentData.PieceCount)
                        AddRequests(peer, available, allPeers, s, Math.Min(s + 1, TorrentData.PieceCount - 1), 2, AsManyAsPeerTakes, w);
            AddRequests(peer, available, allPeers, w.Primary, w.PrimaryEnd, 1, AsManyAsPeerTakes, w);
        }

        void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers, int startPieceIndex, int endPieceIndex, int maxDuplicates, int preferredMaxRequests, ReadWindow w)
        {
            if (!peer.CanRequestMorePieces || TorrentData == null || Enqueuer == null)
                return;

            int maxTotalRequests = Math.Min(preferredMaxRequests, peer.MaxPendingRequests);

            if (!peer.IsChoking)
            {
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (LowPriorityPicker.ContinueAnyExistingRequest(peer, available, startPieceIndex, endPieceIndex, maxDuplicates, out PieceSegment request))
                        Enqueuer.EnqueueRequest(peer, request);
                    else
                        break;
                }
            }

            if (peer.SupportsFastPeer && peer.IsChoking)
            {
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (LowPriorityPicker.ContinueExistingRequest(peer, startPieceIndex, endPieceIndex, out PieceSegment segment))
                        Enqueuer.EnqueueRequest(peer, segment);
                    else
                        break;
                }
            }

            if (!peer.IsChoking || (peer.SupportsFastPeer && peer.IsAllowedFastPieces.Count > 0))
            {
                BitField filtered = null;
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (filtered == null)
                    {
                        Range.SetAll(false).SetTrue((startPieceIndex, endPieceIndex));
                        filtered = GenerateAlreadyHaves().Not().And(available).And(Range);
                    }
                    var buffer = new PieceSegment[Math.Min(PickBatch, maxTotalRequests - peer.AmRequestingPiecesCount)];
                    int requested = PriorityPick(peer, filtered, allPeers, startPieceIndex, endPieceIndex, buffer, w);
                    if (requested > 0)
                        Enqueuer.EnqueueRequests(peer, buffer.AsSpan(0, requested));
                    else
                        break;
                }
            }
        }

        BitField GenerateAlreadyHaves()
        {
            Temp.From(IgnoringBitfields[0]);
            for (int i = 1; i < IgnoringBitfields.Count; i++)
                Temp.Or(IgnoringBitfields[i]);
            return Temp;
        }

        int PriorityPick(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> otherPeers, int startIndex, int endIndex, Span<PieceSegment> requests, ReadWindow w)
        {
            int requestCount;
            int hp = w.Primary;
            if (hp >= startIndex && hp <= endIndex)
            {
                var end = Math.Min(endIndex, hp + HighPriorityCount - 1);
                for (int prioritised = hp; prioritised <= hp + 1 && prioritised <= end; prioritised++)
                {
                    if (available[prioritised])
                    {
                        if ((requestCount = HighPriorityPicker.PickPiece(peer, available, otherPeers, prioritised, prioritised, requests)) > 0)
                            return requestCount;
                        if (HighPriorityPicker.ContinueAnyExistingRequest(peer, available, prioritised, prioritised, 3, out requests[0]))
                            return 1;
                    }
                }
                if ((requestCount = HighPriorityPicker.PickPiece(peer, available, otherPeers, hp, end, requests)) > 0)
                    return requestCount;
            }
            // Остальное окно (или кусок другого читателя) — по порядку
            return LowPriorityPicker.PickPiece(peer, available, otherPeers, startIndex, endIndex, requests);
        }

        // Позиции читателей берём из TorrEngine сами — эти вызовы StreamProvider не нужны
        public void SeekToPosition(ITorrentManagerFile file, long position) { }
        public void ReadToPosition(ITorrentManagerFile file, long position) { }

        public bool ValidatePiece(IRequester peer, PieceSegment blockInfo, out bool pieceComplete, HashSet<IRequester> peersInvolved)
            => HighPriorityPicker.ValidatePiece(peer, blockInfo, out pieceComplete, peersInvolved);

        public bool IsInteresting(IRequester peer, ReadOnlyBitField bitfield)
            => HighPriorityPicker.IsInteresting(peer, bitfield);

        public void CancelRequests(IRequester peer, int startIndex, int endIndex)
        {
            if (HighPriorityPicker == null || Enqueuer == null)
                return;
            if (CancellationsCache.Length < peer.AmRequestingPiecesCount)
                CancellationsCache = new PieceSegment[peer.AmRequestingPiecesCount];
            var cancellations = CancellationsCache.AsSpan(0, peer.AmRequestingPiecesCount);
            var cancelled = HighPriorityPicker.CancelRequests(peer, startIndex, endIndex, cancellations);
            Enqueuer.EnqueueCancellations(peer, cancellations.Slice(0, cancelled));
        }

        public void RequestRejected(IRequester peer, PieceSegment blockInfo)
            => HighPriorityPicker.RequestRejected(peer, blockInfo);

        public int CurrentRequestCount()
            => HighPriorityPicker == null ? 0 : HighPriorityPicker.CurrentRequestCount();
    }
}
