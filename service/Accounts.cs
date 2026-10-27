// База MonoServer — monoserver.db в данных службы (LiteDB): пользователи, сессии
// веб-интерфейса, торренты каждого пользователя, трафик, доступ и балансировка.
// LiteDB — встраиваемая база на чистом C# с журналом и транзакциями: после
// сбоя посреди записи она восстанавливается сама. SQLite и прочие тянут
// нативный код, а на Tizen 5.5 и в Mono из Termux своё нативное не загрузить;
// MariaDB и подобные — отдельный сервер. Всё держим и в памяти (запросов
// много, пользователей мало), в базу пишется только изменённое.
//
// Пользователь admin — служебный, для веб-интерфейса. Пароля у него сначала нет:
// при запуске служба пишет в журнал код первого входа, с ним на странице задаётся
// пароль. Остальные пользователи — для потоков и API (TorrStream, Lampa): Basic,
// как у TorrServer, Bearer, cookie или ?token= в ссылке на поток.
//
// Торренты: движок (TorrEngine) один на всех — одна раздача качается один раз,
// у каждого пользователя свой список (название, data, категория) поверх неё.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiteDB;

namespace TorrStreamTorrServer
{
    public class UserTorrent
    {
        public string Hash, Link, Title = "", Data = "", Category = "";
        public long Timestamp;
        // Что это за фильм или сериал: id в TMDB (тип — category: movie / tv) и сезоны
        // в раздаче. Их присылает TorrStream при добавлении — по ним он и находит
        // карточку и серии, а не разбирает название (так приходится с TorrServer)
        public long TmdbId;
        public int[] Seasons = new int[0];
    }

    /// <summary>tmdb_id и seasons из запроса: числом или строкой, сезоны — списком или «1,2,5-7»</summary>
    public static class TorrMeta
    {
        /// <summary>id TMDB; 0 — не прислали или не число</summary>
        public static long TmdbId(object v)
        {
            if (v is double d) return d >= 1 && d < 1e12 ? (long)d : 0;
            if (v is string s && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0) return n;
            return 0;
        }

        /// <summary>Сезоны по возрастанию без повторов (0 — «спецвыпуски»); null — не прислали</summary>
        public static int[] Seasons(object v)
        {
            if (v == null) return null;
            var set = new SortedSet<int>();
            void Add(long a, long b)
            {
                if (a > b) { long t = a; a = b; b = t; }
                for (long i = Math.Max(0, a); i <= Math.Min(b, 999) && set.Count < 1000; i++) set.Add((int)i);
            }
            if (v is List<object> list)
            {
                foreach (var x in list)
                {
                    if (x is double d) Add((long)d, (long)d);
                    else if (x is string xs && long.TryParse(xs.Trim(), out long n)) Add(n, n);
                }
            }
            else if (v is double one) Add((long)one, (long)one);
            else if (v is string s)
            {
                foreach (var part in s.Split(','))
                {
                    var p = part.Trim();
                    int dash = p.IndexOf('-', 1 < p.Length ? 1 : 0);
                    if (dash > 0 && long.TryParse(p.Substring(0, dash).Trim(), out long a) && long.TryParse(p.Substring(dash + 1).Trim(), out long b)) Add(a, b);
                    else if (long.TryParse(p, out long n)) Add(n, n);
                }
            }
            else return null;
            return set.ToArray();
        }
    }

    public class TrafficCounter
    {
        public long Total;
        // «yyyy-MM-dd» (UTC) → байт
        public readonly SortedDictionary<string, long> Days = new SortedDictionary<string, long>(StringComparer.Ordinal);
        // Дни, изменённые с последней записи в базу
        public readonly HashSet<string> Dirty = new HashSet<string>(StringComparer.Ordinal);

        public void Add(string day, long n)
        {
            Total += n;
            Days.TryGetValue(day, out long d);
            Days[day] = d + n;
            Dirty.Add(day);
        }

        public long Since(string day) { return Days.Where(kv => string.CompareOrdinal(kv.Key, day) >= 0).Sum(kv => kv.Value); }
        public long On(string day) { return Days.TryGetValue(day, out long n) ? n : 0; }

        public void Prune(string before)
        {
            foreach (var k in Days.Keys.Where(k => string.CompareOrdinal(k, before) < 0).ToList()) Days.Remove(k);
        }
    }

    public class User
    {
        public const string AdminLogin = "admin";

        public string Login;
        public string Role = "user";        // admin | user
        public string Salt = "", Hash = "";
        public int Iterations;
        public bool Blocked;
        public long Expires;                // unix-время UTC, 0 — бессрочно
        public long Created;
        public string Note = "";
        // Постоянный ключ пользователя для ссылок на поток (?token=): плеерам
        // заголовок не передать. Сменить — старые ссылки перестанут работать
        public string Token = "";
        public long Stamp;                  // меняется с паролем и блокировкой — сброс кэша входа
        public readonly Dictionary<string, UserTorrent> Torrents = new Dictionary<string, UserTorrent>();
        public readonly TrafficCounter Traffic = new TrafficCounter();

