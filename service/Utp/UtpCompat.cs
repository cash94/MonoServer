// Замены того, чего нет в netstandard2.0 / MonoTorrent 3.0.2, для uTP,
// перенесённого из master MonoTorrent (там он под net8.0): пул буферов
// (ByteBufferPool в 3.0.2 закрыт), очередь вместо System.Threading.Channels
// (не тянем пакет и его зависимости в Tizen и Mono), журнал, случайные числа,
// TimeSpan.TotalMicroseconds и Task.WaitAsync.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TorrStreamTorrServer.Utp
{
    /// <summary>Буфер из UtpMemoryPool; Dispose возвращает его в пул (повторный — ничего не делает)</summary>
    public readonly struct BufferReleaser : IDisposable
    {
        internal sealed class Buffer
        {
            public readonly byte[] Data;
            public int Counter;
            public Buffer(int size) { Data = new byte[size]; }
        }

        readonly Buffer buffer;
        readonly int counter;
        readonly UtpMemoryPool pool;

        internal BufferReleaser(UtpMemoryPool pool, Buffer buffer)
        {
            this.pool = pool;
            this.buffer = buffer;
            counter = buffer.Counter;
        }

        public void Dispose()
        {
            if (buffer == null)
                return;
            // Двойное освобождение — ошибка в uTP; в master MonoTorrent бросает
            // исключение, здесь — просто не кладём буфер в пул второй раз
            if (Interlocked.CompareExchange(ref buffer.Counter, counter + 1, counter) != counter)
                return;
            pool.Return(buffer);
        }
    }

    /// <summary>Закрытая очередь (как ChannelClosedException)</summary>
    public sealed class UtpChannelClosedException : InvalidOperationException
    {
        public UtpChannelClosedException() : base("The channel has been closed.") { }
    }

    /// <summary>
    /// Очередь с тем, что uTP брал у System.Threading.Channels: TryWrite/WriteAsync
    /// (с ограничением — ждать места), TryRead/ReadAsync/WaitToReadAsync, TryComplete.
    /// Продолжения ожидающих — всегда асинхронно (AllowSynchronousContinuations = false).
    /// </summary>
    public sealed class UtpChannel<T>
    {
        readonly Queue<T> items = new Queue<T>();
        readonly Queue<TaskCompletionSource<bool>> writeWaiters = new Queue<TaskCompletionSource<bool>>();
        readonly int capacity;
        readonly object locker = new object();
        TaskCompletionSource<bool> readWaiter;
        bool completed;

        public UtpChannel(int capacity = int.MaxValue)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public UtpChannel<T> Reader => this;
        public UtpChannel<T> Writer => this;

        public bool TryWrite(T item)
        {
            TaskCompletionSource<bool> wake;
            lock (locker)
            {
                if (completed || items.Count >= capacity)
                    return false;
                items.Enqueue(item);
                wake = readWaiter;
                readWaiter = null;
            }
            wake?.TrySetResult(true);
            return true;
        }

        public async Task WriteAsync(T item, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TaskCompletionSource<bool> wait;
                TaskCompletionSource<bool> wake = null;
                lock (locker)
                {
                    if (completed)
                        throw new UtpChannelClosedException();
                    if (items.Count < capacity)
                    {
                        items.Enqueue(item);
                        wake = readWaiter;
                        readWaiter = null;
                        wait = null;
                    }
                    else
                    {
                        wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        writeWaiters.Enqueue(wait);
                    }
                }
                if (wait == null)
                {
                    wake?.TrySetResult(true);
                    return;
                }
                using (token.Register(() => wait.TrySetCanceled()))
                    await wait.Task.ConfigureAwait(false);
            }
        }

        public bool TryRead(out T item)
        {
            lock (locker)
            {
                if (items.Count == 0)
                {
                    item = default;
                    return false;
                }
                item = items.Dequeue();
                // Место освободилось — будим одного ждущего писателя (отменённых пропускаем)
                while (writeWaiters.Count > 0)
                    if (writeWaiters.Dequeue().TrySetResult(true))
                        break;
                return true;
            }
        }

        public async Task<bool> WaitToReadAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TaskCompletionSource<bool> wait;
                lock (locker)
                {
                    if (items.Count > 0)
                        return true;
                    if (completed)
                        return false;
                    if (readWaiter == null || readWaiter.Task.IsCompleted)
                        readWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = readWaiter;
                }
                using (token.Register(() => wait.TrySetCanceled()))
                    await wait.Task.ConfigureAwait(false);
            }
        }

        public async Task<T> ReadAsync(CancellationToken token)
        {
            while (true)
            {
                if (TryRead(out var item))
                    return item;
                if (!await WaitToReadAsync(token).ConfigureAwait(false))
                    throw new UtpChannelClosedException();
            }
        }

        public bool TryComplete()
        {
            TaskCompletionSource<bool> reader;
            TaskCompletionSource<bool>[] writers;
            lock (locker)
            {
                if (completed)
                    return false;
                completed = true;
                reader = readWaiter;
                readWaiter = null;
                writers = writeWaiters.ToArray();
                writeWaiters.Clear();
            }
            reader?.TrySetResult(false);
            foreach (var w in writers)
                w.TrySetResult(false);
            return true;
        }
    }

    /// <summary>Журнал uTP — в журнал службы (Debug — только если включён)</summary>
    public static class UtpLog
    {
        public static Action<string> Sink;
        public static bool DebugEnabled;

        public static void Debug(string message)
        {
            if (DebugEnabled)
                Sink?.Invoke("uTP: " + message);
        }

        public static void Error(string message) => Sink?.Invoke("uTP: " + message);
    }

    static class UtpRandom
    {
        static readonly Random Seed = new Random();
        [ThreadStatic] static Random local;

        static Random Local
        {
            get
            {
                if (local == null)
                    lock (Seed)
                        local = new Random(Seed.Next());
                return local;
            }
        }

        /// <summary>Как RandomNumberGenerator.GetInt32 / Random.Shared.Next — [min, max)</summary>
        public static int Next(int minValue, int maxValue) => Local.Next(minValue, maxValue);
    }

    static class UtpExtensions
    {
        /// <summary>TimeSpan.TotalMicroseconds (.NET 7+)</summary>
        public static double TotalMicros(this TimeSpan span) => span.Ticks / 10.0;

        /// <summary>Task.WaitAsync(CancellationToken) (.NET 6+)</summary>
        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken token)
        {
            if (task.IsCompleted || !token.CanBeCanceled)
                return await task.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(token);
            }
            return await task.ConfigureAwait(false);
        }

        /// <summary>Dictionary.Remove(key, out value) (netstandard2.1+)</summary>
        public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, out TValue value)
            => dictionary.TryGetValue(key, out value) && dictionary.Remove(key);

        public static void Fill<T>(T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++)
                array[i] = value;
        }
    }
}
