// Запуск DHT в обход двух ошибок MonoTorrent 3.0.2 — без них DHT на
// телевизорах так и стоял «Initialising, узлов 0» → «NotReady»:
//  1. Первые узлы он ищет только через router.bittorrent.com:6881, а тот не
//     отвечает (у нас — совсем); других источников нет (InitialiseTask). В
//     master MonoTorrent список уже расширен (#749, #751), но не в 3.0.2.
//  2. Кэш узлов (cache/dht_nodes.cache) он пишет bencode-списком, а читает как
//     сплошные 26-байтные записи — сохранённые узлы при следующем запуске
//     превращаются в мусор.
// Поэтому: адреса нескольких bootstrap-серверов разрешаем при старте службы, и
// перед запуском движка (первый торрент) кладём в dht_nodes.cache узлы в том
// виде, в каком 3.0.2 их читает, — прошлые узлы и серверы. DHT остался без
// узлов при открытом торренте — раз в 2 минуты запускаем поиск узлов заново
// (DhtEngine свой — через Factories.WithDhtCreator). Узлы, пока DHT работает,
// сохраняем сами раз в 10 минут.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht;

namespace TorrStreamTorrServer
{
    public class DhtBootstrap
    {
        // Как в master MonoTorrent (EngineSettings.DhtBootstrapRouters) и у libtorrent
        static readonly KeyValuePair<string, int>[] Routers = {
            new KeyValuePair<string, int>("dht.transmissionbt.com", 6881),
            new KeyValuePair<string, int>("dht.libtorrent.org", 25401),
            new KeyValuePair<string, int>("router.utorrent.com", 6881),
            new KeyValuePair<string, int>("router.bittorrent.com", 6881),
            new KeyValuePair<string, int>("router.bitcomet.com", 6881),
            new KeyValuePair<string, int>("dht.aelitis.com", 6881),
        };
        const int ResolveTimeoutMs = 5000;
        const int MaxNodes = 400;
        const long KickEveryMs = 120000, SaveEveryMs = 600000;

        readonly string cachePath;
        readonly Action<string> log;
        Task<List<IPEndPoint>> routers = Task.FromResult(new List<IPEndPoint>());
        long lastKickMs = long.MinValue / 2, lastSaveMs, runningSince = -1;
        // Первый поиск узлов движок делает сам (до минуты) — до этого не вмешиваемся
        const long FirstKickAfterMs = 75000;
        int kicks;

        /// <summary>DhtEngine движка — его отдаёт Factories.WithDhtCreator(Create)</summary>
        public DhtEngine Engine { get; private set; }

        public DhtBootstrap(string cacheDir, Action<string> log)
        {
            cachePath = Path.Combine(cacheDir, "dht_nodes.cache");   // имя — как у MonoTorrent
            this.log = log ?? (_ => { });
        }

        public IDhtEngine Create()
        {
            Engine = new DhtEngine();
            return Engine;
        }

