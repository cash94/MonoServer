// Выбор кусков и блоков — StandardPicker из MonoTorrent 3.0.2 (MIT, лицензия
// ниже) с одним отличием: блоки куска под главным читателем просим не с начала
// куска, а с позиции читателя (дальше — до конца куска, потом начало: без него
// кусок не проверить). В ResponsiveMode плеер получает блоки подряд от позиции,
// и при кусках по 8 МБ перемотка в середину куска ждала, пока скачаются
// мегабайты до неё, — при 0,7 МБ/с до 11 с, дольше таймаута плеера.
// Изменения против оригинала — BlockOrder и места, где он используется; кэш
// объектов Piece (внутренний тип MonoTorrent) заменён на new.
//
// MIT License, Copyright (C) 2006 Alan McGovern — Permission is hereby granted,
// free of charge, to any person obtaining a copy of this software... (полный
// текст — res/THIRD-PARTY-NOTICES.txt, он же в пакете).
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MonoTorrent;
using MonoTorrent.PiecePicking;

namespace TorrStreamTorrServer
{
    partial class PositionPicker
    {
        /// <summary>
        ///
        /// </summary>
        struct Block
        {
            readonly Piece piece;
            bool received;

            public int PieceIndex => piece.Index;

            public bool Received {
                get => received;
                internal set {
                    if (value && !received)
                        piece.TotalReceived++;

                    else if (!value && received)
                        piece.TotalReceived--;

                    received = value;
                }
            }

            public int BlockIndex { get; private set; }

            public bool Requested => RequestedOff != null;

            internal IRequester? RequestedOff { get; private set; }

            internal Block (Piece piece, int blockIndex)
            {
                RequestedOff = null;
                this.piece = piece;
                received = false;
                BlockIndex = blockIndex;
            }

            internal PieceSegment CreateRequest (IRequester peer)
            {
                if (RequestedOff == null)
                    piece.TotalRequested++;

                RequestedOff = peer;
                RequestedOff.AmRequestingPiecesCount++;
                return new PieceSegment (PieceIndex, BlockIndex);
            }

            internal void CancelRequest ()
            {
                if (RequestedOff != null) {
                    piece.TotalRequested--;
                    RequestedOff.AmRequestingPiecesCount--;
                    RequestedOff = null;
                }
            }

            public override bool Equals (object? obj)
            {
                if (!(obj is Block other))
                    return false;

                return PieceIndex == other.PieceIndex && BlockIndex == other.BlockIndex;
            }

            public override int GetHashCode ()
            {
                return PieceIndex ^ (BlockIndex << 24);
            }

            internal void FromRequest (ActivePieceRequest block)
            {
                Received = block.Received;
                RequestedOff = block.RequestedOff;

                piece.TotalRequested += 1;
            }

            internal void TrySetReceived (IRequester peer)
            {
                if (!received) {
                    CancelRequest ();
                    RequestedOff = peer;
                    Received = true;
                }
            }
        }
    }

    partial class PositionPicker
    {
        
        class Piece : IComparable<Piece>
        {
            public Block this[int index] => Blocks[index];

            /// <summary>
            /// Set to true when the original peer times out sending a piece, disconnects, or chokes us.
            /// This allows other peers to immediately begin downloading blocks from this piece to complete
            /// it.
            /// </summary>
            internal bool Abandoned { get; set; }

            internal Block[] Blocks { get; set; }

            // Пир, которому кусок назначен (в оригинале — Blocks[0].RequestedOff:
            // первым просили нулевой блок, у нас — блок у позиции читателя)
            internal IRequester? Owner { get; set; }

            public bool AllBlocksRequested => TotalRequested == Blocks.Length;

            public bool AllBlocksReceived => TotalReceived == Blocks.Length;

            public bool AllBlocksWritten => TotalWritten == Blocks.Length;

            public int BlockCount => Blocks.Length;

            public int Index { get; private set; }

            public bool NoBlocksRequested => TotalRequested == 0;

            public int TotalReceived { get; internal set; }

            public int TotalRequested { get; internal set; }

            public int TotalWritten { get; internal set; }


            internal Piece ()
            {
                Blocks = Array.Empty<Block> ();
            }