        public bool IsAdmin => Role == "admin";
        public bool HasPassword => Hash.Length > 0;
        public bool Expired => Expires > 0 && Expires <= Accounts.UnixNow();

        /// <summary>null — можно; иначе почему нельзя</summary>
        public string Denied()
        {
            if (Blocked) return "учётная запись заблокирована";
            if (Expired) return "срок доступа истёк";
            return null;
        }
    }

    public class Session
    {
        public string Token, Login, Ip;
        public long Created, Expires;
    }

    /// <summary>Узел балансировки: другой MonoServer, куда мастер отправляет зрителей</summary>
    public class NodeConfig
    {
        public string Id, Name = "", Url = "", Key = "";
        public int Max = 5;                 // зрителей на узле, дальше — следующий
        public bool Enabled = true;
    }

    public class Accounts
    {
        const int PasswordIterations = 20000;
        const int SessionDays = 30;
        const int KeepTrafficDays = 400;
        const string TotalDay = "total";

        readonly string path;
        readonly Action<string> log;
        readonly object sync = new object();
        readonly Dictionary<string, User> users = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        // Трафик службы: отдано клиентам по HTTP, BitTorrent принято и отдано
        readonly TrafficCounter httpOut = new TrafficCounter(), btDown = new TrafficCounter(), btUp = new TrafficCounter();
        // Зрители, которых мастер прислал на этот узел: логин → трафик
        readonly Dictionary<string, TrafficCounter> remote = new Dictionary<string, TrafficCounter>(StringComparer.OrdinalIgnoreCase);
        // Пришло с узлов (мастер): узел → логин → сколько узел уже сообщал
        readonly Dictionary<string, Dictionary<string, long>> nodeSeen = new Dictionary<string, Dictionary<string, long>>();
        readonly HashSet<string> nodeSeenDirty = new HashSet<string>();
        LiteDatabase db;
        Timer flushTimer;
        string prunedDay;

        // Доступ
        public bool RequireAuth;            // потоки и API торрентов — только с входом
        public bool LanWithoutAuth;         // из домашней сети — без входа (со списком admin)
        // Мастер-сервер
        public bool MasterEnabled;
        public int LocalMax = 5;            // зрителей здесь, дальше — на узлы; 0 — всех на узлы
        public readonly List<NodeConfig> Nodes = new List<NodeConfig>();
        // Узел: ключ, которым мастер подписывает перенаправления и опрашивает состояние
        public string NodeKey = "";

        // Код первого входа: пока у admin нет пароля
        string setupCode;
        int setupFails;

        public Accounts(string dataDir, Action<string> log)
        {
            path = Path.Combine(dataDir, "monoserver.db");
            this.log = log ?? (_ => { });
        }

        public static long UnixNow() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
        public static string Today() { return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static string DayAgo(int days) { return DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        /// <summary>Открыть базу и прочитать всё в память; admin без пароля — если его нет</summary>
        public void Load()
        {
            // Не загрузилась сама LiteDB (сборка, рантайм) — служба работает, но без памяти между запусками
            try { db = Open(); }
            catch (Exception e) { log("monoserver.db: LiteDB недоступна — " + e.Message + "; пользователи и трафик — только в памяти"); db = null; }
            if (db != null)
            {
                try { lock (sync) Read(); }
                catch (Exception e)
                {
                    log("monoserver.db: не прочиталась — " + e.Message);
                    db = Reset();
                    lock (sync) Read();
                }
            }
            lock (sync)
            {
                if (!users.TryGetValue(User.AdminLogin, out var admin))
                {
                    admin = NewUser(User.AdminLogin, "admin");
                    users[admin.Login] = admin;
                }
                admin.Role = "admin";
                admin.Blocked = false;
                admin.Expires = 0;
                PutUser(admin);
                if (string.IsNullOrEmpty(NodeKey)) { NodeKey = RandomHex(16); PutConfig(); }
            }
            flushTimer = new Timer(_ => Flush(), null, 15000, 15000);
            NewSetupCode();
        }

        LiteDatabase Open()
        {
            try { return new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct }); }
            catch (IOException e)
            {
                // Занята (другой экземпляр службы) или нет доступа — не трогаем
                log("monoserver.db: не открылась — " + e.Message + "; пользователи и трафик — только в памяти");
                return null;
            }
            catch (Exception e)
            {
                log("monoserver.db: не открылась — " + e.Message);
                return Reset();
            }
        }

        // Битая база — не затираем: в сторону, рядом с датой, и начинаем заново
        LiteDatabase Reset()
        {
            try { db?.Dispose(); } catch { }
            try
            {
                string bad = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bad";
                if (File.Exists(path)) File.Move(path, bad);
                string journal = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-log.db");
                if (File.Exists(journal)) File.Move(journal, bad + "-log");
                log("monoserver.db отложена в " + Path.GetFileName(bad) + ", начинаю новую");
                return new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
            }
            catch (Exception e)
            {
                // Без базы служба работает, но ничего не запомнит до перезапуска
                log("monoserver.db: база недоступна, пользователи и трафик — только в памяти: " + e.Message);
                return null;
            }
        }

