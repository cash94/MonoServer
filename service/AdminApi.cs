// /api/… — своё у MonoServer (у TorrServer такого нет):
//   GET  /api/info                — без входа: что это MonoServer, версия, нужен ли вход,
//                                   кто вошёл (для TorrStream: отличить от TorrServer)
//   POST /api/login {login,password[,web]} → сессия (cookie ms_session — только admin) и ключ
//   POST /api/logout
//   GET  /api/setup               — нужен ли первый вход; POST {code} — проверить код,
//                                   POST {code,password} — задать пароль admin
//   GET  /api/me                  — вошедший: срок, трафик, ключ для ссылок
//   GET  /api/node/status         — для мастера (X-MonoServer-Key: ключ узла)
// Только admin (веб-интерфейс):
//   GET  /api/users; POST /api/users {action: create|update|delete|wipe|torrents|forget, login, …}
//   GET  /api/traffic?days=N; GET /api/streams
//   GET|POST /api/access {require_auth, lan_without_auth}
//   GET|POST /api/master {enabled, local_max, nodes} | {action:"new_node_key"}
//   POST /api/torrents {action:"remove", hash[, login]} — у пользователя или у всех
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace TorrStreamTorrServer
{
    public partial class HttpApi
    {
        const string ApiVersion = "1";

        static bool SendJson(Stream s, Request req, int code, string json, string extra = null)
        {
            var b = Encoding.UTF8.GetBytes(json);
            WriteHead(s, req, code, "application/json; charset=utf-8", b.Length, extra, req.KeepAlive);
            if (req.Method != "HEAD") s.Write(b, 0, b.Length);
            return true;
        }

        static bool Error(Stream s, Request req, int code, string text)
        {
            return SendJson(s, req, code, new JsonWriter().Obj().Prop("error", text).Prop("code", code).EndObj().ToString());
        }

        static bool Ok(Stream s, Request req) { return SendJson(s, req, 200, "{\"ok\":true}"); }

        static string SessionCookie(Session ses)
        {
            return "Set-Cookie: ms_session=" + ses.Token + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=" + (ses.Expires - Accounts.UnixNow()) + "\r\n";
        }

        const string ClearCookie = "Set-Cookie: ms_session=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0\r\n";

        bool Api(TcpClient client, Stream s, Request req, Caller c, string path)
        {
            switch (path)
            {
                case "/api/info": return Info(s, req, c);
                case "/api/login": return Login(s, req, c);
                case "/api/logout":
                    accounts.EndSession(Cookie(req, "ms_session"));
                    return SendJson(s, req, 200, "{\"ok\":true}", ClearCookie);
                case "/api/setup": return Setup(s, req, c);
                case "/api/setup/code": return SetupCodeForApp(client, s, req);
                case "/api/node/status": return NodeStatus(s, req);
                case "/api/me":
                    if (c.User == null) return Unauthorized(s, req, c);
                    return SendJson(s, req, 200, Me(c.User, true));
            }
            if (!c.Admin)
            {
                if (c.User != null && c.ErrorCode == 0) { c.Error = "только для администратора"; c.ErrorCode = 403; }
                return Unauthorized(s, req, c);
            }
            switch (path)
            {
                case "/api/users": return Users(s, req, c);
                case "/api/traffic":
                {
                    int.TryParse(req.Q("days"), out int days);
                    var w = new JsonWriter();
                    accounts.WriteTraffic(w, Math.Max(1, Math.Min(365, days > 0 ? days : 30)));
                    return SendJson(s, req, 200, w.ToString());
                }
                case "/api/streams": return StreamsList(s, req);
                case "/api/access":
                {
                    if (req.Method == "POST")
                    {
                        var o = Body(req);
                        accounts.SetAccess(o.Bool("require_auth"), o.Bool("lan_without_auth"));
                    }
                    return SendJson(s, req, 200, new JsonWriter().Obj().Prop("require_auth", accounts.RequireAuth).Prop("lan_without_auth", accounts.LanWithoutAuth).EndObj().ToString());
                }
                case "/api/master": return Master(s, req);
                case "/api/torrents":
                {
                    var o = Body(req);
                    if (o.Str("action") != "remove") return Error(s, req, 400, "unknown action");
                    string login = o.Str("login");
                    User from = null;
                    if (!string.IsNullOrEmpty(login) && (from = accounts.Get(login)) == null) return Error(s, req, 404, "нет такого пользователя");
                    RemoveTorrent(o.Str("hash"), from, c.Viewer);
                    return Ok(s, req);
                }
            }
            return Error(s, req, 404, "not found");
        }

        bool Info(Stream s, Request req, Caller c)
        {
            var w = new JsonWriter().Obj()
                .Prop("name", "MonoServer").Prop("version", TorrEngine.ServiceVersion).Prop("echo", TorrEngine.EchoVersion)
                .Prop("api", ApiVersion)
                // Как входить: Basic (как TorrServer), Bearer, ?token= — ключ из /api/login или /api/me
                .Prop("auth_required", accounts.RequireAuth && !c.Anonymous)
                .Prop("master", accounts.MasterEnabled);
            w.Key("auth").Arr().Val("basic").Val("bearer").Val("token").EndArr();
            // torrent_meta — у торрента поля tmdb_id и seasons (add/set/list, /stream?save&tmdb_id&seasons)
            w.Key("features").Arr().Val("users").Val("user_torrents").Val("traffic").Val("balancer").Val("torrent_meta").EndArr();
            w.Key("user");
            if (c.User != null) w.Raw(Me(c.User, false));
            else if (c.Remote != null) w.Obj().Prop("login", c.Remote).Prop("role", "remote").EndObj();
            else w.Raw("null");
            if (c.Error != null) w.Prop("auth_error", c.Error);
            return SendJson(s, req, 200, w.EndObj().ToString());
        }

        /// <summary>Пользователь для него самого (withToken — с ключом для ссылок)</summary>
        string Me(User u, bool withToken)
        {
            var w = new JsonWriter().Obj().Prop("login", u.Login).Prop("role", u.Role).Prop("expires", u.Expires);
            if (u.Expires > 0) w.Prop("days_left", Math.Max(0, (u.Expires - Accounts.UnixNow() + 86399) / 86400));
            w.Prop("torrents", u.Torrents.Count)
                .Prop("traffic_today", u.Traffic.On(Accounts.Today())).Prop("traffic_30d", u.Traffic.Since(Accounts.DayAgo(29))).Prop("traffic_total", u.Traffic.Total);
            if (withToken) w.Prop("token", u.Token);
            return w.EndObj().ToString();
        }

        bool Login(Stream s, Request req, Caller c)
        {
            if (req.Method != "POST") return Error(s, req, 400, "POST {login, password}");
            if (Blocked(c.Ip)) return Error(s, req, 429, "слишком много неверных паролей — попробуйте через 10 минут");
            var o = Body(req);
            var u = accounts.Verify((o.Str("login") ?? "").Trim(), o.Str("password"));
            if (u == null)
            {
                Fail(c.Ip);
                log("вход: неверный логин или пароль «" + (o.Str("login") ?? "") + "», " + c.Ip);
                return Error(s, req, 401, accounts.NeedsSetup ? "пароль admin ещё не задан — нужен код первого входа из журнала" : "неверный логин или пароль");
            }
            Succeeded(c.Ip);
            string denied = u.Denied();
            if (denied != null) return Error(s, req, 403, denied);
            bool web = o.Bool("web") ?? false;
            if (web && !u.IsAdmin) return Error(s, req, 403, "веб-интерфейс — только для администратора (admin)");
            var w = new JsonWriter().Obj().Prop("ok", true).Key("user").Raw(Me(u, true));
            string extra = null;
            if (u.IsAdmin)
            {
                var ses = accounts.NewSession(u, c.Ip);
                w.Prop("session", ses.Token);
                extra = SessionCookie(ses);
                log("вход: " + u.Login + ", " + c.Ip);
            }
            return SendJson(s, req, 200, w.EndObj().ToString(), extra);
        }

        bool Setup(Stream s, Request req, Caller c)
        {
            if (req.Method != "POST")
                return SendJson(s, req, 200, new JsonWriter().Obj().Prop("needs_setup", accounts.NeedsSetup).EndObj().ToString());
            if (!accounts.NeedsSetup) return Error(s, req, 409, "пароль admin уже задан — войдите");
            if (Blocked(c.Ip)) return Error(s, req, 429, "слишком много попыток — подождите 10 минут");
            var o = Body(req);
            string code = o.Str("code"), password = o.Str("password");
            if (string.IsNullOrEmpty(password))
            {
                if (accounts.CheckSetupCode(code)) return Ok(s, req);
                Fail(c.Ip);
                return Error(s, req, 401, "неверный код — он в журнале службы, строка «Первый вход в веб-интерфейс»");
            }
            string err = accounts.CompleteSetup(code, password);
            if (err != null)
            {
                if (err.StartsWith("неверный код")) Fail(c.Ip);
                return Error(s, req, 400, err);
            }
            var ses = accounts.NewSession(accounts.Admin, c.Ip);
            return SendJson(s, req, 200, "{\"ok\":true}", SessionCookie(ses));
        }

        /// <summary>
        /// Код первого входа — странице приложения на этом же устройстве (Tizen:
        /// журнал службы на телевизоре не прочитать). Только когда сборка это
        /// разрешила (LocalSetupCode — Program.cs на Tizen), запрос пришёл прямо
        /// с 127.0.0.1, а не через прокси (за nginx на VPS «loopback» — любой из
        /// интернета), и не со страницы сайта: у пакета приложения origin file://
        /// (или его нет), а сайт, открытый в браузере телевизора, иначе прочитал
        /// бы код через CORS. Остальным — 404, как будто адреса нет
        /// </summary>
        bool SetupCodeForApp(TcpClient client, Stream s, Request req)
        {
            string origin = req.Header("Origin");
            bool appOrigin = string.IsNullOrEmpty(origin) || origin == "null" || origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            bool direct = req.Header("X-Forwarded-For") == null && req.Header("X-Real-IP") == null && req.Header("Forwarded") == null;
            // С этого же устройства: 127.0.0.1 или собственный адрес — на Tizen 7+
            // страница пакета до 127.0.0.1 не достаёт и стучится по IP телевизора
            bool own = false;
            try { var ip = ((System.Net.IPEndPoint)client.Client.RemoteEndPoint).Address; own = IsOwnAddress(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip); } catch { }
            if (!LocalSetupCode || !own || !direct || !appOrigin) return Error(s, req, 404, "not found");
            string code = accounts.SetupCode;
            var w = new JsonWriter().Obj().Prop("needs_setup", accounts.NeedsSetup);
            if (code != null) w.Prop("code", code);
            return SendJson(s, req, 200, w.EndObj().ToString());
        }

        /// <summary>Адрес этого устройства: loopback или один из адресов его сетевых интерфейсов</summary>
        static bool IsOwnAddress(System.Net.IPAddress ip)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.Equals(ip)) return true;
            }
            catch { }
            return false;
        }

        bool NodeStatus(Stream s, Request req)
        {
            string key = accounts.NodeKey;
            if (string.IsNullOrEmpty(key) || !Accounts.SameText(req.Header("X-MonoServer-Key") ?? "", key))
                return Error(s, req, 403, "нужен ключ узла (X-MonoServer-Key)");
            var w = new JsonWriter().Obj().Prop("name", "MonoServer").Prop("version", TorrEngine.ServiceVersion)
                .Prop("viewers", Streams.Viewers()).Prop("streams", Streams.Snapshot().Count).Prop("http_total", accounts.HttpTotal);
            w.Key("users").Obj();
            foreach (var kv in accounts.RemoteTotals()) w.Prop(kv.Key, kv.Value);
            return SendJson(s, req, 200, w.EndObj().EndObj().ToString());
        }

        bool Users(Stream s, Request req, Caller c)
        {
            if (req.Method != "POST")
            {
                var streams = Streams.Snapshot();
                string today = Accounts.Today(), month = Accounts.DayAgo(29);
                var w = new JsonWriter().Arr();
                foreach (var u in accounts.Users())
                {
                    w.Obj().Prop("login", u.Login).Prop("role", u.Role).Prop("blocked", u.Blocked).Prop("expires", u.Expires)
                        .Prop("expired", u.Expired).Prop("created", u.Created).Prop("note", u.Note).Prop("token", u.Token)
                        .Prop("has_password", u.HasPassword).Prop("torrents", u.Torrents.Count)
                        .Prop("traffic_today", u.Traffic.On(today)).Prop("traffic_30d", u.Traffic.Since(month)).Prop("traffic_total", u.Traffic.Total)
                        .Prop("streams", streams.Count(x => string.Equals(x.Login, u.Login, StringComparison.OrdinalIgnoreCase)))
                        .EndObj();
                }
                return SendJson(s, req, 200, w.EndArr().ToString());
            }
            var o = Body(req);
            string action = o.Str("action") ?? "", login = (o.Str("login") ?? "").Trim();
            string err = null;
            switch (action)
            {
                case "create":
                    err = accounts.CreateUser(login, o.Str("password"), o.Str("period") ?? "forever", (int)(o.Num("amount") ?? 0), o.Str("note"));
                    break;
                case "update":
                {
                    err = accounts.UpdateUser(login, o);
                    ForgetLogins();
                    // Свой пароль сменил админ — его сессии закрылись, эту выдаём заново
                    if (err == null && !string.IsNullOrEmpty(o.Str("password")) && c.User != null && string.Equals(c.User.Login, login, StringComparison.OrdinalIgnoreCase) && !c.Local)
                    {
                        var ses = accounts.NewSession(c.User, c.Ip);
                        return SendJson(s, req, 200, "{\"ok\":true}", SessionCookie(ses));
                    }
                    break;
                }
                case "delete":
                {
                    var hashes = accounts.DeleteUser(login, out err);
                    ForgetLogins();
                    if (hashes != null) foreach (var h in hashes) Release(h, "");
                    break;
                }
                case "wipe":
                {
                    var u = accounts.Get(login);
                    if (u == null) { err = "нет такого пользователя"; break; }
                    foreach (var t in accounts.OwnList(u)) RemoveTorrent(t.Hash, u, "");
                    log("торренты пользователя " + u.Login + " удалены");
                    break;
                }
                case "forget":
                {
                    var u = accounts.Get(login);
                    if (u == null) { err = "нет такого пользователя"; break; }
                    RemoveTorrent(o.Str("hash"), u, "");
                    break;
                }
                case "torrents":
                {
                    var u = accounts.Get(login);
                    if (u == null) return Error(s, req, 404, "нет такого пользователя");
                    var w = new JsonWriter().Arr();
                    foreach (var t in accounts.OwnList(u).OrderByDescending(x => x.Timestamp))
                    {
                        var e = engine.Get(t.Hash);
                        w.Obj().Prop("hash", t.Hash).Prop("title", string.IsNullOrEmpty(t.Title) ? (e != null ? e.Title : t.Hash) : t.Title)
                            .Prop("timestamp", t.Timestamp).Prop("size", e != null ? e.Size : 0).Prop("active", e != null && e.Active)
                            .Prop("poster", e != null ? e.Poster : "").EndObj();
                    }
                    return SendJson(s, req, 200, w.EndArr().ToString());
                }
                default:
                    return Error(s, req, 400, "unknown action: " + action);
            }
            return err != null ? Error(s, req, 400, err) : Ok(s, req);
        }

        bool StreamsList(Stream s, Request req)
        {
            long now = Accounts.UnixNow();
            var w = new JsonWriter().Obj().Prop("viewers", Streams.Viewers()).Key("streams").Arr();
            foreach (var x in Streams.Snapshot())
                w.Obj().Prop("id", x.Id).Prop("who", x.Who).Prop("login", x.Login ?? "").Prop("hash", x.Hash).Prop("title", x.Title ?? "")
                    .Prop("file", x.Path.Substring(x.Path.LastIndexOf('/') + 1)).Prop("file_id", x.FileId).Prop("ip", x.Ip)
                    .Prop("seconds", now - x.Started).Prop("from", x.From).Prop("sent", x.Sent).EndObj();
            return SendJson(s, req, 200, w.EndArr().EndObj().ToString());
        }

        bool Master(Stream s, Request req)
        {
            if (req.Method == "POST")
            {
                var o = Body(req);
                if (o.Str("action") == "new_node_key") accounts.NewNodeKey();
                else
                {
                    string err = accounts.SetMaster(o);
                    if (err != null) return Error(s, req, 400, err);
                    log("балансировка: " + (accounts.MasterEnabled ? "мастер, здесь до " + accounts.LocalMax + " зрителей, узлов " + accounts.Nodes.Count : "выключена"));
                }
            }
            var states = Balancer.States().ToDictionary(x => x.Config.Id);
            var w = new JsonWriter().Obj().Prop("enabled", accounts.MasterEnabled).Prop("local_max", accounts.LocalMax)
                .Prop("viewers", Streams.Viewers()).Prop("node_key", accounts.NodeKey).Key("nodes").Arr();
            foreach (var n in accounts.NodeList())
            {
                w.Obj().Prop("id", n.Id).Prop("name", n.Name).Prop("url", n.Url).Prop("key", n.Key).Prop("max", n.Max).Prop("enabled", n.Enabled);
                if (states.TryGetValue(n.Id, out var st))
                    w.Prop("online", st.Online).Prop("error", st.Error).Prop("version", st.Version).Prop("node_viewers", st.Viewers)
                        .Prop("streams", st.Streams).Prop("pending", st.Pending).Prop("checked_at", st.CheckedAt).Prop("latency_ms", st.LatencyMs);
                w.EndObj();
            }
            return SendJson(s, req, 200, w.EndArr().EndObj().ToString());
        }
    }
}