            public int CompareTo (Piece? other)
                => other == null ? 1 : Index.CompareTo (other.Index);

            public override bool Equals (object? obj)
                => obj is Piece p && Index.Equals (p.Index);

            public System.Collections.IEnumerator GetEnumerator ()
            {
                return Blocks.GetEnumerator ();
            }

            public override int GetHashCode ()
            {
                return Index;
            }

            public void Initialise ()
                => Initialise (-1, -1);

            public Piece Initialise (int pieceIndex, int blockCount)
            {
                Index = pieceIndex;

                Abandoned = false;
                Owner = null;
                TotalReceived = 0;
                TotalRequested = 0;
                TotalWritten = 0;

                if (blockCount != -1 && Blocks.Length != blockCount)
                    Blocks = new Block[blockCount];

                for (int i = 0; i < Blocks.Length; i++)
                    Blocks[i] = new Block (this, i);

                return this;
            }

            string ToDebuggerString ()
            {
                return $"Piece {Index}";
            }

            internal void CalculatePeersInvolved (HashSet<IRequester> peersInvolved)
            {
                foreach (var block in Blocks)
                    if (block.RequestedOff != null)
                        peersInvolved.Add (block.RequestedOff);
            }
        }
    }

    partial class PositionPicker
    {
        class PickedPieces
        {
            readonly BitField alreadyRequestedBitField;
            readonly Dictionary<int, List<Piece>> duplicates;
            readonly Dictionary<IRequester, Piece> mostRecentRequest;
            readonly Dictionary<int, Piece> requests;
            List<IRequester> tmpList;

            public ReadOnlyBitField AlreadyRequestedBitfield => alreadyRequestedBitField;

            public Dictionary<int, Piece>.ValueCollection Values => requests.Values;

            public PickedPieces (int pieceCount)
            {
                alreadyRequestedBitField = new BitField (pieceCount);
                duplicates = new Dictionary<int, List<Piece>> ();
                mostRecentRequest = new Dictionary<IRequester, Piece> ();
                requests = new Dictionary<int, Piece> ();
                tmpList = new List<IRequester> ();
            }

            internal void Remove (int index)
            {
                alreadyRequestedBitField[index] = false;
                requests.Remove (index);
                duplicates.Remove (index);

                foreach (var v in mostRecentRequest)
                    if (v.Value.Index == index)
                        tmpList.Add (v.Key);

                foreach (var toRemove in tmpList)
                    mostRecentRequest.Remove (toRemove);
                tmpList.Clear ();
            }

            internal bool TryGetValue (int pieceIndex, out Piece piece)
                => requests.TryGetValue (pieceIndex, out piece);

            internal bool TryGetDuplicates (int pieceIndex, out List<Piece> extraPieces)
                => duplicates.TryGetValue (pieceIndex, out extraPieces);

            internal bool TryGetMostRecentRequest (IRequester peer, out Piece mostRecent)
                => mostRecentRequest.TryGetValue (peer, out mostRecent);

            internal void CreateDuplicates (int index, List<Piece> extraPieces)
                => duplicates.Add (index, extraPieces);

            internal void AddRequest (IRequester peer, Piece piece)
            {
                requests.Add (piece.Index, piece);
                piece.Owner = peer;
                alreadyRequestedBitField[piece.Index] = true;
                mostRecentRequest[peer] = piece;
            }

            internal void ClearMostRecentRequest (IRequester peer)
                => mostRecentRequest.Remove (peer);
        }
    }

    public partial class PositionPicker : IPiecePicker
    {

        // static readonly Logger logger = Logger.Create (nameof(PositionPicker));

        BitField? CanRequestBitField;
        PickedPieces? Requests { get; set; }
        IPieceRequesterData? TorrentData { get; set; }

        readonly Func<long> primaryOffset;

        /// <param name="primaryOffset">Смещение главного читателя от начала торрента, −1 — читателя нет</param>
        public PositionPicker (Func<long> primaryOffset)
        {
            this.primaryOffset = primaryOffset;
        }

