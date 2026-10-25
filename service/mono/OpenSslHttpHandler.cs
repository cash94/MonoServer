// HttpClient на Android (Mono из Termux без TLS): HTTP/1.1 поверх своего сокета, для
// https — OpenSSL (OpenSsl.cs). Каждый запрос — новое соединение с Connection: close:
// запросов у службы мало (ретрекеры, постеры, .torrent, анонсы HTTP-трекеров), пул не нужен.
// Переадресации — здесь же: и http → https, которую штатный обработчик Mono не осилил бы.
// Ввод-вывод блокирующий — в своём потоке на запрос, чтобы не занимать пул потоков,
// которым живёт MonoTorrent; отмена закрывает сокет.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TorrStreamTorrServer
{
    class OpenSslHttpHandler : HttpMessageHandler
    {
        const int MaxRedirects = 5;
        const int ConnectTimeoutMs = 15000;
        const int IoTimeoutMs = 30000;

        readonly AddressFamily family;

        public OpenSslHttpHandler(AddressFamily family)
        {
            this.family = family;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try { tcs.TrySetResult(Send(request, ct)); }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                    else tcs.TrySetException(e is HttpRequestException ? e : new HttpRequestException(e.Message, e));
                }
            }) { IsBackground = true, Name = "http-out" }.Start();
            return tcs.Task;
        }

        HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            var method = request.Method;
            byte[] body = request.Content != null ? request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() : null;
            for (int hop = 0; ; hop++)
            {
                ct.ThrowIfCancellationRequested();
                var conn = Connection.Open(uri, family, ct);
                try
                {
                    conn.Write(BuildRequest(request, method, uri, body));
                    var resp = ReadResponse(conn, request, method);
                    int code = (int)resp.StatusCode;
                    var location = resp.Headers.Location;
                    if (code >= 300 && code < 400 && code != 304 && location != null && hop < MaxRedirects)
                    {
                        resp.Dispose();
                        conn.Dispose();
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        // 303 и 301/302 после POST — дальше GET без тела, как у браузеров
                        if (code == 303 || ((code == 301 || code == 302) && method == HttpMethod.Post))
                        {
                            method = HttpMethod.Get;
                            body = null;
                        }
                        continue;
                    }
                    return resp;   // соединение закроет тело ответа
                }
                catch
                {
                    conn.Dispose();
                    throw;
                }
            }
        }

        static byte[] BuildRequest(HttpRequestMessage request, HttpMethod method, Uri uri, byte[] body)
        {
            var sb = new StringBuilder();
            sb.Append(method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(uri.IdnHost);
            if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
            sb.Append("\r\nConnection: close\r\n");
            var headers = request.Headers.AsEnumerable();
            if (body != null && request.Content != null) headers = headers.Concat(request.Content.Headers);
            foreach (var h in headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(h.Key).Append(": ").Append(string.Join(h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ", ", h.Value)).Append("\r\n");
            }
            if (body != null) sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            if (body == null || body.Length == 0) return head;
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }

        static HttpResponseMessage ReadResponse(Connection conn, HttpRequestMessage request, HttpMethod method)
        {
            string status;
            do
            {
                status = conn.ReadLine() ?? throw new IOException("сервер закрыл соединение без ответа");
                // 100 Continue и прочие 1xx — пропускаем вместе с их заголовками
                if (status.StartsWith("HTTP/1.1 1") || status.StartsWith("HTTP/1.0 1"))
                {
                    while (!string.IsNullOrEmpty(conn.ReadLine())) { }
                    status = null;
                }
            } while (status == null);

            var parts = status.Split(new[] { ' ' }, 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/") || !int.TryParse(parts[1], out int code))
                throw new IOException("не HTTP-ответ: " + status);
            var resp = new HttpResponseMessage((HttpStatusCode)code)
            {
                ReasonPhrase = parts.Length > 2 ? parts[2] : "",
                RequestMessage = request,
                Version = parts[0] == "HTTP/1.0" ? new Version(1, 0) : new Version(1, 1),
            };
            var content = new StreamContent(Stream.Null);
            long length = -1;
            bool chunked = false;
            string line;
            var contentHeaders = new System.Collections.Generic.List<(string, string)>();
            while (!string.IsNullOrEmpty(line = conn.ReadLine()))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) long.TryParse(value, out length);
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
                if (!resp.Headers.TryAddWithoutValidation(name, value)) contentHeaders.Add((name, value));
            }
            if (line == null) throw new IOException("ответ оборвался в заголовках");

            Stream bodyStream;
            if (method == HttpMethod.Head || code == 204 || code == 304) { bodyStream = new BodyStream(conn, 0, false); }
            else if (chunked) bodyStream = new BodyStream(conn, -1, true);
            else bodyStream = new BodyStream(conn, length, false);
            content = new StreamContent(bodyStream);
            foreach (var (name, value) in contentHeaders) content.Headers.TryAddWithoutValidation(name, value);
            resp.Content = content;
            return resp;
        }

        /// <summary>Сокет и, для https, SSL поверх него; чтение с буфером под заголовки</summary>
        sealed class Connection : IDisposable
        {
            readonly Socket socket;
            IntPtr ssl;
            readonly object io = new object();
            readonly byte[] buf = new byte[16 * 1024];
            int pos, len;
            CancellationTokenRegistration cancel;
            int disposed;

            Connection(Socket socket) { this.socket = socket; }

            public static Connection Open(Uri uri, AddressFamily family, CancellationToken ct)
            {
                var addrs = Dns.GetHostAddresses(uri.DnsSafeHost);
                // Для трекеров семейство задано (анонс по IPv4 / IPv6); иначе — IPv4 первым
                var list = addrs.Where(a => family == AddressFamily.Unspecified || a.AddressFamily == family)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
                if (list.Count == 0) throw new IOException("нет адреса для " + uri.Host);
                Exception last = null;
                foreach (var addr in list)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    var c = new Connection(s);
                    c.cancel = ct.Register(() => c.Abort());
                    try
                    {
                        var ar = s.BeginConnect(addr, uri.Port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) throw new TimeoutException("нет ответа от " + uri.Host);
                        s.EndConnect(ar);
                        s.Blocking = true;
                        s.ReceiveTimeout = IoTimeoutMs;
                        s.SendTimeout = IoTimeoutMs;
                        if (uri.Scheme == Uri.UriSchemeHttps) c.ssl = OpenSsl.Connect(s, uri.IdnHost);
                        return c;
                    }
                    catch (Exception e)
                    {
                        c.Dispose();
                        last = e;
                    }
                }
                ct.ThrowIfCancellationRequested();
                throw last;
            }

            /// <summary>Прервать блокирующий ввод-вывод (отмена) — SSL освобождает Dispose</summary>
            void Abort()
            {
                try { socket.Shutdown(SocketShutdown.Both); } catch { }
            }

            public unsafe void Write(byte[] data)
            {
                lock (io)
                {
                    if (ssl == IntPtr.Zero) { int off = 0; while (off < data.Length) off += socket.Send(data, off, data.Length - off, SocketFlags.None); return; }
                    fixed (byte* p = data)
                    {
                        int off = 0;
                        while (off < data.Length)
                        {
                            int n = OpenSsl.SSL_write(ssl, p + off, data.Length - off);
                            if (n <= 0) throw new IOException("TLS: запись не удалась " + OpenSsl.LastError(OpenSsl.SSL_get_error(ssl, n)));
                            off += n;
                        }
                    }
                }
            }

            /// <summary>Прочитать из соединения (сначала — остаток буфера); 0 — конец</summary>
            public unsafe int Read(byte[] dst, int off, int count)
            {
                if (count == 0) return 0;
                if (pos < len)
                {
                    int n = Math.Min(count, len - pos);
                    Buffer.BlockCopy(buf, pos, dst, off, n);
                    pos += n;
                    return n;
                }
                lock (io)
                {
                    if (Volatile.Read(ref disposed) != 0) return 0;
                    if (ssl == IntPtr.Zero) return socket.Receive(dst, off, count, SocketFlags.None);
                    fixed (byte* p = dst)
                    {
                        int n = OpenSsl.SSL_read(ssl, p + off, count);
                        if (n > 0) return n;
                        int err = OpenSsl.SSL_get_error(ssl, n);
                        // Закрыли как положено (close_notify) или просто оборвали после ответа
                        if (err == OpenSsl.SSL_ERROR_ZERO_RETURN || n == 0) return 0;
                        throw new IOException("TLS: чтение не удалось " + OpenSsl.LastError(err));
                    }
                }
            }

            /// <summary>Строка до CRLF (без него) или null — соединение закрылось</summary>
            public string ReadLine()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= len)
                    {
                        pos = 0;
                        len = Read(buf, 0, buf.Length);
                        if (len <= 0) { len = 0; return sb.Length > 0 ? sb.ToString() : null; }
                    }
                    char ch = (char)buf[pos++];
                    if (ch == '\n') return sb.ToString().TrimEnd('\r');
                    if (sb.Length > 16 * 1024) throw new IOException("слишком длинная строка заголовка");
                    sb.Append(ch);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                cancel.Dispose();
                Abort();
                lock (io)
                {
                    if (ssl != IntPtr.Zero)
                    {
                        OpenSsl.SSL_free(ssl);
                        ssl = IntPtr.Zero;
                    }
                }
                try { socket.Close(); } catch { }
            }
        }

        /// <summary>Тело ответа: по Content-Length, по кускам (chunked) или до закрытия</summary>
        sealed class BodyStream : Stream
        {
            readonly Connection conn;
            readonly bool chunked;
            long left;          // по длине: сколько осталось; -1 — до закрытия
            long chunkLeft;     // chunked: остаток текущего куска
            bool done;

            public BodyStream(Connection conn, long length, bool chunked)
            {
                this.conn = conn;
                this.chunked = chunked;
                left = length;
                done = !chunked && length == 0;
                if (done) conn.Dispose();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (done || count == 0) return 0;
                if (chunked)
                {
                    if (chunkLeft == 0)
                    {
                        var size = conn.ReadLine() ?? throw new IOException("ответ оборвался");
                        if (size.Length == 0) size = conn.ReadLine() ?? throw new IOException("ответ оборвался");   // CRLF после куска
                        int semi = size.IndexOf(';');
                        chunkLeft = Convert.ToInt64((semi >= 0 ? size.Substring(0, semi) : size).Trim(), 16);
                        if (chunkLeft == 0)
                        {
                            while (!string.IsNullOrEmpty(conn.ReadLine())) { }   // трейлеры
                            Finish();
                            return 0;
                        }
                    }
                    int n = conn.Read(buffer, offset, (int)Math.Min(count, chunkLeft));
                    if (n <= 0) throw new IOException("ответ оборвался");
                    chunkLeft -= n;
                    return n;
                }
                int want = left >= 0 ? (int)Math.Min(count, left) : count;
                int got = conn.Read(buffer, offset, want);
                if (got <= 0)
                {
                    if (left > 0) throw new IOException("ответ оборвался");
                    Finish();
                    return 0;
                }
                if (left > 0 && (left -= got) == 0) Finish();
                return got;
            }

            void Finish()
            {
                done = true;
                conn.Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) conn.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