        User NewUser(string login, string role)
        {
            return new User { Login = login, Role = role, Created = UnixNow(), Token = RandomHex(16), Stamp = UnixNow() };
        }

        // ==================== база ====================

        static string S(BsonDocument d, string k) { var v = d[k]; return v != null && v.IsString ? v.AsString : ""; }
        static long L(BsonDocument d, string k) { var v = d[k]; return v != null && v.IsNumber ? Convert.ToInt64(v.RawValue, CultureInfo.InvariantCulture) : 0; }
        static bool B(BsonDocument d, string k, bool def) { var v = d[k]; return v != null && v.IsBoolean ? v.AsBoolean : def; }

        // Под sync
        void Read()
        {
            users.Clear(); sessions.Clear(); remote.Clear(); nodeSeen.Clear(); Nodes.Clear();
            foreach (var d in db.GetCollection("users").FindAll())
            {
                var u = new User
                {
                    Login = S(d, "login"),
                    Role = S(d, "role") == "admin" ? "admin" : "user",
                    Salt = S(d, "salt"),
                    Hash = S(d, "hash"),
                    Iterations = (int)L(d, "iterations"),
                    Blocked = B(d, "blocked", false),
                    Expires = L(d, "expires"),
                    Created = L(d, "created"),
                    Note = S(d, "note"),
                    Token = S(d, "token"),
                    Stamp = L(d, "stamp"),
                };
                if (u.Login.Length == 0) continue;
                if (u.Iterations <= 0) u.Iterations = PasswordIterations;
                if (u.Token.Length == 0) u.Token = RandomHex(16);
                users[u.Login] = u;
            }
            foreach (var d in db.GetCollection("torrents").FindAll())
            {
                if (!users.TryGetValue(S(d, "login"), out var u)) continue;
                var t = new UserTorrent { Hash = S(d, "hash").ToLowerInvariant(), Link = S(d, "link"), Title = S(d, "title"), Data = S(d, "data"), Category = S(d, "category"), Timestamp = L(d, "timestamp"), TmdbId = L(d, "tmdb_id") };
                var sv = d["seasons"];
                if (sv != null && sv.IsArray) t.Seasons = sv.AsArray.Where(x => x.IsNumber).Select(x => Convert.ToInt32(x.RawValue, CultureInfo.InvariantCulture)).ToArray();
                if (t.Hash.Length > 0) u.Torrents[t.Hash] = t;
            }
            long now = UnixNow();
            foreach (var d in db.GetCollection("sessions").FindAll())
            {
                var s = new Session { Token = d["_id"].AsString, Login = S(d, "login"), Ip = S(d, "ip"), Created = L(d, "created"), Expires = L(d, "expires") };
                if (s.Expires > now && users.ContainsKey(s.Login)) sessions[s.Token] = s;
            }
            foreach (var d in db.GetCollection("traffic").FindAll())
            {
                var c = Counter(S(d, "scope"), true);
                if (c == null) continue;
                string day = S(d, "day");
                long n = L(d, "bytes");
                if (day == TotalDay) c.Total = n;
                else c.Days[day] = n;
            }
            foreach (var d in db.GetCollection("node_seen").FindAll())
            {
                string nodeId = S(d, "node");
                if (!nodeSeen.TryGetValue(nodeId, out var map)) nodeSeen[nodeId] = map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                map[S(d, "login")] = L(d, "bytes");
            }
            var cfg = db.GetCollection("config");
            var access = cfg.FindById("access");
            if (access != null)
            {
                RequireAuth = B(access, "require_auth", false);
                LanWithoutAuth = B(access, "lan_without_auth", false);
            }
            var master = cfg.FindById("master");
            if (master != null)
            {
                MasterEnabled = B(master, "enabled", false);
                LocalMax = (int)Math.Max(0, Math.Min(10000, master["local_max"].IsNumber ? L(master, "local_max") : 5));
                var nodes = master["nodes"];
                if (nodes != null && nodes.IsArray)
                    foreach (var v in nodes.AsArray.Where(x => x.IsDocument))
                    {
                        var n = v.AsDocument;
                        Nodes.Add(new NodeConfig { Id = S(n, "id"), Name = S(n, "name"), Url = S(n, "url"), Key = S(n, "key"), Max = (int)L(n, "max"), Enabled = B(n, "enabled", true) });
                    }
            }
            var node = cfg.FindById("node");
            if (node != null) NodeKey = S(node, "key");
        }

        /// <summary>Счётчик по имени в базе: http, bt_down, bt_up, u:логин, r:логин</summary>
        TrafficCounter Counter(string scope, bool create)
        {
            switch (scope)
            {
                case "http": return httpOut;
                case "bt_down": return btDown;
                case "bt_up": return btUp;
            }
            if (scope.StartsWith("u:")) return users.TryGetValue(scope.Substring(2), out var u) ? u.Traffic : null;
            if (scope.StartsWith("r:"))
            {
                string login = scope.Substring(2);
                if (!remote.TryGetValue(login, out var c) && create) remote[login] = c = new TrafficCounter();
                return c;
            }
            return null;
        }