        // С какого блока просить кусок: у куска под главным читателем — с его позиции
        int FirstBlock (Piece piece)
        {
            if (TorrentData == null || piece.BlockCount == 0)
                return 0;
            long off;
            try { off = primaryOffset (); } catch { return 0; }
            if (off < 0 || off / TorrentData.PieceLength != piece.Index)
                return 0;
            int block = (int) ((off - (long) piece.Index * TorrentData.PieceLength) / Constants.BlockSize);
            return block > 0 && block < piece.BlockCount ? block : 0;
        }

        // k-й по очереди блок: от первого до конца куска. Начало куска (до
        // позиции) пока читатель в нём не просим вовсе: иначе пир, докачав хвост,
        // качал бы начало, а следующий кусок ждал — плеер стоял секунды на стыке.
        // Начало докачивается, когда читатель уйдёт дальше (WindowRequester —
        // кусок позади него, в последнюю очередь)
        static int BlockOrder (int first, int count, int k)
            => first + k;

        static int OrderLength (int first, int count)
            => count - first;

        public int CancelRequests (IRequester peer, int startIndex, int endIndex, Span<PieceSegment> cancellations)
        {
            if (Requests == null)
                return 0;

            var length = cancellations.Length;
            foreach (var piece in Requests.Values) {
                if (piece.Index < startIndex || piece.Index > endIndex)
                    continue;

                CancelRequests (peer, piece, ref cancellations);
                if (Requests.TryGetDuplicates(piece.Index, out List<Piece>? duplicates)) {
                    foreach (var dupe in duplicates)
                        CancelRequests (peer, dupe, ref cancellations);
                }
            }

            Requests.ClearMostRecentRequest (peer);
            return length - cancellations.Length;
        }

        void CancelRequests (IRequester peer, Piece piece, ref Span<PieceSegment> cancellations)
        {
            foreach (ref Block block in piece.Blocks.AsSpan ()) {
                if (!block.Received && block.RequestedOff == peer) {
                    block.CancelRequest ();
                    piece.Abandoned = true;
                    cancellations[0] = new PieceSegment (piece.Index, block.BlockIndex);
                    cancellations = cancellations.Slice (1);
                }
            }
        }

        public void RequestRejected (IRequester peer, PieceSegment rejectedRequest)
        {
            if (Requests == null)
                return;

            foreach (var piece in Requests.Values) {
                if (piece.Index != rejectedRequest.PieceIndex)
                    continue;

                RequestRejected (peer, piece, rejectedRequest);

                if (Requests.TryGetDuplicates (piece.Index, out List<Piece>? duplicates)) {
                    foreach (var dupe in duplicates)
                        RequestRejected (peer, piece, rejectedRequest);
                }
            }

            Requests.ClearMostRecentRequest (peer);
        }

        void RequestRejected (IRequester peer, Piece piece, PieceSegment rejectedRequest)
        {
            if (piece.Blocks[rejectedRequest.BlockIndex].RequestedOff == peer) {
                piece.Abandoned = true;
                piece.Blocks[rejectedRequest.BlockIndex].CancelRequest ();
            }
        }

        public int CurrentReceivedCount ()
        {
            int count = 0;
            if (Requests != null)
                foreach (var piece in Requests.Values)
                    count += piece.TotalReceived;
            return count;
        }

        public int CurrentRequestCount ()
        {
            int count = 0;
            if (Requests != null)
                foreach (var piece in Requests.Values)
                    count += piece.TotalRequested - piece.TotalReceived;
            return count;
        }

        // В 3.0.2 не вызывается никем, а конструктор ActivePieceRequest — внутренний
        public IList<ActivePieceRequest> ExportActiveRequests ()
            => Array.Empty<ActivePieceRequest> ();

        public void Initialise (IPieceRequesterData torrentData)
        {
            TorrentData = torrentData;

            CanRequestBitField = new BitField (TorrentData.PieceCount);
            Requests = new PickedPieces (TorrentData.PieceCount);
        }

        public bool IsInteresting (IRequester peer, ReadOnlyBitField bitfield)
        {
            return !bitfield.AllFalse;
        }

