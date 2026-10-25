#nullable enable
// Из master MonoTorrent (src/MonoTorrent.Connections/MonoTorrent.Connections.Peer/Utp,
// MIT, Alan McGovern; в NuGet пока не вышло) — перенесено под C# 8 и
// netstandard2.0 (Tizen), net472 (Mono на Android) и интерфейсы MonoTorrent 3.0.2.
//
// UtpConnectionDiagnosticSnapshot.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2026 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

namespace TorrStreamTorrServer.Utp
{
    public readonly struct UtpConnectionDiagnosticSnapshot
    {
        public UtpConnectionDiagnosticSnapshot (uint SendWindowBytes, uint PeerWindowBytes, int BytesInFlight, uint ReceiveWindowBytes, uint RttMicroseconds, uint RetransmitTimeoutMicroseconds, int MtuFloorBytes, int MtuCeilingBytes, int CurrentMtuBytes, uint RecentDelayMicroseconds, string State)
        {
            this.SendWindowBytes = SendWindowBytes;
            this.PeerWindowBytes = PeerWindowBytes;
            this.BytesInFlight = BytesInFlight;
            this.ReceiveWindowBytes = ReceiveWindowBytes;
            this.RttMicroseconds = RttMicroseconds;
            this.RetransmitTimeoutMicroseconds = RetransmitTimeoutMicroseconds;
            this.MtuFloorBytes = MtuFloorBytes;
            this.MtuCeilingBytes = MtuCeilingBytes;
            this.CurrentMtuBytes = CurrentMtuBytes;
            this.RecentDelayMicroseconds = RecentDelayMicroseconds;
            this.State = State;
        }

        public uint SendWindowBytes { get; }
        public uint PeerWindowBytes { get; }
        public int BytesInFlight { get; }
        public uint ReceiveWindowBytes { get; }
        public uint RttMicroseconds { get; }
        public uint RetransmitTimeoutMicroseconds { get; }
        public int MtuFloorBytes { get; }
        public int MtuCeilingBytes { get; }
        public int CurrentMtuBytes { get; }
        public uint RecentDelayMicroseconds { get; }
        public string State { get; }
    }
}