        // Запись — под sync. Ошибка записи не роняет запрос: в памяти уже всё
        // поменялось, в журнале будет видно
        void Db(string what, Action<LiteDatabase> write)
        {
            if (db == null) return;
            try { write(db); }
            catch (Exception e) { log("monoserver.db, " + what + ": " + e.Message); }
        }

        static string Key(string login) { return login.ToLowerInvariant(); }

        void PutUser(User u)
        {
            Db("пользователь", d => d.GetCollection("users").Upsert(new BsonDocument
            {
                ["_id"] = Key(u.Login), ["login"] = u.Login, ["role"] = u.Role, ["salt"] = u.Salt, ["hash"] = u.Hash,
                ["iterations"] = u.Iterations, ["blocked"] = u.Blocked, ["expires"] = u.Expires, ["created"] = u.Created,
                ["note"] = u.Note, ["token"] = u.Token, ["stamp"] = u.Stamp,
            }));
        }

        void PutTorrent(User u, UserTorrent t)
        {
            Db("торрент", d => d.GetCollection("torrents").Upsert(new BsonDocument
            {
                ["_id"] = Key(u.Login) + "|" + t.Hash, ["login"] = u.Login, ["hash"] = t.Hash, ["link"] = t.Link ?? "",
                ["title"] = t.Title ?? "", ["data"] = t.Data ?? "", ["category"] = t.Category ?? "", ["timestamp"] = t.Timestamp,
                ["tmdb_id"] = t.TmdbId, ["seasons"] = new BsonArray((t.Seasons ?? new int[0]).Select(x => new BsonValue(x))),
            }));
        }

        void DelTorrent(User u, string hash)
        {
            Db("торрент", d => d.GetCollection("torrents").Delete(Key(u.Login) + "|" + hash));
        }

        void PutSession(Session s)
        {
            Db("сессия", d => d.GetCollection("sessions").Upsert(new BsonDocument
            {
                ["_id"] = s.Token, ["login"] = s.Login, ["ip"] = s.Ip, ["created"] = s.Created, ["expires"] = s.Expires,
            }));
        }

        void PutConfig()
        {
            Db("настройки", d =>
            {
                var cfg = d.GetCollection("config");
                cfg.Upsert(new BsonDocument { ["_id"] = "access", ["require_auth"] = RequireAuth, ["lan_without_auth"] = LanWithoutAuth });
                var nodes = new BsonArray();
                foreach (var n in Nodes)
                    nodes.Add(new BsonDocument { ["id"] = n.Id, ["name"] = n.Name, ["url"] = n.Url, ["key"] = n.Key, ["max"] = n.Max, ["enabled"] = n.Enabled });
                cfg.Upsert(new BsonDocument { ["_id"] = "master", ["enabled"] = MasterEnabled, ["local_max"] = LocalMax, ["nodes"] = nodes });
                cfg.Upsert(new BsonDocument { ["_id"] = "node", ["key"] = NodeKey });
            });
        }

        /// <summary>Трафик — в базу раз в 15 с (и при остановке); раз в сутки — чистка старого</summary>
        public void Flush()
        {
            lock (sync)
            {
                if (db == null) return;
                Db("трафик", d =>
                {
                    var col = d.GetCollection("traffic");
                    d.BeginTrans();
                    try
                    {
                        FlushCounter(col, "http", httpOut);
                        FlushCounter(col, "bt_down", btDown);
                        FlushCounter(col, "bt_up", btUp);
                        foreach (var u in users.Values) FlushCounter(col, "u:" + u.Login, u.Traffic);
                        foreach (var kv in remote) FlushCounter(col, "r:" + kv.Key, kv.Value);
                        var seen = d.GetCollection("node_seen");
                        foreach (var node in nodeSeenDirty)
                        {
                            seen.DeleteMany(Query.EQ("node", node));
                            if (nodeSeen.TryGetValue(node, out var map))
                                foreach (var kv in map)
                                    seen.Upsert(new BsonDocument { ["_id"] = node + "|" + Key(kv.Key), ["node"] = node, ["login"] = kv.Key, ["bytes"] = kv.Value });
                        }
                        nodeSeenDirty.Clear();
                        d.Commit();
                    }
                    catch { d.Rollback(); throw; }
                });
                string today = Today();
                if (prunedDay != today)
                {
                    prunedDay = today;
                    Prune();
                }
            }
        }

        static void FlushCounter(ILiteCollection<BsonDocument> col, string scope, TrafficCounter c)
        {
            if (c.Dirty.Count == 0) return;
            foreach (var day in c.Dirty)
                col.Upsert(new BsonDocument { ["_id"] = scope + "|" + day, ["scope"] = scope, ["day"] = day, ["bytes"] = c.On(day) });
            col.Upsert(new BsonDocument { ["_id"] = scope + "|" + TotalDay, ["scope"] = scope, ["day"] = TotalDay, ["bytes"] = c.Total });
            c.Dirty.Clear();
        }