        public int PickPiece (IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> otherPeers, int startIndex, int endIndex, Span<PieceSegment> requests)
        {
            // If there is already a request on this peer, try to request the next block. If the peer is choking us, then the only
            // requests that could be continued would be existing "Fast" pieces.
            if (ContinueExistingRequest (peer, available, startIndex, endIndex, 1, false, false, out requests[0]))
                return 1;

            // Then we check if there are any allowed "Fast" pieces to download
            if (peer.IsChoking && GetFromList (peer, available, peer.IsAllowedFastPieces, out requests[0]))
                return 1;

            // If the peer is choking, then we can't download from them as they had no "fast" pieces for us to download
            if (peer.IsChoking)
                return 0;

            // Only try to continue an abandoned piece if this peer has not recently been involved in downloading data which
            // failed it's hash check.
            if (peer.RepeatedHashFails == 0 && ContinueExistingRequest (peer, available, startIndex, endIndex, 1, true, false, out requests[0]))
                return 1;

            // We see if the peer has suggested any pieces we should request
            if (GetFromList (peer, available, peer.SuggestedPieces, out requests[0]))
                return 1;

            // Now we see what pieces the peer has that we don't have and try and request one
            return GetStandardRequest (peer, available, startIndex, endIndex, requests);
        }

        public void Reset ()
        {
            if (TorrentData != null)
                Requests = new PickedPieces (TorrentData.PieceCount);
        }

        static readonly Func<Piece, int, int> IndexComparer = (Piece piece, int comparand)
            => piece.Index.CompareTo (comparand);

        public bool ValidatePiece (IRequester peer, PieceSegment request, out bool pieceComplete, HashSet<IRequester> peersInvolved)
        {
            if (Requests == null || !Requests.TryGetValue (request.PieceIndex, out Piece? primaryPiece)) {
                //logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} No piece.", request.PieceIndex, request.StartOffset, peer);
                pieceComplete = false;
                return false;
            }

            var result = ValidateRequestWithPiece (peer, request, primaryPiece, out pieceComplete, peersInvolved);

            // If there are no duplicate requests, exit early! Otherwise we'll need to do book keeping to
            // ensure our received piece is not re-requested again.
            if (!Requests.TryGetDuplicates (request.PieceIndex, out List<Piece>? extraPieces)) {
                if (pieceComplete) {
                    Requests.Remove (request.PieceIndex);
                }
                return result;
            }

            // We have duplicate requests and have, so far, failed to validate the block. Try to validate it now.
            if (!result) {
                for (int i = 0; i < extraPieces.Count && !result; i++)
                    if ((result = ValidateRequestWithPiece (peer, request, extraPieces[i], out pieceComplete, peersInvolved)))
                        break;
            }


            // If we successfully validated the block using *any* version of the request, update the primary piece and
            // all duplicates to reflect this. This will implicitly cancel any outstanding requests from other peers.
            if (result) {
                primaryPiece.Blocks[request.BlockIndex].TrySetReceived (peer);
                for (int i = 0; i < extraPieces.Count; i++)
                    extraPieces[i].Blocks[request.BlockIndex].TrySetReceived (peer);
            }

            // If the piece is complete then remove it, and any dupes, from the picker.
            if (pieceComplete) {
                Requests.Remove (request.PieceIndex);
                return result;
            }
            return result;
        }

        bool ValidateRequestWithPiece (IRequester peer, PieceSegment request, Piece piece, out bool pieceComplete, HashSet<IRequester> peersInvolved)
        {
            pieceComplete = false;

            // Pick out the block that this piece message belongs to
            int blockIndex = request.BlockIndex;
            ref Block block = ref piece.Blocks[blockIndex];
            if (blockIndex == -1 || !peer.Equals (block.RequestedOff)) {
                //logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} No block ", request.PieceIndex, request.StartOffset, peer);
                return false;
            }
            if (block.Received) {
                //logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} Already received.", request.PieceIndex, request.StartOffset, peer);
                return false;
            }
            if (!block.Requested) {
                //logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} Not requested.", request.PieceIndex, request.StartOffset, peer);
                return false;
            }
            peer.AmRequestingPiecesCount--;
            block.Received = true;

            if (piece.AllBlocksReceived) {
                pieceComplete = true;
                piece.CalculatePeersInvolved (peersInvolved);
            }
            return true;
        }

