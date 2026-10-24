// Страница отладки на «/» — web/index.html, вшита в сборку (EmbeddedResource
// в обоих .csproj): торренты и файлы, карта кэша с окнами читателей (сколько
// скачано вперёд), пиры, журнал вживую, настройки. Открывается с телефона или
// ПК: http://<IP телевизора>:8090/
using System.IO;
using System.Reflection;

namespace TorrStreamTorrServer
{
    static class WebPage
    {
        static string html;

        public static string Html
        {
            get
            {
                if (html != null) return html;
                try
                {
                    using (var s = typeof(WebPage).Assembly.GetManifestResourceStream("web.index.html"))
                    using (var r = new StreamReader(s))
                        html = r.ReadToEnd();
                }
                catch
                {
                    html = "<!doctype html><meta charset=\"utf-8\"><title>TorrServer</title><p>Нет страницы в сборке. " +
                        "<a href=\"/version\">/version</a> · <a href=\"/log\">/log</a></p>";
                }
                return html;
            }
        }
    }
}