        // Под sync: просроченные сессии, трафик старше KeepTrafficDays
        void Prune()
        {
            long now = UnixNow();
            foreach (var t in sessions.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) sessions.Remove(t);
            string before = DayAgo(KeepTrafficDays);
            foreach (var u in users.Values) u.Traffic.Prune(before);
            foreach (var c in remote.Values) c.Prune(before);
            httpOut.Prune(before); btDown.Prune(before); btUp.Prune(before);
            Db("чистка", d =>
            {
                d.GetCollection("sessions").DeleteMany(Query.LTE("expires", now));
                // «total» больше любой даты — под чистку не попадёт
                d.GetCollection("traffic").DeleteMany(Query.LT("day", before));
                d.Checkpoint();
            });
        }

        public void Close()
        {
            try { flushTimer?.Dispose(); } catch { }
            Flush();
            lock (sync)
            {
                try { db?.Dispose(); } catch { }
                db = null;
            }
        }

        // ==================== пароли и ключи ====================

        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            lock (Rng) Rng.GetBytes(b);
            return Hex(b);
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static byte[] FromHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        /// <summary>Сравнение за одно и то же время — по нему не подобрать ключ</summary>
        public static bool SameText(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // PBKDF2 (HMAC-SHA1 — другого в netstandard2.0 нет). Проверка дорогая
        // (десятки мс на телевизоре), поэтому успешный вход HttpApi помнит
        static string HashPassword(string password, string salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), FromHex(salt), iterations))
                return Hex(kdf.GetBytes(32));
        }

        static long stampSeq;

        static void SetPassword(User u, string password)
        {
            u.Salt = RandomHex(16);
            u.Iterations = PasswordIterations;
            u.Hash = HashPassword(password, u.Salt, u.Iterations);
            u.Stamp = UnixNow() + Interlocked.Increment(ref stampSeq);
        }

        public static string CheckPassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 6) return "пароль — не короче 6 символов";
            if (password.Length > 200) return "слишком длинный пароль";
            return null;
        }

        public static string CheckLogin(string login)
        {
            if (string.IsNullOrEmpty(login)) return "нужен логин";
            if (login.Length > 64) return "логин — не длиннее 64 символов";
            foreach (char c in login)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return "логин — буквы, цифры, «_», «-», «.»";
            return null;
        }

        /// <summary>Логин и пароль → пользователь (не проверяя блокировку и срок) или null</summary>
        public User Verify(string login, string password)
        {
            User u;
            string salt, hash;
            int iter;
            lock (sync)
            {
                if (login == null || !users.TryGetValue(login, out u) || !u.HasPassword) return null;
                salt = u.Salt; hash = u.Hash; iter = u.Iterations;
            }
            return SameText(HashPassword(password ?? "", salt, iter), hash) ? u : null;
        }

        /// <summary>Ключ пользователя (?token=) или сессия</summary>
        public User ByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (sync)
            {
                foreach (var u in users.Values) if (SameText(u.Token, token)) return u;
                if (sessions.TryGetValue(token, out var s) && s.Expires > UnixNow() && users.TryGetValue(s.Login, out var su)) return su;
            }
            return null;
        }

        public User Get(string login)
        {
            if (login == null) return null;
            lock (sync) return users.TryGetValue(login, out var u) ? u : null;
        }

        public User Admin => Get(User.AdminLogin);

        public List<User> Users()
        {
            lock (sync) return users.Values.OrderBy(u => u.IsAdmin ? 0 : 1).ThenBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ==================== первый вход ====================

        public bool NeedsSetup { get { var a = Admin; return a == null || !a.HasPassword; } }

        void NewSetupCode()
        {
            lock (sync)
            {
                if (!NeedsSetup) { setupCode = null; return; }
                // 8 цифр, группами: его переписывают с экрана журнала
                var b = new byte[8];
                lock (Rng) Rng.GetBytes(b);
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) { if (i == 4) sb.Append('-'); sb.Append((char)('0' + b[i] % 10)); }
                setupCode = sb.ToString();
                setupFails = 0;
            }
            log("Первый вход в веб-интерфейс: код " + setupCode + " — введите его на странице службы и задайте пароль пользователя admin");
        }

        /// <summary>Код первого входа верен. Пять неверных подряд — новый код в журнале</summary>
        public bool CheckSetupCode(string code)
        {
            bool renew = false;
            lock (sync)
            {
                if (!NeedsSetup || setupCode == null) return false;
                string want = setupCode.Replace("-", ""), got = (code ?? "").Replace("-", "").Replace(" ", "").Trim();
                if (SameText(want, got)) return true;
                if (++setupFails >= 5) renew = true;
            }
            if (renew) NewSetupCode();
            return false;
        }

        public string CompleteSetup(string code, string password)
        {
            string bad = CheckPassword(password);
            if (bad != null) return bad;
            if (!CheckSetupCode(code)) return "неверный код — посмотрите журнал службы";
            lock (sync)
            {
                if (!NeedsSetup) return "пароль уже задан";
                SetPassword(Admin, password);
                setupCode = null;
                PutUser(Admin);
            }
            log("Пароль admin задан — веб-интерфейс открыт только с ним");
            return null;
        }

        // ==================== сессии ====================

        public Session NewSession(User u, string ip)
        {
            var s = new Session { Token = RandomHex(32), Login = u.Login, Ip = ip ?? "", Created = UnixNow(), Expires = UnixNow() + SessionDays * 86400L };
            lock (sync) { sessions[s.Token] = s; PutSession(s); }
            return s;
        }

        public User BySession(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (sync)
                return sessions.TryGetValue(token, out var s) && s.Expires > UnixNow() && users.TryGetValue(s.Login, out var u) ? u : null;
        }

        public void EndSession(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (sync)
                if (sessions.Remove(token)) Db("сессия", d => d.GetCollection("sessions").Delete(token));
        }

        // Под sync
        void EndSessionsOf(string login)
        {
            foreach (var t in sessions.Where(kv => string.Equals(kv.Value.Login, login, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) sessions.Remove(t);
            Db("сессии", d => d.GetCollection("sessions").DeleteMany(Query.EQ("login", login)));
        }

        // ==================== пользователи ====================

        /// <summary>Срок: period — days | months | forever, от from (unix) или от сейчас</summary>
        public static long ExpiryFrom(long from, string period, int amount)
        {
            if (period == "forever" || amount <= 0) return 0;
            var start = DateTimeOffset.FromUnixTimeSeconds(Math.Max(from, UnixNow()));
            return (period == "months" ? start.AddMonths(Math.Min(amount, 1200)) : start.AddDays(Math.Min(amount, 36500))).ToUnixTimeSeconds();
        }

        public string CreateUser(string login, string password, string period, int amount, string note)
        {
            login = (login ?? "").Trim();
            string bad = CheckLogin(login) ?? CheckPassword(password);
            if (bad != null) return bad;
            lock (sync)
            {
                if (users.ContainsKey(login)) return "пользователь «" + login + "» уже есть";
                var u = NewUser(login, "user");
                SetPassword(u, password);
                u.Expires = ExpiryFrom(0, period, amount);
                u.Note = note ?? "";
                users[u.Login] = u;
                PutUser(u);
            }
            log("пользователь " + login + " создан");
            return null;
        }

        /// <summary>
        /// Изменить пользователя: o — password, blocked, note, period+amount
        /// (extend — продлить от текущего срока, иначе срок от сегодня), new_token
        /// </summary>
        public string UpdateUser(string login, Dictionary<string, object> o)
        {
            lock (sync)
            {
                if (!users.TryGetValue(login ?? "", out var u)) return "нет такого пользователя";
                string pass = o.Str("password");
                if (!string.IsNullOrEmpty(pass))
                {
                    string bad = CheckPassword(pass);
                    if (bad != null) return bad;
                    SetPassword(u, pass);
                    // Пароль админа меняет сам админ из веб-интерфейса — его сессию HttpApi выдаст заново
                    EndSessionsOf(u.Login);
                }
                if (!u.IsAdmin)
                {
                    var blocked = o.Bool("blocked");
                    if (blocked.HasValue && blocked.Value != u.Blocked)
                    {
                        u.Blocked = blocked.Value;
                        u.Stamp++;
                        if (u.Blocked) EndSessionsOf(u.Login);
                        log("пользователь " + u.Login + (u.Blocked ? " заблокирован" : " разблокирован"));
                    }
                    string period = o.Str("period");
                    if (period != null)
                    {
                        int amount = (int)(o.Num("amount") ?? 0);
                        bool extend = o.Bool("extend") ?? false;
                        u.Expires = ExpiryFrom(extend && u.Expires > 0 ? u.Expires : 0, period, amount);
                        u.Stamp++;
                    }
                }
                if (o.Str("note") != null) u.Note = o.Str("note");
                if (o.Bool("new_token") == true) { u.Token = RandomHex(16); u.Stamp++; }
                PutUser(u);
            }
            return null;
        }

        /// <summary>Удалить пользователя; его торренты — вызывающему (кому ещё они нужны)</summary>
        public List<string> DeleteUser(string login, out string error)
        {
            error = null;
            lock (sync)
            {
                if (!users.TryGetValue(login ?? "", out var u)) { error = "нет такого пользователя"; return null; }
                if (u.IsAdmin) { error = "служебного admin удалить нельзя"; return null; }
                users.Remove(u.Login);
                EndSessionsOf(u.Login);
                Db("пользователь", d =>
                {
                    d.GetCollection("users").Delete(Key(u.Login));
                    d.GetCollection("torrents").DeleteMany(Query.EQ("login", u.Login));
                    d.GetCollection("traffic").DeleteMany(Query.EQ("scope", "u:" + u.Login));
                });
                log("пользователь " + u.Login + " удалён");
                return u.Torrents.Keys.ToList();
            }
        }

        // ==================== торренты пользователей ====================

        public UserTorrent Own(User u, string hash)
        {
            if (u == null || hash == null) return null;
            lock (sync) return u.Torrents.TryGetValue(hash.ToLowerInvariant(), out var t) ? t : null;
        }

        public List<UserTorrent> OwnList(User u)
        {
            lock (sync) return u.Torrents.Values.ToList();
        }

        /// <summary>
        /// Записать торрент в список пользователя (добавить или обновить). tmdbId 0 и
        /// seasons null — оставить как были (новому — как у торрента в движке)
        /// </summary>
        public void Keep(User u, TorrEntry e, string title, string data, string category, long tmdbId = 0, int[] seasons = null)
        {
            lock (sync)
            {
                if (!u.Torrents.TryGetValue(e.Hash, out var t))
                {
                    t = new UserTorrent { Hash = e.Hash, Timestamp = UnixNow(), Title = e.Title, Data = "", Category = e.Category, TmdbId = e.TmdbId, Seasons = e.Seasons };
                    u.Torrents[e.Hash] = t;
                }
                t.Link = e.Link;
                if (!string.IsNullOrEmpty(title)) t.Title = title;
                if (!string.IsNullOrEmpty(data)) t.Data = data;
                if (!string.IsNullOrEmpty(category)) t.Category = category;
                if (tmdbId > 0) t.TmdbId = tmdbId;
                if (seasons != null) t.Seasons = seasons;
                PutTorrent(u, t);
            }
        }

        /// <summary>Поменять название/data/категорию/TMDB в списке пользователя (null и 0 — не трогать)</summary>
        public bool SetMeta(User u, string hash, string title, string data, string category, long tmdbId = 0, int[] seasons = null)
        {
            lock (sync)
            {
                if (!u.Torrents.TryGetValue((hash ?? "").ToLowerInvariant(), out var t)) return false;
                if (title != null) t.Title = title;
                if (data != null) t.Data = data;
                if (category != null) t.Category = category;
                if (tmdbId > 0) t.TmdbId = tmdbId;
                if (seasons != null) t.Seasons = seasons;
                PutTorrent(u, t);
                return true;
            }
        }

        public bool Forget(User u, string hash)
        {
            hash = (hash ?? "").ToLowerInvariant();
            lock (sync)
            {
                bool had = u.Torrents.Remove(hash);
                if (had) DelTorrent(u, hash);
                return had;
            }
        }

        /// <summary>Кто держит торрент в своём списке</summary>
        public List<string> Owners(string hash)
        {
            lock (sync) return users.Values.Where(u => u.Torrents.ContainsKey(hash)).Select(u => u.Login).ToList();
        }

        public Dictionary<string, List<string>> OwnersAll()
        {
            var map = new Dictionary<string, List<string>>();
            lock (sync)
                foreach (var u in users.Values)
                    foreach (var h in u.Torrents.Keys)
                    {
                        if (!map.TryGetValue(h, out var l)) map[h] = l = new List<string>();
                        l.Add(u.Login);
                    }
            return map;
        }

        // ==================== трафик ====================

        /// <summary>Отдано клиенту по HTTP: login — пользователь, remoteLogin — зритель от мастера</summary>
        public void AddHttp(string login, string remoteLogin, long bytes)
        {
            if (bytes <= 0) return;
            string day = Today();
            lock (sync)
            {
                httpOut.Add(day, bytes);
                if (remoteLogin != null) Counter("r:" + remoteLogin, true).Add(day, bytes);
                else if (login != null && users.TryGetValue(login, out var u)) u.Traffic.Add(day, bytes);
            }
        }

        public void AddBt(long down, long up)
        {
            if (down <= 0 && up <= 0) return;
            string day = Today();
            lock (sync)
            {
                if (down > 0) btDown.Add(day, down);
                if (up > 0) btUp.Add(day, up);
            }
        }

        /// <summary>
        /// Мастер: узел сообщил, сколько всего отдал каждому зрителю — разницу с
        /// прошлым разом добавляем пользователю (база узла начата заново — всё заново)
        /// </summary>
        public void AddFromNode(string nodeId, Dictionary<string, long> totals)
        {
            string day = Today();
            lock (sync)
            {
                if (!nodeSeen.TryGetValue(nodeId, out var seen)) nodeSeen[nodeId] = seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in totals)
                {
                    seen.TryGetValue(kv.Key, out long was);
                    if (kv.Value == was) continue;
                    long delta = kv.Value > was ? kv.Value - was : kv.Value;
                    seen[kv.Key] = kv.Value;
                    nodeSeenDirty.Add(nodeId);
                    if (delta > 0 && users.TryGetValue(kv.Key, out var u)) u.Traffic.Add(day, delta);
                }
            }
        }

        public Dictionary<string, long> RemoteTotals()
        {
            lock (sync) return remote.ToDictionary(kv => kv.Key, kv => kv.Value.Total, StringComparer.OrdinalIgnoreCase);
        }

        public long HttpTotal { get { lock (sync) return httpOut.Total; } }

        /// <summary>Сводка трафика: сервер по дням и пользователи (для веб-интерфейса)</summary>
        public void WriteTraffic(JsonWriter w, int days)
        {
            string today = Today(), month = DayAgo(29);
            lock (sync)
            {
                w.Obj().Prop("today", today).Prop("days", days);
                w.Key("server").Obj()
                    .Prop("http_total", httpOut.Total).Prop("http_today", httpOut.On(today)).Prop("http_30d", httpOut.Since(month))
                    .Prop("bt_down_total", btDown.Total).Prop("bt_down_today", btDown.On(today)).Prop("bt_down_30d", btDown.Since(month))
                    .Prop("bt_up_total", btUp.Total).Prop("bt_up_today", btUp.On(today)).Prop("bt_up_30d", btUp.Since(month));
                w.Key("by_day").Arr();
                for (int i = days - 1; i >= 0; i--)
                {
                    string d = DayAgo(i);
                    w.Obj().Prop("day", d).Prop("http", httpOut.On(d)).Prop("bt_down", btDown.On(d)).Prop("bt_up", btUp.On(d)).EndObj();
                }
                w.EndArr().EndObj();
                w.Key("users").Arr();
                foreach (var u in users.Values.OrderByDescending(x => x.Traffic.Total))
                {
                    w.Obj().Prop("login", u.Login).Prop("total", u.Traffic.Total).Prop("today", u.Traffic.On(today)).Prop("d30", u.Traffic.Since(month)).Key("by_day").Arr();
                    for (int i = days - 1; i >= 0; i--) w.Val(u.Traffic.On(DayAgo(i)));
                    w.EndArr().EndObj();
                }
                w.EndArr();
                w.Key("remote").Arr();
                foreach (var kv in remote.OrderByDescending(x => x.Value.Total))
                    w.Obj().Prop("login", kv.Key).Prop("total", kv.Value.Total).Prop("today", kv.Value.On(today)).Prop("d30", kv.Value.Since(month)).EndObj();
                w.EndArr();
                w.EndObj();
            }
        }

        // ==================== доступ и балансировка ====================

        public void SetAccess(bool? requireAuth, bool? lanWithoutAuth)
        {
            lock (sync)
            {
                if (requireAuth.HasValue) RequireAuth = requireAuth.Value;
                if (lanWithoutAuth.HasValue) LanWithoutAuth = lanWithoutAuth.Value;
                PutConfig();
            }
            log("доступ: " + (RequireAuth ? "потоки и API — только со входом" : "потоки и API — без входа") + (LanWithoutAuth ? ", домашняя сеть — без входа" : ""));
        }

        public string SetMaster(Dictionary<string, object> o)
        {
            lock (sync)
            {
                List<NodeConfig> list = null;
                if (o.TryGetValue("nodes", out var nv) && nv is List<object> nl)
                {
                    list = new List<NodeConfig>();
                    foreach (var n in nl.OfType<Dictionary<string, object>>())
                    {
                        string url = (n.Str("url") ?? "").Trim().TrimEnd('/');
                        if (url.Length == 0) continue;
                        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return "неверный адрес узла: " + url;
                        string key = (n.Str("key") ?? "").Trim();
                        if (key.Length == 0) return "у узла " + url + " нет ключа";
                        list.Add(new NodeConfig
                        {
                            Id = string.IsNullOrEmpty(n.Str("id")) ? RandomHex(4) : n.Str("id"),
                            Name = (n.Str("name") ?? "").Trim(),
                            Url = url,
                            Key = key,
                            Max = (int)Math.Max(0, Math.Min(10000, n.Num("max") ?? 5)),
                            Enabled = n.Bool("enabled") ?? true,
                        });
                    }
                }
                var en = o.Bool("enabled");
                if (en.HasValue) MasterEnabled = en.Value;
                var lm = o.Num("local_max");
                if (lm.HasValue) LocalMax = (int)Math.Max(0, Math.Min(10000, lm.Value));
                if (list != null)
                {
                    foreach (var gone in Nodes.Where(x => !list.Any(y => y.Id == x.Id)).ToList())
                        if (nodeSeen.Remove(gone.Id)) nodeSeenDirty.Add(gone.Id);
                    Nodes.Clear();
                    Nodes.AddRange(list);
                }
                PutConfig();
            }
            return null;
        }

        public List<NodeConfig> NodeList()
        {
            lock (sync) return Nodes.Select(n => new NodeConfig { Id = n.Id, Name = n.Name, Url = n.Url, Key = n.Key, Max = n.Max, Enabled = n.Enabled }).ToList();
        }

        public void NewNodeKey()
        {
            lock (sync) { NodeKey = RandomHex(16); PutConfig(); }
            log("ключ узла заменён — мастеру нужен новый");
        }
    }
}