        public bool ContinueExistingRequest (IRequester peer, int startIndex, int endIndex, out PieceSegment segment)
            => ContinueExistingRequest (peer, null, startIndex, endIndex, 1, false, false, out segment);

        bool ContinueExistingRequest (IRequester peer, ReadOnlyBitField? availablePieces, int startIndex, int endIndex, int maxDuplicateRequests, bool allowAbandoned, bool allowAny, out PieceSegment segment)
        {
            segment = PieceSegment.Invalid;
            if (Requests is null || TorrentData is null)
                return false;

            if (Requests.TryGetMostRecentRequest (peer, out Piece? mostRecent)) {
                int first = FirstBlock (mostRecent);
                for (int k = 0; k < OrderLength (first, mostRecent.BlockCount); k++) {
                    ref Block block = ref mostRecent.Blocks[BlockOrder (first, mostRecent.BlockCount, k)];
                    if (!block.Requested && !block.Received) {
                        segment = block.CreateRequest (peer);
                        return true;
                    }
                }

                if (maxDuplicateRequests == 1 && !allowAbandoned && !allowAny)
                    return false;
            }

            if (availablePieces == null)
                return false;

            foreach (var p in Requests.Values) {
                int index = p.Index;
                if (p.AllBlocksRequested || index < startIndex || index > endIndex || !availablePieces[index])
                    continue;

                // For each piece that was assigned to this peer, try to request a block from it
                // A piece is 'assigned' to a peer if he is the first person to request a block from that piece
                if (allowAny || (allowAbandoned && p.Abandoned) || peer == p.Owner) {
                    int first = FirstBlock (p);
                    for (int k = 0; k < OrderLength (first, p.BlockCount); k++) {
                        ref Block block = ref p.Blocks[BlockOrder (first, p.BlockCount, k)];
                        if (!block.Requested && !block.Received) {
                            segment = block.CreateRequest (peer);
                            return true;
                        }
                    }
                }
            }

            // If all blocks have been requested at least once and we're allowed more than 1 request then
            // let's try and issue a duplicate!
            for (int duplicate = 1; duplicate < maxDuplicateRequests; duplicate++) {
                foreach (var primaryPiece in Requests.Values) {
                    if (primaryPiece.Index < startIndex || primaryPiece.Index > endIndex || !availablePieces[primaryPiece.Index])
                        continue;

                    if (!Requests.TryGetDuplicates (primaryPiece.Index, out List<Piece>? extraPieces)) {
                        extraPieces = new List<Piece> ();
                        Requests.CreateDuplicates (primaryPiece.Index, extraPieces);
                    }

                    if (extraPieces.Count < duplicate) {
                        var newPiece = new Piece ().Initialise (primaryPiece.Index, TorrentData.SegmentsPerPiece (primaryPiece.Index));
                        for (int i = 0; i < primaryPiece.BlockCount; i++)
                            if (primaryPiece.Blocks[i].Received)
                                newPiece.Blocks[i].TrySetReceived (primaryPiece.Blocks[i].RequestedOff!);
                        extraPieces.Add (newPiece);
                    }

                    int firstDup = FirstBlock (primaryPiece);
                    for (int extraPieceIndex = 0; extraPieceIndex < extraPieces.Count; extraPieceIndex++) {
                        var extraPiece = extraPieces[extraPieceIndex];
                        for (int k = 0; k < OrderLength (firstDup, extraPiece.BlockCount); k++) {
                            int i = BlockOrder (firstDup, extraPiece.BlockCount, k);
                            if (!extraPiece.Blocks[i].Requested && !HasAlreadyRequestedBlock (primaryPiece, extraPieces, peer, i)) {
                                segment = extraPiece.Blocks[i].CreateRequest (peer);
                                return true;
                            }
                        }
                    }
                }
            }

            // If we get here it means all the blocks in the pieces being downloaded by the peer are already requested
            return false;
        }

        static bool HasAlreadyRequestedBlock (Piece piece, IList<Piece> extraPieces, IRequester peer, int blockIndex)
        {
            if (piece.Blocks[blockIndex].RequestedOff == peer)
                return true;
            for (int i = 0; i < extraPieces.Count; i++)
                if (extraPieces[i].Blocks[blockIndex].RequestedOff == peer)
                    return true;
            return false;
        }

