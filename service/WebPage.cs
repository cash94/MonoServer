// Веб-интерфейс на «/» — web/index.html, вшит в сборку (EmbeddedResource
// во всех .csproj): торренты и файлы, карта кэша с окнами читателей (сколько
// скачано вперёд), пиры, журнал вживую, настройки, пользователи, трафик,
// балансировка. Открывается с телефона или ПК: http://<IP телевизора>:8090/.
// Без входа admin вместо него — web/login.html: вход или первый вход по коду
// из журнала
using System.IO;

namespace TorrStreamTorrServer
{
    static class WebPage
    {
        static string html, login;

        public static string Html => html ?? (html = Load("web.index.html"));
        public static string Login => login ?? (login = Load("web.login.html"));

        static string Load(string name)
        {
            try
            {
                using (var s = typeof(WebPage).Assembly.GetManifestResourceStream(name))
                using (var r = new StreamReader(s))
                    return r.ReadToEnd();
            }
            catch
            {
                return "<!doctype html><meta charset=\"utf-8\"><title>MonoServer</title><p>Нет страницы в сборке: " + name + "</p>";
            }
        }
    }
}
