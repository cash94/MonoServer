// Куски торрента — только в памяти, как кэш TorrServer: на диск телевизора
// ничего не пишем (мало места, износ флеш-памяти). MonoTorrent пишет и читает
// через IPieceWriter — это он и есть (MemoryStorage), а данные каждого торрента
// лежат в своём PieceStore: кусок целиком в byte[], лимит — «Размер кэша».
//
// Места не хватает — вытесняем проверенный кусок, который дольше всех не
// трогали и который не нужен ни одному читателю (окно вокруг позиции, см.
// TorrEngine.IsProtected). Вытеснили — об этом узнаёт TorrEngine и снимает бит
// куска в bitfield MonoTorrent: иначе тот считал бы кусок скачанным и не стал бы
// качать его снова при перемотке назад.
using System;
using System.Collections.Generic;
using System.Threading;
using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace TorrStreamTorrServer
{
    public class PieceStore
    {
        // Недокачанный кусок вне окон читателей, который так долго не трогали,
        // тоже можно выкинуть: запрос к нему отменили (перемотка), а память он
        // держит — у раздач с кусками по 8–16 МБ это заметно
        const long StaleUnverifiedMs = 30000;

        class Slot
        {
            public byte[] Data;
            public bool Verified;
            public long Touched;     // Now() последнего обращения, мс
        }

        public readonly int PieceLength;
        public readonly int PieceCount;
        public readonly long TotalSize;
        readonly object sync = new object();
        readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();
        readonly Stack<byte[]> spare = new Stack<byte[]>();
        long capacity;
        bool disposed;

        /// <summary>Кусок нужен читателю — не вытеснять (TorrEngine)</summary>
        public Func<int, bool> IsProtected = _ => false;
        /// <summary>Кусок вытеснен — снять бит в MonoTorrent (зовётся вне блокировки)</summary>
        public Action<int> Evicted = _ => { };

        public PieceStore(int pieceLength, int pieceCount, long totalSize, long capacity)
        {
            PieceLength = pieceLength;
            PieceCount = pieceCount;
            TotalSize = totalSize;
            this.capacity = capacity;
        }

        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long Now() { return clock.ElapsedMilliseconds; }

        public long Capacity
        {
            get { lock (sync) return capacity; }
            set { lock (sync) capacity = value; }
        }

        /// <summary>Сколько байт держат куски (выделенные буферы)</summary>
        public long Filled
        {
            get { lock (sync) return (long)slots.Count * PieceLength; }
        }

        int PieceSize(int piece)
        {
            if (piece == PieceCount - 1)
            {
                long rest = TotalSize - (long)piece * PieceLength;
                return (int)rest;
            }
            return PieceLength;
        }

        /// <summary>Запись блока от MonoTorrent: смещение — от начала торрента</summary>
        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            List<int> evicted = null;
            lock (sync)
            {
                if (disposed) return;
                while (data.Length > 0)
                {
                    int piece = (int)(offset / PieceLength);
                    int inPiece = (int)(offset - (long)piece * PieceLength);
                    int n = Math.Min(data.Length, PieceLength - inPiece);
                    Slot slot;
                    if (!slots.TryGetValue(piece, out slot))
                    {
                        slot = new Slot { Data = Allocate(piece, ref evicted) };
                        slots[piece] = slot;
                    }
                    data.Slice(0, n).CopyTo(new Span<byte>(slot.Data, inPiece, n));
                    slot.Touched = Now();
                    offset += n;
                    data = data.Slice(n);
                }
            }
            if (evicted != null)
                foreach (int p in evicted) Evicted(p);
        }

        /// <summary>
        /// Чтение для MonoTorrent (проверка хэша, отдача пирам): сколько байт есть
        /// подряд с этого места. Кусок вытеснен — меньше, чем просили
        /// </summary>
        public int Read(long offset, Span<byte> dst)
        {
            lock (sync) return ReadLocked(offset, dst, false);
        }

        /// <summary>То же, но только из проверенных кусков — для плеера</summary>
        public int ReadVerified(long offset, Span<byte> dst)
        {
            lock (sync) return ReadLocked(offset, dst, true);
        }

        int ReadLocked(long offset, Span<byte> dst, bool verifiedOnly)
        {
            int done = 0;
            while (dst.Length > 0)
            {
                int piece = (int)(offset / PieceLength);
                int inPiece = (int)(offset - (long)piece * PieceLength);
                Slot slot;
                if (!slots.TryGetValue(piece, out slot) || (verifiedOnly && !slot.Verified)) break;
                int n = Math.Min(dst.Length, PieceSize(piece) - inPiece);
                if (n <= 0) break;
                new ReadOnlySpan<byte>(slot.Data, inPiece, n).CopyTo(dst);
                slot.Touched = Now();
                done += n;
                offset += n;
                dst = dst.Slice(n);
            }
            return done;
        }

        /// <summary>MonoTorrent проверил хэш куска (TorrentManager.PieceHashed)</summary>
        public void MarkHashed(int piece, bool passed)
        {
            lock (sync)
            {
                Slot slot;
                if (slots.TryGetValue(piece, out slot))
                {
                    slot.Verified = passed;
                    slot.Touched = Now();
                }
                Monitor.PulseAll(sync);
            }
        }

        public bool IsVerified(int piece)
        {
            lock (sync)
            {
                Slot slot;
                return slots.TryGetValue(piece, out slot) && slot.Verified;
            }
        }

        /// <summary>Ждать проверенный кусок не дольше timeoutMs; true — есть</summary>
        public bool WaitVerified(int piece, int timeoutMs)
        {
            lock (sync)
            {
                Slot slot;
                if (slots.TryGetValue(piece, out slot) && slot.Verified) return true;
                if (disposed) return false;
                Monitor.Wait(sync, timeoutMs);
                return slots.TryGetValue(piece, out slot) && slot.Verified;
            }
        }

        /// <summary>Сколько байт подряд скачано и проверено с этого места (не дальше limit)</summary>
        public long VerifiedRun(long offset, long limit)
        {
            lock (sync)
            {
                long end = Math.Min(TotalSize, offset + limit), pos = offset;
                while (pos < end)
                {
                    int piece = (int)(pos / PieceLength);
                    Slot slot;
                    if (!slots.TryGetValue(piece, out slot) || !slot.Verified) break;
                    pos = (long)(piece + 1) * PieceLength;
                }
                return Math.Max(0, Math.Min(pos, end) - offset);
            }
        }

        /// <summary>Куски в памяти: номер → проверен ли (для /cache)</summary>
        public List<KeyValuePair<int, bool>> Snapshot()
        {
            lock (sync)
            {
                var list = new List<KeyValuePair<int, bool>>(slots.Count);
                foreach (var kv in slots) list.Add(new KeyValuePair<int, bool>(kv.Key, kv.Value.Verified));
                list.Sort((a, b) => a.Key.CompareTo(b.Key));
                return list;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                slots.Clear();
                spare.Clear();
                Monitor.PulseAll(sync);
            }
        }

        // Буфер под новый кусок; не влезает в лимит — сначала вытеснить.
        // Вытеснить нечего (всё нужно читателям) — всё равно выделяем: лимит
        // мягкий, а размер окна читателей и так от него считается
        byte[] Allocate(int piece, ref List<int> evicted)
        {
            while ((long)(slots.Count + 1) * PieceLength > capacity)
            {
                int victim = PickVictim(piece);
                if (victim < 0) break;
                var data = slots[victim].Data;
                slots.Remove(victim);
                if (data.Length == PieceLength) spare.Push(data);
                (evicted ?? (evicted = new List<int>())).Add(victim);
            }
            if (spare.Count > 0) return spare.Pop();
            return new byte[PieceLength];
        }

        int PickVictim(int forPiece)
        {
            long now = Now();
            int best = -1;
            long bestTouched = long.MaxValue;
            bool bestVerified = false;
            foreach (var kv in slots)
            {
                if (kv.Key == forPiece) continue;
                var s = kv.Value;
                if (!s.Verified && now - s.Touched < StaleUnverifiedMs) continue;
                if (IsProtected(kv.Key)) continue;
                // Проверенные — в первую очередь, среди них — самый давний
                if (best < 0 || (s.Verified && !bestVerified) || (s.Verified == bestVerified && s.Touched < bestTouched))
                {
                    best = kv.Key;
                    bestTouched = s.Touched;
                    bestVerified = s.Verified;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// IPieceWriter для MonoTorrent: файлы торрента → PieceStore этого торрента.
    /// Какой торрент — по пути файла (у каждого свой каталог, TorrEngine.Resolve)
    /// </summary>
    public class MemoryStorage : IPieceWriter
    {
        readonly Func<ITorrentManagerFile, PieceStore> resolve;

        public MemoryStorage(Func<ITorrentManagerFile, PieceStore> resolve)
        {
            this.resolve = resolve;
        }

        public int OpenFiles => 0;
        public int MaximumOpenFiles => 0;

        public ReusableTask CloseAsync(ITorrentManagerFile file) => ReusableTask.CompletedTask;
        // Файлов «нет»: при запуске MonoTorrent не проверяет хэши — проверять нечего
        public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => ReusableTask.FromResult(false);
        public ReusableTask FlushAsync(ITorrentManagerFile file) => ReusableTask.CompletedTask;
        public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite) => ReusableTask.CompletedTask;
        public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => ReusableTask.CompletedTask;

        public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
        {
            var store = resolve(file);
            if (store == null) return ReusableTask.FromResult(0);
            return ReusableTask.FromResult(store.Read(file.OffsetInTorrent + offset, buffer.Span));
        }

        public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
        {
            var store = resolve(file);
            if (store != null) store.Write(file.OffsetInTorrent + offset, buffer.Span);
            return ReusableTask.CompletedTask;
        }

        public void Dispose() { }
    }
}