        public bool ContinueAnyExistingRequest (IRequester peer, ReadOnlyBitField available, int startIndex, int endIndex, int maxDuplicateRequests, out PieceSegment segment)
        {
            // If this peer is currently a 'dodgy' peer, then don't allow him to help with someone else's
            // piece request.
            if (peer.RepeatedHashFails != 0) {
                segment = PieceSegment.Invalid;
                return false;
            }

            return ContinueExistingRequest (peer, available, startIndex, endIndex, maxDuplicateRequests, true, true, out segment);
        }

        bool GetFromList (IRequester peer, ReadOnlyBitField bitfield, IList<int> pieces, out PieceSegment segment)
        {
            segment = PieceSegment.Invalid;
            if (!peer.SupportsFastPeer || Requests is null || TorrentData is null)
                return false;

            for (int i = 0; i < pieces.Count; i++) {
                int index = pieces[i];
                if (index >= bitfield.Length || !bitfield[index] || Requests.AlreadyRequestedBitfield [index])
                    continue;

                pieces.RemoveAt (i);
                var p = new Piece ().Initialise (index, TorrentData.SegmentsPerPiece (index));
                Requests.AddRequest (peer, p);
                segment = p.Blocks[FirstBlock (p)].CreateRequest (peer);
                return true;
            }


            return false;
        }

        int GetStandardRequest (IRequester peer, ReadOnlyBitField current, int startIndex, int endIndex, Span<PieceSegment> requests)
        {
            if (TorrentData == null || Requests == null)
                return 0;

            int piecesNeeded = (requests.Length * Constants.BlockSize) / TorrentData.PieceLength;
            if ((requests.Length * Constants.BlockSize) % TorrentData.PieceLength != 0)
                piecesNeeded++;
            int checkIndex = CanRequest (current, startIndex, endIndex, ref piecesNeeded);

            // Nothing to request.
            if (checkIndex == -1)
                return 0;

            var totalRequested = 0;
            for (int i = 0; totalRequested < requests.Length && i < piecesNeeded; i++) {
                // Request the piece
                var p = new Piece ().Initialise (checkIndex + i, TorrentData.SegmentsPerPiece (checkIndex + i));
                Requests.AddRequest (peer, p);
                int first = FirstBlock (p);
                for (int k = 0; k < OrderLength (first, p.Blocks.Length) && totalRequested < requests.Length; k++)
                    requests[totalRequested++] = p.Blocks[BlockOrder (first, p.Blocks.Length, k)].CreateRequest (peer);
            }
            return totalRequested;
        }

        int CanRequest (ReadOnlyBitField bitfield, int pieceStartIndex, int pieceEndIndex, ref int pieceCount)
        {
            if (CanRequestBitField == null || Requests == null)
                return 0;

            // This is the easiest case to consider - special case it
            if (pieceCount == 1) {
                return CanRequestBitField.From (bitfield).NAnd (Requests.AlreadyRequestedBitfield).FirstTrue (pieceStartIndex, pieceEndIndex);
            }

            int largestStart = 0;
            int largestEnd = 0;
            while (pieceStartIndex <= pieceEndIndex && (pieceStartIndex = bitfield.FirstTrue (pieceStartIndex, pieceEndIndex)) != -1) {
                int end = bitfield.FirstFalse (pieceStartIndex, pieceEndIndex);
                if (end == -1)
                    end = Math.Min (pieceStartIndex + pieceCount, bitfield.Length);

                // Do not include 'end' as it's the first *false* piece.
                for (int i = pieceStartIndex; i < end; i++)
                    if (Requests.AlreadyRequestedBitfield[i])
                        end = i;

                if ((end - pieceStartIndex) >= pieceCount)
                    return pieceStartIndex;

                if ((largestEnd - largestStart) < (end - pieceStartIndex)) {
                    largestStart = pieceStartIndex;
                    largestEnd = end;
                }

                pieceStartIndex = Math.Max (pieceStartIndex + 1, end);
            }

            pieceCount = largestEnd - largestStart;
            return pieceCount == 0 ? -1 : largestStart;
        }
    }
}
