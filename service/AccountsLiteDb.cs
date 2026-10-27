// База MonoServer в LiteDB — monoserver.db (Linux, Windows, Android; pc/Main.cs и
// mono/Main.cs ставят Accounts.StoreFactory). LiteDB — встраиваемая база на чистом
// C# с журналом и транзакциями: после сбоя посреди записи восстанавливается сама;
// пишется только изменённое. В пакет Tizen не входит (TorrStreamTorrServer.csproj:
// Compile Remove) — там JSON (AccountsStore.cs): с LiteDB служба на Tizen 4.0 не
// запускалась.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;

namespace TorrStreamTorrServer
{
    public class LiteDbAccountsStore : IAccountsStore
    {
        readonly string path;
        readonly Action<string> log;
        LiteDatabase db;

        public LiteDbAccountsStore(string dataDir, Action<string> log)
        {
            path = Path.Combine(dataDir, "monoserver.db");
            this.log = log ?? (_ => { });
        }

        public string Name => "monoserver.db";

        public void Load(Accounts a)
        {
            db = Open();
            try { Read(a); }
            catch (Exception e)
            {
                log(Name + ": не прочиталась — " + e.Message);
                db = Reset();
                a.ClearAll();
                Read(a);
            }
        }

        LiteDatabase Open()
        {
            try { return new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct }); }
            catch (IOException e)
            {
                // Занята (другой экземпляр службы) или нет доступа — не трогаем
                throw new IOException("занята или нет доступа: " + e.Message, e);
            }
            catch (Exception e)
            {
                log(Name + ": не открылась — " + e.Message);
                return Reset();
            }
        }

        // Битая база — не затираем: в сторону, рядом с датой, и начинаем заново
        LiteDatabase Reset()
        {
            try { db?.Dispose(); } catch { }
            db = null;
            string bad = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bad";
            if (File.Exists(path)) File.Move(path, bad);
            string journal = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-log.db");
            if (File.Exists(journal)) File.Move(journal, bad + "-log");
            log(Name + " отложена в " + Path.GetFileName(bad) + ", начинаю новую");
            return new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        }

        static string S(BsonDocument d, string k) { var v = d[k]; return v != null && v.IsString ? v.AsString : ""; }
        static long L(BsonDocument d, string k) { var v = d[k]; return v != null && v.IsNumber ? Convert.ToInt64(v.RawValue, CultureInfo.InvariantCulture) : 0; }
        static bool B(BsonDocument d, string k, bool def) { var v = d[k]; return v != null && v.IsBoolean ? v.AsBoolean : def; }
        static string Key(string login) { return login.ToLowerInvariant(); }

        void Read(Accounts a)
        {
            foreach (var d in db.GetCollection("users").FindAll())
            {
                a.AddLoadedUser(new User
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
                });
            }
            foreach (var d in db.GetCollection("torrents").FindAll())
            {
                if (!a.UsersMap.TryGetValue(S(d, "login"), out var u)) continue;
                var t = new UserTorrent { Hash = S(d, "hash").ToLowerInvariant(), Link = S(d, "link"), Title = S(d, "title"), Data = S(d, "data"), Category = S(d, "category"), Timestamp = L(d, "timestamp"), TmdbId = L(d, "tmdb_id") };
                var sv = d["seasons"];
                if (sv != null && sv.IsArray) t.Seasons = sv.AsArray.Where(x => x.IsNumber).Select(x => Convert.ToInt32(x.RawValue, CultureInfo.InvariantCulture)).ToArray();
                if (t.Hash.Length > 0) u.Torrents[t.Hash] = t;
            }
            long now = Accounts.UnixNow();
            foreach (var d in db.GetCollection("sessions").FindAll())
                a.AddLoadedSession(new Session { Token = d["_id"].AsString, Login = S(d, "login"), Ip = S(d, "ip"), Created = L(d, "created"), Expires = L(d, "expires") }, now);
            foreach (var d in db.GetCollection("traffic").FindAll())
            {
                var c = a.Counter(S(d, "scope"), true);
                if (c == null) continue;
                string day = S(d, "day");
                long n = L(d, "bytes");
                if (day == Accounts.TotalDay) c.Total = n;
                else c.Days[day] = n;
            }
            foreach (var d in db.GetCollection("node_seen").FindAll())
                a.AddLoadedNodeSeen(S(d, "node"), S(d, "login"), L(d, "bytes"));
            var cfg = db.GetCollection("config");
            var access = cfg.FindById("access");
            if (access != null)
            {
                a.RequireAuth = B(access, "require_auth", false);
                a.LanWithoutAuth = B(access, "lan_without_auth", false);
            }
            var master = cfg.FindById("master");
            if (master != null)
            {
                a.MasterEnabled = B(master, "enabled", false);
                a.LocalMax = (int)Math.Max(0, Math.Min(10000, master["local_max"].IsNumber ? L(master, "local_max") : 5));
                var nodes = master["nodes"];
                if (nodes != null && nodes.IsArray)
                    foreach (var v in nodes.AsArray.Where(x => x.IsDocument))
                    {
                        var n = v.AsDocument;
                        a.Nodes.Add(new NodeConfig { Id = S(n, "id"), Name = S(n, "name"), Url = S(n, "url"), Key = S(n, "key"), Max = (int)L(n, "max"), Enabled = B(n, "enabled", true) });
                    }
            }
            var node = cfg.FindById("node");
            if (node != null) a.NodeKey = S(node, "key");
        }

        public void PutUser(User u)
        {
            db?.GetCollection("users").Upsert(new BsonDocument
            {
                ["_id"] = Key(u.Login), ["login"] = u.Login, ["role"] = u.Role, ["salt"] = u.Salt, ["hash"] = u.Hash,
                ["iterations"] = u.Iterations, ["blocked"] = u.Blocked, ["expires"] = u.Expires, ["created"] = u.Created,
                ["note"] = u.Note, ["token"] = u.Token, ["stamp"] = u.Stamp,
            });
        }

        public void DelUser(User u)
        {
            if (db == null) return;
            db.GetCollection("users").Delete(Key(u.Login));
            db.GetCollection("torrents").DeleteMany(Query.EQ("login", u.Login));
            db.GetCollection("traffic").DeleteMany(Query.EQ("scope", "u:" + u.Login));
            db.GetCollection("sessions").DeleteMany(Query.EQ("login", u.Login));
        }

        public void PutTorrent(User u, UserTorrent t)
        {
            db?.GetCollection("torrents").Upsert(new BsonDocument
            {
                ["_id"] = Key(u.Login) + "|" + t.Hash, ["login"] = u.Login, ["hash"] = t.Hash, ["link"] = t.Link ?? "",
                ["title"] = t.Title ?? "", ["data"] = t.Data ?? "", ["category"] = t.Category ?? "", ["timestamp"] = t.Timestamp,
                ["tmdb_id"] = t.TmdbId, ["seasons"] = new BsonArray((t.Seasons ?? new int[0]).Select(x => new BsonValue(x))),
            });
        }

        public void DelTorrent(User u, string hash)
        {
            db?.GetCollection("torrents").Delete(Key(u.Login) + "|" + hash);
        }

        public void PutSession(Session s)
        {
            db?.GetCollection("sessions").Upsert(new BsonDocument
            {
                ["_id"] = s.Token, ["login"] = s.Login, ["ip"] = s.Ip, ["created"] = s.Created, ["expires"] = s.Expires,
            });
        }

        public void DelSession(string token) { db?.GetCollection("sessions").Delete(token); }

        public void DelSessionsOf(string login) { db?.GetCollection("sessions").DeleteMany(Query.EQ("login", login)); }

        public void PutConfig(Accounts a)
        {
            if (db == null) return;
            var cfg = db.GetCollection("config");
            cfg.Upsert(new BsonDocument { ["_id"] = "access", ["require_auth"] = a.RequireAuth, ["lan_without_auth"] = a.LanWithoutAuth });
            var nodes = new BsonArray();
            foreach (var n in a.Nodes)
                nodes.Add(new BsonDocument { ["id"] = n.Id, ["name"] = n.Name, ["url"] = n.Url, ["key"] = n.Key, ["max"] = n.Max, ["enabled"] = n.Enabled });
            cfg.Upsert(new BsonDocument { ["_id"] = "master", ["enabled"] = a.MasterEnabled, ["local_max"] = a.LocalMax, ["nodes"] = nodes });
            cfg.Upsert(new BsonDocument { ["_id"] = "node", ["key"] = a.NodeKey });
        }

        /// <summary>Трафик — изменённые дни счётчиков и node_seen, одной транзакцией</summary>
        public void Flush(Accounts a)
        {
            if (db == null) return;
            var col = db.GetCollection("traffic");
            db.BeginTrans();
            try
            {
                foreach (var kv in a.Counters()) FlushCounter(col, kv.Key, kv.Value);
                var seen = db.GetCollection("node_seen");
                foreach (var node in a.NodeSeenDirty)
                {
                    seen.DeleteMany(Query.EQ("node", node));
                    if (a.NodeSeenMap.TryGetValue(node, out var map))
                        foreach (var kv in map)
                            seen.Upsert(new BsonDocument { ["_id"] = node + "|" + Key(kv.Key), ["node"] = node, ["login"] = kv.Key, ["bytes"] = kv.Value });
                }
                a.NodeSeenDirty.Clear();
                db.Commit();
            }
            catch { db.Rollback(); throw; }
        }

        static void FlushCounter(ILiteCollection<BsonDocument> col, string scope, TrafficCounter c)
        {
            if (c.Dirty.Count == 0) return;
            foreach (var day in c.Dirty)
                col.Upsert(new BsonDocument { ["_id"] = scope + "|" + day, ["scope"] = scope, ["day"] = day, ["bytes"] = c.On(day) });
            col.Upsert(new BsonDocument { ["_id"] = scope + "|" + Accounts.TotalDay, ["scope"] = scope, ["day"] = Accounts.TotalDay, ["bytes"] = c.Total });
            c.Dirty.Clear();
        }

        public void Prune(Accounts a, long now, string before)
        {
            if (db == null) return;
            db.GetCollection("sessions").DeleteMany(Query.LTE("expires", now));
            // «total» больше любой даты — под чистку не попадёт
            db.GetCollection("traffic").DeleteMany(Query.LT("day", before));
            db.Checkpoint();
        }

        public void Close()
        {
            try { db?.Dispose(); } catch { }
            db = null;
        }
    }
}
