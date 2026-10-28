// Служба TorrServer на ПК — проверить движок и API без телевизора:
//   dotnet run -c Release -- 18090 %TEMP%\ts-mono
// Ctrl+C — остановить
using System;
using System.IO;
using System.Runtime.InteropServices;
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
            Action<string> journal = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m);
            var host = new TorrServerHost();
            host.Start(data, port, journal);
            var stop = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            // systemd (stop, restart, monoserver.sh update) останавливает SIGTERM: без
            // обработчика .NET завершал процесс сразу (код 143, «Failed» в systemctl),
            // host.Stop не вызывался — не сохранялись кэш DHT и последние изменения базы
            using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Set(); }))
            {
                stop.WaitOne();
                journal("остановка");
                // Движок закрывает торренты (до 8 с) и пишет кэш DHT; завис — выходим всё равно
                var done = new Thread(() => { try { host.Stop(); } catch { } }) { IsBackground = true };
                done.Start();
                done.Join(15000);
            }
            Console.Out.Flush();
            Environment.Exit(0);
        }
    }
}
