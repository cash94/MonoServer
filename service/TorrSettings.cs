// Настройки — с теми же именами, что BTSets у TorrServer MatriX (POST /settings
// {"action":"get"} / {"action":"set","sets":{…}}): страница TorrServer и
// клиенты вроде Lampa их и показывают. Понимаем не все — остальные
// сохраняем как пришли и отдаём обратно. Файл — settings.json в данных службы.
using System;
using System.Collections.Generic;
using System.IO;

namespace TorrStreamTorrServer
{
    public class TorrSettings
    {
        public long CacheSize = 64L << 20;      // байт на торрент, как у TorrServer
        public int ReaderReadAHead = 95;        // % кэша — вперёд от позиции читателя
        public int PreloadCache = 50;           // % кэша — предзагрузка
        // Пиров на торрент, как у TorrServer. До 1.0.14 было 50: незавершённые
        // подключения занимали места в лимите MonoTorrent — теперь под них запас
        // (PeerRotation.Headroom), а лишних и медленных служба отключает
        public int ConnectionsLimit = 25;
        public int TorrentDisconnectTimeout = 30; // с без читателей — и торрент закрывается
        public int PeersListenPort;             // 0 — выбрать при первом запуске
        public bool EnableDHT = true;
        public bool EnableUPNP = true;
        // uTP — пиры по UDP (Utp/). У TorrServer это DisableUTP — понимаем и его
        public bool EnableUTP = true;
        public int DownloadRateLimit;           // КБ/с, 0 — без ограничения
        public int UploadRateLimit;
        public int RetrackersMode = 1;          // 1 — добавлять ретрекеры к magnet
        // Как у TorrServer (по умолчанию включено): плееру — скачанные подряд блоки
        // куска, не дожидаясь проверки хэша всего куска. Иначе при кусках по 8 МБ
        // плеер секундами не получал ни байта и обрывал запрос
        public bool ResponsiveMode = true;
        // Внешний адрес, как PublicIPv4 у TorrServer: провайдер не даёт белого IP —
        // порт пробрасывается с VPS (WireGuard + nginx), и трекерам, DHT и пирам надо
        // сообщать адрес VPS. Можно «IP:порт» и имя хоста; пусто — не подменять.
        // PublicPort — порт на VPS, если он не тот же, что PeersListenPort (0 — тот же)
        public string PublicIPv4 = "";
        public int PublicPort;

        // Нижние границы: 0 и меньше на телевизоре сломали бы окно читателя
        public const long MinCache = 8L << 20;
        public const long MaxCache = 1024L << 20;

        // Версия набора умолчаний: settings.json пишется целиком (порт пиров при
        // первом запуске), и старое умолчание там не отличить от выбранного
        const int SettingsVersion = 4;

        Dictionary<string, object> extra = new Dictionary<string, object>();

