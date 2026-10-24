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
// Файлы — с Range (перемотка в AVPlay), HEAD, keep-alive. CORS — для всех:
// страница TorrStream лежит в пакете приложения (file://) и шлёт preflight;
// Chrome 94+ спрашивает и про Private Network Access.
// Пароля нет, как у TorrServer по умолчанию: служба слушает домашнюю сеть.
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
    public class HttpApi
    {
        const int MaxConnections = 64;
        const int ChunkSize = 64 * 1024;
        const int InfoTimeoutMs = 60000;

        readonly TorrEngine engine;
        readonly int port;
        readonly Action<string> log;
        readonly LogRing journal;
        TcpListener listener;
        int connections;

        public HttpApi(TorrEngine engine, int port, Action<string> log, LogRing journal)
        {
            this.engine = engine;
            this.port = port;
            this.log = log ?? (_ => { });
            this.journal = journal;
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
                (path == "/torrents" && (req.BodyText.Contains("\"list\"") || req.BodyText.Contains("\"get\"")));
            if (file || (polling && ms < 1000)) return;
            string what = req.Method + " " + path;
            if (req.Query.Count > 0) what += "?" + string.Join("&", req.Query.Select(kv => kv.Key == "link" && kv.Value.Length > 48 ? kv.Key + "=" + kv.Value.Substring(0, 48) + "…" : (kv.Value.Length > 0 ? kv.Key + "=" + kv.Value : kv.Key)));
            if (req.Body.Length > 0 && path != "/settings")
            {
                var body = req.BodyText;
                int at = body.IndexOf("\"action\"", StringComparison.Ordinal);
                if (at >= 0) what += " " + body.Substring(at, Math.Min(24, body.Length - at));
            }
            log("HTTP " + what + " — " + (ms / 1000.0).ToString("0.0") + " с, " + Remote(client));
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
            sb.Append("Access-Control-Expose-Headers: Content-Range, Content-Length, Accept-Ranges\r\n");
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
                case 404: return "Not Found";
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
                if (path == "/version")
                {
                    var w = new JsonWriter();
                    engine.WriteVersion(w, port);
                    return SendJson(s, req, w.ToString());
                }
                if (path == "/log")
                {
                    // ?after=N — страница отладки: строки после N-й, JSON
                    if (req.Has("after") && journal != null)
                    {
                        long.TryParse(req.Q("after"), out long after);
                        return SendJson(s, req, journal.Json(after));
                    }
                    return SendText(s, req, 200, journal != null ? journal.Text() : "");
                }
                if (path == "" || path == "/index.html") return Send(s, req, 200, "text/html; charset=utf-8", WebPage.Html);
                if (path == "/favicon.ico") { WriteHead(s, req, 204, null, 0, null, req.KeepAlive); return true; }
                if (path == "/torrents") return Torrents(s, req);
                if (path == "/stream" || path.StartsWith("/stream/")) return StreamRoute(client, s, req);
                if (path.StartsWith("/play/")) return Play(client, s, req, path);
                if (path == "/cache") return Cache(s, req);
                if (path == "/settings") return SettingsRoute(s, req);
                if (path.StartsWith("/download/")) return Download(s, req, path);
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

        string Status(TorrEntry e)
        {
            var w = new JsonWriter();
            engine.WriteStatus(w, e);
            return w.ToString();
        }

        bool Torrents(Stream s, Request req)
        {
            var o = Body(req);
            string action = o.Str("action") ?? "";
            string hash = o.Str("hash");
            switch (action)
            {
                case "add":
                {
                    var e = engine.AddAsync(o.Str("link"), o.Str("title"), o.Str("poster"), o.Str("data"), o.Str("category"),
                        o.Bool("save_to_db") ?? false).GetAwaiter().GetResult();
                    return SendJson(s, req, Status(e));
                }
                case "get":
                {
                    var e = engine.Get(hash);
                    if (e == null) return SendText(s, req, 404, "torrent not found");
                    return SendJson(s, req, Status(e));
                }
                case "set":
                    engine.SetMeta(hash, o.Str("title"), o.Str("poster"), o.Str("data"), o.Str("category"));
                    return SendText(s, req, 200, "");
                case "rem":
                    engine.RemoveAsync(hash).GetAwaiter().GetResult();
                    return SendText(s, req, 200, "");
                case "drop":
                    engine.DropAsync(hash).GetAwaiter().GetResult();
                    return SendText(s, req, 200, "");
                case "wipe":
                    engine.WipeAsync().GetAwaiter().GetResult();
                    return SendText(s, req, 200, "");
                case "list":
                {
                    var w = new JsonWriter().Arr();
                    foreach (var e in engine.List()) engine.WriteStatus(w, e);
                    return SendJson(s, req, w.EndArr().ToString());
                }
                default:
                    return SendText(s, req, 400, "unknown action: " + action);
            }
        }

        bool Cache(Stream s, Request req)
        {
            var o = Body(req);
            var e = engine.Get(o.Str("hash"));
            if (e == null) return SendJson(s, req, "{}");
            var w = new JsonWriter();
            engine.WriteCache(w, e, o.Bool("peers") ?? false);
            return SendJson(s, req, w.ToString());
        }

        bool SettingsRoute(Stream s, Request req)
        {
            var o = Body(req);
            string action = o.Str("action") ?? "get";
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

        bool Download(Stream s, Request req, string path)
        {
            if (!int.TryParse(path.Substring("/download/".Length), out int mb) || mb <= 0) mb = 1;
            mb = Math.Min(mb, 1024);
            long total = (long)mb << 20;
            WriteHead(s, req, 200, "application/octet-stream", total, null, req.KeepAlive);
            if (req.Method == "HEAD") return true;
            var zero = new byte[ChunkSize];
            for (long left = total; left > 0; left -= ChunkSize)
                s.Write(zero, 0, (int)Math.Min(ChunkSize, left));
            return true;
        }

        // /stream?link=…&index=N&play|stat|m3u|preload — как у TorrServer
        bool StreamRoute(TcpClient client, Stream s, Request req)
        {
            string link = req.Q("link");
            if (string.IsNullOrEmpty(link)) return SendText(s, req, 400, "link should not be empty");
            var e = engine.AddAsync(link, req.Q("title"), req.Q("poster"), null, null, req.Has("save")).GetAwaiter().GetResult();
            bool hasInfo = engine.WaitInfoAsync(e, InfoTimeoutMs).GetAwaiter().GetResult();

            if (req.Has("stat")) return SendJson(s, req, Status(e));
            if (!hasInfo) return SendText(s, req, 404, "torrent info timeout");
            if (req.Has("m3u")) return Send(s, req, 200, "audio/x-mpegurl; charset=utf-8", M3u(req, e));

            int index;
            if (!int.TryParse(req.Q("index"), out index))
            {
                // Без номера: единственный файл — он, иначе список
                var files = e.Files.Where(f => f.File != null).ToList();
                if (files.Count != 1) return Send(s, req, 200, "audio/x-mpegurl; charset=utf-8", M3u(req, e));
                index = files[0].Id;
            }
            var stat = engine.FileById(e, index);
            if (stat == null || stat.File == null) return SendText(s, req, 404, "file index out of range");

            if (req.Has("preload"))
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                log(e.Hash + ": предзагрузка файла " + stat.Id + ", " + Remote(client));
                bool ok = engine.Preload(e, stat, () => Gone(client));
                log(e.Hash + ": предзагрузка " + (ok ? "готова" : "прервана") + " за " + (t.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с, " + Mb(e.PreloadedBytes) + " из " + Mb(e.PreloadSize));
                return SendJson(s, req, Status(e));
            }
            return ServeFile(client, s, req, e, stat);
        }

        // /play/<хэш>/<номер>
        bool Play(TcpClient client, Stream s, Request req, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return SendText(s, req, 400, "use /play/hash/index");
            string hash = parts[1];
            var e = engine.OpenAsync(hash).GetAwaiter().GetResult();
            if (e == null)
                e = engine.AddAsync(hash, null, null, null, null, false).GetAwaiter().GetResult();
            if (!engine.WaitInfoAsync(e, InfoTimeoutMs).GetAwaiter().GetResult()) return SendText(s, req, 404, "torrent info timeout");
            if (!int.TryParse(parts[2], out int index)) return SendText(s, req, 400, "wrong index");
            var stat = engine.FileById(e, index);
            if (stat == null || stat.File == null) return SendText(s, req, 404, "file index out of range");
            return ServeFile(client, s, req, e, stat);
        }

        string M3u(Request req, TorrEntry e)
        {
            string host = req.Header("Host") ?? ("127.0.0.1:" + port);
            var sb = new StringBuilder("#EXTM3U\n");
            foreach (var f in e.Files.Where(x => x.File != null))
            {
                string name = f.Path.Substring(f.Path.LastIndexOf('/') + 1);
                sb.Append("#EXTINF:0,").Append(name).Append('\n');
                sb.Append("http://").Append(host).Append("/stream/").Append(Uri.EscapeDataString(name))
                  .Append("?link=").Append(e.Hash).Append("&index=").Append(f.Id).Append("&play\n");
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

        bool ServeFile(TcpClient client, Stream s, Request req, TorrEntry e, FileStat f)
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
            // В журнал — кто и откуда читает: по нему видно, чего ждёт плеер
            var started = System.Diagnostics.Stopwatch.StartNew();
            long sent = 0;
            string how = "прервано";
            log(e.Hash + ": файл " + f.Id + " с " + Mb(start) + (partial ? " (Range " + range + ")" : "") + ", " + Remote(client));
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
                }
                how = "до конца запроса";
                return true;
            }
            finally
            {
                engine.CloseReader(reader);
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
