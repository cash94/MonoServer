// Служба целиком — движок и HTTP; одна и та же на телевизоре (Program.cs) и на
// ПК (pc/Main.cs, проверка консолью)
using System;

namespace TorrStreamTorrServer
{
    public class TorrServerHost
    {
        public const int DefaultPort = 8090;

        public readonly LogRing Journal = new LogRing(2000);
        public TorrEngine Engine { get; private set; }
        public HttpApi Api { get; private set; }

        /// <summary>Порт занят (стоит другой TorrServer) — исключение от HttpApi.Start</summary>
        public void Start(string dataDir, int port, Action<string> sink)
        {
            Action<string> log = m =>
            {
                Journal.Add(m);
                if (sink != null) sink(m);
            };
            log("MonoServer " + TorrEngine.ServiceVersion + ", " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            // HTTP — первым: занят порт — движок и не нужен
            Engine = new TorrEngine(dataDir, log);
            Api = new HttpApi(Engine, port, log, Journal);
            Api.Start();
            Engine.Start();
        }

        public void Stop()
        {
            if (Api != null) Api.Stop();
            if (Engine != null) Engine.StopAll();
        }
    }
}