        /// <summary>Разрешить адреса серверов в фоне, каждый не дольше ResolveTimeoutMs</summary>
        public void ResolveInBackground()
        {
            routers = Task.Run(async () =>
            {
                var tasks = Routers.Select(async r =>
                {
                    try
                    {
                        var dns = Dns.GetHostAddressesAsync(r.Key);
                        if (await Task.WhenAny(dns, Task.Delay(ResolveTimeoutMs)).ConfigureAwait(false) != dns) return new IPEndPoint[0];
                        return (await dns.ConfigureAwait(false)).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => new IPEndPoint(a, r.Value)).ToArray();
                    }
                    catch { return new IPEndPoint[0]; }
                }).ToArray();
                var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x).Distinct().ToList();
                log("DHT: серверов для первых узлов " + all.Count + " (" + string.Join(", ", all) + ")");
                return all;
            });
        }

        // Узлы из файла: bencode-список (так пишет MonoTorrent) или сплошные записи (так пишем мы)
        static List<byte[]> Parse(byte[] data)
        {
            var nodes = new List<byte[]>();
            if (data == null || data.Length == 0) return nodes;
            if (data[0] == (byte)'l')
            {
                try
                {
                    var list = BEncodedValue.Decode<BEncodedList>(data);
                    if (list.All(v => v is BEncodedString s && s.Span.Length % 26 == 0))
                    {
                        foreach (BEncodedString s in list)
                            for (int i = 0; i + 26 <= s.Span.Length; i += 26) nodes.Add(s.Span.Slice(i, 26).ToArray());
                        return nodes;
                    }
                }
                catch { }
            }
            if (data.Length % 26 == 0)
                for (int i = 0; i + 26 <= data.Length; i += 26)
                {
                    var n = new byte[26];
                    Array.Copy(data, i, n, 0, 26);
                    nodes.Add(n);
                }
            return nodes;
        }

        static byte[] Compact(IPEndPoint ep, Random rnd)
        {
            var n = new byte[26];
            rnd.NextBytes(n);   // id сервера не знаем — любой: ответ сопоставляется по номеру запроса
            Array.Copy(ep.Address.GetAddressBytes(), 0, n, 20, 4);
            n[24] = (byte)(ep.Port >> 8);
            n[25] = (byte)ep.Port;
            return n;
        }

        static string Key(byte[] n) { return n[20] + "." + n[21] + "." + n[22] + "." + n[23] + ":" + ((n[24] << 8) | n[25]); }

        byte[] BuildNodes(List<byte[]> known)
        {
            List<IPEndPoint> eps;
            try { eps = routers.Wait(3000) ? routers.Result : new List<IPEndPoint>(); } catch { eps = new List<IPEndPoint>(); }
            var rnd = new Random();
            var seen = new HashSet<string>();
            var result = new List<byte[]>();
            // Серверы — первыми: прошлые узлы за часы простоя могли уйти
            foreach (var n in eps.Select(ep => Compact(ep, rnd)).Concat(known))
                if (n[20] != 0 && seen.Add(Key(n)) && result.Count < MaxNodes) result.Add(n);
            return result.SelectMany(n => n).ToArray();
        }

        List<byte[]> ReadFile()
        {
            try { return File.Exists(cachePath) ? Parse(File.ReadAllBytes(cachePath)) : new List<byte[]>(); }
            catch { return new List<byte[]>(); }
        }

        /// <summary>Перед запуском движка: dht_nodes.cache — в виде, который 3.0.2 читает</summary>
        public void PrepareCache()
        {
            try
            {
                var known = ReadFile();
                var raw = BuildNodes(known);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllBytes(cachePath, raw);
                log("DHT: узлов для старта " + raw.Length / 26 + " (сохранённых " + known.Count + ")");
            }
            catch (Exception e) { log("DHT: кэш узлов — " + e.Message); }
        }

        /// <summary>Раз в 2 с из TorrEngine: перезапуск поиска узлов и сохранение</summary>
        public void Tick(bool engineRunning, long now)
        {
            var dht = Engine;
            if (!engineRunning) runningSince = -1;
            if (dht == null || !engineRunning) return;
            if (runningSince < 0) runningSince = now;
            if (dht.State == DhtState.NotReady && now - runningSince >= FirstKickAfterMs && now - lastKickMs >= KickEveryMs)
            {
                lastKickMs = now;
                var raw = BuildNodes(ReadFile());
                if (raw.Length == 0) return;
                kicks++;
                log("DHT: узлов " + dht.NodeCount + " — поиск узлов заново (" + kicks + "), начальных " + raw.Length / 26);
                _ = dht.StartAsync(raw).ContinueWith(t => { if (t.IsFaulted) log("DHT: " + t.Exception.GetBaseException().Message); });
            }
            else if (dht.State == DhtState.Ready && now - lastSaveMs >= SaveEveryMs)
            {
                lastSaveMs = now;
                _ = SaveAsync(dht);
            }
        }

        async Task SaveAsync(DhtEngine dht)
        {
            try
            {
                var nodes = Parse((await dht.SaveNodesAsync().ConfigureAwait(false)).ToArray());
                if (nodes.Count == 0) return;
                File.WriteAllBytes(cachePath, nodes.Take(MaxNodes).SelectMany(n => n).ToArray());
            }
            catch (Exception e) { log("DHT: сохранение узлов — " + e.Message); }
        }
    }
}
