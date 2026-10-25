// TorrServer для Samsung Tizen 5.5 — обвязка Tizen. На Tizen 5.5 служба не может
// запустить ни одну программу и загрузить свою .so (см. ffprobe-service в TorrStream-Tizen), так что
// официальный TorrServer (Go) там не работает. Эта служба — TorrServer на C#:
// MonoTorrent (TorrEngine.cs) и API TorrServer MatriX на 8090 (HttpApi.cs), так
// что TorrStream и Lampa видят её как обычный TorrServer.
// Стартует вместе с телевизором (on-boot), ещё её будит страница пакета
// (app/). Данные — каталог данных приложения: база торрентов,
// настройки, кэш DHT и метаданных; сами фильмы — только в памяти.
using System;
using Tizen.Applications;

namespace TorrStreamTorrServer
{
    class Program : ServiceApplication
    {
        const string LogTag = "TorrStreamTorrServer";

        TorrServerHost host;

        protected override void OnCreate()
        {
            base.OnCreate();
            host = new TorrServerHost();
            try
            {
                host.Start(Application.Current.DirectoryInfo.Data, TorrServerHost.DefaultPort, msg => Tizen.Log.Info(LogTag, msg));
            }
            catch (Exception e)
            {
                Tizen.Log.Error(LogTag, "start: " + e);
                // Чаще всего — порт 8090 уже занят другим TorrServer (служба no-cover)
                ShowToast("MonoServer не запустился: " + e.Message);
            }
        }

        protected override void OnTerminate()
        {
            if (host != null) host.Stop();
            base.OnTerminate();
        }

        // Системное уведомление (org.tizen.alert-syspopup) — как у службы ffprobe;
        // только при ошибке, чтобы не мелькать на каждом включении телевизора
        static void ShowToast(string text)
        {
            try
            {
                var ctrl = new AppControl { ApplicationId = "org.tizen.alert-syspopup" };
                ctrl.ExtraData.Add("type", "toast");
                ctrl.ExtraData.Add("timeout", "8");
                ctrl.ExtraData.Add("text", text);
                AppControl.SendLaunchRequest(ctrl);
            }
            catch (Exception e)
            {
                Tizen.Log.Error(LogTag, "toast: " + e.Message);
            }
        }

        static void Main(string[] args)
        {
            new Program().Run(args);
        }
    }
}
