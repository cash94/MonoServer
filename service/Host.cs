// Служба целиком — движок, база MonoServer и HTTP; одна и та же на телевизоре
// (Program.cs) и на ПК (pc/Main.cs, проверка консолью)
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace TorrStreamTorrServer
{
    public class TorrServerHost
    {
        public const int DefaultPort = 8090;

        public readonly LogRing Journal = new LogRing(2000);
        public TorrEngine Engine { get; private set; }
        public Accounts Accounts { get; private set; }
        public HttpApi Api { get; private set; }
        /// <summary>
        /// Код первого входа показывает страница приложения на этом устройстве
        /// (/api/setup/code) — для Tizen, где журнала службы не видно
        /// </summary>
        public bool LocalSetupCode;
        Timer trafficTimer;
        long btDownSeen, btUpSeen;

        /// <summary>
        /// Порт занят (стоит другой TorrServer) — исключение от HttpApi.Start.
        /// Всё после HTTP исключений не бросает: что не запустилось — в журнал и в
        /// /version (Problems), а служба отвечает и объясняет, что с ней. На Tizen
        /// журнала не видно — причину показывает страница пакета по /version
        /// </summary>
        public void Start(string dataDir, int port, Action<string> sink)
        {
            Action<string> log = m =>
            {
                Journal.Add(m);
                if (sink != null) sink(m);
            };
            log("MonoServer " + TorrEngine.ServiceVersion + ", " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Directory.CreateDirectory(dataDir);
            Engine = new TorrEngine(dataDir, log);
            Accounts = new Accounts(dataDir, log);
            // HTTP — первым: занят порт — движок и не нужен, и базу второй экземпляр не трогает
            string localKey = Accounts.RandomHex(16);
            Api = new HttpApi(Engine, Accounts, port, log, Journal, localKey) { LocalSetupCode = LocalSetupCode };
            Api.Start();
            WriteLocalKey(dataDir, localKey, log);
            // Движок — до базы пользователей, как до 1.0.26: торренты и потоки
            // работают, даже если с базой что-то не так
            Step("торрент-движок", log, () => Engine.Start());
            Step("база пользователей", log, () => Accounts.Load());
            if (Accounts.LoadError != null) Api.Problem("база пользователей " + Accounts.LoadError + " — пользователи только до перезапуска");
            Step("сверка торрентов с базой", log, () => Reconcile(log));
            // Трафик BitTorrent движок копит сам — в статистику раз в 10 с
            trafficTimer = new Timer(_ =>
            {
                try
                {
                    long d = Engine.BtDownloaded, u = Engine.BtUploaded;
                    Accounts.AddBt(d - btDownSeen, u - btUpSeen);
                    btDownSeen = d; btUpSeen = u;
                }
                catch (Exception e) { log("трафик: " + e.Message); }
            }, null, 10000, 10000);
            log("служба готова" + (Api.Problems != null ? " с ошибками: " + Api.Problems : ""));
        }

        void Step(string what, Action<string> log, Action step)
        {
            try { step(); }
            catch (Exception e)
            {
                log(what + " не запустился: " + e);
                Api.Problem(what + ": " + (e.GetBaseException().Message ?? e.GetType().Name));
            }
        }

        // Базы движка и пользователей — заодно: торрент в списке пользователя, но не
        // в db.json — вернуть; в db.json, но ни у кого в списке (прежние версии,
        // одна база на всех) — admin
        void Reconcile(Action<string> log)
        {
            var owners = Accounts.OwnersAll();
            var admin = Accounts.Admin;
            int adopted = 0;
            foreach (var e in Engine.List().Where(x => x.InDb && !owners.ContainsKey(x.Hash)))
            {
                Accounts.Keep(admin, e, e.Title, e.Data, e.Category, e.TmdbId, e.Seasons);
                adopted++;
            }
            if (adopted > 0) log("торренты из общей базы — в список admin: " + adopted);
            foreach (var u in Accounts.Users())
                foreach (var t in Accounts.OwnList(u))
                {
                    var e = Engine.Get(t.Hash);
                    if (e == null || !e.InDb) Engine.Remember(t);
                }
        }

        // Ключ приложения на этом же устройстве (Android: настройки службы через
        // /settings): файл в данных службы, читать его может только владелец данных
        static void WriteLocalKey(string dataDir, string key, Action<string> log)
        {
            try { File.WriteAllText(Path.Combine(dataDir, "local.key"), key); }
            catch (Exception e) { log("local.key: " + e.Message); }
        }

        public void Stop()
        {
            try { trafficTimer?.Dispose(); } catch { }
            if (Api != null) Api.Stop();
            if (Engine != null) Engine.StopAll();
            if (Accounts != null) Accounts.Close();
        }
    }
}
