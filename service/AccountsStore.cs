// Где лежит база MonoServer (Accounts.cs держит всё в памяти, хранилище — только
// запись на диск и чтение при запуске):
//   - LiteDB, monoserver.db (AccountsLiteDb.cs) — Linux, Windows, Android: журнал
//     и транзакции, пишется только изменённое;
//   - JSON, monoserver.json (здесь) — Tizen: с 1.0.26 служба на Tizen 4.0
//     (.NET Core 2.0) не запускалась вовсе, а LiteDB в пакет Tizen теперь не
//     входит — загружать нечего. Файл пишется целиком, атомарно, с копией .bak.
// Какое — Accounts.StoreFactory: по умолчанию JSON, pc/ и mono/ ставят LiteDB.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TorrStreamTorrServer
{
    /// <summary>
    /// Хранилище базы MonoServer. Всё, кроме Load и Close, Accounts зовёт под
    /// своей блокировкой (Accounts.Sync); ошибки записи Accounts ловит сам
    /// </summary>
    public interface IAccountsStore
    {
        /// <summary>Имя файла — для журнала</summary>
        string Name { get; }
        /// <summary>Прочитать всё в a (под a.Sync). Бросает — не открылась / не прочиталась</summary>
        void Load(Accounts a);
        void PutUser(User u);
        /// <summary>Пользователь удалён — с его торрентами, сессиями и трафиком</summary>
        void DelUser(User u);
        void PutTorrent(User u, UserTorrent t);
        void DelTorrent(User u, string hash);
        void PutSession(Session s);
        void DelSession(string token);
        void DelSessionsOf(string login);
        void PutConfig(Accounts a);
        /// <summary>Накопленное (трафик, node_seen) — на диск; раз в 15 с и при остановке</summary>
        void Flush(Accounts a);
        /// <summary>Раз в сутки: сессии до now, трафик до дня before (память Accounts уже почистил)</summary>
        void Prune(Accounts a, long now, string before);
        void Close();
    }

    /// <summary>
    /// JSON-файл целиком: изменения пользователей и настроек — через секунду,
    /// трафик — с Flush. Пишется во временный файл и подменяет прежний, прежний
    /// остаётся копией .bak: оборвалась запись — при запуске берётся копия
    /// </summary>
    public class JsonAccountsStore : IAccountsStore
    {
        readonly string path;
        readonly Action<string> log;
        readonly Timer saveTimer;
        Accounts owner;
        volatile bool dirty;

        public JsonAccountsStore(string dataDir, Action<string> log)
        {
            path = Path.Combine(dataDir, "monoserver.json");
            this.log = log ?? (_ => { });
            saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Name => "monoserver.json";

        // Изменение — запись через секунду: подряд идущие правки — одной записью
        void Changed()
        {
            dirty = true;
            try { saveTimer.Change(1000, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        public void PutUser(User u) { Changed(); }
        public void DelUser(User u) { Changed(); }
        public void PutTorrent(User u, UserTorrent t) { Changed(); }
        public void DelTorrent(User u, string hash) { Changed(); }
        public void PutSession(Session s) { Changed(); }
        public void DelSession(string token) { Changed(); }
        public void DelSessionsOf(string login) { Changed(); }
        public void PutConfig(Accounts a) { Changed(); }
        public void Prune(Accounts a, long now, string before) { Changed(); }

        public void Flush(Accounts a)
        {
            // Трафик копится в памяти — счётчики с изменёнными днями
            if (a.Counters().Any(kv => kv.Value.Dirty.Count > 0) || a.NodeSeenDirty.Count > 0) dirty = true;
            if (dirty) Save();
        }

        public void Close()
        {
            try { saveTimer.Dispose(); } catch { }
            if (dirty) Save();
        }

        // ==================== чтение ====================

        public void Load(Accounts a)
        {
            owner = a;
            string tmp = path + ".tmp", bak = path + ".bak";
            Exception first = null;
            // Основной файл; оборвалась запись между подменами — копия
            foreach (var f in new[] { path, bak })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    var root = Json.ParseObject(File.ReadAllText(f));
                    a.ClearAll();
                    Read(a, root);
                    if (f != path) log(Name + ": основной файл не прочитался — взята копия " + Path.GetFileName(f));
                    return;
                }
                catch (Exception e)
                {
                    if (first == null) first = e;
                    log(Name + ": " + Path.GetFileName(f) + " не прочитался — " + e.Message);
                }
            }
            if (first != null)
            {
                // Оба битые — в сторону, начинаем с чистого листа (как LiteDB)
                try { File.Move(path, path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bad"); } catch { }
                a.ClearAll();
                log(Name + ": начинаю новую базу");
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> o, string k)
        {
            return o.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        }

        static List<object> Arr(Dictionary<string, object> o, string k)
        {
            return o.TryGetValue(k, out var v) ? v as List<object> : null;
        }

        static void ReadTraffic(TrafficCounter c, Dictionary<string, object> o)
        {
            if (c == null || o == null) return;
            c.Total = o.Num("total") ?? 0;
            var days = Obj(o, "days");
            if (days != null)
                foreach (var kv in days) c.Days[kv.Key] = days.Num(kv.Key) ?? 0;
        }

        void Read(Accounts a, Dictionary<string, object> root)
        {
            foreach (var o in (Arr(root, "users") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var u = new User
                {
                    Login = o.Str("login") ?? "",
                    Role = o.Str("role") == "admin" ? "admin" : "user",
                    Salt = o.Str("salt") ?? "",
                    Hash = o.Str("hash") ?? "",
                    Iterations = (int)(o.Num("iterations") ?? 0),
                    Blocked = o.Bool("blocked") ?? false,
                    Expires = o.Num("expires") ?? 0,
                    Created = o.Num("created") ?? 0,
                    Note = o.Str("note") ?? "",
                    Token = o.Str("token") ?? "",
                    Stamp = o.Num("stamp") ?? 0,
                };
                if (!a.AddLoadedUser(u)) continue;
                foreach (var t in (Arr(o, "torrents") ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    var rec = new UserTorrent
                    {
                        Hash = (t.Str("hash") ?? "").ToLowerInvariant(),
                        Link = t.Str("link") ?? "",
                        Title = t.Str("title") ?? "",
                        Data = t.Str("data") ?? "",
                        Category = t.Str("category") ?? "",
                        Timestamp = t.Num("timestamp") ?? 0,
                        TmdbId = t.Num("tmdb_id") ?? 0,
                        Seasons = TorrMeta.Seasons(t.TryGetValue("seasons", out var sv) ? sv : null) ?? new int[0],
                    };
                    if (rec.Hash.Length > 0) u.Torrents[rec.Hash] = rec;
                }
                ReadTraffic(u.Traffic, Obj(o, "traffic"));
            }
            long now = Accounts.UnixNow();
            foreach (var o in (Arr(root, "sessions") ?? new List<object>()).OfType<Dictionary<string, object>>())
                a.AddLoadedSession(new Session { Token = o.Str("token") ?? "", Login = o.Str("login") ?? "", Ip = o.Str("ip") ?? "", Created = o.Num("created") ?? 0, Expires = o.Num("expires") ?? 0 }, now);
            var traffic = Obj(root, "traffic");
            if (traffic != null)
                foreach (var kv in traffic)
                    ReadTraffic(a.Counter(kv.Key, true), kv.Value as Dictionary<string, object>);
            var seen = Obj(root, "node_seen");
            if (seen != null)
                foreach (var kv in seen)
                    if (kv.Value is Dictionary<string, object> per)
                        foreach (var p in per) a.AddLoadedNodeSeen(kv.Key, p.Key, per.Num(p.Key) ?? 0);
            var access = Obj(root, "access");
            if (access != null)
            {
                a.RequireAuth = access.Bool("require_auth") ?? false;
                a.LanWithoutAuth = access.Bool("lan_without_auth") ?? false;
            }
            var master = Obj(root, "master");
            if (master != null)
            {
                a.MasterEnabled = master.Bool("enabled") ?? false;
                a.LocalMax = (int)Math.Max(0, Math.Min(10000, master.Num("local_max") ?? 5));
                foreach (var n in (Arr(master, "nodes") ?? new List<object>()).OfType<Dictionary<string, object>>())
                    a.Nodes.Add(new NodeConfig { Id = n.Str("id") ?? "", Name = n.Str("name") ?? "", Url = n.Str("url") ?? "", Key = n.Str("key") ?? "", Max = (int)(n.Num("max") ?? 5), Enabled = n.Bool("enabled") ?? true });
            }
            var node = Obj(root, "node");
            if (node != null) a.NodeKey = node.Str("key") ?? "";
        }

        // ==================== запись ====================

        static void WriteTraffic(JsonWriter w, TrafficCounter c)
        {
            w.Obj().Prop("total", c.Total).Key("days").Obj();
            foreach (var kv in c.Days) w.Prop(kv.Key, kv.Value);
            w.EndObj().EndObj();
            c.Dirty.Clear();
        }

        string Serialize(Accounts a)
        {
            var w = new JsonWriter().Obj().Prop("version", 1);
            w.Key("users").Arr();
            foreach (var u in a.UsersMap.Values.OrderBy(x => x.Created))
            {
                w.Obj().Prop("login", u.Login).Prop("role", u.Role).Prop("salt", u.Salt).Prop("hash", u.Hash).Prop("iterations", u.Iterations)
                    .Prop("blocked", u.Blocked).Prop("expires", u.Expires).Prop("created", u.Created).Prop("note", u.Note)
                    .Prop("token", u.Token).Prop("stamp", u.Stamp);
                w.Key("torrents").Arr();
                foreach (var t in u.Torrents.Values.OrderBy(x => x.Timestamp))
                {
                    w.Obj().Prop("hash", t.Hash).Prop("link", t.Link ?? "").Prop("title", t.Title ?? "").Prop("data", t.Data ?? "")
                        .Prop("category", t.Category ?? "").Prop("timestamp", t.Timestamp).Prop("tmdb_id", t.TmdbId).Key("seasons").Arr();
                    foreach (var sn in t.Seasons ?? new int[0]) w.Val(sn);
                    w.EndArr().EndObj();
                }
                w.EndArr();
                w.Key("traffic"); WriteTraffic(w, u.Traffic);
                w.EndObj();
            }
            w.EndArr();
            w.Key("sessions").Arr();
            foreach (var s in a.SessionsMap.Values)
                w.Obj().Prop("token", s.Token).Prop("login", s.Login).Prop("ip", s.Ip ?? "").Prop("created", s.Created).Prop("expires", s.Expires).EndObj();
            w.EndArr();
            // Трафик службы и зрителей мастера — по имени счётчика (http, bt_down, bt_up, r:логин)
            w.Key("traffic").Obj();
            foreach (var kv in a.Counters().Where(kv => !kv.Key.StartsWith("u:")))
            {
                w.Key(kv.Key); WriteTraffic(w, kv.Value);
            }
            w.EndObj();
            w.Key("node_seen").Obj();
            foreach (var kv in a.NodeSeenMap)
            {
                w.Key(kv.Key).Obj();
                foreach (var p in kv.Value) w.Prop(p.Key, p.Value);
                w.EndObj();
            }
            w.EndObj();
            a.NodeSeenDirty.Clear();
            w.Key("access").Obj().Prop("require_auth", a.RequireAuth).Prop("lan_without_auth", a.LanWithoutAuth).EndObj();
            w.Key("master").Obj().Prop("enabled", a.MasterEnabled).Prop("local_max", a.LocalMax).Key("nodes").Arr();
            foreach (var n in a.Nodes)
                w.Obj().Prop("id", n.Id).Prop("name", n.Name).Prop("url", n.Url).Prop("key", n.Key).Prop("max", n.Max).Prop("enabled", n.Enabled).EndObj();
            w.EndArr().EndObj();
            w.Key("node").Obj().Prop("key", a.NodeKey ?? "").EndObj();
            return w.EndObj().ToString();
        }

        readonly object fileLock = new object();

        void Save()
        {
            var a = owner;
            if (a == null) return;
            string text;
            lock (a.Sync)
            {
                dirty = false;
                text = Serialize(a);
            }
            lock (fileLock)
            {
                try
                {
                    string tmp = path + ".tmp", bak = path + ".bak";
                    File.WriteAllText(tmp, text);
                    // Прежний файл — копией: оборвалась запись между подменами — возьмём её
                    if (!File.Exists(path)) File.Move(tmp, path);
                    else
                    {
                        try { File.Replace(tmp, path, bak); }
                        catch (PlatformNotSupportedException)
                        {
                            // Рантайм без Replace — то же вручную
                            File.Copy(path, bak, true);
                            File.Delete(path);
                            File.Move(tmp, path);
                        }
                    }
                }
                catch (Exception e)
                {
                    dirty = true;
                    log(Name + ": не записался — " + e.Message);
                }
            }
        }
    }
}
