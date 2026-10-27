// Служба TorrServer на ПК — проверить движок и API без телевизора:
//   dotnet run -c Release -- 18090 %TEMP%\ts-mono
// Ctrl+C — остановить
using System;
using System.IO;
using System.Threading;

namespace TorrStreamTorrServer
{
    static class PcMain
    {
        static void Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0]) : TorrServerHost.DefaultPort;
            string data = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "torrstream-torrserver");
            // База пользователей — LiteDB (на Tizen — JSON, см. AccountsStore.cs);
            // MONOSERVER_STORE=json — как на Tizen, проверить то хранилище без телевизора
            if (Environment.GetEnvironmentVariable("MONOSERVER_STORE") != "json")
                Accounts.StoreFactory = (dir, log) => new LiteDbAccountsStore(dir, log);
            var host = new TorrServerHost();
            host.Start(data, port, m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m));
            var stop = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            stop.WaitOne();
            host.Stop();
        }
    }
}
