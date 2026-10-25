// Проверка постера, как у TorrServer (torr/utils/webImageChecker.go: CheckImgUrl).
// Постер — ссылка, которую клиенты (TorrStream, Lampa, страница службы) кладут
// в <img src> и CSS. Без проверки туда можно записать javascript:, data:text/html,
// SVG со скриптом или страницу, которая выдаёт себя за картинку. Поэтому:
//  - только http(s) с адресом сервера, без пробелов, кавычек и угловых скобок;
//  - служба сама скачивает начало файла (не дольше 5 с) и проверяет, что это
//    JPEG, PNG, GIF или WebP — по сигнатуре, как декодер TorrServer отличает
//    картинку от HTML/SVG;
//  - сервер не ответил (сеть, таймаут) — «можно, но не проверено»: новый
//    постер берём, только если прежнего не было (как в TorrServer).
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TorrStreamTorrServer
{
    public static class PosterGuard
    {
        const int TimeoutMs = 5000;
        const int MaxLength = 2048;

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = Http.Client(TimeSpan.FromMilliseconds(TimeoutMs));
            // Некоторые хостинги картинок (fastpic) отвечают 404 «неизвестному» агенту
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            return c;
        }

        /// <summary>Ссылка пригодна в принципе: http(s), есть сервер, нет опасных символов</summary>
        public static bool ValidUrl(string link)
        {
            if (string.IsNullOrEmpty(link) || link.Length > MaxLength) return false;
            if (link.Any(ch => ch <= ' ' || ch == '"' || ch == '\'' || ch == '<' || ch == '>' || ch == '`' || ch == '\\')) return false;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) return false;
            return (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);
        }

        public struct Result
        {
            public bool Ok;         // можно сохранить
            public bool Verified;   // скачали и это действительно картинка
            public string Reason;   // почему нет (для журнала)
        }

        static Result R(bool ok, bool verified, string reason) { return new Result { Ok = ok, Verified = verified, Reason = reason }; }

        public static async Task<Result> CheckAsync(string link)
        {
            if (!ValidUrl(link)) return R(false, false, "не http(s)-ссылка");
            using (var cts = new CancellationTokenSource(TimeoutMs))
            {
                try
                {
                    var get = http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    // HttpClient netstandard2.0 на Tizen может застрять в DNS мимо таймаута
                    if (await Task.WhenAny(get, Task.Delay(TimeoutMs + 1000)).ConfigureAwait(false) != get)
                        return R(true, false, "сервер не ответил");
                    using (var resp = await get.ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return R(false, false, "ответ " + (int)resp.StatusCode);
                        var head = new byte[16];
                        int got = 0;
                        using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            while (got < head.Length)
                            {
                                int n = await s.ReadAsync(head, got, head.Length - got, cts.Token).ConfigureAwait(false);
                                if (n <= 0) break;
                                got += n;
                            }
                        }
                        string kind = ImageKind(head, got);
                        return kind != null ? R(true, true, kind) : R(false, false, "это не картинка (JPEG, PNG, GIF, WebP)");
                    }
                }
                catch (Exception e)
                {
                    // Сеть или таймаут — как в TorrServer: «можно, но не проверено»
                    return R(true, false, e.GetBaseException().Message);
                }
            }
        }

        /// <summary>Формат по сигнатуре или null</summary>
        public static string ImageKind(byte[] b, int n)
        {
            if (n >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
            if (n >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "PNG";
            if (n >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "GIF";
            if (n >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "WebP";
            return null;
        }
    }
}
