// Служба на Android: программа под Mono из Termux, её запускает и держит приложение
// android/ (ServerService). Аргументы — порт и каталог данных, как у pc/Main.cs.
// Журнал — в stdout (приложение показывает его на экране «Журнал»).
// Остановка — строка «stop» или конец stdin: так служба не переживёт приложение и не
// займёт порт, если процесс приложения убит; SIGTERM — запасной путь.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TorrStreamTorrServer
{
    static class MonoMain
    {
        static void Main(string[] args)
        {
            // Mono берёт кодировку консоли из LANG — журнал по-русски должен идти в UTF-8 всегда
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
            Action<string> log = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m);

            int port = args.Length > 0 ? int.Parse(args[0]) : TorrServerHost.DefaultPort;
            string data = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "torrstream-torrserver");
            Directory.CreateDirectory(data);

            // Mono из Termux собран без TLS — HTTPS через OpenSSL (OpenSslHttpHandler)
            string tls = OpenSsl.Init(Environment.GetEnvironmentVariable("SSL_CERT_FILE"));
            if (tls == null)
            {
                Http.HandlerFactory = family => new OpenSslHttpHandler(family);
                log("HTTPS: " + OpenSsl.Version);
            }
            else log("HTTPS недоступен: " + tls);

            var host = new TorrServerHost();
            try
            {
                host.Start(data, port, log);
            }
            catch (Exception e)
            {
                log("не запустился: " + e.Message);
                Environment.Exit(2);
            }

            var stop = new ManualResetEvent(false);
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.In.ReadLine()) != null)
                        if (line.Trim() == "stop") break;
                }
                catch { }
                stop.Set();
            }) { IsBackground = true, Name = "stdin" }.Start();
            stop.WaitOne();
            log("остановка");
            // Движок закрывает торренты и пишет кэш DHT; завис — выходим всё равно
            var done = new Thread(() => { try { host.Stop(); } catch { } }) { IsBackground = true };
            done.Start();
            done.Join(5000);
            Environment.Exit(0);
        }
    }
}
