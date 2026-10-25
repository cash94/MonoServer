#nullable enable
// Из master MonoTorrent (src/MonoTorrent.Connections/MonoTorrent.Connections.Peer/Utp,
// MIT, Alan McGovern; в NuGet пока не вышло) — перенесено под C# 8 и
// netstandard2.0 (Tizen), net472 (Mono на Android) и интерфейсы MonoTorrent 3.0.2.
using System.Diagnostics;

namespace TorrStreamTorrServer.Utp
{
    interface IUtpClock
    {
        uint Microseconds { get; }
    }

    sealed class StopwatchUtpClock : IUtpClock
    {
        public static StopwatchUtpClock Instance { get; } = new StopwatchUtpClock ();

        StopwatchUtpClock ()
        {
        }

        public uint Microseconds {
            get {
                ulong ticks = (ulong) Stopwatch.GetTimestamp ();
                ulong microseconds = ticks * 1_000_000UL / (ulong) Stopwatch.Frequency;
                return unchecked((uint) microseconds);
            }
        }
    }
}
