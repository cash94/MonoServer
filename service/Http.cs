// HttpClient для запросов службы наружу (ретрекеры, постеры, .torrent по ссылке,
// HTTP-трекеры и веб-сиды MonoTorrent). Обычно — штатный.
// На Android служба работает в Mono из Termux, а он собран без TLS (--disable-btls):
// там Main подставляет свой обработчик с HTTPS через OpenSSL (mono/OpenSslHttpHandler.cs)
using System;
using System.Net.Http;
using System.Net.Sockets;

namespace TorrStreamTorrServer
{
    public static class Http
    {
        /// <summary>
        /// Свой обработчик запросов (семейство адресов — для HTTP-трекеров MonoTorrent,
        /// Unspecified — любое) или null — штатный HttpClientHandler
        /// </summary>
        public static Func<AddressFamily, HttpMessageHandler> HandlerFactory;

        public static HttpClient Client(TimeSpan timeout, AddressFamily family = AddressFamily.Unspecified)
        {
            var handler = HandlerFactory != null ? HandlerFactory(family) : null;
            var c = handler != null ? new HttpClient(handler) : new HttpClient();
            c.Timeout = timeout;
            return c;
        }
    }
}
