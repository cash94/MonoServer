// Кто сейчас смотрит (StreamRegistry) и мастер-сервер (Balancer): маленькая
// балансировка между несколькими MonoServer.
//
// Зритель — пользователь (или адрес, если вход выключен), у которого открыт
// поток или был открыт последние ViewerWindowMs: плеер при перемотке закрывает
// соединение и тут же открывает новое — между ними он всё ещё «смотрит».
//
// Мастер: здесь смотрят уже LocalMax человек — новый зритель получает 302 на
// узел, где есть место (меньше всего занят по доле от его «Макс.»). Кто уже
// смотрит здесь — остаётся здесь; кого уже отправили на узел — туда же и
// дальше (перемотка по старой ссылке). Мест нет нигде — смотрит здесь.
// Узлу зритель приходит с подписью (?ms_user&ms_exp&ms_sig, ключ узла): вход
// пользователей узел не знает, а заголовок Authorization браузер и плеер при
// перенаправлении на другой адрес не передают. Состояние узлов мастер
// спрашивает раз в 5 с: GET /api/node/status с X-MonoServer-Key — там и
// зрители, и сколько узел отдал каждому (в трафик пользователя на мастере).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TorrStreamTorrServer
{
    public class StreamInfo
    {
        public long Id;
        public string Viewer, Who, Login, Remote, Hash, Title, Path, Ip;
        public int FileId;
        public long Started;        // unix
        public long From, Sent;
    }

    public class StreamRegistry
    {
        public const long ViewerWindowMs = 60000;

        readonly object sync = new object();
        readonly Dictionary<long, StreamInfo> open = new Dictionary<long, StreamInfo>();
        readonly Dictionary<string, long> lastSeen = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long seq;

        public StreamInfo Begin(StreamInfo s)
        {
            lock (sync)
            {
                s.Id = ++seq;
                s.Started = Accounts.UnixNow();
                open[s.Id] = s;
                lastSeen[s.Viewer] = clock.ElapsedMilliseconds;
            }
            return s;
        }

        public void End(StreamInfo s)
        {
            lock (sync)
            {
                open.Remove(s.Id);
                lastSeen[s.Viewer] = clock.ElapsedMilliseconds;
            }
        }

        void Expire()
        {
            long now = clock.ElapsedMilliseconds;
            foreach (var k in lastSeen.Where(kv => now - kv.Value > ViewerWindowMs).Select(kv => kv.Key).ToList()) lastSeen.Remove(k);
        }

        /// <summary>Сколько человек смотрит сейчас (с запасом ViewerWindowMs)</summary>
        public int Viewers()
        {
            lock (sync)
            {
                Expire();
                var set = new HashSet<string>(lastSeen.Keys, StringComparer.Ordinal);
                foreach (var s in open.Values) set.Add(s.Viewer);
                return set.Count;
            }
        }

        public bool Watching(string viewer)
        {
            lock (sync)
            {
                Expire();
                return lastSeen.ContainsKey(viewer) || open.Values.Any(s => s.Viewer == viewer);
            }
        }

        /// <summary>Торрент сейчас читает кто-то, кроме viewer</summary>
        public bool OthersOn(string hash, string viewer)
        {
            lock (sync) return open.Values.Any(s => s.Hash == hash && s.Viewer != viewer);
        }

        public List<StreamInfo> Snapshot()
        {
            lock (sync) return open.Values.OrderBy(s => s.Id).ToList();
        }
    }

    public class NodeState
    {
        public NodeConfig Config;
        public bool Online;
        public string Error = "", Version = "";
        public int Viewers, Streams, Pending;
        public long CheckedAt, LatencyMs;
        // Отправленные, которых узел может ещё не видеть (поток не начат): до этого времени, мс
        public readonly List<long> PendingUntil = new List<long>();

        public int PendingAt(long now)
        {
            PendingUntil.RemoveAll(t => t <= now);
            return PendingUntil.Count;
        }
    }

    public class Balancer
    {
        const int PollMs = 5000;
        const long StickyMs = 10 * 60 * 1000;
        const long SignedForSec = 24 * 3600;

        readonly Accounts accounts;
        readonly StreamRegistry streams;
        readonly Action<string> log;
        readonly HttpClient http = Http.Client(TimeSpan.FromSeconds(4));
        readonly object sync = new object();
        readonly Dictionary<string, NodeState> states = new Dictionary<string, NodeState>();
        // зритель → узел и до какого времени он там
        readonly Dictionary<string, KeyValuePair<string, long>> sticky = new Dictionary<string, KeyValuePair<string, long>>(StringComparer.Ordinal);
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly Timer timer;
        int polling;
        long fullLoggedMs = -60000;

        public Balancer(Accounts accounts, StreamRegistry streams, Action<string> log)
        {
            this.accounts = accounts;
            this.streams = streams;
            this.log = log;
            timer = new Timer(_ => Poll(), null, 3000, PollMs);
        }

        public void Stop() { try { timer.Dispose(); } catch { } }

        void Poll()
        {
            if (!accounts.MasterEnabled || Interlocked.Exchange(ref polling, 1) == 1) return;
            var nodes = accounts.NodeList();
            lock (sync)
            {
                foreach (var id in states.Keys.Where(k => !nodes.Any(n => n.Id == k)).ToList()) states.Remove(id);
                foreach (var n in nodes)
                {
                    if (!states.TryGetValue(n.Id, out var st)) states[n.Id] = st = new NodeState();
                    st.Config = n;
                }
            }
            var tasks = nodes.Where(n => n.Enabled).Select(PollNode).ToArray();
            Task.WhenAll(tasks).ContinueWith(_ => Interlocked.Exchange(ref polling, 0));
        }

        async Task PollNode(NodeConfig n)
        {
            var started = clock.ElapsedMilliseconds;
            string error = null, version = "";
            int viewers = 0, count = 0;
            Dictionary<string, long> users = null;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, n.Url + "/api/node/status"))
                {
                    req.Headers.TryAddWithoutValidation("X-MonoServer-Key", n.Key);
                    using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if ((int)resp.StatusCode == 403) error = "неверный ключ узла";
                        else if (!resp.IsSuccessStatusCode) error = "HTTP " + (int)resp.StatusCode;
                        else
                        {
                            var o = Json.ParseObject(body);
                            version = o.Str("version") ?? "";
                            viewers = (int)(o.Num("viewers") ?? 0);
                            count = (int)(o.Num("streams") ?? 0);
                            if (o.TryGetValue("users", out var uv) && uv is Dictionary<string, object> um)
                            {
                                users = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                                foreach (var kv in um) users[kv.Key] = um.Num(kv.Key) ?? 0;
                            }
                        }
                    }
                }
            }
            catch (Exception e) { error = (e.GetBaseException() is TaskCanceledException) ? "не ответил за 4 с" : e.GetBaseException().Message; }
            if (users != null) accounts.AddFromNode(n.Id, users);
            lock (sync)
            {
                if (!states.TryGetValue(n.Id, out var st)) return;
                bool was = st.Online;
                st.Online = error == null;
                st.Error = error ?? "";
                st.Version = version;
                st.Viewers = viewers;
                st.Streams = count;
                st.CheckedAt = Accounts.UnixNow();
                st.LatencyMs = clock.ElapsedMilliseconds - started;
                if (was != st.Online) log("балансировка: узел " + Name(n) + (st.Online ? " на связи" : " недоступен — " + error));
            }
        }

        static string Name(NodeConfig n) { return string.IsNullOrEmpty(n.Name) ? n.Url : n.Name; }

        public List<NodeState> States()
        {
            lock (sync) return states.Values.Select(s => new NodeState
            {
                Config = s.Config, Online = s.Online, Error = s.Error, Version = s.Version, Viewers = s.Viewers,
                Streams = s.Streams, Pending = s.PendingAt(clock.ElapsedMilliseconds), CheckedAt = s.CheckedAt, LatencyMs = s.LatencyMs,
            }).ToList();
        }

        /// <summary>
        /// Куда отправить зрителя: адрес узла с подписью или null — смотреть здесь.
        /// pathAndQuery — путь и запрос на узле (без своих параметров входа)
        /// </summary>
        public string Redirect(Caller c, string pathAndQuery)
        {
            if (!accounts.MasterEnabled || c.Remote != null || c.Local) return null;
            string viewer = c.Viewer;
            long now = clock.ElapsedMilliseconds;
            NodeState target = null;
            lock (sync)
            {
                foreach (var k in sticky.Where(kv => now > kv.Value.Value).Select(kv => kv.Key).ToList()) sticky.Remove(k);
                if (sticky.TryGetValue(viewer, out var st) && states.TryGetValue(st.Key, out var was) && was.Online && was.Config.Enabled)
                    target = was;
            }
            if (target == null)
            {
                // Уже смотрит здесь — пусть и дальше здесь (перемотка, следующая серия)
                if (streams.Watching(viewer)) return null;
                if (streams.Viewers() < accounts.LocalMax) return null;
                lock (sync)
                {
                    target = states.Values
                        .Where(s => s.Online && s.Config.Enabled && s.Config.Max > 0 && s.Viewers + s.PendingAt(now) < s.Config.Max)
                        .OrderBy(s => (double)(s.Viewers + s.PendingAt(now)) / s.Config.Max).ThenBy(s => s.LatencyMs)
                        .FirstOrDefault();
                    if (target == null)
                    {
                        if (now - fullLoggedMs > 60000)
                        {
                            fullLoggedMs = now;
                            log("балансировка: здесь " + accounts.LocalMax + "+ зрителей, а на узлах мест нет — " + c.Who + " смотрит здесь");
                        }
                        return null;
                    }
                    // Узел увидит зрителя, когда тот начнёт смотреть; до тех пор считаем сами
                    target.PendingUntil.Add(now + 20000);
                    log("балансировка: " + c.Who + " → " + Name(target.Config) + " (там " + (target.Viewers + target.PendingAt(now)) + " из " + target.Config.Max + ")");
                }
            }
            lock (sync) sticky[viewer] = new KeyValuePair<string, long>(target.Config.Id, now + StickyMs);
            long exp = Accounts.UnixNow() + SignedForSec;
            string login = c.Login ?? "";
            var sb = new StringBuilder(target.Config.Url).Append(pathAndQuery);
            sb.Append(pathAndQuery.IndexOf('?') >= 0 ? '&' : '?')
              .Append("ms_user=").Append(Uri.EscapeDataString(login))
              .Append("&ms_exp=").Append(exp)
              .Append("&ms_sig=").Append(HttpApi.Sign(target.Config.Key, login, exp));
            return sb.ToString();
        }
    }
}
