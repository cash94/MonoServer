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
        public long Seq;
        public volatile bool Closed;
    }

    public class TorrEngine
    {
        public const string ServiceVersion = "1.0.9";
        // Клиенты (Lampa и др.) по «MatriX» в /echo узнают API TorrServer MatriX
        public const string EchoVersion = "MatriX.136 (TorrStream " + ServiceVersion + ", MonoTorrent)";

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
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        ClientEngine engine;
        MemoryStorage storage;
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
                MaximumHalfOpenConnections = 16,
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
            var factories = Factories.Default.WithPieceWriterCreator(_ => storage)
                .WithPeerConnectionCreator("ipv4", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new PeerConnector()))
                .WithPeerConnectionCreator("ipv6", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new PeerConnector()));
            return new ClientEngine(b.ToSettings(), factories);
        }

        TorrentSettings TorrentSettingsFor()
        {
            return new TorrentSettingsBuilder
            {
                MaximumConnections = Settings.ConnectionsLimit,
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
                        Poster = item.Str("poster") ?? "",
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
                if (!string.IsNullOrEmpty(poster)) e.Poster = poster;
                if (!string.IsNullOrEmpty(data)) e.Data = data;
                if (!string.IsNullOrEmpty(category)) e.Category = category;
                if (string.IsNullOrEmpty(e.Title)) e.Title = (magnet != null ? magnet.Name : torrent?.Name) ?? hash;
                if (saveToDb && !e.InDb) { e.InDb = true; save = true; }
                else if (e.InDb && (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(poster) || !string.IsNullOrEmpty(data))) save = true;
                e.LastActiveMs = Now();
            }
            if (save) SaveDb();
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
                    await AddTrackersAsync(e, m, torrent != null ? (Settings.RetrackersMode == 1 ? Retrackers : new string[0]) : (IEnumerable<string>)magnet.AnnounceUrls).ConfigureAwait(false);
                    await m.ChangePickerAsync(new WindowRequester(() => e.Window)).ConfigureAwait(false);
                    m.PieceHashed += (s, a) =>
                    {
                        var store = e.Store;
                        if (store != null) store.MarkHashed(a.PieceIndex, a.HashPassed);
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
                    };
                    m.TrackerManager.AnnounceComplete += (s, a) =>
                    {
                        int peers = 0;
                        if (a.Peers != null) foreach (var l in a.Peers.Values) peers += l.Count;
                        lock (d) d.Trackers[a.Tracker.Uri.Host] = a.Successful ? "ок, пиров " + peers : "нет (" + a.Tracker.Status + ")";
                    };
                    m.TorrentStateChanged += (s, a) =>
                    {
                        if (a.NewState == TorrentState.Error) log(e.Hash + ": ошибка торрента — " + (m.Error != null ? m.Error.Reason + " " + m.Error.Exception?.Message : "?"));
                    };
                }
                lock (sync) e.Manager = m;
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

        // Файлы в порядке словаря info — номера как у TorrServer (с 1, вместе с
        // файлами-заполнителями). Не вышло — порядок MonoTorrent
        static List<FileStat> BuildFiles(TorrentManager m)
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
                if (poster != null) e.Poster = poster;
                if (data != null) e.Data = data;
                if (category != null) e.Category = category;
            }
            if (e.InDb) SaveDb();
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
            lock (sync)
            {
                foreach (var e in entries.Values)
                {
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
                sb.Append(" · подключено ").Append(m.OpenConnections).Append(", ждут ").Append(m.Peers.Available)
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

        public TorrReader OpenReader(TorrEntry e, FileStat f, long position)
        {
            var r = new TorrReader { Entry = e, Stat = f, Position = position, Seq = Interlocked.Increment(ref readerSeq) };
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
                arr[i * 2 + 1] = Math.Min(live[i].Stat.File.EndPieceIndex, p + ahead);
            }
            e.Protect = arr;

            var primary = live.OrderByDescending(r => r.Seq).First();
            int pp = PieceOf(primary, pl);
            e.Window = new ReadWindow
            {
                Primary = pp,
                PrimaryEnd = Math.Min(primary.Stat.File.EndPieceIndex, pp + ahead - 1),
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
                if (store.WaitVerified(piece, 250))
                {
                    int got = store.ReadVerified(tOff, new Span<byte>(buffer, offset, n));
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
        /// Предзагрузка, как у TorrServer: PreloadCache % кэша с начала файла.
        /// Блокирует до конца (или прерывания)
        /// </summary>
        public bool Preload(TorrEntry e, FileStat f, Func<bool> aborted)
        {
            long size = Math.Min(f.Length, Settings.CacheSize * Settings.PreloadCache / 100);
            if (size <= 0) return true;
            var r = OpenReader(e, f, 0);
            lock (sync) { e.Preloading = true; e.PreloadSize = size; e.PreloadedBytes = 0; }
            try
            {
                var buf = new byte[64 * 1024];
                while (r.Position < size)
                {
                    int n = Read(r, buf, 0, (int)Math.Min(buf.Length, size - r.Position), aborted);
                    if (n <= 0) return false;
                    lock (sync) e.PreloadedBytes = r.Position;
                }
                return true;
            }
            finally
            {
                lock (sync)
                {
                    e.Preloading = false;
                    e.KeepUntilMs = Now() + PreloadKeepMs;
                }
                CloseReader(r);
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
                        .Prop("total_peers", m.Peers.Available + m.OpenConnections).Prop("pending_peers", m.Peers.Available)
                        .Prop("active_peers", m.OpenConnections).Prop("connected_seeders", m.Peers.Seeds)
                        .Prop("half_open_peers", 0)
                        .Prop("bytes_written", m.Monitor.DataBytesSent).Prop("bytes_read", m.Monitor.DataBytesReceived)
                        .Prop("bytes_read_data", m.Monitor.DataBytesReceived).Prop("bytes_read_useful_data", m.Monitor.DataBytesReceived);
                }
                w.Key("file_stats").Arr();
                foreach (var f in e.Files) w.Obj().Prop("id", f.Id).Prop("path", f.Path).Prop("length", f.Length).EndObj();
                w.EndArr().EndObj();
            }
        }

        // «Буфер TorrServer» в плеере — preloaded_bytes. Пока идёт предзагрузка —
        // её прогресс; при просмотре — сколько скачано подряд вперёд от позиции
        // главного читателя (из размера окна); без читателей — сколько в кэше.
        // Раньше здесь навсегда оставался итог предзагрузки (32 МБ). Под sync
        void BufferState(TorrEntry e, long filled, out long buffered, out long size)
        {
            if (e.Preloading || e.Store == null)
            {
                buffered = e.PreloadedBytes;
                size = e.PreloadSize;
                return;
            }
            var primary = e.Readers.Where(r => !r.Closed && r.Stat.File != null).OrderByDescending(r => r.Seq).FirstOrDefault();
            int pl = PieceLengthOf(e);
            if (primary == null || pl <= 0)
            {
                buffered = filled;
                size = e.Store.Capacity;
                return;
            }
            long window = (long)AheadPieces(pl) * pl;
            long left = Math.Max(0, primary.Stat.Length - primary.Position);
            size = Math.Max(1, Math.Min(window, left));
            buffered = e.Store.VerifiedRun(primary.Stat.File.OffsetInTorrent + primary.Position, size);
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
        public void WriteCache(JsonWriter w, TorrEntry e)
        {
            PieceStore store;
            int pl;
            List<int[]> readers = new List<int[]>();
            lock (sync)
            {
                store = e.Store;
                pl = PieceLengthOf(e);
                if (pl > 0)
                    foreach (var r in e.Readers.Where(x => x.Stat.File != null))
                    {
                        int p = PieceOf(r, pl);
                        readers.Add(new[] { p, Math.Min(r.Stat.File.EndPieceIndex, p + AheadPieces(pl) - 1) });
                    }
            }
            w.Obj().Prop("Hash", e.Hash)
                .Prop("Capacity", store != null ? store.Capacity : Settings.CacheSize)
                .Prop("Filled", store != null ? store.Filled : 0)
                .Prop("PiecesLength", pl)
                .Prop("PiecesCount", store != null ? store.PieceCount : 0)
                .Key("Torrent");
            WriteStatus(w, e);
            w.Key("Pieces").Obj();
            if (store != null)
                foreach (var kv in store.Snapshot())
                    w.Key(kv.Key.ToString()).Obj().Prop("Id", kv.Key).Prop("Length", pl).Prop("Size", kv.Value ? pl : 0)
                        .Prop("Completed", kv.Value).Prop("Priority", 0).EndObj();
            w.EndObj().Key("Readers").Arr();
            foreach (var r in readers) w.Obj().Prop("Start", r[0]).Prop("End", r[1]).Prop("Reader", r[0]).EndObj();
            w.EndArr().EndObj();
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
            w.Obj().Prop("name", "TorrStream TorrServer").Prop("service", ServiceVersion).Prop("echo", EchoVersion)
                .Prop("engine", "MonoTorrent " + EngineVersion())
                .Prop("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
                .Prop("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
                .Prop("port", httpPort).Prop("peers_port", Settings.PeersListenPort)
                .Prop("cache_mb", Settings.CacheSize >> 20).Prop("memory_mb", filled >> 20)
                .Prop("gc_mb", GC.GetTotalMemory(false) >> 20)
                .Prop("torrents", entries.Count).Prop("active", active)
                .Prop("evict", CanForgetPieces)
                .Prop("dht", engine != null ? engine.Dht.State + ", узлов " + engine.Dht.NodeCount : "")
                .Prop("uptime_s", (long)(DateTime.UtcNow - StartedAt).TotalSeconds);
            if (StartError != null) w.Prop("error", StartError);
            w.EndObj();
        }
    }
}
