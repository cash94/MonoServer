// HTTP службы — API TorrServer MatriX, та часть, которой пользуются TorrStream
// (torrents.js, player.js, torrserverstats.js) и клиенты вроде Lampa:
//   GET  /echo                              — версия (с «MatriX»)
//   POST /torrents {"action": add|get|set|rem|list|drop|wipe}
//   GET  /stream[/имя]?link=…&index=N&play|stat|m3u|preload[&save&title&poster]
//   GET  /play/<хэш>/<N>                    — файл торрента из списка
//   POST /cache {"action":"get","hash":…}   — куски в памяти, читатели
//   POST /settings {"action": get|set|def}  — BTSets (TorrSettings.cs)
//   GET  /download/<МБ>                     — нули для замера скорости
//   GET  /version, /log[?after=N]           — служебное: состояние, журнал
//   GET  /                                  — страница отладки (WebPage.cs, web/index.html)
//   /api/…                                  — MonoServer: вход, пользователи, трафик,
//                                             балансировка (AdminApi.cs)
// Файлы — с Range (перемотка в AVPlay), HEAD, keep-alive. CORS — для всех:
// страница TorrStream лежит в пакете приложения (file://) и шлёт preflight;
// Chrome 94+ спрашивает и про Private Network Access.
// Вход (Accounts.cs): веб-интерфейс — только admin; потоки и API торрентов —
// со входом, если он включён («Доступ»), иначе как у TorrServer по умолчанию —
// без пароля, со списком торрентов admin. Кто спрашивает — Caller (Auth.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TorrStreamTorrServer
{
    public partial class HttpApi
    {
        const int MaxConnections = 64;
        const int ChunkSize = 64 * 1024;
        const int InfoTimeoutMs = 60000;

        readonly TorrEngine engine;
        readonly Accounts accounts;
        readonly int port;
        readonly Action<string> log;
        readonly LogRing journal;
        readonly string localKey;
        public readonly StreamRegistry Streams = new StreamRegistry();
        /// <summary>Отдавать код первого входа странице приложения на этом устройстве (Tizen)</summary>
        public bool LocalSetupCode;

        /// <summary>Что не запустилось при старте (Host.Start) — в /version «problem»; null — всё в порядке</summary>
        public string Problems { get; private set; }

        public void Problem(string text)
        {
            lock (this) Problems = Problems == null ? text : Problems + "; " + text;
        }
        public readonly Balancer Balancer;
        TcpListener listener;
        int connections;

        /// <summary>localKey — ключ приложения на том же устройстве (local.key в данных службы): с ним — как admin</summary>
        public HttpApi(TorrEngine engine, Accounts accounts, int port, Action<string> log, LogRing journal, string localKey)
        {
            this.engine = engine;
            this.accounts = accounts;
            this.port = port;
            this.log = log ?? (_ => { });
            this.journal = journal;
            this.localKey = localKey;
            Balancer = new Balancer(accounts, Streams, this.log);
        }

        public int Port => port;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(32);
            new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" }.Start();
            log("HTTP: порт " + port);
        }

        public void Stop()
        {
            try { listener?.Stop(); } catch { }
            Balancer.Stop();
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; }
                if (Interlocked.Increment(ref connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref connections);
                    try { client.Close(); } catch { }
                    continue;
                }
                // Своя нить на соединение: поток файла часами ждёт куски, а пул
                // потоков нужен самому MonoTorrent
                var t = new Thread(() =>
                {
                    try { Serve(client); }
                    finally { Interlocked.Decrement(ref connections); }
                }) { IsBackground = true, Name = "http" };
                t.Start();
            }
        }

        class Request
        {
            public string Method, Path, Version;
            public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
            public Caller Caller;
            public string Header(string k) { return Headers.TryGetValue(k, out var v) ? v : null; }
            public bool Has(string k) { return Query.ContainsKey(k); }
            public string Q(string k) { return Query.TryGetValue(k, out var v) ? v : null; }
            public string BodyText => Encoding.UTF8.GetString(Body);
            public bool KeepAlive
            {
                get
                {
                    var c = Header("Connection");
                    if (c != null && c.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    return Version == "HTTP/1.1" || (c != null && c.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
        }

        // Заголовки читаем сами, байтами: StreamReader забрал бы в буфер и тело POST
        class Input
        {
            readonly Stream s;
            readonly byte[] buf = new byte[8192];
            int pos, len;
            public Input(Stream s) { this.s = s; }

            int ReadByte()
            {
                if (pos >= len)
                {
                    len = s.Read(buf, 0, buf.Length);
                    pos = 0;
                    if (len <= 0) return -1;
                }
                return buf[pos++];
            }

            public string ReadLine()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    int b = ReadByte();
                    if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                    if (b == '\n') break;
                    if (b != '\r') sb.Append((char)b);
                    if (sb.Length > 16384) throw new IOException("слишком длинная строка");
                }
                return sb.ToString();
            }

            public byte[] ReadBytes(int n)
            {
                var res = new byte[n];
                int done = 0;
                while (done < n)
                {
                    if (pos < len)
                    {
                        int k = Math.Min(n - done, len - pos);
                        Buffer.BlockCopy(buf, pos, res, done, k);
                        pos += k;
                        done += k;
                        continue;
                    }
                    int r = s.Read(res, done, n - done);
                    if (r <= 0) throw new IOException("обрыв тела запроса");
                    done += r;
                }
                return res;
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.SendBufferSize = 256 * 1024;
                    var stream = client.GetStream();
                    var input = new Input(stream);
                    while (true)
                    {
                        client.ReceiveTimeout = 60000;
                        var req = ReadRequest(input);
                        if (req == null) return;
                        client.ReceiveTimeout = 0;
                        var took = System.Diagnostics.Stopwatch.StartNew();
                        bool keep = Handle(client, stream, req) && req.KeepAlive;
                        LogRequest(client, req, took.ElapsedMilliseconds);
                        stream.Flush();
                        if (!keep) return;
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception e) { log("HTTP: " + e.Message); }
            }
        }

        // Все запросы — в журнал, кроме частого опроса (список, состояние, /cache)
        // и чтения файлов (их ServeFile пишет сам): видно, кто что спрашивал и
        // где ждал. Медленный опрос (дольше 1 с) — тоже в журнал
        void LogRequest(TcpClient client, Request req, long ms)
        {
            string path = req.Path.TrimEnd('/');
            bool file = path.StartsWith("/play/") || ((path == "/stream" || path.StartsWith("/stream/")) && !req.Has("stat") && !req.Has("m3u") && !req.Has("preload"));
            bool polling = req.Method == "OPTIONS" || path == "/echo" || path == "/version" || path == "/log" || path == "/cache" || path == "" || path == "/favicon.ico" ||
                (path.StartsWith("/api/") && req.Method == "GET") ||
                (path == "/torrents" && (req.BodyText.Contains("\"list\"") || req.BodyText.Contains("\"get\"")));
            if (file || (polling && ms < 1000)) return;
            // Адрес клиента: за nginx на этой машине — из X-Forwarded-For (Identify), а не 127.0.0.1
            string who = req.Caller?.Ip ?? Remote(client);
            if (!KnownRoute(path)) { LogScan(who, req.Method + " " + path); return; }
            string what = req.Method + " " + path;
            if (req.Query.Count > 0) what += "?" + string.Join("&", req.Query.Select(kv => kv.Key == "link" && kv.Value.Length > 48 ? kv.Key + "=" + kv.Value.Substring(0, 48) + "…" : (kv.Value.Length > 0 ? kv.Key + "=" + kv.Value : kv.Key)));
            if (req.Body.Length > 0 && path != "/settings")
            {
                var body = req.BodyText;
                int at = body.IndexOf("\"action\"", StringComparison.Ordinal);
                if (at >= 0) what += " " + body.Substring(at, Math.Min(24, body.Length - at));
            }
            log("HTTP " + what + " — " + (ms / 1000.0).ToString("0.0") + " с, " + who + (req.Caller != null && req.Caller.User != null ? " (" + req.Caller.Who + ")" : ""));
        }

        // Адреса, которые служба обслуживает (Handle); остальное — 404
        static bool KnownRoute(string path)
        {
            switch (path)
            {
                case "": case "/echo": case "/favicon.ico": case "/version": case "/index.html": case "/login": case "/log":
                case "/torrents": case "/stream": case "/cache": case "/settings": case "/viewed": case "/shutdown":
                    return true;
            }
            return path.StartsWith("/stream/") || path.StartsWith("/play/") || path.StartsWith("/download/") || path.StartsWith("/api/");
        }

        // Сканеры (/.env, /phpinfo.php, /wp-config.php…): служба им отвечает 404, но по
        // строке на запрос они забивали журнал. Первый такой запрос с адреса — строкой,
        // дальше с него — сводкой раз в ScanEveryMs (сколько и какие адреса)
        const long ScanEveryMs = 10 * 60 * 1000;
        sealed class ScanStat { public long FromMs; public int Count; public readonly List<string> Paths = new List<string>(); }
        readonly Dictionary<string, ScanStat> scans = new Dictionary<string, ScanStat>();
        readonly System.Diagnostics.Stopwatch scanClock = System.Diagnostics.Stopwatch.StartNew();

        void LogScan(string who, string what)
        {
            string line = null;
            long now = scanClock.ElapsedMilliseconds;
            lock (scans)
            {
                if (scans.Count > 1000) scans.Clear();
                if (!scans.TryGetValue(who, out var st))
                {
                    scans[who] = new ScanStat { FromMs = now };
                    line = "HTTP " + what + " — нет такого адреса (404), " + who + "; дальше такие с этого адреса — сводкой раз в 10 минут";
                }
                else
                {
                    st.Count++;
                    if (st.Paths.Count < 5) st.Paths.Add(what.Substring(what.IndexOf(' ') + 1));
                    if (now - st.FromMs >= ScanEveryMs)
                    {
                        line = "HTTP: с " + who + " ещё " + st.Count + " запросов несуществующих адресов за " + ((now - st.FromMs) / 60000) + " мин (" + string.Join(", ", st.Paths) + (st.Count > st.Paths.Count ? ", …" : "") + ") — похоже на сканер, всем 404";
                        st.FromMs = now;
                        st.Count = 0;
                        st.Paths.Clear();
                    }
                }
            }
            if (line != null) log(line);
        }

        static Request ReadRequest(Input input)
        {
            string line = input.ReadLine();
            while (line != null && line.Length == 0) line = input.ReadLine();
            if (line == null) return null;
            var parts = line.Split(' ');
            if (parts.Length < 2) return null;
            var req = new Request { Method = parts[0].ToUpperInvariant(), Version = parts.Length > 2 ? parts[2] : "HTTP/1.0" };
            string target = parts[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string k = Decode(eq >= 0 ? pair.Substring(0, eq) : pair);
                    string v = eq >= 0 ? Decode(pair.Substring(eq + 1)) : "";
                    if (!req.Query.ContainsKey(k)) req.Query[k] = v;
                }
            }
            string h;
            while (!string.IsNullOrEmpty(h = input.ReadLine()))
            {
                int c = h.IndexOf(':');
                if (c > 0) req.Headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim();
            }
            if (long.TryParse(req.Header("Content-Length"), out long cl) && cl > 0)
            {
                if (cl > 4 << 20) throw new IOException("слишком большое тело запроса");
                req.Body = input.ReadBytes((int)cl);
            }
            return req;
        }

        static string Decode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; }
        }

        // ==================== ответы ====================

        static string Cors(Request req)
        {
            var sb = new StringBuilder();
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, HEAD, OPTIONS\r\n");
            string want = req.Header("Access-Control-Request-Headers");
            sb.Append("Access-Control-Allow-Headers: ").Append(string.IsNullOrEmpty(want) ? "Authorization, Content-Type, Range, Accept" : want).Append("\r\n");
            sb.Append("Access-Control-Expose-Headers: Content-Range, Content-Length, Accept-Ranges, X-MonoServer\r\n");
            if (req.Header("Access-Control-Request-Private-Network") != null)
                sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Access-Control-Max-Age: 600\r\n");
            return sb.ToString();
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 400: return "Bad Request";
                case 302: return "Found";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 429: return "Too Many Requests";
                case 416: return "Range Not Satisfiable";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }

        static void WriteHead(Stream s, Request req, int code, string type, long length, string extra, bool keep)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n");
            if (type != null) sb.Append("Content-Type: ").Append(type).Append("\r\n");
            if (length >= 0) sb.Append("Content-Length: ").Append(length).Append("\r\n");
            sb.Append(Cors(req));
            sb.Append("Cache-Control: no-cache\r\n");
            // По нему TorrStream отличает MonoServer от TorrServer, не спрашивая /api/info
            sb.Append("X-MonoServer: ").Append(TorrEngine.ServiceVersion).Append("\r\n");
            sb.Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n");
            if (extra != null) sb.Append(extra);
            sb.Append("\r\n");
            var b = Encoding.UTF8.GetBytes(sb.ToString());
            s.Write(b, 0, b.Length);
        }

        static bool Send(Stream s, Request req, int code, string type, string body)
        {
            var b = Encoding.UTF8.GetBytes(body ?? "");
            WriteHead(s, req, code, type, b.Length, null, req.KeepAlive);
            if (req.Method != "HEAD") s.Write(b, 0, b.Length);
            return true;
        }

        static bool SendJson(Stream s, Request req, string json) { return Send(s, req, 200, "application/json; charset=utf-8", json); }
        static bool SendText(Stream s, Request req, int code, string text) { return Send(s, req, code, "text/plain; charset=utf-8", text); }

        // ==================== маршруты ====================

        /// <summary>true — соединение можно держать для следующего запроса</summary>
        bool Handle(TcpClient client, Stream s, Request req)
        {
            if (req.Method == "OPTIONS") { WriteHead(s, req, 204, null, 0, null, req.KeepAlive); return true; }
            string path = req.Path.TrimEnd('/');
            try
            {
                if (path == "/echo") return SendText(s, req, 200, TorrEngine.EchoVersion);
                if (path == "/favicon.ico") { WriteHead(s, req, 204, null, 0, null, req.KeepAlive); return true; }
                var c = req.Caller = Identify(client, req);
                if (path.StartsWith("/api/")) return Api(client, s, req, c, path);
                if (path == "/version")
                {
                    // Без входа — коротко: имя и версия (страница Tizen, проверка «это MonoServer»)
                    var w = new JsonWriter();
                    // ?gc — сначала собрать мусор (кнопка на странице отладки, проверка утечек)
                    engine.WriteVersion(w, port, c.Admin && req.Has("gc"), !c.Admin, Problems);
                    return SendJson(s, req, w.ToString());
                }
                // Веб-интерфейс — только admin; без входа на «/» — страница входа
                if (path == "" || path == "/index.html" || path == "/login")
                    return Send(s, req, 200, "text/html; charset=utf-8", c.Admin && path != "/login" ? WebPage.Html : WebPage.Login);
                if (path == "/log")
                {
                    if (!c.Admin) return Unauthorized(s, req, c);
                    // ?after=N — страница отладки: строки после N-й, JSON
                    if (req.Has("after") && journal != null)
                    {
                        long.TryParse(req.Q("after"), out long after);
                        return SendJson(s, req, journal.Json(after));
                    }
                    return SendText(s, req, 200, journal != null ? journal.Text() : "");
                }
                // API TorrServer и потоки: вошедший, зритель мастера (только потоки) или без входа, где можно
                bool streamRoute = path == "/stream" || path.StartsWith("/stream/") || path.StartsWith("/play/");
                if (c.User == null && !(c.Remote != null && streamRoute) && !c.Anonymous) return Unauthorized(s, req, c);
                if (c.ErrorCode != 0 && c.User == null && c.Remote == null) return Unauthorized(s, req, c);
                if (path == "/torrents") return Torrents(s, req, c);
                if (path == "/stream" || path.StartsWith("/stream/")) return StreamRoute(client, s, req, c);
                if (path.StartsWith("/play/")) return Play(client, s, req, c, path);
                if (path == "/cache") return Cache(s, req, c);
                if (path == "/settings") return SettingsRoute(s, req, c);
                if (path.StartsWith("/download/")) return Download(s, req, c, path);
                if (path == "/viewed") return SendJson(s, req, "[]");
                if (path == "/shutdown") return SendText(s, req, 200, "");
                return SendText(s, req, 404, "not found");
            }
            catch (ArgumentException e) { return SendText(s, req, 400, e.Message); }
            catch (IOException) { throw; }
            catch (Exception e)
            {
                var inner = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
                if (inner is ArgumentException) return SendText(s, req, 400, inner.Message);
                log("HTTP " + req.Path + ": " + inner.Message);
                return SendText(s, req, 500, inner.Message);
            }
        }

        static Dictionary<string, object> Body(Request req)
        {
            try { return Json.ParseObject(req.BodyText); }
            catch (FormatException e) { throw new ArgumentException(e.Message); }
        }

        // ==================== торренты по пользователям ====================

        // Открытые без «в базу» — чьи они (TempKey), пока торрент открыт: в списке
        // TorrServer такие видны до закрытия, но другим пользователям — нет
        readonly Dictionary<string, HashSet<string>> temp = new Dictionary<string, HashSet<string>>();

        string TempKey(Caller c)
        {
            if (c.Remote != null) return "r:" + c.Remote.ToLowerInvariant();
            var owner = ListOwner(c);
            return owner != null ? "u:" + owner.Login.ToLowerInvariant() : "?";
        }

        void MarkTemp(Caller c, string hash)
        {
            lock (temp)
            {
                if (!temp.TryGetValue(hash, out var set)) temp[hash] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(TempKey(c));
            }
        }

        void UnmarkTemp(Caller c, string hash)
        {
            lock (temp) if (temp.TryGetValue(hash, out var set) && set.Remove(TempKey(c)) && set.Count == 0) temp.Remove(hash);
        }

        bool IsTemp(Caller c, TorrEntry e)
        {
            lock (temp)
            {
                if (!temp.TryGetValue(e.Hash, out var set)) return false;
                // Закрылся — больше не «открытый без базы»
                if (!e.Active) { temp.Remove(e.Hash); return false; }
                return set.Contains(TempKey(c));
            }
        }

        bool Visible(Caller c, TorrEntry e)
        {
            return c.Admin || accounts.Own(ListOwner(c), e.Hash) != null || IsTemp(c, e);
        }

        string Status(Caller c, TorrEntry e)
        {
            var w = new JsonWriter();
            WriteStatus(w, c, e, null);
            return w.ToString();
        }

        void WriteStatus(JsonWriter w, Caller c, TorrEntry e, List<string> owners)
        {
            engine.WriteStatus(w, e, accounts.Own(ListOwner(c), e.Hash), owners);
        }

        /// <summary>
        /// Торрент убран из списка пользователя: больше ни у кого его нет — из
        /// движка; смотрит кто-то другой — только из базы движка, закроется сам
        /// </summary>
        void Release(string hash, string viewer)
        {
            if (accounts.Owners(hash).Count > 0) return;
            bool othersTemp;
            lock (temp) othersTemp = temp.TryGetValue(hash, out var set) && set.Count > 0;
            if (Streams.OthersOn(hash, viewer) || othersTemp) engine.Unsave(hash);
            else engine.RemoveAsync(hash).GetAwaiter().GetResult();
        }

        /// <summary>Убрать торрент из списка пользователя (null — у всех) и, если не нужен, из движка</summary>
        public void RemoveTorrent(string hash, User from, string viewer)
        {
            if (string.IsNullOrEmpty(hash)) return;
            hash = hash.Trim().ToLowerInvariant();
            if (from != null) accounts.Forget(from, hash);
            else
            {
                foreach (var u in accounts.Users()) accounts.Forget(u, hash);
                lock (temp) temp.Remove(hash);
                engine.RemoveAsync(hash).GetAwaiter().GetResult();
                return;
            }
            Release(hash, viewer);
        }

        bool Torrents(Stream s, Request req, Caller c)
        {
            var o = Body(req);
            string action = o.Str("action") ?? "";
            string hash = (o.Str("hash") ?? "").Trim().ToLowerInvariant();
            var owner = ListOwner(c);
            switch (action)
            {
                case "add":
                {
                    bool save = (o.Bool("save_to_db") ?? false) && owner != null;
                    long tmdbId = TorrMeta.TmdbId(o.TryGetValue("tmdb_id", out var tv) ? tv : null);
                    int[] seasons = TorrMeta.Seasons(o.TryGetValue("seasons", out var sv) ? sv : null);
                    var e = engine.AddAsync(o.Str("link"), o.Str("title"), o.Str("poster"), o.Str("data"), o.Str("category"), save).GetAwaiter().GetResult();
                    engine.SetTmdb(e.Hash, tmdbId, seasons);
                    if (save || accounts.Own(owner, e.Hash) != null) accounts.Keep(owner, e, o.Str("title"), o.Str("data"), o.Str("category"), tmdbId, seasons);
                    else MarkTemp(c, e.Hash);
                    return SendJson(s, req, Status(c, e));
                }
                case "get":
                {
                    var e = engine.Get(hash);
                    if (e == null || !Visible(c, e)) return SendText(s, req, 404, "torrent not found");
                    return SendJson(s, req, Status(c, e));
                }
                case "set":
                {
                    var e = engine.Get(hash);
                    if (e == null || !Visible(c, e)) return SendText(s, req, 404, "torrent not found");
                    // В списке пользователя — его название, data и TMDB; постер общий (проверяет движок)
                    long tmdbId = TorrMeta.TmdbId(o.TryGetValue("tmdb_id", out var tv) ? tv : null);
                    int[] seasons = TorrMeta.Seasons(o.TryGetValue("seasons", out var sv) ? sv : null);
                    if (accounts.SetMeta(owner, hash, o.Str("title"), o.Str("data"), o.Str("category"), tmdbId, seasons))
                        engine.SetMeta(hash, null, o.Str("poster"), null, null);
                    else
                    {
                        engine.SetMeta(hash, o.Str("title"), o.Str("poster"), o.Str("data"), o.Str("category"));
                        engine.SetTmdb(hash, tmdbId, seasons);
                    }
                    return SendText(s, req, 200, "");
                }
                case "rem":
                {
                    var e = engine.Get(hash);
                    if (e == null) return SendText(s, req, 200, "");
                    UnmarkTemp(c, hash);
                    // Админ удаляет чужой торрент (его не было в своём списке) — у всех
                    if (c.Admin && accounts.Own(owner, hash) == null) RemoveTorrent(hash, null, c.Viewer);
                    else if (accounts.Own(owner, hash) != null) RemoveTorrent(hash, owner, c.Viewer);
                    else if (Visible(c, e) || !e.InDb) Release(hash, c.Viewer);
                    return SendText(s, req, 200, "");
                }
                case "drop":
                {
                    var e = engine.Get(hash);
                    if (e == null) return SendText(s, req, 200, "");
                    UnmarkTemp(c, hash);
                    // Смотрит кто-то ещё — не закрываем у него из-под ног
                    if (!Streams.OthersOn(hash, c.Viewer)) engine.DropAsync(hash).GetAwaiter().GetResult();
                    return SendText(s, req, 200, "");
                }
                case "wipe":
                    // Только свой список: у админа тоже — чужие удаляются в веб-интерфейсе
                    if (owner != null)
                        foreach (var t in accounts.OwnList(owner)) RemoveTorrent(t.Hash, owner, c.Viewer);
                    return SendText(s, req, 200, "");
                case "list":
                {
                    var w = new JsonWriter().Arr();
                    if (c.Admin)
                    {
                        var owners = accounts.OwnersAll();
                        foreach (var e in engine.List())
                            WriteStatus(w, c, e, owners.TryGetValue(e.Hash, out var l) ? l : new List<string>());
                    }
                    else
                    {
                        var shown = new List<KeyValuePair<long, TorrEntry>>();
                        var seen = new HashSet<string>();
                        if (owner != null)
                            foreach (var t in accounts.OwnList(owner))
                            {
                                var e = engine.Get(t.Hash) ?? engine.Remember(t);
                                if (e != null && seen.Add(e.Hash)) shown.Add(new KeyValuePair<long, TorrEntry>(t.Timestamp, e));
                            }
                        foreach (var e in engine.List())
                            if (!seen.Contains(e.Hash) && IsTemp(c, e) && seen.Add(e.Hash)) shown.Add(new KeyValuePair<long, TorrEntry>(e.Timestamp, e));
                        foreach (var kv in shown.OrderByDescending(x => x.Key)) WriteStatus(w, c, kv.Value, null);
                    }
                    return SendJson(s, req, w.EndArr().ToString());
                }
                default:
                    return SendText(s, req, 400, "unknown action: " + action);
            }
        }

        bool Cache(Stream s, Request req, Caller c)
        {
            var o = Body(req);
            var e = engine.Get(o.Str("hash"));
            if (e == null || !Visible(c, e)) return SendJson(s, req, "{}");
            var w = new JsonWriter();
            engine.WriteCache(w, e, o.Bool("peers") ?? false);
            return SendJson(s, req, w.ToString());
        }

        bool SettingsRoute(Stream s, Request req, Caller c)
        {
            var o = Body(req);
            string action = o.Str("action") ?? "get";
            // Настройки общие на сервер: менять — админ (и без входа, где вход выключен, — как раньше)
            if ((action == "set" || action == "def") && !c.Admin && !(c.Anonymous && c.User == null))
            {
                c.Error = "настройки сервера меняет только администратор";
                c.ErrorCode = 403;
                return Unauthorized(s, req, c);
            }
            if (action == "set")
            {
                engine.ApplySettings(o.TryGetValue("sets", out var v) ? v as Dictionary<string, object> : null);
                return SendText(s, req, 200, "");
            }
            if (action == "def")
            {
                engine.ResetSettings();
                return SendText(s, req, 200, "");
            }
            return SendJson(s, req, engine.Settings.ToJson());
        }

        bool Download(Stream s, Request req, Caller c, string path)
        {
            if (!int.TryParse(path.Substring("/download/".Length), out int mb) || mb <= 0) mb = 1;
            mb = Math.Min(mb, 1024);
            long total = (long)mb << 20;
            WriteHead(s, req, 200, "application/octet-stream", total, null, req.KeepAlive);
            if (req.Method == "HEAD") return true;
            var zero = new byte[ChunkSize];
            long sent = 0;
            try
            {
                for (long left = total; left > 0; left -= ChunkSize)
                {
                    int n = (int)Math.Min(ChunkSize, left);
                    s.Write(zero, 0, n);
                    sent += n;
                }
            }
            finally { accounts.AddHttp(c.Login, c.Remote, sent); }
            return true;
        }

        // /stream?link=…&index=N&play|stat|m3u|preload — как у TorrServer
        bool StreamRoute(TcpClient client, Stream s, Request req, Caller c)
        {
            string link = req.Q("link");
            if (string.IsNullOrEmpty(link)) return SendText(s, req, 400, "link should not be empty");
            // Мастер: здесь мест нет — на узел, пока торрент здесь даже не открыт
            if (!req.Has("stat") && !req.Has("m3u"))
            {
                string to = Balancer.Redirect(c, NodePath(req, req.Path, link));
                if (to != null) return Redirect(s, req, to);
            }
            var owner = ListOwner(c);
            bool save = req.Has("save") && owner != null;
            var e = engine.AddAsync(link, req.Q("title"), req.Q("poster"), null, null, save).GetAwaiter().GetResult();
            long tmdbId = TorrMeta.TmdbId(req.Q("tmdb_id"));
            int[] seasons = TorrMeta.Seasons(req.Q("seasons"));
            engine.SetTmdb(e.Hash, tmdbId, seasons);
            if (save || accounts.Own(owner, e.Hash) != null) accounts.Keep(owner, e, req.Q("title"), null, null, tmdbId, seasons);
            else MarkTemp(c, e.Hash);
            bool hasInfo = engine.WaitInfoAsync(e, InfoTimeoutMs).GetAwaiter().GetResult();

            if (req.Has("stat")) return SendJson(s, req, Status(c, e));
            if (!hasInfo) return SendText(s, req, 404, "torrent info timeout");
            if (req.Has("m3u")) return Send(s, req, 200, "audio/x-mpegurl; charset=utf-8", M3u(req, c, e));

            int index;
            if (!int.TryParse(req.Q("index"), out index))
            {
                // Без номера: единственный файл — он, иначе список
                var files = e.Files.Where(f => f.File != null).ToList();
                if (files.Count != 1) return Send(s, req, 200, "audio/x-mpegurl; charset=utf-8", M3u(req, c, e));
                index = files[0].Id;
            }
            var stat = engine.FileById(e, index);
            if (stat == null || stat.File == null) return SendText(s, req, 404, "file index out of range");

            if (req.Has("preload"))
            {
                // Ждём конца предзагрузки, пока клиент на связи; ушёл — она идёт
                // дальше, как у TorrServer. Lampa так и делает: ?preload с таймаутом
                // 2 с, дальше раз в секунду ?stat (preloaded_bytes / preload_size) до
                // 95 %. Раньше разрыв соединения прерывал предзагрузку, и Lampa
                // ждала прогресса, которого не было
                var job = StartPreload(e, stat, Remote(client));
                while (!job.Wait(500))
                    if (Gone(client)) return false;
                return SendJson(s, req, Status(c, e));
            }
            return ServeFile(client, s, req, c, e, stat);
        }

        // Идущие предзагрузки: хэш → (номер файла, задача). Повторный ?preload того
        // же файла ждёт ту же, а не запускает вторую поверх
        readonly Dictionary<string, KeyValuePair<int, System.Threading.Tasks.Task<bool>>> preloads = new Dictionary<string, KeyValuePair<int, System.Threading.Tasks.Task<bool>>>();
        const long PreloadStallMs = 180000;

        System.Threading.Tasks.Task<bool> StartPreload(TorrEntry e, FileStat stat, string who)
        {
            lock (preloads)
            {
                if (preloads.TryGetValue(e.Hash, out var running) && running.Key == stat.Id && !running.Value.IsCompleted) return running.Value;
                // Без клиента не бесконечно: пиров нет — 3 минуты без прогресса, и
                // хватит (торрент иначе не закроется). Закрыли торрент — читатели
                // предзагрузки закрываются сами (Read → −1)
                long lastBytes = -1, lastChange = Ms();
                Func<bool> stalled = () =>
                {
                    long b = e.PreloadedBytes;
                    if (b != lastBytes) { lastBytes = b; lastChange = Ms(); return false; }
                    return Ms() - lastChange > PreloadStallMs;
                };
                // Свой поток (LongRunning): предзагрузка ждёт куски минутами, а пул нужен MonoTorrent
                var job = System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    log(e.Hash + ": предзагрузка файла " + stat.Id + ", " + who);
                    bool ok = false;
                    try { ok = engine.Preload(e, stat, stalled); }
                    catch (Exception ex) { log(e.Hash + ": предзагрузка — " + ex.Message); }
                    log(e.Hash + ": предзагрузка " + (ok ? "готова" : "прервана") + " за " + (t.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с, " + Mb(e.PreloadedBytes) + " из " + Mb(e.PreloadSize));
                    return ok;
                }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
                preloads[e.Hash] = new KeyValuePair<int, System.Threading.Tasks.Task<bool>>(stat.Id, job);
                foreach (var done in preloads.Where(kv => kv.Value.Value.IsCompleted && kv.Key != e.Hash).Select(kv => kv.Key).ToList()) preloads.Remove(done);
                return job;
            }
        }

        // /play/<хэш>/<номер>
        bool Play(TcpClient client, Stream s, Request req, Caller c, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return SendText(s, req, 400, "use /play/hash/index");
            string hash = parts[1].ToLowerInvariant();
            // На узел — /stream с полной ссылкой: торрента в его базе нет, а голый хэш — только DHT
            string to = Balancer.Redirect(c, NodePath(req, "/stream", hash, parts[2]));
            if (to != null) return Redirect(s, req, to);
            var e = engine.OpenAsync(hash).GetAwaiter().GetResult();
            if (e == null)
                e = engine.AddAsync(hash, null, null, null, null, false).GetAwaiter().GetResult();
            if (accounts.Own(ListOwner(c), e.Hash) == null) MarkTemp(c, e.Hash);
            if (!engine.WaitInfoAsync(e, InfoTimeoutMs).GetAwaiter().GetResult()) return SendText(s, req, 404, "torrent info timeout");
            // Как у TorrServer (web/api/play.go): файл в раздаче один — он, какой бы
            // номер ни пришел; иначе номер — число из file_stats
            var single = engine.SingleFile(e);
            FileStat stat;
            if (single != null) stat = single;
            else
            {
                if (!int.TryParse(parts[2], out int index)) return SendText(s, req, 400, "file \"index\" is wrong");
                stat = engine.FileById(e, index);
            }
            if (stat == null || stat.File == null) return SendText(s, req, 404, "file index out of range");
            return ServeFile(client, s, req, c, e, stat);
        }

        static bool Redirect(Stream s, Request req, string url)
        {
            WriteHead(s, req, 302, "text/plain; charset=utf-8", 0, "Location: " + url + "\r\n", req.KeepAlive);
            return true;
        }

        // Ссылка на поток для узла: путь и запрос как пришли, без своих параметров
        // входа; голый хэш — полной ссылкой (с трекерами) из базы
        string NodePath(Request req, string path, string link, string index = null)
        {
            string hash = TorrEngine.HashFromLink(link);
            if (hash != null && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var known = engine.Get(hash);
                string full = known != null ? known.Link : accounts.Own(ListOwner(req.Caller), hash)?.Link;
                if (!string.IsNullOrEmpty(full)) link = full;
            }
            var sb = new StringBuilder();
            foreach (var seg in path.Split('/'))
                if (seg.Length > 0) sb.Append('/').Append(Uri.EscapeDataString(seg));
            sb.Append("?link=").Append(Uri.EscapeDataString(link));
            if (index != null) sb.Append("&index=").Append(Uri.EscapeDataString(index)).Append("&play");
            foreach (var kv in req.Query)
            {
                if (kv.Key == "link" || kv.Key == "token" || kv.Key.StartsWith("ms_")) continue;
                if (index != null && (kv.Key == "index" || kv.Key == "play")) continue;
                sb.Append('&').Append(Uri.EscapeDataString(kv.Key));
                if (kv.Value.Length > 0) sb.Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        string M3u(Request req, Caller c, TorrEntry e)
        {
            string host = req.Header("Host") ?? ("127.0.0.1:" + port);
            // Плеер, который откроет плейлист, пароля не знает — ссылки с ключом пользователя
            string auth = c.User != null && !c.Local ? "&token=" + c.User.Token : "";
            var sb = new StringBuilder("#EXTM3U\n");
            foreach (var f in e.Files.Where(x => x.File != null))
            {
                string name = f.Path.Substring(f.Path.LastIndexOf('/') + 1);
                sb.Append("#EXTINF:0,").Append(name).Append('\n');
                sb.Append("http://").Append(host).Append("/stream/").Append(Uri.EscapeDataString(name))
                  .Append("?link=").Append(e.Hash).Append("&index=").Append(f.Id).Append("&play").Append(auth).Append('\n');
            }
            return sb.ToString();
        }

        // Клиент закрыл соединение (плеер перемотал или ушёл)
        static bool Gone(TcpClient client)
        {
            try
            {
                var sock = client.Client;
                return sock == null || (sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0);
            }
            catch { return true; }
        }

        static string MimeOf(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".mkv": case ".mk3d": return "video/x-matroska";
                case ".mka": return "audio/x-matroska";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".m4a": return "audio/mp4";
                case ".avi": return "video/x-msvideo";
                case ".ts": case ".m2ts": case ".mts": return "video/mp2t";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".wmv": return "video/x-ms-wmv";
                case ".flv": return "video/x-flv";
                case ".mpg": case ".mpeg": case ".vob": return "video/mpeg";
                case ".mp3": return "audio/mpeg";
                case ".flac": return "audio/flac";
                case ".ogg": return "audio/ogg";
                case ".srt": return "application/x-subrip";
                case ".vtt": return "text/vtt";
                case ".ass": case ".ssa": return "text/x-ssa";
                default: return "application/octet-stream";
            }
        }

        bool ServeFile(TcpClient client, Stream s, Request req, Caller c, TorrEntry e, FileStat f)
        {
            long total = f.Length, start = 0, end = total - 1;
            bool partial = false;
            string range = req.Header("Range");
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = range.Substring(6).Split(',')[0].Trim();
                int dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    string a = spec.Substring(0, dash), b = spec.Substring(dash + 1);
                    if (a.Length == 0 && long.TryParse(b, out long suffix))
                    {
                        start = Math.Max(0, total - suffix);
                    }
                    else if (long.TryParse(a, out long from))
                    {
                        start = from;
                        if (long.TryParse(b, out long to)) end = Math.Min(to, total - 1);
                    }
                    partial = true;
                }
                if (start >= total || start > end)
                {
                    WriteHead(s, req, 416, null, 0, "Content-Range: bytes */" + total + "\r\n", req.KeepAlive);
                    return true;
                }
            }
            long length = end - start + 1;
            string extra = "Accept-Ranges: bytes\r\n" +
                (partial ? "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n" : "") +
                "transferMode.dlna.org: Streaming\r\n";
            WriteHead(s, req, partial ? 206 : 200, MimeOf(f.Path), length, extra, req.KeepAlive);
            if (req.Method == "HEAD" || length <= 0) return true;

            var reader = engine.OpenReader(e, f, start);
            // Кто смотрит — для «Потоков», счёта зрителей (балансировка) и трафика
            var info = Streams.Begin(new StreamInfo
            {
                Viewer = c.Viewer, Who = c.Who, Login = c.Login, Remote = c.Remote, Hash = e.Hash, Title = e.Title,
                Path = f.Path, FileId = f.Id, Ip = c.Ip, From = start,
            });
            // В журнал — кто и откуда читает: по нему видно, чего ждёт плеер
            var started = System.Diagnostics.Stopwatch.StartNew();
            long sent = 0, counted = 0;
            string how = "прервано";
            log(e.Hash + ": файл " + f.Id + " с " + Mb(start) + (partial ? " (Range " + range + ")" : "") + ", " + Remote(client) + (c.User != null || c.Remote != null ? " (" + c.Who + ")" : ""));
            try
            {
                var buf = new byte[ChunkSize];
                long left = length;
                while (left > 0)
                {
                    int n = engine.Read(reader, buf, 0, (int)Math.Min(buf.Length, left), () => Gone(client));
                    if (n <= 0) return false;   // прервано — соединение не продолжаем
                    s.Write(buf, 0, n);
                    left -= n;
                    sent += n;
                    info.Sent = sent;
                    // Трафик — порциями: в статистике виден и идущий просмотр
                    if (sent - counted >= 4 << 20) { accounts.AddHttp(c.Login, c.Remote, sent - counted); counted = sent; }
                }
                how = "до конца запроса";
                return true;
            }
            finally
            {
                engine.CloseReader(reader);
                Streams.End(info);
                accounts.AddHttp(c.Login, c.Remote, sent - counted);
                log(e.Hash + ": файл " + f.Id + " — " + how + ": отдано " + Mb(sent) + " за " + (started.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с, позиция " + Mb(start + sent));
            }
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("0.0") + " МБ"; }

        static string Remote(TcpClient client)
        {
            try { return ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); } catch { return "?"; }
        }
    }

    /// <summary>Последние строки журнала — GET /log (с телефона видно, что на ТВ)</summary>
    public class LogRing
    {
        readonly Queue<KeyValuePair<long, string>> lines = new Queue<KeyValuePair<long, string>>();
        readonly int max;
        long seq;
        public LogRing(int max) { this.max = max; }

        public void Add(string line)
        {
            lock (lines)
            {
                lines.Enqueue(new KeyValuePair<long, string>(++seq, DateTime.Now.ToString("HH:mm:ss ") + line));
                while (lines.Count > max) lines.Dequeue();
            }
        }

        public string Text()
        {
            lock (lines) return string.Join("\n", lines.Select(l => l.Value));
        }

        /// <summary>{"seq": последний номер, "lines": [строки после after]}</summary>
        public string Json(long after)
        {
            var w = new JsonWriter();
            lock (lines)
            {
                // Служба перезапускалась — номера пошли сначала: всё целиком
                if (after > seq) after = 0;
                w.Obj().Prop("seq", seq).Key("lines").Arr();
                foreach (var l in lines) if (l.Key > after) w.Val(l.Value);
            }
            return w.EndArr().EndObj().ToString();
        }
    }
}
