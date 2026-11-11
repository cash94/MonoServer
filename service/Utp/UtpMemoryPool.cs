// Из master MonoTorrent (src/MonoTorrent/MonoTorrent/UtpMemoryPool.cs, MIT,
// Alan McGovern): пул буферов под один UDP-пакет uTP. Свой пул вместо
// ByteBufferPool, который в MonoTorrent 3.0.2 закрыт.
using System;
using System.Collections.Concurrent;

namespace TorrStreamTorrServer.Utp
{
    public sealed class UtpMemoryPool
    {
        public const int BufferSize = 1500;

        public static readonly UtpMemoryPool Default = new UtpMemoryPool();

        readonly ConcurrentBag<BufferReleaser.Buffer> pool = new ConcurrentBag<BufferReleaser.Buffer>();

        public BufferReleaser Rent(out Memory<byte> memory)
        {
            if (!pool.TryTake(out var buffer))
                buffer = new BufferReleaser.Buffer(BufferSize);
            memory = buffer.Data;
            return new BufferReleaser(this, buffer);
        }

        internal void Return(BufferReleaser.Buffer buffer)
        {
            // Сверх этого — отдаём сборщику мусора: всплеск не держим в памяти навсегда.
            // 1024 × 1,5 КБ — с запасом на поток в десятки МБ/с (было 4096: на сервере
            // в простое лежало 3 тыс. буферов, ~6 МБ)
            if (pool.Count < 1024)
                pool.Add(buffer);
        }

        /// <summary>Открытых торрентов нет — оставить keep буферов; байт освобождено</summary>
        public long Trim(int keep = 64)
        {
            long freed = 0;
            while (pool.Count > keep && pool.TryTake(out _)) freed += BufferSize;
            return freed;
        }
    }
}
