// Пул буферов MonoTorrent (MemoryPool.Default — на нём сообщения пиров, кэш диска,
// шифрование) только растёт: отданный буфер возвращается в стек и лежит там до конца
// процесса. На пике (сотни пиров, десятки МБ/с) их набирается тысячи — на сервере
// после 9 дней работы в простое лежало 1 771 буфер по 16 КБ, 29 МБ. И все они
// закреплённые (pinned, куча POH): сборщик их не двигает, и регионы кучи под ними
// не освобождаются.
//
// Trim — когда открытых торрентов нет (TorrEngine.TrimMemory): лишние буферы из
// стеков вон, дальше их забирает сборка мусора. Под тем же замком, что берёт сам пул
// (SpinLocked.Enter): буферы, которые сейчас на руках, не трогаем — они вернутся в
// пул как обычно. Понадобятся снова — пул просто выделит новые, как при запуске.
// Внутренности MonoTorrent 3.0.2 — через reflection; не нашлись — ничего не делаем.
using System;
using System.Collections;
using System.Reflection;
using MonoTorrent;

namespace TorrStreamTorrServer
{
    public static class BufferPools
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Сколько оставить: столько же пул выделяет при создании (32 + 32)
        const int KeepSmall = 32, KeepLarge = 32;

        /// <summary>Урезать MemoryPool.Default; байт освобождено (оценка)</summary>
        public static long Trim()
        {
            long freed = 0;
            try
            {
                var pool = MemoryPool.Default;
                freed += TrimOne(pool, "LargeMessageBuffers", "Pop", KeepLarge, ByteBufferPool.LargeMessageBufferSize);
                freed += TrimOne(pool, "SmallMessageBuffers", "Pop", KeepSmall, ByteBufferPool.SmallMessageBufferSize);
                // Нестандартные размеры (метаданные, большие сообщения) — все
                freed += TrimOne(pool, "MassiveBuffers", "Dequeue", 0, -1);
            }
            catch (Exception e) { Console.Error.WriteLine("BufferPools: " + e.Message); }
            return freed;
        }

        static long TrimOne(object pool, string property, string take, int keep, int size)
        {
            var prop = typeof(ByteBufferPool).GetProperty(property, Inst);
            var locked = prop?.GetValue(pool);
            var enter = locked?.GetType().GetMethod("Enter", Inst);
            if (enter == null) return 0;
            var args = new object[1];
            var releaser = enter.Invoke(locked, args) as IDisposable;
            long freed = 0;
            try
            {
                if (!(args[0] is ICollection c)) return 0;
                var takeMethod = args[0].GetType().GetMethod(take, Type.EmptyTypes);
                if (takeMethod == null) return 0;
                while (c.Count > keep)
                {
                    var buffer = takeMethod.Invoke(args[0], null);
                    freed += size > 0 ? size : SegmentLength(buffer);
                }
            }
            finally { releaser?.Dispose(); }
            return freed;
        }

        static long SegmentLength(object buffer)
        {
            try
            {
                var seg = buffer?.GetType().GetProperty("Segment", Inst)?.GetValue(buffer);
                return seg is ArraySegment<byte> a ? a.Count : 0;
            }
            catch { return 0; }
        }
    }
}
