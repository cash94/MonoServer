// TLS для Mono из Termux: он собран без TLS (--disable-btls), SslStream там не работает.
// Здесь — клиентское соединение OpenSSL прямо на дескрипторе сокета. libssl.so.3 и
// libcrypto.so.3 — из Termux, как и сам Mono (приложение кладёт их в LD_LIBRARY_PATH).
// Корневые сертификаты — файл SSL_CERT_FILE: его пишет приложение из хранилища Android
// (системные и добавленные пользователем).
using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TorrStreamTorrServer
{
    static class OpenSsl
    {
        const string Ssl = "libssl.so.3";
        const string Crypto = "libcrypto.so.3";

        const int SSL_VERIFY_PEER = 1;
        const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55;
        const int SSL_CTRL_SET_MIN_PROTO_VERSION = 123;
        const int TLSEXT_NAMETYPE_host_name = 0;
        const int TLS1_2_VERSION = 0x0303;
        public const int SSL_ERROR_ZERO_RETURN = 6;

        [DllImport(Ssl)] static extern int OPENSSL_init_ssl(ulong opts, IntPtr settings);
        [DllImport(Ssl)] static extern IntPtr TLS_client_method();
        [DllImport(Ssl)] static extern IntPtr SSL_CTX_new(IntPtr method);
        [DllImport(Ssl)] static extern int SSL_CTX_load_verify_locations(IntPtr ctx, string file, string path);
        [DllImport(Ssl)] static extern void SSL_CTX_set_verify(IntPtr ctx, int mode, IntPtr callback);
        // long в C — 32 бита на armeabi-v7a и 64 на arm64: IntPtr совпадает с ним везде
        [DllImport(Ssl)] static extern IntPtr SSL_CTX_ctrl(IntPtr ctx, int cmd, IntPtr larg, IntPtr parg);
        [DllImport(Ssl)] public static extern IntPtr SSL_new(IntPtr ctx);
        [DllImport(Ssl)] public static extern void SSL_free(IntPtr ssl);
        [DllImport(Ssl)] public static extern int SSL_set_fd(IntPtr ssl, int fd);
        [DllImport(Ssl)] static extern IntPtr SSL_ctrl(IntPtr ssl, int cmd, IntPtr larg, IntPtr parg);
        [DllImport(Ssl)] static extern int SSL_set1_host(IntPtr ssl, string host);
        [DllImport(Ssl)] public static extern int SSL_connect(IntPtr ssl);
        [DllImport(Ssl)] public static extern unsafe int SSL_read(IntPtr ssl, byte* buf, int num);
        [DllImport(Ssl)] public static extern unsafe int SSL_write(IntPtr ssl, byte* buf, int num);
        [DllImport(Ssl)] public static extern int SSL_shutdown(IntPtr ssl);
        [DllImport(Ssl)] public static extern int SSL_get_error(IntPtr ssl, int ret);
        [DllImport(Ssl)] static extern IntPtr SSL_get_verify_result(IntPtr ssl);
        [DllImport(Crypto)] static extern IntPtr OpenSSL_version(int type);
        [DllImport(Crypto)] static extern UIntPtr ERR_get_error();
        [DllImport(Crypto)] static extern void ERR_clear_error();
        [DllImport(Crypto)] static extern void ERR_error_string_n(UIntPtr e, byte[] buf, UIntPtr len);
        [DllImport(Crypto)] static extern IntPtr X509_verify_cert_error_string(IntPtr n);

        static IntPtr ctx;
        public static string Version { get; private set; }

        /// <summary>Загрузить OpenSSL и сертификаты; null — готово, иначе причина</summary>
        public static string Init(string certFile)
        {
            try
            {
                OPENSSL_init_ssl(0, IntPtr.Zero);
                Version = Marshal.PtrToStringAnsi(OpenSSL_version(0));
                var c = SSL_CTX_new(TLS_client_method());
                if (c == IntPtr.Zero) return "SSL_CTX_new: " + LastError();
                SSL_CTX_ctrl(c, SSL_CTRL_SET_MIN_PROTO_VERSION, (IntPtr)TLS1_2_VERSION, IntPtr.Zero);
                if (string.IsNullOrEmpty(certFile) || SSL_CTX_load_verify_locations(c, certFile, null) != 1)
                    return "нет корневых сертификатов (" + (certFile ?? "SSL_CERT_FILE не задан") + ") " + LastError();
                SSL_CTX_set_verify(c, SSL_VERIFY_PEER, IntPtr.Zero);
                ctx = c;
                return null;
            }
            catch (Exception e)
            {
                // DllNotFoundException, EntryPointNotFoundException — нет OpenSSL
                return e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Рукопожатие TLS на подключённом блокирующем сокете; SSL* или исключение</summary>
        public static IntPtr Connect(Socket socket, string host)
        {
            if (ctx == IntPtr.Zero) throw new InvalidOperationException("OpenSSL не загружен");
            ERR_clear_error();
            var ssl = SSL_new(ctx);
            if (ssl == IntPtr.Zero) throw new System.IO.IOException("SSL_new: " + LastError());
            var name = Marshal.StringToHGlobalAnsi(host);
            try
            {
                SSL_set_fd(ssl, socket.Handle.ToInt32());
                // SNI и проверка имени в сертификате
                SSL_ctrl(ssl, SSL_CTRL_SET_TLSEXT_HOSTNAME, (IntPtr)TLSEXT_NAMETYPE_host_name, name);
                SSL_set1_host(ssl, host);
                int r = SSL_connect(ssl);
                if (r != 1)
                {
                    long verify = SSL_get_verify_result(ssl).ToInt64();
                    string why = verify != 0
                        ? "сертификат: " + Marshal.PtrToStringAnsi(X509_verify_cert_error_string((IntPtr)verify))
                        : LastError(SSL_get_error(ssl, r));
                    SSL_free(ssl);
                    throw new System.IO.IOException("TLS " + host + ": " + why);
                }
                return ssl;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }

        public static string LastError(int sslError = 0)
        {
            var e = ERR_get_error();
            if (e == UIntPtr.Zero) return sslError != 0 ? "SSL_get_error " + sslError : "";
            var buf = new byte[256];
            ERR_error_string_n(e, buf, (UIntPtr)buf.Length);
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n);
        }
    }
}