        public static TorrSettings Load(string path)
        {
            var s = new TorrSettings();
            try
            {
                if (File.Exists(path))
                {
                    var o = Json.ParseObject(File.ReadAllText(path));
                    s.Apply(o);
                    // до 1.0.11 ResponsiveMode не было — false мог прийти от клиента и лежать без дела
                    if ((o.Num("TorrStreamSettingsVersion") ?? 1) < 3) s.ResponsiveMode = true;
                    // до 1.0.14 умолчание было 50 — на новое умолчание TorrServer
                    if ((o.Num("TorrStreamSettingsVersion") ?? 1) < 4 && s.ConnectionsLimit == 50) s.ConnectionsLimit = 25;
                }
            }
            catch { }
            return s;
        }

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public void Apply(Dictionary<string, object> o)
        {
            if (o == null) return;
            foreach (var kv in o)
            {
                switch (kv.Key)
                {
                    case "CacheSize": CacheSize = Clamp(o.Num(kv.Key) ?? CacheSize, MinCache, MaxCache); break;
                    case "ReaderReadAHead": ReaderReadAHead = (int)Clamp(o.Num(kv.Key) ?? ReaderReadAHead, 5, 100); break;
                    case "PreloadCache": PreloadCache = (int)Clamp(o.Num(kv.Key) ?? PreloadCache, 0, 100); break;
                    case "ConnectionsLimit": ConnectionsLimit = (int)Clamp(o.Num(kv.Key) ?? ConnectionsLimit, 5, 200); break;
                    case "TorrentDisconnectTimeout": TorrentDisconnectTimeout = (int)Clamp(o.Num(kv.Key) ?? TorrentDisconnectTimeout, 5, 3600); break;
                    case "PeersListenPort": PeersListenPort = (int)Clamp(o.Num(kv.Key) ?? PeersListenPort, 0, 65535); break;
                    case "EnableDHT": EnableDHT = o.Bool(kv.Key) ?? EnableDHT; break;
                    case "EnableUPNP": EnableUPNP = o.Bool(kv.Key) ?? EnableUPNP; break;
                    case "EnableUTP": EnableUTP = o.Bool(kv.Key) ?? EnableUTP; break;
                    case "DisableUTP": if (!o.ContainsKey("EnableUTP")) EnableUTP = !(o.Bool(kv.Key) ?? !EnableUTP); break;
                    case "DownloadRateLimit": DownloadRateLimit = (int)Clamp(o.Num(kv.Key) ?? 0, 0, int.MaxValue / 1024); break;
                    case "UploadRateLimit": UploadRateLimit = (int)Clamp(o.Num(kv.Key) ?? 0, 0, int.MaxValue / 1024); break;
                    case "RetrackersMode": RetrackersMode = (int)Clamp(o.Num(kv.Key) ?? RetrackersMode, 0, 3); break;
                    case "ResponsiveMode": ResponsiveMode = o.Bool(kv.Key) ?? ResponsiveMode; break;
                    case "PublicIPv4": PublicIPv4 = (o.Str(kv.Key) ?? "").Trim(); break;
                    case "PublicPort": PublicPort = (int)Clamp(o.Num(kv.Key) ?? 0, 0, 65535); break;
                    case "TorrStreamSettingsVersion": break;
                    default: extra[kv.Key] = kv.Value; break;
                }
            }
        }

        static long Clamp(long v, long min, long max) { return Math.Max(min, Math.Min(max, v)); }

        /// <summary>
        /// Внешний адрес из PublicIPv4 и PublicPort: хост (IP или имя) и порт; null — не задан.
        /// Порт: из «хост:порт», иначе PublicPort, иначе порт пиров
        /// </summary>
        public (string host, int port)? PublicEndPoint()
        {
            string s = (PublicIPv4 ?? "").Trim();
            if (s.Length == 0) return null;
            int port = PublicPort > 0 ? PublicPort : PeersListenPort;
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s.Substring(colon + 1), out int p) && p > 0 && p < 65536)
            {
                port = p;
                s = s.Substring(0, colon);
            }
            return (s, port);
        }

        public string ToJson()
        {
            var w = new JsonWriter().Obj()
                .Prop("CacheSize", CacheSize)
                .Prop("ReaderReadAHead", ReaderReadAHead)
                .Prop("PreloadCache", PreloadCache)
                .Prop("ConnectionsLimit", ConnectionsLimit)
                .Prop("TorrentDisconnectTimeout", TorrentDisconnectTimeout)
                .Prop("PeersListenPort", PeersListenPort)
                .Prop("EnableDHT", EnableDHT)
                .Prop("EnableUPNP", EnableUPNP)
                .Prop("EnableUTP", EnableUTP)
                .Prop("DisableUTP", !EnableUTP)
                .Prop("DownloadRateLimit", DownloadRateLimit)
                .Prop("UploadRateLimit", UploadRateLimit)
                .Prop("RetrackersMode", RetrackersMode)
                .Prop("ResponsiveMode", ResponsiveMode)
                .Prop("PublicIPv4", PublicIPv4)
                .Prop("PublicPort", PublicPort)
                // Кэш у нас всегда в памяти — на диск не пишем
                .Prop("UseDisk", false)
                .Prop("TorrStreamSettingsVersion", SettingsVersion);
            foreach (var kv in extra)
                if (kv.Key != "UseDisk" && kv.Key != "TorrStreamSettingsVersion") w.Key(kv.Key).Val(kv.Value);
            return w.EndObj().ToString();
        }
    }
}
