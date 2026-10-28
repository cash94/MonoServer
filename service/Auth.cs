// Кто спрашивает (Caller) и что ему можно. Откуда берётся пользователь, по порядку:
//   X-MonoServer-Local: <local.key>  — приложение на том же устройстве (Android: настройки) — как admin;
//   cookie ms_session                — веб-интерфейс (только admin);
//   Authorization: Bearer <ключ>     — ключ пользователя или сессия;
//   Authorization: Basic             — логин и пароль, как у TorrServer (TorrStream, Lampa);
//   ?token=<ключ>                    — ссылка на поток для плеера, которому заголовок не передать;
//   ?ms_user&ms_exp&ms_sig           — зритель, которого прислал мастер (подпись ключом узла, Balancer.cs).
// Неверный пароль при выключенном входе — как без пароля (клиенты TorrServer шлют
// старый Basic куда попало); при включённом — 401. С одного адреса больше
// MaxFails неверных за 10 минут — 429 до конца этих 10 минут.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TorrStreamTorrServer
{
    public class Caller
    {
        public User User;           // вошедший пользователь или null
        public string Remote;       // логин зрителя от мастера (этот сервер — узел)
        public bool Local;          // приложение на этом устройстве (local.key)
        public bool Anonymous;      // без входа там, где вход не нужен
        public bool LanAdmin;       // веб-интерфейс из домашней сети без входа (LanWithoutAuth)
        public string Ip;
        public string Error;        // почему нельзя (блокировка, срок, неверный пароль)
        public int ErrorCode;

        public bool Admin => Local || LanAdmin || (User != null && User.IsAdmin);
        public string Login => User != null ? User.Login : null;
        /// <summary>Зритель для счёта «сколько человек смотрит»: пользователь, зритель мастера или адрес</summary>
        public string Viewer => User != null ? "u:" + User.Login.ToLowerInvariant() : Remote != null ? "r:" + Remote.ToLowerInvariant() : "ip:" + Ip;
        public string Who => User != null ? User.Login : Remote != null ? Remote + "@мастер" : Local ? "приложение" : "без входа";
    }

    public partial class HttpApi
    {
        const int MaxFails = 10;
        const long FailWindowMs = 10 * 60 * 1000;
        const long BasicCacheMs = 10 * 60 * 1000;

        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        // Проверенный Basic (заголовок целиком → пользователь): PBKDF2 на каждый
        // Range-запрос плеера был бы слишком дорог
        readonly Dictionary<string, KeyValuePair<User, long>> basicCache = new Dictionary<string, KeyValuePair<User, long>>(StringComparer.Ordinal);
        readonly Dictionary<string, KeyValuePair<int, long>> fails = new Dictionary<string, KeyValuePair<int, long>>(StringComparer.Ordinal);

        long Ms() { return clock.ElapsedMilliseconds; }

        /// <summary>Адрес клиента: за nginx на этой же машине — последний из X-Forwarded-For</summary>
        static string ClientIp(TcpClient client, Request req)
        {
            IPAddress ip = null;
            try { ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address; } catch { }
            if (ip != null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            string s = ip != null ? ip.ToString() : "?";
            if (ip != null && IPAddress.IsLoopback(ip))
            {
                string xff = req.Header("X-Forwarded-For") ?? req.Header("X-Real-IP");
                if (!string.IsNullOrEmpty(xff))
                {
                    string last = xff.Split(',').Last().Trim();
                    if (IPAddress.TryParse(last, out _)) s = last;
                }
            }
            return s;
        }

        static bool IsLan(string ip)
        {
            if (!IPAddress.TryParse(ip ?? "", out var a)) return false;
            if (IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return (b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
            return false;
        }

        static string Cookie(Request req, string name)
        {
            var c = req.Header("Cookie");
            if (string.IsNullOrEmpty(c)) return null;
            foreach (var part in c.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        bool Blocked(string ip)
        {
            lock (fails) return fails.TryGetValue(ip, out var f) && f.Key >= MaxFails && Ms() < f.Value;
        }

        public void Fail(string ip)
        {
            lock (fails)
            {
                long now = Ms();
                if (!fails.TryGetValue(ip, out var f) || now >= f.Value) f = new KeyValuePair<int, long>(0, now + FailWindowMs);
                fails[ip] = new KeyValuePair<int, long>(f.Key + 1, f.Value);
                if (f.Key + 1 == MaxFails) log("вход: с " + ip + " " + MaxFails + " неверных паролей — 10 минут не пускаем");
                if (fails.Count > 10000) fails.Clear();
            }
        }

        void Succeeded(string ip)
        {
            lock (fails) fails.Remove(ip);
        }

        /// <summary>Пользователь по Basic: из кэша или проверкой пароля</summary>
        User FromBasic(string header, string ip)
        {
            long now = Ms();
            lock (basicCache)
            {
                if (basicCache.TryGetValue(header, out var hit) && now < hit.Value)
                {
                    var current = accounts.Get(hit.Key.Login);
                    if (current == hit.Key) return current;
                    basicCache.Remove(header);
                }
            }
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim())); }
            catch { return null; }
            int colon = text.IndexOf(':');
            if (colon <= 0) return null;
            var u = accounts.Verify(text.Substring(0, colon), text.Substring(colon + 1));
            if (u == null) return null;
            lock (basicCache)
            {
                if (basicCache.Count > 1000) basicCache.Clear();
                basicCache[header] = new KeyValuePair<User, long>(u, now + BasicCacheMs);
            }
            Succeeded(ip);
            return u;
        }

        /// <summary>Сбросить кэш входа (пароль сменился, пользователь удалён или заблокирован)</summary>
        public void ForgetLogins()
        {
            lock (basicCache) basicCache.Clear();
        }

        /// <summary>Подпись перенаправления мастера: HMAC-SHA256 ключом узла от «ms1|логин|срок»</summary>
        public static string Sign(string key, string login, long exp)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(key ?? "")))
                return Accounts.Hex(h.ComputeHash(Encoding.UTF8.GetBytes("ms1|" + (login ?? "") + "|" + exp)));
        }

        Caller Identify(TcpClient client, Request req)
        {
            var c = new Caller { Ip = ClientIp(client, req) };
            if (!string.IsNullOrEmpty(localKey) && Accounts.SameText(req.Header("X-MonoServer-Local") ?? "", localKey))
            {
                c.Local = true;
                c.User = accounts.Admin;
                return c;
            }
            User u = null;
            bool tried = false;
            string auth = req.Header("Authorization") ?? "";
            var session = Cookie(req, "ms_session");
            if (!string.IsNullOrEmpty(session)) u = accounts.BySession(session);
            if (u == null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                tried = true;
                u = accounts.ByToken(auth.Substring(7).Trim());
            }
            if (u == null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                tried = true;
                if (Blocked(c.Ip)) { c.Error = "слишком много неверных паролей — попробуйте через 10 минут"; c.ErrorCode = 429; return c; }
                u = FromBasic(auth, c.Ip);
            }
            string token = req.Q("token");
            if (u == null && !string.IsNullOrEmpty(token))
            {
                tried = true;
                u = accounts.ByToken(token);
            }
            if (u == null && req.Has("ms_sig"))
            {
                long.TryParse(req.Q("ms_exp"), out long exp);
                string key = accounts.NodeKey;
                if (!string.IsNullOrEmpty(key) && exp > Accounts.UnixNow() && Accounts.SameText(Sign(key, req.Q("ms_user"), exp), req.Q("ms_sig") ?? ""))
                {
                    c.Remote = req.Q("ms_user") ?? "";
                    if (c.Remote.Length == 0) c.Remote = "без входа";
                    return c;
                }
                tried = true;
            }
            if (u != null)
            {
                string denied = u.Denied();
                if (denied != null) { c.Error = denied; c.ErrorCode = 403; return c; }
                c.User = u;
                return c;
            }
            if (tried)
            {
                Fail(c.Ip);
                c.Error = "неверный логин, пароль или ключ";
                c.ErrorCode = 401;
            }
            // Без входа: где вход выключен, и (если разрешено) из домашней сети
            bool lan = accounts.LanWithoutAuth && IsLan(c.Ip);
            c.Anonymous = !accounts.RequireAuth || lan;
            if (c.Anonymous && c.ErrorCode == 401) { c.Error = null; c.ErrorCode = 0; }
            // Веб-интерфейс из домашней сети — сразу admin, но только со своей страницы
            // (OwnPage): CORS у службы открыт всем, и иначе любой сайт, открытый в
            // браузере дома, управлял бы службой от имени admin
            c.LanAdmin = lan && !tried && OwnPage(req);
            return c;
        }

        /// <summary>
        /// Запрос с собственной страницы службы, не с чужого сайта: Origin (браузер шлёт
        /// его на запросы с других сайтов и на POST) — тот же адрес, что Host, и Host —
        /// IP, localhost или локальное имя. Последнее — от DNS rebinding: чужое имя,
        /// которое резолвится в адрес службы, дало бы совпадение Origin и Host
        /// </summary>
        static bool OwnPage(Request req)
        {
            string host = req.Header("Host");
            if (string.IsNullOrEmpty(host) || !LocalHostName(host)) return false;
            if (req.Header("Sec-Fetch-Site") is string site && site != "same-origin" && site != "none") return false;
            string origin = req.Header("Origin");
            if (string.IsNullOrEmpty(origin)) return true;
            return Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase);
        }

        static bool LocalHostName(string host)
        {
            // Host — «имя:порт» или «[IPv6]:порт»
            string name = host.StartsWith("[") ? host.Substring(1, Math.Max(0, host.IndexOf(']') - 1)) : host.Split(':')[0];
            name = name.TrimEnd('.').ToLowerInvariant();
            if (IPAddress.TryParse(name, out _) || name == "localhost") return true;
            if (name.IndexOf('.') < 0) return name.Length > 0;   // имя компьютера в домашней сети
            foreach (var suffix in new[] { ".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain" })
                if (name.EndsWith(suffix)) return true;
            return false;
        }

        /// <summary>
        /// Чей список торрентов: вошедшего; без входа — admin (как одна база
        /// TorrServer до пользователей); у зрителя мастера списка нет
        /// </summary>
        User ListOwner(Caller c)
        {
            if (c.User != null) return c.User;
            return c.Remote == null ? accounts.Admin : null;
        }

        // Ответ «нужен вход»: Basic-вызов браузеру — только если это не наша страница
        bool Unauthorized(Stream s, Request req, Caller c)
        {
            int code = c.ErrorCode != 0 ? c.ErrorCode : 401;
            string text = c.Error ?? "нужен вход: логин и пароль MonoServer";
            string extra = code == 401 && req.Header("X-MonoServer-Web") == null ? "WWW-Authenticate: Basic realm=\"MonoServer\", charset=\"UTF-8\"\r\n" : null;
            var b = Encoding.UTF8.GetBytes(new JsonWriter().Obj().Prop("error", text).Prop("code", code).EndObj().ToString());
            WriteHead(s, req, code, "application/json; charset=utf-8", b.Length, extra, req.KeepAlive);
            if (req.Method != "HEAD") s.Write(b, 0, b.Length);
            return true;
        }
    }
}
