// Торрент-движок службы: MonoTorrent (чистый C#, без нативного кода — на Tizen
// 5.5 своё нативное из пакета не исполняется) с поведением TorrServer:
//   - торренты по magnet, хэшу или ссылке на .torrent; «база» — db.json
//     (название, постер, файлы), метаданные .torrent — в кэше MonoTorrent;
//   - данные только в памяти (MemoryStorage.cs), лимит — CacheSize на торрент;
//   - качается только окно вокруг читателей (WindowRequester.cs);
//   - без читателей TorrentDisconnectTimeout секунд — торрент закрывается
//     (в базе остаётся, если его туда сохраняли).
// Без зависимостей от Tizen — запускается и на ПК (pc/).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace TorrStreamTorrServer
{
    public class FileStat
    {
        public int Id;              // с 1, в порядке файлов торрента — как у TorrServer
        public string Path;         // с названием торрента в начале, если файлов несколько
        public long Length;
        public ITorrentManagerFile File;   // null — служебный файл-заполнитель (BEP 47)
    }

    public class TorrEntry
    {
        public string Hash;
        public string Link;         // magnet — по нему торрент открывается снова
        public string Title = "", Poster = "", Data = "", Category = "", Name = "";
        public long Timestamp, Size;
        public bool InDb;
        public List<FileStat> Files = new List<FileStat>();

        // Открыт сейчас
        public TorrentManager Manager;
        public PieceStore Store;
        public string SaveDir;
        public Task Activation;
        // Открытие и закрытие — строго по очереди: иначе закрытие по простою и
        // новый запрос делили один TorrentManager (тот открывал недозакрытый,
        // а закрытие его потом удаляло из движка)
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public readonly List<TorrReader> Readers = new List<TorrReader>();
        public long LastActiveMs, OpenedMs;
        // Не закрывать по простою до этого момента: после предзагрузки фронтенд
        // открывает фильм не сразу, а закрытие выбросило бы весь прогретый кэш
        public long KeepUntilMs;
        public string Error;
        public TorrDiag Diag = new TorrDiag();
        public bool Preloading;
        public long PreloadedBytes, PreloadSize;
        // Окна читателей в кусках: пары [начало, конец] — их не вытесняем.
        // Пересчитываются при каждом чтении, читаются без блокировки
        public volatile int[] Protect = new int[0];
        // Что качать (WindowRequester спрашивает на каждом такте) — считается
        // вместе с Protect, при смене читателей и переходе через кусок
        public volatile ReadWindow Window = ReadWindow.None;
        // Замена медленных пиров (PeerRotation.cs)
        public readonly PeerRotation Rotation = new PeerRotation();
        // Свой запас адресов (PeerPool.cs)
        public readonly PeerPool Pool = new PeerPool();
        // Файл, чьи начало и конец закреплены в PieceStore
        public ITorrentManagerFile PinnedFile;

        public bool Active => Manager != null;
        public bool HasInfo => Files.Count > 0;
    }

    /// <summary>
    /// Откуда пиры и что с ними — для журнала (/log): пока метаданных нет, раз в
    /// 15 с, при просмотре — раз в 30 с. Без этого на телевизоре не понять,
    /// почему торрент не качается
    /// </summary>
    public class TorrDiag
    {
        public int Tracker, Dht, Pex, Lpd;
        public readonly Dictionary<string, int> Fails = new Dictionary<string, int>();
        public readonly Dictionary<string, string> Trackers = new Dictionary<string, string>();
        public long LastReportMs;
        public bool InfoLogged;
    }

    public class TorrReader
    {
        public TorrEntry Entry;
        public FileStat Stat;
        public long Position;
        // Дальше этого места (от начала файла) окно не качает: −1 — без предела.
        // У ридеров предзагрузки — их участок, как SetReadahead(0) у TorrServer
        public long Limit = -1;
        public long Seq;
        public volatile bool Closed;
    }

    public class TorrEngine
    {
        public const string ServiceVersion = "1.0.18";
        // Клиенты (Lampa и др.) по «MatriX» в /echo узнают API TorrServer MatriX
        public const string EchoVersion = "MatriX.136 (MonoServer " + ServiceVersion + ")";

        // Ретрекеры, как у TorrServer: magnet с одними хэшами иначе ищет пиров
        // только по DHT, а на холодном телевизоре это долго
        static readonly string[] Retrackers = {
            "http://retracker.local/announce",
            "http://bt4.t-ru.org/ann?magnet",
            "http://retracker.mgts.by:80/announce",
            "udp://opentor.net:6969",
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://exodus.desync.com:6969/announce",
            "udp://tracker.torrent.eu.org:451/announce",
        };

        readonly string dataDir, dlDir, cacheDir, dbPath, settingsPath;
        readonly Action<string> log;
        readonly object sync = new object();
        readonly Dictionary<string, TorrEntry> entries = new Dictionary<string, TorrEntry>();
        readonly HttpClient http = Http.Client(TimeSpan.FromSeconds(30));
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        ClientEngine engine;
        MemoryStorage storage;
        DhtBootstrap dhtBoot;
        TrackerList trackers;
        Timer idleTimer;
        long readerSeq;
        public TorrSettings Settings { get; private set; }
        public string StartError { get; private set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        // Внутренности MonoTorrent 3.0.2, без которых кэш в памяти не работает:
        // снять бит вытесненного куска (TorrentManager.MutableBitField, его
        // главный поток — ClientEngine.MainLoop). Нет их — /version скажет
        static readonly PropertyInfo MutableBitFieldProp = typeof(TorrentManager).GetProperty("MutableBitField", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly SynchronizationContext MainLoop = typeof(ClientEngine).GetField("MainLoop", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as SynchronizationContext;
        static readonly PropertyInfo InfoMetadataProp = typeof(Torrent).GetProperty("InfoMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
        // Пиры, к которым идёт подключение (PeerManager.ConnectingToPeers — внутреннее).
        // Без них «всего» в плеере (active / total) скакало: попытки (до 48 сразу)
        // выпадали из счёта, неудачные MonoTorrent выбрасывает, ответы трекеров и
        // PEX список снова пополняют. У TorrServer (anacrolix) total — с ними
        static readonly FieldInfo ConnectingField = typeof(PeerManager).GetField("ConnectingToPeers", BindingFlags.Instance | BindingFlags.NonPublic);

        static int Connecting(TorrentManager m)
        {
            try { return ConnectingField?.GetValue(m.Peers) is System.Collections.ICollection c ? c.Count : 0; }
            catch { return 0; }
        }
        const long PreloadKeepMs = 120000;
        public static bool CanForgetPieces => MutableBitFieldProp != null && MainLoop != null;

        public TorrEngine(string dataDir, Action<string> log)
        {
            this.dataDir = dataDir;
            this.log = log ?? (_ => { });
            dlDir = Path.Combine(dataDir, "dl");
            cacheDir = Path.Combine(dataDir, "cache");
            dbPath = Path.Combine(dataDir, "db.json");
            settingsPath = Path.Combine(dataDir, "settings.json");
        }

        long Now() { return clock.ElapsedMilliseconds; }

        // Ошибки самого MonoTorrent — в журнал. Info у него — на каждое соединение,
        // это слишком часто для телевизора
        class JournalLogger : MonoTorrent.Logging.ILogger
        {
            readonly string name;
            readonly Action<string> log;
            public JournalLogger(string name, Action<string> log) { this.name = name; this.log = log; }
            public void Info(string message) { }
            public void Debug(string message) { }
            public void Error(string message) { log("MonoTorrent " + name + ": " + message); }
        }

        int unhandled;

        public void Start()
        {
            // Запас потоков пула: на телевизоре их по числу ядер, а блокирующие вызовы
            // netstandard2.0-сборки MonoTorrent (DNS, HTTP трекеров) их занимают —
            // и движок вставал целиком: и подключения, и метаданные, и DHT
            ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
            ThreadPool.SetMinThreads(Math.Max(minWorkers, 32), Math.Max(minIo, 32));
            // До первого обращения к типам MonoTorrent: их журналы создаются при загрузке
            MonoTorrent.Logging.LoggerFactory.Register(name => new JournalLogger(name, log));
            TaskScheduler.UnobservedTaskException += (s, a) =>
            {
                if (Interlocked.Increment(ref unhandled) <= 20) log("необработанная ошибка задачи: " + a.Exception?.GetBaseException());
                a.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => log("необработанная ошибка: " + a.ExceptionObject);
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(cacheDir);
            // Каталоги торрентов от прошлого запуска — пустышки (данные были в памяти)
            try { if (Directory.Exists(dlDir)) Directory.Delete(dlDir, true); } catch { }
            Directory.CreateDirectory(dlDir);

            Settings = TorrSettings.Load(settingsPath);
            if (Settings.PeersListenPort == 0)
            {
                // Постоянный порт для входящих: пиры и DHT помнят нас между запусками
                Settings.PeersListenPort = new Random().Next(20000, 60000);
                SaveSettings();
            }
            LoadDb();
            trackers = new TrackerList(Path.Combine(dataDir, "trackers_best_ip.txt"), log);
            if (Settings.RetrackersMode == 1) trackers.RefreshInBackground();
            dhtBoot = new DhtBootstrap(cacheDir, log);
            // Адреса от DHT — и в запас торрента: MonoTorrent берёт из них не больше своего потолка
            dhtBoot.PeersFound = (ih, peers) => Get(ih.ToHex())?.Pool.Add(peers);
            if (Settings.EnableDHT) dhtBoot.ResolveInBackground();
            if (!CanForgetPieces) log("внимание: внутренности MonoTorrent не найдены — вытесненные куски не перекачаются");
            try
            {
                engine = CreateEngine();
                engine.Dht.StateChanged += (s, a) => log("DHT: " + engine.Dht.State + ", узлов " + engine.Dht.NodeCount);
                log("движок готов: порт пиров " + Settings.PeersListenPort + ", кэш " + (Settings.CacheSize >> 20) + " МБ, в базе " + entries.Count);
            }
            catch (Exception e)
            {
                StartError = e.Message;
                log("движок не запустился: " + e);
            }
            idleTimer = new Timer(_ => CloseIdle(), null, 2000, 2000);
        }

        ClientEngine CreateEngine()
        {
            storage = new MemoryStorage(Resolve);
            var port = Settings.PeersListenPort;
            var b = new EngineSettingsBuilder
            {
                AllowPortForwarding = Settings.EnableUPNP,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = cacheDir,
                DiskCacheBytes = 0,      // MemoryStorage — сам кэш
                UsePartialFiles = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, port) } },
                DhtEndPoint = Settings.EnableDHT ? new IPEndPoint(IPAddress.Any, port) : null,
                // Шифруем только рукопожатие: от провайдера этого хватает, а RC4 на
                // всём потоке — лишняя нагрузка на процессор телевизора
                AllowedEncryption = new List<EncryptionType> { EncryptionType.RC4Header, EncryptionType.PlainText, EncryptionType.RC4Full },
                MaximumConnections = 200,
                // Подключений «в процессе» сразу. Большинство адресов с трекеров
                // недоступны (за NAT, устарели), и при 16 попытках по 8 с пиры
                // перебирались по 2 в секунду — 200 адресов почти две минуты
                MaximumHalfOpenConnections = 48,
                // Рукопожатие (и шифрование) после подключения; по умолчанию 10 с
                ConnectionTimeout = TimeSpan.FromSeconds(6),
                MaximumDownloadRate = Settings.DownloadRateLimit * 1024,
                MaximumUploadRate = Settings.UploadRateLimit * 1024,
                // Веб-сиды (url-list) — уже через 5 с, если пиры дают меньше 15 КБ/с:
                // по умолчанию MonoTorrent ждёт минуту, а плеер столько не ждёт
                WebSeedDelay = TimeSpan.FromSeconds(5),
            };
            // Подключение к пирам — своё (PeerConnector.cs): в netstandard2.0-сборке
            // MonoTorrent (Tizen) оно блокирующее и зависало на недоступных пирах.
            // Через WithPeerConnectionCreator: свой SocketConnector в соединения с
            // пирами MonoTorrent подставляет сам, мимо WithSocketConnectorCreator
            // DHT — свой экземпляр (DhtBootstrap): первые узлы и перезапуск поиска
            var factories = Factories.Default.WithPieceWriterCreator(_ => storage)
                .WithDhtCreator(dhtBoot.Create)
                .WithPeerConnectionCreator("ipv4", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new PeerConnector()))
                .WithPeerConnectionCreator("ipv6", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new PeerConnector()));
            // HTTP-трекеры и веб-сиды — через свой обработчик службы, если он есть (Http.cs):
            // на Android штатный HttpClient не умеет HTTPS
            if (Http.HandlerFactory != null)
                factories = factories.WithHttpClientCreator(family => Http.Client(TimeSpan.FromSeconds(30), family));
            return new ClientEngine(b.ToSettings(), factories);
        }

        TorrentSettings TorrentSettingsFor()
        {
            return new TorrentSettingsBuilder
            {
                // С запасом под незавершённые подключения: MonoTorrent считает их в
                // лимит, лишних сверх ConnectionsLimit отключает PeerRotation
                MaximumConnections = Settings.ConnectionsLimit + PeerRotation.Headroom(Settings.ConnectionsLimit),
                UploadSlots = 4,
                AllowDht = Settings.EnableDHT,
                AllowPeerExchange = true,
                AllowInitialSeeding = false,
                CreateContainingDirectory = true,
            }.ToSettings();
        }

        public void SaveSettings()
        {
            try { Settings.Save(settingsPath); } catch (Exception e) { log("settings.json: " + e.Message); }
        }

        public void ApplySettings(Dictionary<string, object> sets)
        {
            Settings.Apply(sets);
            SaveSettings();
            List<TorrEntry> active;
            lock (sync) active = entries.Values.Where(e => e.Store != null).ToList();
            foreach (var e in active) e.Store.Capacity = Settings.CacheSize;
            // Порт, DHT, скорость — в движке; без перезапуска службы — для новых торрентов
            if (engine != null)
            {
                try
                {
                    var b = new EngineSettingsBuilder(engine.Settings)
                    {
                        MaximumDownloadRate = Settings.DownloadRateLimit * 1024,
                        MaximumUploadRate = Settings.UploadRateLimit * 1024,
                    };
                    _ = engine.UpdateSettingsAsync(b.ToSettings());
                }
                catch (Exception e) { log("настройки движка: " + e.Message); }
            }
        }

        public void ResetSettings()
        {
            int port = Settings.PeersListenPort;
            Settings = new TorrSettings { PeersListenPort = port };
            SaveSettings();
        }

        // ==================== база ====================

        void LoadDb()
        {
            try
            {
                if (!File.Exists(dbPath)) return;
                var root = Json.ParseObject(File.ReadAllText(dbPath));
                var list = root.TryGetValue("torrents", out var v) ? v as List<object> : null;
                if (list == null) return;
                foreach (var item in list.OfType<Dictionary<string, object>>())
                {
                    var e = new TorrEntry
                    {
                        Hash = item.Str("hash"),
                        Link = item.Str("link"),
                        Title = item.Str("title") ?? "",
                        // База от прежних версий могла сохранить что угодно — только http(s)
                        Poster = PosterGuard.ValidUrl(item.Str("poster")) ? item.Str("poster") : "",
                        Data = item.Str("data") ?? "",
                        Category = item.Str("category") ?? "",
                        Name = item.Str("name") ?? "",
                        Timestamp = item.Num("timestamp") ?? 0,
                        Size = item.Num("size") ?? 0,
                        InDb = true,
                    };
                    if (string.IsNullOrEmpty(e.Hash)) continue;
                    if (item.TryGetValue("files", out var fv) && fv is List<object> files)
                        foreach (var f in files.OfType<Dictionary<string, object>>())
                            e.Files.Add(new FileStat { Id = (int)(f.Num("id") ?? 0), Path = f.Str("path") ?? "", Length = f.Num("length") ?? 0 });
                    // База до 1.0.10 — в порядке и с номерами словаря info
                    e.Files = FileOrder.Apply(e.Files);
                    entries[e.Hash] = e;
                }
            }
            catch (Exception e) { log("db.json: " + e.Message); }
        }

        void SaveDb()
        {
            string text;
            lock (sync)
            {
                var w = new JsonWriter().Obj().Key("torrents").Arr();
                foreach (var e in entries.Values.Where(x => x.InDb).OrderBy(x => x.Timestamp))
                {
                    w.Obj().Prop("hash", e.Hash).Prop("link", e.Link ?? "").Prop("title", e.Title).Prop("poster", e.Poster)
                        .Prop("data", e.Data).Prop("category", e.Category).Prop("name", e.Name)
                        .Prop("timestamp", e.Timestamp).Prop("size", e.Size).Key("files").Arr();
                    foreach (var f in e.Files) w.Obj().Prop("id", f.Id).Prop("path", f.Path).Prop("length", f.Length).EndObj();
                    w.EndArr().EndObj();
                }
                text = w.EndArr().EndObj().ToString();
            }
            try
            {
                string tmp = dbPath + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(dbPath)) File.Delete(dbPath);
                File.Move(tmp, dbPath);
            }
            catch (Exception e) { log("db.json: " + e.Message); }
        }

        // ==================== торренты ====================

        public List<TorrEntry> List()
        {
            lock (sync) return entries.Values.OrderByDescending(e => e.Timestamp).ToList();
        }

        public TorrEntry Get(string hash)
        {
            if (hash == null) return null;
            lock (sync) return entries.TryGetValue(hash.Trim().ToLowerInvariant(), out var e) ? e : null;
        }

        /// <summary>
        /// Найти или добавить торрент по ссылке (magnet, хэш, http(s) на .torrent)
        /// и открыть его. Метаданные приходят потом — WaitInfo
        /// </summary>
        public async Task<TorrEntry> AddAsync(string link, string title, string poster, string data, string category, bool saveToDb)
        {
            if (engine == null) throw new InvalidOperationException("движок не запущен: " + StartError);
            if (string.IsNullOrWhiteSpace(link)) throw new ArgumentException("link should not be empty");
            link = link.Trim();

            MagnetLink magnet = null;
            Torrent torrent = null;
            string hash = HashFromLink(link);
            TorrEntry known = Get(hash);
            if (hash != null && known != null && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(known.Link))
                link = known.Link;   // голый хэш — открываем как раньше, со старыми трекерами

            if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                magnet = WithRetrackers(MagnetLink.Parse(link));
            else if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await http.GetByteArrayAsync(link).ConfigureAwait(false);
                torrent = Torrent.Load(bytes);
            }
            else if (hash != null)
                magnet = WithRetrackers(new MagnetLink(ParseHash(link)));
            else
                throw new ArgumentException("wrong link: " + link);

            hash = (torrent != null ? torrent.InfoHashes : magnet.InfoHashes).V1OrV2.ToHex().ToLowerInvariant();
            TorrEntry e;
            bool save = false;
            lock (sync)
            {
                if (!entries.TryGetValue(hash, out e))
                {
                    e = new TorrEntry { Hash = hash, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    entries[hash] = e;
                }
                // Ссылка, по которой торрент откроется из базы: с трекерами и
                // веб-сидами .torrent — в кэш метаданных MonoTorrent они не попадают
                if (magnet != null) e.Link = magnet.ToV1String();
                else if (torrent != null)
                    e.Link = new MagnetLink(torrent.InfoHashes, torrent.Name, torrent.AnnounceUrls.SelectMany(t => t).Distinct().ToList(),
                        torrent.HttpSeeds.Select(u => u.ToString()), torrent.Size).ToV1String();
                if (!string.IsNullOrEmpty(title)) e.Title = title;
                if (!string.IsNullOrEmpty(data)) e.Data = data;
                if (!string.IsNullOrEmpty(category)) e.Category = category;
                if (string.IsNullOrEmpty(e.Title)) e.Title = (magnet != null ? magnet.Name : torrent?.Name) ?? hash;
                if (saveToDb && !e.InDb) { e.InDb = true; save = true; }
                else if (e.InDb && (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(poster) || !string.IsNullOrEmpty(data))) save = true;
                e.LastActiveMs = Now();
            }
            if (save) SaveDb();
            ApplyPoster(e, poster);
            await EnsureActiveAsync(e, magnet, torrent).ConfigureAwait(false);
            return e;
        }

        MagnetLink WithRetrackers(MagnetLink m)
        {
            if (Settings.RetrackersMode != 1) return m;
            var list = new List<string>(m.AnnounceUrls ?? new List<string>());
            foreach (var r in Retrackers) if (!list.Contains(r)) list.Add(r);
            return new MagnetLink(m.InfoHashes, m.Name, list, m.Webseeds, m.Size);
        }

        static InfoHash ParseHash(string s)
        {
            s = s.Trim();
            return s.Length == 32 ? InfoHash.FromBase32(s) : InfoHash.FromHex(s);
        }

        /// <summary>Хэш из ссылки без сети (magnet или сам хэш); иначе null</summary>
        public static string HashFromLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link)) return null;
            link = link.Trim();
            try
            {
                if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    return MagnetLink.Parse(link).InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
                if ((link.Length == 40 || link.Length == 64) && link.All(Uri.IsHexDigit))
                    return link.ToLowerInvariant();
                if (link.Length == 32)
                    return InfoHash.FromBase32(link).ToHex().ToLowerInvariant();
            }
            catch { }
            return null;
        }

        /// <summary>Открыть торрент из базы (по хэшу) — для /play/хэш/номер</summary>
        public async Task<TorrEntry> OpenAsync(string hash)
        {
            var e = Get(hash);
            if (e == null) return null;
            lock (sync) e.LastActiveMs = Now();
            await EnsureActiveAsync(e, null, null).ConfigureAwait(false);
            return e;
        }

        Task EnsureActiveAsync(TorrEntry e, MagnetLink magnet, Torrent torrent)
        {
            lock (sync)
            {
                e.LastActiveMs = Now();
                // Открыт или открывается — ждём то же открытие
                if (e.Activation != null && !e.Activation.IsFaulted && (e.Manager != null || !e.Activation.IsCompleted)) return e.Activation;
                e.Error = null;
                e.OpenedMs = Now();
                e.LastActiveMs = Now();
                e.Activation = ActivateAsync(e, magnet, torrent);
                return e.Activation;
            }
        }

        async Task ActivateAsync(TorrEntry e, MagnetLink magnet, Torrent torrent)
        {
            await e.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (e.Manager != null) return;
                if (magnet == null && torrent == null) magnet = WithRetrackers(MagnetLink.Parse(e.Link));
                // Полный путь — как его хранит MonoTorrent (Resolve сравнивает с ним)
                e.SaveDir = Path.GetFullPath(Path.Combine(dlDir, e.Hash)) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(e.SaveDir);

                // Вдруг движок ещё держит его (закрытие не доделалось) — заберём
                var infoHashes = torrent != null ? torrent.InfoHashes : magnet.InfoHashes;
                var m = engine.Torrents.FirstOrDefault(t => t.InfoHashes.V1OrV2 == infoHashes.V1OrV2);
                if (m == null)
                {
                    // Трекеры magnet MonoTorrent кладёт в ОДИН уровень (tier): перемешивает
                    // и спрашивает по очереди до первого ответившего — даже если тот
                    // пиров не знает (ретрекер «ок, пиров 0»), а до трекера раздачи
                    // очередь не доходит. Поэтому magnet — без трекеров, а каждый
                    // трекер — своим уровнем (AddTrackers): их спрашивают все сразу
                    m = torrent != null
                        ? await engine.AddAsync(torrent, e.SaveDir, TorrentSettingsFor()).ConfigureAwait(false)
                        : await engine.AddAsync(new MagnetLink(magnet.InfoHashes, magnet.Name, null, magnet.Webseeds, magnet.Size), e.SaveDir, TorrentSettingsFor()).ConfigureAwait(false);
                    // У .torrent уровни из announce-list остаются, ретрекеры — отдельно;
                    // у magnet ретрекеры уже в ссылке (WithRetrackers)
                    var urls = torrent != null ? (Settings.RetrackersMode == 1 ? Retrackers : new string[0]) : (IEnumerable<string>)magnet.AnnounceUrls;
                    // Ретрекеры ngosang (TrackerList) — только здесь, в ссылку в базе не идут
                    if (Settings.RetrackersMode == 1) urls = urls.Concat(trackers.Current);
                    await AddTrackersAsync(e, m, urls).ConfigureAwait(false);
                    await m.ChangePickerAsync(new WindowRequester(() => e.Window)).ConfigureAwait(false);
                    m.PieceHashed += (s, a) =>
                    {
                        var store = e.Store;
                        bool had = store != null && store.MarkHashed(a.PieceIndex, a.HashPassed);
                        // В ResponsiveMode его блоки могли уже уйти плееру — видно, откуда артефакты.
                        // Без данных — это начальная проверка .torrent, она вся «не проходит»
                        if (!a.HashPassed && had) log(e.Hash + ": кусок " + a.PieceIndex + " не прошёл проверку хэша, качается заново");
                    };
                    var d = e.Diag;
                    m.PeersFound += (s, a) =>
                    {
                        lock (d)
                        {
                            if (a is TrackerPeersAdded) d.Tracker += a.NewPeers;
                            else if (a is DhtPeersAdded) d.Dht += a.NewPeers;
                            else if (a is PeerExchangePeersAdded) d.Pex += a.NewPeers;
                            else d.Lpd += a.NewPeers;
                        }
                    };
                    m.ConnectionAttemptFailed += (s, a) =>
                    {
                        lock (d) { d.Fails.TryGetValue(a.Reason.ToString(), out int n); d.Fails[a.Reason.ToString()] = n + 1; }
                        e.Pool.MarkTried(a.Peer?.ConnectionUri);
                    };
                    m.PeerConnected += (s, a) => e.Pool.MarkTried(a.Peer?.Uri);
                    m.TrackerManager.AnnounceComplete += (s, a) =>
                    {
                        int peers = 0;
                        if (a.Peers != null) foreach (var l in a.Peers.Values) { peers += l.Count; e.Pool.Add(l); }
                        lock (d) d.Trackers[a.Tracker.Uri.Host] = a.Successful ? "ок, пиров " + peers : "нет (" + a.Tracker.Status + ")";
                    };
                    m.TorrentStateChanged += (s, a) =>
                    {
                        if (a.NewState == TorrentState.Error) log(e.Hash + ": ошибка торрента — " + (m.Error != null ? m.Error.Reason + " " + m.Error.Exception?.Message : "?"));
                    };
                }
                lock (sync) e.Manager = m;
                // Движок запустится с этим торрентом — и DHT с узлами из кэша
                if (!engine.IsRunning && Settings.EnableDHT) await Task.Run(() => dhtBoot.PrepareCache()).ConfigureAwait(false);
                if (m.State == TorrentState.Stopped || m.State == TorrentState.Error) await m.StartAsync().ConfigureAwait(false);
                log(e.Hash + ": открыт" + (m.HasMetadata ? "" : ", жду метаданные"));
                _ = WatchInfoAsync(e, m);
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                log(e.Hash + ": не открылся — " + ex);
                throw;
            }
            finally { e.Gate.Release(); }
        }

        async Task WatchInfoAsync(TorrEntry e, TorrentManager m)
        {
            try
            {
                if (!m.HasMetadata) await m.WaitForMetadataAsync().ConfigureAwait(false);
                OnInfo(e, m);
            }
            catch (Exception ex) { log(e.Hash + ": метаданные — " + ex.Message); }
        }

        void OnInfo(TorrEntry e, TorrentManager m)
        {
            var t = m.Torrent;
            if (t == null) return;
            var files = BuildFiles(m);
            bool save;
            lock (sync)
            {
                if (e.Manager != m) return;
                e.Files = files;
                e.Name = t.Name ?? "";
                e.Size = t.Size;
                if (string.IsNullOrEmpty(e.Title) || e.Title == e.Hash) e.Title = e.Name;
                EnsureStore(e);
                save = e.InDb;
            }
            AddWebSeeds(e, m);
            log(e.Hash + ": метаданные за " + ((Now() - e.OpenedMs) / 1000) + " с");
            log(e.Hash + ": " + e.Name + " — " + files.Count + " файл(ов), " + (t.Size >> 20) + " МБ, кусок " + (t.PieceLength >> 10) + " КБ");
            if (save) SaveDb();
        }

        async Task AddTrackersAsync(TorrEntry e, TorrentManager m, IEnumerable<string> urls)
        {
            if (urls == null || (m.Torrent != null && m.Torrent.IsPrivate)) return;
            var have = new HashSet<string>(m.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()));
            int added = 0;
            foreach (var url in urls.Distinct())
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || have.Contains(uri.ToString())) continue;
                // MonoTorrent понимает http(s) и udp; wss:// (WebTorrent) — нет
                if (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "udp") continue;
                try { await m.TrackerManager.AddTrackerAsync(uri).ConfigureAwait(false); added++; have.Add(uri.ToString()); }
                catch (Exception ex) { log(e.Hash + ": трекер " + uri.Host + " — " + ex.Message); }
            }
            if (added > 0) log(e.Hash + ": трекеров " + m.TrackerManager.Tiers.Count + " (каждый отдельно)");
        }

        // Веб-сиды из ссылки (ws=) — в Torrent.HttpSeeds: сам MonoTorrent их из
        // magnet не берёт. Список он перебирает в главном потоке — туда и добавляем
        void AddWebSeeds(TorrEntry e, TorrentManager m)
        {
            List<Uri> seeds;
            try
            {
                if (string.IsNullOrEmpty(e.Link) || !e.Link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return;
                seeds = MagnetLink.Parse(e.Link).Webseeds
                    .Select(x => Uri.TryCreate(x, UriKind.Absolute, out var u) ? u : null)
                    .Where(u => u != null && (u.Scheme == "http" || u.Scheme == "https")).ToList();
            }
            catch { return; }
            if (seeds.Count == 0 || m.Torrent == null) return;
            Action add = () =>
            {
                foreach (var u in seeds)
                    if (!m.Torrent.HttpSeeds.Contains(u)) m.Torrent.HttpSeeds.Add(u);
            };
            if (MainLoop != null) MainLoop.Post(_ => { try { add(); } catch { } }, null);
            else add();
        }

        // Хранилище кусков: при метаданных (OnInfo) или при первой записи —
        // что раньше. Под sync
        PieceStore EnsureStore(TorrEntry e)
        {
            if (e.Store != null) return e.Store;
            var t = e.Manager?.Torrent;
            if (t == null) return null;
            var store = new PieceStore(t.PieceLength, t.PieceCount, t.Size, Settings.CacheSize);
            store.IsProtected = p =>
            {
                var r = e.Protect;
                for (int i = 0; i + 1 < r.Length; i += 2)
                    if (p >= r[i] && p <= r[i + 1]) return true;
                return false;
            };
            var m = e.Manager;
            store.Evicted = p => ForgetPiece(m, p);
            e.Store = store;
            return store;
        }

        // MemoryStorage: файл → хранилище его торрента (по каталогу торрента)
        PieceStore Resolve(ITorrentManagerFile file)
        {
            string full = Path.GetFullPath(file.FullPath);
            lock (sync)
            {
                foreach (var e in entries.Values)
                {
                    if (e.Manager == null || e.SaveDir == null) continue;
                    if (full.StartsWith(e.SaveDir, StringComparison.OrdinalIgnoreCase)) return EnsureStore(e);
                }
            }
            return null;
        }

        // Вытесненный кусок: MonoTorrent должен считать его нескачанным — иначе
        // не станет качать снова. Бит снимаем в его главном потоке
        void ForgetPiece(TorrentManager m, int piece)
        {
            if (!CanForgetPieces || m == null) return;
            MainLoop.Post(_ =>
            {
                try
                {
                    if (!(MutableBitFieldProp.GetValue(m) is BitField bf) || piece >= bf.Length) return;
                    bf[piece] = false;
                    foreach (var f in m.Files)
                    {
                        if (f.Length == 0 || piece < f.StartPieceIndex || piece > f.EndPieceIndex) continue;
                        var prop = f.GetType().GetProperty("BitField", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (prop != null && prop.GetValue(f) is BitField fb) fb[piece - f.StartPieceIndex] = false;
                    }
                }
                catch (Exception ex) { log("ForgetPiece: " + ex.Message); }
            }, null);
        }

        static string Norm(string p) { return (p ?? "").Replace('\\', '/').Trim('/'); }

        // Файлы и номера — как у TorrServer: по пути, с 1, вместе с
        // файлами-заполнителями (FileOrder)
        static List<FileStat> BuildFiles(TorrentManager m)
        {
            return FileOrder.Apply(BuildFilesInfoOrder(m));
        }

        // Пути из словаря info (там и заполнители BEP 47, в MonoTorrent их нет).
        // Не вышло — файлы MonoTorrent
        static List<FileStat> BuildFilesInfoOrder(TorrentManager m)
        {
            var t = m.Torrent;
            var list = new List<FileStat>();
            var byPath = new Dictionary<string, ITorrentManagerFile>();
            foreach (var f in m.Files)
            {
                string key = Norm(f.Path);
                if (!byPath.ContainsKey(key)) byPath[key] = f;
            }
            try
            {
                var raw = InfoMetadataProp != null ? (ReadOnlyMemory<byte>)InfoMetadataProp.GetValue(t) : ReadOnlyMemory<byte>.Empty;
                if (!raw.IsEmpty)
                {
                    var info = BEncodedValue.Decode<BEncodedDictionary>(raw.Span);
                    if (info.TryGetValue("files", out BEncodedValue fv) && fv is BEncodedList files)
                    {
                        int id = 1;
                        foreach (var item in files.OfType<BEncodedDictionary>())
                        {
                            BEncodedValue pv;
                            if (!item.TryGetValue("path.utf-8", out pv)) item.TryGetValue("path", out pv);
                            var parts = (pv as BEncodedList)?.OfType<BEncodedString>().Select(x => x.Text) ?? Enumerable.Empty<string>();
                            string path = string.Join("/", parts);
                            long len = item.TryGetValue("length", out BEncodedValue lv) && lv is BEncodedNumber n ? n.Number : 0;
                            byPath.TryGetValue(Norm(path), out var file);
                            list.Add(new FileStat { Id = id++, Path = t.Name + "/" + path, Length = len, File = file });
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch { list.Clear(); }
            int i = 1;
            foreach (var f in m.Files)
                list.Add(new FileStat { Id = i++, Path = m.Files.Count == 1 ? t.Name : t.Name + "/" + Norm(f.Path), Length = f.Length, File = f });
            return list;
        }

        /// <summary>Дождаться метаданных (файлов) не дольше timeoutMs</summary>
        public async Task<bool> WaitInfoAsync(TorrEntry e, int timeoutMs)
        {
            var activation = e.Activation;
            if (activation != null) await activation.ConfigureAwait(false);
            var until = Now() + timeoutMs;
            while (Now() < until)
            {
                lock (sync)
                {
                    // Файлы из базы — без File, пока MonoTorrent не дал метаданные
                    if (e.Manager != null && e.Files.Any(f => f.File != null)) return true;
                    if (e.Manager == null) return false;
                    e.LastActiveMs = Now();
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>Закрыть торрент (в базе остаётся); не в базе — убрать из списка</summary>
        public async Task DropAsync(string hash)
        {
            var e = Get(hash);
            if (e == null) return;
            await CloseAsync(e, -1).ConfigureAwait(false);
            lock (sync) if (!e.InDb && e.Manager == null) entries.Remove(e.Hash);
        }

        /// <summary>Удалить торрент совсем — и из базы, и метаданные</summary>
        public async Task RemoveAsync(string hash)
        {
            var e = Get(hash);
            if (e == null) return;
            await CloseAsync(e, -1).ConfigureAwait(false);
            lock (sync) entries.Remove(e.Hash);
            if (e.InDb) SaveDb();
            try
            {
                var meta = Path.Combine(cacheDir, "metadata");
                if (Directory.Exists(meta))
                    foreach (var f in Directory.GetFiles(meta))
                        if (Path.GetFileName(f).IndexOf(e.Hash, StringComparison.OrdinalIgnoreCase) >= 0) File.Delete(f);
            }
            catch { }
        }

        public async Task WipeAsync()
        {
            foreach (var e in List()) await RemoveAsync(e.Hash).ConfigureAwait(false);
        }

        public void SetMeta(string hash, string title, string poster, string data, string category)
        {
            var e = Get(hash);
            if (e == null) return;
            lock (sync)
            {
                if (title != null) e.Title = title;
                if (poster == "") e.Poster = "";   // убрать постер можно без проверки
                if (data != null) e.Data = data;
                if (category != null) e.Category = category;
            }
            if (e.InDb) SaveDb();
            ApplyPoster(e, poster);
        }

        /// <summary>
        /// Новый постер — только после проверки (PosterGuard, как CheckImgUrl у
        /// TorrServer), в фоне: добавление торрента не ждёт до 5 с. Пока идёт
        /// проверка, остаётся прежний постер
        /// </summary>
        void ApplyPoster(TorrEntry e, string poster)
        {
            if (string.IsNullOrEmpty(poster)) return;
            string existing;
            lock (sync) existing = e.Poster ?? "";
            if (poster == existing) return;
            if (!PosterGuard.ValidUrl(poster))
            {
                log(e.Hash + ": постер отклонён — не http(s)-ссылка");
                return;
            }
            Task.Run(async () =>
            {
                var r = await PosterGuard.CheckAsync(poster).ConfigureAwait(false);
                // Как в TorrServer: не картинка — нет; не проверили (сеть) — только если прежнего не было
                bool accept = r.Ok && (existing == "" || r.Verified);
                if (!accept)
                {
                    log(e.Hash + ": постер не принят — " + r.Reason + (existing != "" ? ", оставлен прежний" : ""));
                    return;
                }
                bool save;
                lock (sync)
                {
                    if ((e.Poster ?? "") != existing) return;   // пока проверяли, поставили другой
                    e.Poster = poster;
                    save = e.InDb;
                }
                if (save) SaveDb();
            });
        }

        /// <summary>
        /// Закрыть торрент. idleMs ≥ 0 — только если он и сейчас простаивает так
        /// долго (проверка под Gate: пока ждали, мог прийти запрос). true — закрыт
        /// </summary>
        async Task<bool> CloseAsync(TorrEntry e, long idleMs)
        {
            await e.Gate.WaitAsync().ConfigureAwait(false);
            try { return await CloseLockedAsync(e, idleMs).ConfigureAwait(false); }
            finally { e.Gate.Release(); }
        }

        async Task<bool> CloseLockedAsync(TorrEntry e, long idleMs)
        {
            TorrentManager m;
            PieceStore store;
            TorrReader[] readers;
            lock (sync)
            {
                if (idleMs >= 0 && (e.Manager == null || e.Readers.Count > 0 || e.Preloading || Now() < e.KeepUntilMs || Now() - e.LastActiveMs <= idleMs)) return false;
                m = e.Manager;
                store = e.Store;
                readers = e.Readers.ToArray();
                e.Manager = null;
                e.Store = null;
                // Открытие, которое ждёт Gate (запрос пришёл во время закрытия), — не трогаем
                if (e.Activation != null && e.Activation.IsCompleted) e.Activation = null;
                e.Readers.Clear();
                // Файлы — без привязки к закрытому TorrentManager: иначе запрос сразу
                // после закрытия (фронтенд шлёт drop и тут же открывает снова)
                // счёл бы метаданные готовыми и читал файлы старого менеджера
                e.Files = e.Files.Select(f => new FileStat { Id = f.Id, Path = f.Path, Length = f.Length }).ToList();
                e.Protect = new int[0];
                e.Window = ReadWindow.None;
                e.Preloading = false;
            }
            foreach (var r in readers) r.Closed = true;
            if (store != null) store.Dispose();
            if (m != null)
            {
                try
                {
                    // Ответ трекеров на «stopped» ждём недолго: пока идёт закрытие,
                    // новый запрос к этому торренту стоит у Gate (так 8 с ждал плеер)
                    await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await engine.RemoveAsync(m, RemoveMode.KeepAllData).ConfigureAwait(false);
                }
                catch (Exception ex) { log(e.Hash + ": закрытие — " + ex.Message); }
                log(e.Hash + ": закрыт");
            }
            try { if (e.SaveDir != null && Directory.Exists(e.SaveDir)) Directory.Delete(e.SaveDir, true); } catch { }
            return true;
        }

        // Раз в 2 с: торренты без читателей дольше TorrentDisconnectTimeout — закрыть.
        // Ждущие метаданные — не дольше 3 минут без обращений
        void CloseIdle()
        {
            var idle = new List<KeyValuePair<TorrEntry, long>>();
            long now = Now();
            var reports = new List<string>();
            var rotate = new List<KeyValuePair<TorrEntry, bool>>();
            lock (sync)
            {
                foreach (var e in entries.Values)
                {
                    if (e.Manager != null && e.Manager.HasMetadata && e.Manager.State == TorrentState.Downloading)
                        rotate.Add(new KeyValuePair<TorrEntry, bool>(e, NeedsDownload(e)));
                    if (e.Manager != null)
                    {
                        bool noInfo = !e.Manager.HasMetadata;
                        long every = noInfo ? 15000 : 30000;
                        if ((noInfo || e.Readers.Count > 0) && now - e.Diag.LastReportMs >= every && now - e.OpenedMs >= 10000)
                        {
                            e.Diag.LastReportMs = now;
                            reports.Add(Report(e, now));
                        }
                    }
                    if (e.Manager == null || e.Readers.Count > 0 || e.Preloading || now < e.KeepUntilMs) continue;
                    long limit = e.Manager.HasMetadata ? Settings.TorrentDisconnectTimeout * 1000L : 180000L;
                    if (now - e.LastActiveMs > limit) idle.Add(new KeyValuePair<TorrEntry, long>(e, limit));
                }
            }
            foreach (var r in reports) log(r);
            try { dhtBoot?.Tick(engine != null && engine.IsRunning, now); } catch (Exception ex) { log("DHT: " + ex.Message); }
            // Вне sync: PeerRotation ждёт главный цикл MonoTorrent, а его обработчики берут sync
            foreach (var kv in rotate)
            {
                string r = kv.Key.Rotation.Tick(engine, kv.Key.Manager, Settings.ConnectionsLimit, kv.Value, now);
                if (r != null) log(kv.Key.Hash + ": " + r);
                try { kv.Key.Pool.Tick(kv.Key.Manager); } catch (Exception ex) { log(kv.Key.Hash + ": запас пиров — " + ex.Message); }
            }
            foreach (var kv in idle)
            {
                var e = kv.Key;
                _ = CloseAsync(e, kv.Value).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result)
                        lock (sync) if (!e.InDb && e.Manager == null && e.Activation == null) entries.Remove(e.Hash);
                });
            }
        }

        // Есть что качать: идёт предзагрузка или в окне главного читателя есть
        // непроверенные куски. Окно скачано — скорость пиров упирается в нас, а не
        // в них, и сравнивать её незачем. Под sync
        static bool NeedsDownload(TorrEntry e)
        {
            if (e.Preloading) return true;
            var w = e.Window;
            var store = e.Store;
            if (w == null || w.Primary < 0 || store == null) return false;
            for (int p = w.Primary; p <= w.PrimaryEnd; p++)
                if (!store.IsVerified(p)) return true;
            return false;
        }

        // Под sync
        string Report(TorrEntry e, long now)
        {
            var m = e.Manager;
            var d = e.Diag;
            var sb = new System.Text.StringBuilder(e.Hash);
            sb.Append(m.HasMetadata ? ": " : ": без метаданных ").Append((now - e.OpenedMs) / 1000).Append(" с");
            sb.Append(" · ").Append(m.State);
            lock (d)
            {
                sb.Append(" · найдено пиров: трекеры ").Append(d.Tracker).Append(", DHT ").Append(d.Dht)
                  .Append(", PEX ").Append(d.Pex).Append(", сеть ").Append(d.Lpd);
                sb.Append(" · подключено ").Append(m.OpenConnections).Append(", подключаются ").Append(Connecting(m)).Append(", ждут ").Append(m.Peers.Available)
                  .Append(", в запасе службы ").Append(e.Pool.Extra).Append(" (подложено ").Append(e.Pool.Fed).Append(')')
                  .Append(", сидов ").Append(m.Peers.Seeds);
                if (d.Fails.Count > 0)
                    sb.Append(" · не подключились: ").Append(string.Join(", ", d.Fails.Select(kv => kv.Key + " " + kv.Value)));
                if (d.Trackers.Count > 0)
                    sb.Append(" · трекеры: ").Append(string.Join("; ", d.Trackers.Select(kv => kv.Key + " " + kv.Value)));
                else
                    sb.Append(" · трекеры ещё не ответили");
            }
            try { sb.Append(" · DHT ").Append(engine.Dht.State).Append(", узлов ").Append(engine.Dht.NodeCount); } catch { }
            if (m.HasMetadata)
                sb.Append(" · ").Append(m.Monitor.DownloadRate / 1024).Append(" КБ/с, в памяти ").Append((e.Store != null ? e.Store.Filled : 0) >> 20).Append(" МБ");
            if (m.Error != null) sb.Append(" · ошибка: ").Append(m.Error.Reason).Append(' ').Append(m.Error.Exception?.Message);
            return sb.ToString();
        }

        public void StopAll()
        {
            try { idleTimer?.Dispose(); } catch { }
            try { engine?.StopAllAsync(TimeSpan.FromSeconds(5)).Wait(8000); } catch { }
        }

        // ==================== читатели ====================

        public FileStat FileById(TorrEntry e, int id)
        {
            lock (sync) return e.Files.FirstOrDefault(f => f.Id == id);
        }

        /// <summary>Единственный файл раздачи (без заполнителей) или null</summary>
        public FileStat SingleFile(TorrEntry e)
        {
            lock (sync)
            {
                var real = e.Files.Where(f => f.File != null).Take(2).ToList();
                return real.Count == 1 ? real[0] : null;
            }
        }

        public TorrReader OpenReader(TorrEntry e, FileStat f, long position, long limit = -1)
        {
            var r = new TorrReader { Entry = e, Stat = f, Position = position, Limit = limit, Seq = Interlocked.Increment(ref readerSeq) };
            lock (sync)
            {
                e.Readers.Add(r);
                e.LastActiveMs = Now();
                UpdateProtect(e);
            }
            return r;
        }

        public void CloseReader(TorrReader r)
        {
            r.Closed = true;
            var e = r.Entry;
            lock (sync)
            {
                e.Readers.Remove(r);
                e.LastActiveMs = Now();
                UpdateProtect(e);
            }
        }

        int PieceLengthOf(TorrEntry e) { return e.Manager?.Torrent?.PieceLength ?? 0; }

        int AheadPieces(int pieceLength)
        {
            return (int)Math.Max(2, Settings.CacheSize * Settings.ReaderReadAHead / 100 / pieceLength);
        }

        int BehindPieces(int pieceLength)
        {
            return (int)Math.Max(1, Settings.CacheSize * (100 - Settings.ReaderReadAHead) / 100 / pieceLength);
        }

        static int PieceOf(TorrReader r, int pieceLength)
        {
            long pos = Math.Min(r.Position, Math.Max(0, r.Stat.Length - 1));
            return (int)((r.Stat.File.OffsetInTorrent + pos) / pieceLength);
        }

        // Окна читателей: что не вытеснять (Protect) и что качать (Window —
        // главный читатель последний открытый, у остальных — по паре кусков). Под sync
        // Служебный кэш: первые и последние max(кусок, 8 МБ) файла — заголовок
        // контейнера, индекс MKV, moov MP4 (у фильма на 2 часа — около 5 МБ). Плеер
        // просит их и при перемотках. Одного куска мало: при кусках по 256 КБ
        // последний мегабайт после прокрутки кэша качался заново (3,9 с). Объём —
        // как предзагрузка конца у TorrServer
        static IEnumerable<int> FileEnds(FileStat f, int pl)
        {
            long pin = Math.Max(pl, 8L << 20);
            long off = f.File.OffsetInTorrent, len = f.Length;
            if (len <= 0) yield break;
            int first = (int)(off / pl), last = f.File.EndPieceIndex;
            int headEnd = (int)((off + Math.Min(len, pin) - 1) / pl);
            int tailStart = (int)((off + Math.Max(0, len - pin)) / pl);
            for (int p = first; p <= headEnd; p++) yield return p;
            for (int p = Math.Max(tailStart, headEnd + 1); p <= last; p++) yield return p;
        }

        // Последний кусок окна: не дальше конца файла и предела читателя
        static int WindowEnd(TorrReader r, int pl, int want)
        {
            int end = Math.Min(r.Stat.File.EndPieceIndex, want);
            if (r.Limit > 0)
                end = Math.Min(end, (int)((r.Stat.File.OffsetInTorrent + Math.Min(r.Limit, r.Stat.Length) - 1) / pl));
            return end;
        }

        void UpdateProtect(TorrEntry e)
        {
            int pl = PieceLengthOf(e);
            var live = e.Readers.Where(r => !r.Closed && r.Stat.File != null).ToList();
            if (pl <= 0 || live.Count == 0)
            {
                e.Protect = new int[0];
                e.Window = ReadWindow.None;
                return;
            }
            int ahead = AheadPieces(pl), behind = BehindPieces(pl);
            var arr = new int[live.Count * 2];
            for (int i = 0; i < live.Count; i++)
            {
                int p = PieceOf(live[i], pl);
                arr[i * 2] = p - behind;
                arr[i * 2 + 1] = WindowEnd(live[i], pl, p + ahead);
            }
            e.Protect = arr;

            var primary = live.OrderByDescending(r => r.Seq).First();
            // Начало и конец файла главного читателя — служебный кэш, отдельно от кэша (PieceStore.SetPinned)
            if (e.Store != null && !ReferenceEquals(e.PinnedFile, primary.Stat.File))
            {
                e.PinnedFile = primary.Stat.File;
                e.Store.SetPinned(FileEnds(primary.Stat, pl));
            }
            int pp = PieceOf(primary, pl);
            e.Window = new ReadWindow
            {
                Primary = pp,
                PrimaryEnd = Math.Max(pp, WindowEnd(primary, pl, pp + ahead - 1)),
                PrimaryOffset = primary.Stat.File.OffsetInTorrent + Math.Min(primary.Position, Math.Max(0, primary.Stat.Length - 1)),
                Secondary = live.Where(r => r != primary).Select(r => PieceOf(r, pl)).Distinct().Where(x => x != pp).ToArray(),
            };
        }

        /// <summary>
        /// Прочитать с позиции читателя (не дальше конца куска): ждёт, пока кусок
        /// скачается. 0 — конец файла, −1 — прервано (aborted или торрент закрыт)
        /// </summary>
        public int Read(TorrReader r, byte[] buffer, int offset, int count, Func<bool> aborted)
        {
            var e = r.Entry;
            var f = r.Stat;
            if (r.Position >= f.Length) return 0;
            while (true)
            {
                PieceStore store;
                int pl;
                lock (sync)
                {
                    store = e.Store ?? EnsureStore(e);
                    pl = PieceLengthOf(e);
                    e.LastActiveMs = Now();
                }
                if (r.Closed || store == null || pl <= 0 || f.File == null) return -1;
                long tOff = f.File.OffsetInTorrent + r.Position;
                int piece = (int)(tOff / pl);
                int n = (int)Math.Min(count, Math.Min(f.Length - r.Position, (long)(piece + 1) * pl - tOff));
                // ResponsiveMode — блоки по мере прихода, иначе кусок целиком после проверки
                bool responsive = Settings.ResponsiveMode;
                if (responsive ? store.WaitAvailable(tOff, 250) : store.WaitVerified(piece, 250))
                {
                    var span = new Span<byte>(buffer, offset, n);
                    int got = responsive ? store.ReadAvailable(tOff, span) : store.ReadVerified(tOff, span);
                    if (got > 0)
                    {
                        int before = piece;
                        r.Position += got;
                        if ((int)((tOff + got) / pl) != before)
                            lock (sync) UpdateProtect(e);
                        return got;
                    }
                }
                if (aborted()) return -1;
            }
        }

        /// <summary>
        /// Предзагрузка, как у TorrServer (torr/preload.go): PreloadCache % кэша —
        /// начало файла и, параллельно, его конец (последние max(кусок, 8 МБ)). В
        /// конце MKV — индекс для перемотки, у многих MP4 — заголовок moov: плеер
        /// просит их сразу после старта, и без предзагрузки конца его качали уже
        /// при запуске («файл 1 с 4102 МБ» в логах с телевизоров). Блокирует до
        /// конца (или прерывания)
        /// </summary>
        public bool Preload(TorrEntry e, FileStat f, Func<bool> aborted)
        {
            long size = Math.Min(f.Length, Settings.CacheSize * Settings.PreloadCache / 100);
            if (size <= 0) return true;
            int pl;
            lock (sync) pl = PieceLengthOf(e);
            long tail = Math.Max(pl, 8L << 20);
            long headEnd = size - tail;
            if (headEnd < 0) headEnd = size;
            headEnd = Math.Min(headEnd, f.Length);
            long tailStart = f.Length - tail;
            bool withTail = tailStart > headEnd;
            if (!withTail) headEnd = size;   // файл небольшой: начало и конец сходятся
            long total = withTail ? headEnd + (f.Length - tailStart) : headEnd;

            // Конец — открываем первым: главный читатель (окно вперёд) — последний
            // открытый, им должно быть начало; конец идёт как второй читатель
            TorrReader rt = withTail ? OpenReader(e, f, tailStart) : null;
            // Окно начала — только до своего участка: иначе оно качало обычные
            // 60 МБ вперёд, забивало кэш и канал, пока конец файла ждал
            var rh = OpenReader(e, f, 0, headEnd);
            lock (sync) { e.Preloading = true; e.PreloadSize = total; e.PreloadedBytes = 0; }
            long headDone = 0, tailDone = 0;
            bool failed = false;
            Func<bool> stop = () => failed || aborted();
            Action report = () => { lock (sync) e.PreloadedBytes = Interlocked.Read(ref headDone) + Interlocked.Read(ref tailDone); };
            try
            {
                Task tailTask = Task.CompletedTask;
                if (rt != null)
                    tailTask = Task.Run(() =>
                    {
                        var tb = new byte[64 * 1024];
                        while (rt.Position < f.Length)
                        {
                            int n = Read(rt, tb, 0, (int)Math.Min(tb.Length, f.Length - rt.Position), stop);
                            if (n <= 0) { failed = true; return; }
                            Interlocked.Exchange(ref tailDone, rt.Position - tailStart);
                            report();
                        }
                    });
                var buf = new byte[64 * 1024];
                while (rh.Position < headEnd)
                {
                    int n = Read(rh, buf, 0, (int)Math.Min(buf.Length, headEnd - rh.Position), stop);
                    if (n <= 0) { failed = true; break; }
                    Interlocked.Exchange(ref headDone, rh.Position);
                    report();
                }
                // Начало готово — его читатель закрываем: конец становится главным
                // читателем с окном до конца файла (вторым — только 2 куска вперёд)
                CloseReader(rh);
                rh = null;
                tailTask.Wait();
                return !failed;
            }
            finally
            {
                lock (sync)
                {
                    e.Preloading = false;
                    e.KeepUntilMs = Now() + PreloadKeepMs;
                }
                if (rh != null) CloseReader(rh);
                if (rt != null) CloseReader(rt);
            }
        }

        // ==================== состояние для API ====================

        static readonly string[] StatNames = { "Torrent added", "Torrent getting info", "Torrent preload", "Torrent working", "Torrent closed", "Torrent in db" };

        int Stat(TorrEntry e)
        {
            if (e.Manager == null) return e.InDb ? 5 : 4;
            if (!e.Manager.HasMetadata) return 1;
            if (e.Preloading) return 2;
            return 3;
        }

        /// <summary>Состояние торрента полями TorrentStatus из TorrServer MatriX</summary>
        public void WriteStatus(JsonWriter w, TorrEntry e)
        {
            lock (sync)
            {
                int stat = Stat(e);
                var m = e.Manager;
                w.Obj()
                    .Prop("title", e.Title).Prop("category", e.Category).Prop("poster", e.Poster)
                    .Prop("data", string.IsNullOrEmpty(e.Data) ? FilesData(e) : e.Data)
                    .Prop("timestamp", e.Timestamp).Prop("name", e.Name).Prop("hash", e.Hash)
                    .Prop("stat", stat).Prop("stat_string", StatNames[stat])
                    .Prop("torrent_size", e.Size);
                if (m != null)
                {
                    long filled = e.Store != null ? e.Store.Filled : 0;
                    long buffered, bufferSize;
                    BufferState(e, filled, out buffered, out bufferSize);
                    w.Prop("loaded_size", filled)
                        .Prop("preloaded_bytes", buffered).Prop("preload_size", bufferSize)
                        .Prop("download_speed", (double)m.Monitor.DownloadRate).Prop("upload_speed", (double)m.Monitor.UploadRate)
                        // Ждут — очередь MonoTorrent и свой запас (PeerPool), как pending у anacrolix
                        .Prop("total_peers", m.Peers.Available + e.Pool.Extra + Connecting(m) + m.OpenConnections).Prop("pending_peers", m.Peers.Available + e.Pool.Extra)
                        .Prop("active_peers", m.OpenConnections).Prop("connected_seeders", m.Peers.Seeds)
                        .Prop("half_open_peers", Connecting(m))
                        .Prop("bytes_written", m.Monitor.DataBytesSent).Prop("bytes_read", m.Monitor.DataBytesReceived)
                        .Prop("bytes_read_data", m.Monitor.DataBytesReceived).Prop("bytes_read_useful_data", m.Monitor.DataBytesReceived);
                }
                w.Key("file_stats").Arr();
                foreach (var f in e.Files) w.Obj().Prop("id", f.Id).Prop("path", f.Path).Prop("length", f.Length).EndObj();
                w.EndArr().EndObj();
            }
        }

        // «TorrServer: …» в плеере — preloaded_bytes. Как у TorrServer
        // (torr/torrent.go: PreloadedBytes = cache.Filled): пока идёт предзагрузка —
        // её прогресс, дальше — сколько кэша занято (то же, что на странице).
        // Раньше здесь было «скачано и проверено подряд от позиции читателя»: с
        // ResponsiveMode читатель стоит в ещё не проверенном куске, и плеер
        // показывал 0–3 МБ при полном кэше. Под sync
        void BufferState(TorrEntry e, long filled, out long buffered, out long size)
        {
            if (e.Preloading || e.Store == null)
            {
                buffered = e.PreloadedBytes;
                size = e.PreloadSize;
                return;
            }
            // Скачанное, а не выделенное: начатый кусок по 8 МБ — не 8 МБ кэша
            buffered = e.Store.Downloaded;
            size = e.PreloadSize > 0 ? e.PreloadSize : e.Store.Capacity;
        }

        // TorrServer кладёт список файлов и в «data» — фронтенд читает и оттуда
        static string FilesData(TorrEntry e)
        {
            if (e.Files.Count == 0) return "";
            var w = new JsonWriter().Obj().Key("TorrServer").Obj().Key("Files").Arr();
            foreach (var f in e.Files) w.Obj().Prop("id", f.Id).Prop("path", f.Path).Prop("length", f.Length).EndObj();
            return w.EndArr().EndObj().EndObj().ToString();
        }

        /// <summary>POST /cache {"action":"get"} — как у TorrServer: куски в памяти и читатели</summary>
        class ReaderView
        {
            public int Piece, End, FileId;
            public long Pos, Length, Offset, Seq;
            public string Path;
            public bool Primary;
        }

        /// <summary>
        /// POST /cache — как у TorrServer (Capacity, Filled, Pieces, Readers…) и
        /// сверх того для страницы отладки: у читателя — файл, позиция, сколько
        /// скачано подряд вперёд (Ahead) из окна (Window); окно WindowRequester,
        /// откуда пиры; с peers — список пиров
        /// </summary>
        public void WriteCache(JsonWriter w, TorrEntry e, bool withPeers = false)
        {
            PieceStore store;
            int pl, ahead = 0, behind = 0;
            ReadWindow win;
            TorrentManager m;
            var readers = new List<ReaderView>();
            lock (sync)
            {
                store = e.Store;
                m = e.Manager;
                win = e.Window;
                pl = PieceLengthOf(e);
                if (pl > 0)
                {
                    ahead = AheadPieces(pl);
                    behind = BehindPieces(pl);
                    var live = e.Readers.Where(x => !x.Closed && x.Stat.File != null).ToList();
                    var primary = live.OrderByDescending(r => r.Seq).FirstOrDefault();
                    foreach (var r in live)
                    {
                        int p = PieceOf(r, pl);
                        readers.Add(new ReaderView
                        {
                            Piece = p, End = Math.Max(p, WindowEnd(r, pl, p + ahead - 1)),
                            FileId = r.Stat.Id, Path = r.Stat.Path, Pos = r.Position, Length = r.Stat.Length,
                            Offset = r.Stat.File.OffsetInTorrent, Seq = r.Seq, Primary = r == primary,
                        });
                    }
                }
            }
            var pieces = store != null ? store.Snapshot() : new List<PieceStore.PieceInfo>();

            w.Obj().Prop("Hash", e.Hash)
                .Prop("Capacity", store != null ? store.Capacity : Settings.CacheSize)
                .Prop("Filled", store != null ? store.Filled : 0)
                .Prop("PiecesLength", pl)
                .Prop("PiecesCount", store != null ? store.PieceCount : 0)
                .Prop("ReadAheadPieces", ahead).Prop("BehindPieces", behind)
                .Prop("Pinned", store != null ? store.PinnedBytes : 0)
                .Key("Torrent");
            WriteStatus(w, e);
            // Приоритет — как у TorrServer: 5 — кусок читателя, 4 — следующий, 3 — окно вперёд
            w.Key("Pieces").Obj();
            foreach (var pc in pieces)
            {
                int prio = 0;
                foreach (var r in readers)
                {
                    if (pc.Id == r.Piece) prio = Math.Max(prio, 5);
                    else if (pc.Id == r.Piece + 1) prio = Math.Max(prio, 4);
                    else if (pc.Id > r.Piece && pc.Id <= r.End) prio = Math.Max(prio, 3);
                }
                w.Key(pc.Id.ToString()).Obj().Prop("Id", pc.Id).Prop("Length", pc.Length).Prop("Size", pc.Size)
                    .Prop("Completed", pc.Verified).Prop("Priority", prio).EndObj();
            }
            w.EndObj().Key("Readers").Arr();
            foreach (var r in readers)
            {
                long left = Math.Max(0, r.Length - r.Pos);
                long window = Math.Min((long)ahead * pl, left);
                // Подряд от позиции — и непроверенные блоки: в ResponsiveMode плеер их читает
                long run = store != null ? (Settings.ResponsiveMode ? store.AvailableRun(r.Offset + r.Pos, window) : store.VerifiedRun(r.Offset + r.Pos, window)) : 0;
                int inWindow = 0;
                foreach (var pc in pieces) if (pc.Verified && pc.Id >= r.Piece && pc.Id <= r.End) inWindow++;
                w.Obj().Prop("Start", r.Piece).Prop("End", r.End).Prop("Reader", r.Piece)
                    .Prop("FileId", r.FileId).Prop("Path", r.Path).Prop("Pos", r.Pos).Prop("FileLength", r.Length)
                    .Prop("FileOffset", r.Offset).Prop("Primary", r.Primary).Prop("Seq", r.Seq)
                    .Prop("Ahead", run).Prop("Window", window)
                    .Prop("WindowPieces", r.End - r.Piece + 1).Prop("WindowReady", inWindow)
                    .EndObj();
            }
            w.EndArr();
            if (win != null && win.Primary >= 0)
            {
                w.Key("Window").Obj().Prop("Primary", win.Primary).Prop("PrimaryEnd", win.PrimaryEnd).Key("Secondary").Arr();
                foreach (int x in win.Secondary ?? new int[0]) w.Val(x);
                w.EndArr().EndObj();
            }
            var d = e.Diag;
            w.Key("Diag").Obj();
            lock (d)
            {
                w.Prop("Rotated", e.Rotation.Rotated).Prop("RotationSupported", PeerRotation.Supported);
                w.Prop("PoolKnown", e.Pool.Known).Prop("PoolExtra", e.Pool.Extra).Prop("PoolFed", e.Pool.Fed);
                w.Key("Found").Obj().Prop("tracker", d.Tracker).Prop("dht", d.Dht).Prop("pex", d.Pex).Prop("lpd", d.Lpd).EndObj();
                w.Key("Fails").Obj();
                foreach (var kv in d.Fails) w.Prop(kv.Key, kv.Value);
                w.EndObj().Key("Trackers").Obj();
                foreach (var kv in d.Trackers) w.Prop(kv.Key, kv.Value);
                w.EndObj();
            }
            if (m != null)
            {
                w.Prop("State", m.State.ToString());
                if (m.Error != null) w.Prop("Error", m.Error.Reason + " " + m.Error.Exception?.Message);
            }
            w.EndObj();
            if (withPeers && m != null)
            {
                w.Key("Peers").Arr();
                try
                {
                    var task = m.GetPeersAsync();
                    if (task.Wait(3000))
                        foreach (var p in task.Result.OrderByDescending(x => x.Monitor.DownloadRate))
                        {
                            w.Obj().Prop("Address", p.Uri != null ? p.Uri.Host + ":" + p.Uri.Port : "")
                                .Prop("Client", PeerClient(p.ClientApp))
                                .Prop("Down", p.Monitor.DownloadRate).Prop("Up", p.Monitor.UploadRate)
                                .Prop("Received", p.Monitor.DataBytesReceived)
                                .Prop("Seeder", p.IsSeeder)
                                .Prop("Have", p.BitField != null ? p.BitField.PercentComplete : 0.0)
                                .Prop("Requests", p.AmRequestingPiecesCount)
                                .Prop("Choking", p.IsChoking).Prop("Interested", p.AmInterested)
                                .Prop("Encryption", p.EncryptionType.ToString())
                                .Prop("Incoming", p.ConnectionDirection == Direction.Incoming)
                                .EndObj();
                        }
                }
                catch { }
                w.EndArr();
            }
            w.EndObj();
        }

        // Клиент пира: «qBittorrent -qB4630-»; неизвестный — печатные символы его id
        static string PeerClient(Software sw)
        {
            string id = new string((sw.ShortId ?? "").Where(ch => ch >= ' ' && ch <= '~').ToArray());
            return sw.Client != ClientApp.Unknown ? sw.Client + " " + id : id;
        }

        static string EngineVersion()
        {
            var a = typeof(ClientEngine).Assembly;
            var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string v = info != null ? info.InformationalVersion : a.GetName().Version.ToString();
            int plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }

        public void WriteVersion(JsonWriter w, int httpPort)
        {
            int active;
            long filled = 0;
            lock (sync)
            {
                active = entries.Values.Count(e => e.Manager != null);
                foreach (var e in entries.Values) if (e.Store != null) filled += e.Store.Filled;
            }
            w.Obj().Prop("name", "MonoServer").Prop("service", ServiceVersion).Prop("echo", EchoVersion)
                .Prop("engine", "MonoTorrent " + EngineVersion())
                .Prop("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
                .Prop("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
                .Prop("port", httpPort).Prop("peers_port", Settings.PeersListenPort)
                .Prop("cache_mb", Settings.CacheSize >> 20).Prop("memory_mb", filled >> 20)
                .Prop("gc_mb", GC.GetTotalMemory(false) >> 20)
                .Prop("torrents", entries.Count).Prop("active", active)
                .Prop("evict", CanForgetPieces)
                .Prop("dht", engine != null ? engine.Dht.State + ", узлов " + engine.Dht.NodeCount : "")
                .Prop("trackers", trackers != null ? trackers.Current.Length + " (" + trackers.Source + (trackers.UpdatedAt.HasValue ? ", " + trackers.UpdatedAt.Value.ToString("dd.MM HH:mm") : "") + ")" : "")
                .Prop("uptime_s", (long)(DateTime.UtcNow - StartedAt).TotalSeconds);
            if (StartError != null) w.Prop("error", StartError);
            w.EndObj();
        }
    }
}
