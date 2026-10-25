// Ретрекеры из ngosang/trackerslist (trackers_best_ip.txt — лучшие трекеры по
// IP, без DNS). Обновляются при старте службы в фоне, с таймаутом: GitHub
// бывает недоступен, и тогда берётся список из прошлого удачного обновления
// (trackers_best_ip.txt в данных службы), а без него — встроенный ниже.
// Добавляются к торренту отдельными уровнями (TorrEngine.AddTrackersAsync), в
// ссылку в базе не пишутся: список меняется, а старые адреса копились бы.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace TorrStreamTorrServer
{
    public class TrackerList
    {
        public const string Url = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best_ip.txt";
        const int TimeoutMs = 8000;

        // trackers_best_ip.txt на 24.10.2026 — если ни сети, ни файла
        static readonly string[] BuiltIn = {
            "udp://93.158.213.92:1337/announce",
            "udp://151.242.104.187:80/announce",
            "udp://89.234.156.205:451/announce",
            "udp://185.121.168.96:1337/announce",
            "udp://208.83.20.20:6969/announce",
            "udp://43.250.54.126:6969/announce",
            "udp://34.66.57.33:80/announce",
            "udp://34.66.57.33:1337/announce",
            "udp://109.201.134.183:80/announce",
            "udp://83.102.180.21:80/announce",
            "udp://23.157.120.14:6969/announce",
            "udp://135.125.198.235:1984/announce",
            "udp://211.75.205.188:6969/announce",
            "udp://45.137.199.107:6969/announce",
            "udp://211.75.205.187:80/announce",
            "http://211.75.205.187:6969/announce",
            "udp://209.141.59.25:6969/announce",
            "udp://65.109.28.17:6969/announce",
            "udp://95.217.80.20:6969/announce",
            "udp://95.217.80.22:6969/announce",
        };

        readonly string path;
        readonly Action<string> log;
        volatile string[] current;

        /// <summary>Откуда текущий список: «GitHub», «файл», «встроенный»</summary>
        public string Source { get; private set; }
        public DateTime? UpdatedAt { get; private set; }

        public TrackerList(string path, Action<string> log)
        {
            this.path = path;
            this.log = log ?? (_ => { });
            current = BuiltIn;
            Source = "встроенный";
            try
            {
                if (File.Exists(path))
                {
                    var saved = Parse(File.ReadAllText(path));
                    if (saved.Length > 0)
                    {
                        current = saved;
                        Source = "файл";
                        UpdatedAt = File.GetLastWriteTime(path);
                    }
                }
            }
            catch (Exception e) { this.log("ретрекеры: " + e.Message); }
        }

        public string[] Current => current;

        static string[] Parse(string text)
        {
            return (text ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#") && Uri.TryCreate(l, UriKind.Absolute, out var u)
                    && (u.Scheme == "udp" || u.Scheme == "http" || u.Scheme == "https"))
                .Distinct()
                .ToArray();
        }

        /// <summary>Обновить с GitHub в фоне; не дольше TimeoutMs, ошибки — в журнал</summary>
        public void RefreshInBackground()
        {
            Task.Run(async () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var http = Http.Client(TimeSpan.FromMilliseconds(TimeoutMs)))
                    {
                        var get = http.GetStringAsync(Url);
                        // HttpClient netstandard2.0 на Tizen может застрять в DNS мимо Timeout
                        if (await Task.WhenAny(get, Task.Delay(TimeoutMs)).ConfigureAwait(false) != get)
                        {
                            log("ретрекеры: GitHub не ответил за " + TimeoutMs / 1000 + " с — список «" + Source + "», " + current.Length + " шт.");
                            return;
                        }
                        var list = Parse(await get.ConfigureAwait(false));
                        if (list.Length < 3)
                        {
                            log("ретрекеры: в ответе GitHub " + list.Length + " адресов — оставлен список «" + Source + "»");
                            return;
                        }
                        current = list;
                        Source = "GitHub";
                        UpdatedAt = DateTime.Now;
                        try { File.WriteAllText(path, string.Join("\n", list) + "\n"); } catch (Exception e) { log("ретрекеры: " + e.Message); }
                        log("ретрекеры: с GitHub " + list.Length + " шт. за " + (t.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с");
                    }
                }
                catch (Exception e)
                {
                    var inner = e.GetBaseException();
                    log("ретрекеры: GitHub недоступен (" + inner.Message + ") — список «" + Source + "», " + current.Length + " шт.");
                }
            });
        }
    }
}
