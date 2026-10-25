package online.torrstream.monoserver

import android.content.Context
import android.system.Os
import android.util.Base64
import java.io.File
import java.net.Inet4Address
import java.net.NetworkInterface
import java.security.KeyStore
import java.security.cert.X509Certificate
import java.util.zip.ZipInputStream

/**
 * Пути, адреса и подготовка запуска службы.
 *
 * Служба — TorrServer на C# (MonoTorrent, исходники service/ этого репозитория) под Mono
 * из пакетов Termux, собранным под bionic, как всё на Android:
 * - программа mono и её библиотеки лежат в APK как lib*.so (jniLibs): система распаковывает
 *   их в nativeLibraryDir, откуда разрешён запуск (tools/fetch-runtime.py);
 * - сборки Mono, его конфиги и сама служба — assets/torrserver.zip, распаковывается в
 *   память приложения при первом запуске новой версии APK.
 */
object Env {
    /** 8099, а не 8090: рядом может работать обычный TorrServer — он на 8090 */
    const val DEFAULT_PORT = 8099

    private fun prefs(ctx: Context) = ctx.getSharedPreferences("server", Context.MODE_PRIVATE)
    fun port(ctx: Context) = prefs(ctx).getInt("port", DEFAULT_PORT)
    fun setPort(ctx: Context, port: Int) = prefs(ctx).edit().putInt("port", port).apply()

    fun nativeDir(ctx: Context) = File(ctx.applicationInfo.nativeLibraryDir)
    fun mono(ctx: Context) = File(nativeDir(ctx), "libmono.so")
    /** Ссылки на библиотеки с настоящими именами — для LD_LIBRARY_PATH */
    fun libDir(ctx: Context) = File(ctx.filesDir, "lib")
    /** Распакованный torrserver.zip: mono/ (сборки, конфиги) и app/ (служба) */
    fun runtime(ctx: Context) = File(ctx.filesDir, "runtime")
    fun monoRoot(ctx: Context) = File(runtime(ctx), "mono")
    fun serviceExe(ctx: Context) = File(runtime(ctx), "app/TorrServer.exe")
    /** Данные службы: база торрентов, settings.json, кэш DHT и метаданных */
    fun dataDir(ctx: Context) = File(ctx.filesDir, "data").apply { mkdirs() }
    fun settingsFile(ctx: Context) = File(dataDir(ctx), "settings.json")
    fun certFile(ctx: Context) = File(ctx.filesDir, "cacerts.pem")
    fun serverLog(ctx: Context) = File(ctx.filesDir, "server.log")

    /** Версии службы и Mono из torrserver.zip (version.json) */
    fun versions(ctx: Context): Pair<String?, String?> = try {
        val j = org.json.JSONObject(File(runtime(ctx), "version.json").readText())
        j.optString("torrserver").ifEmpty { null } to j.optString("mono").ifEmpty { null }
    } catch (_: Exception) {
        null to null
    }

    /** IPv4-адреса устройства в локальной сети — по ним клиенты найдут службу */
    fun lanAddresses(): List<String> = try {
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            .filter { it.isUp && !it.isLoopback }
            // Мобильный интернет (rmnet, ccmni…) из домашней сети недоступен — Wi-Fi, Ethernet и точка доступа впереди
            .sortedBy { if (it.name.startsWith("wlan") || it.name.startsWith("eth") || it.name.startsWith("ap") || it.name.startsWith("swlan")) 0 else 1 }
            .flatMap { ni -> ni.inetAddresses.toList().filterIsInstance<Inet4Address>().map { it.hostAddress ?: "" } }
            .filter { it.isNotEmpty() }
            .distinct()
    } catch (_: Exception) {
        emptyList()
    }

    /**
     * Библиотеки лежат в nativeLibraryDir под закодированными именами (fetch-runtime.py):
     * libdep_<hex имени>.so — сама библиотека, libdeplink_<hex ссылки>_<hex цели>.so — пустая
     * метка символической ссылки. Здесь — ссылки с настоящими именами (libssl.so.3…),
     * по ним их находит загрузчик (LD_LIBRARY_PATH) и Mono (P/Invoke).
     */
    fun prepareLibs(ctx: Context) {
        val native = nativeDir(ctx)
        val dir = libDir(ctx)
        dir.deleteRecursively()
        dir.mkdirs()
        fun decode(hex: String) = String(hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray())
        native.listFiles().orEmpty().forEach { f ->
            val n = f.name
            when {
                n.startsWith("libdeplink_") -> {
                    val (link, target) = n.removePrefix("libdeplink_").removeSuffix(".so").split('_')
                    val real = File(native, "libdep_$target.so")
                    if (real.exists()) Os.symlink(real.absolutePath, File(dir, decode(link)).absolutePath)
                }
                n.startsWith("libdep_") ->
                    Os.symlink(f.absolutePath, File(dir, decode(n.removePrefix("libdep_").removeSuffix(".so"))).absolutePath)
            }
        }
    }

    /** torrserver.zip → files/runtime; заново — только когда сменился APK. true — распаковали */
    fun prepareRuntime(ctx: Context): Boolean {
        val dir = runtime(ctx)
        val stamp = ctx.packageManager.getPackageInfo(ctx.packageName, 0).lastUpdateTime.toString()
        val marker = File(dir, ".installed")
        if (marker.isFile && marker.readText() == stamp && serviceExe(ctx).isFile) return false
        dir.deleteRecursively()
        dir.mkdirs()
        ZipInputStream(ctx.assets.open("torrserver.zip")).use { zip ->
            while (true) {
                val e = zip.nextEntry ?: break
                val out = File(dir, e.name)
                // Защита от «../» в именах архива
                if (!out.canonicalPath.startsWith(dir.canonicalPath)) continue
                if (e.isDirectory) out.mkdirs()
                else {
                    out.parentFile?.mkdirs()
                    out.outputStream().use { zip.copyTo(it) }
                }
            }
        }
        marker.writeText(stamp)
        return true
    }

    /**
     * Корневые сертификаты для HTTPS службы (OpenSSL): все доверенные из хранилища Android —
     * системные и добавленные пользователем — одним PEM-файлом (SSL_CERT_FILE).
     * Каталог /system/etc/security/cacerts OpenSSL 3 сам не прочтёт: там старые имена хэшей.
     */
    fun writeCerts(ctx: Context): File {
        val out = certFile(ctx)
        val sb = StringBuilder()
        try {
            val ks = KeyStore.getInstance("AndroidCAStore")
            ks.load(null, null)
            for (alias in ks.aliases()) {
                val cert = ks.getCertificate(alias) as? X509Certificate ?: continue
                sb.append("-----BEGIN CERTIFICATE-----\n")
                sb.append(Base64.encodeToString(cert.encoded, Base64.DEFAULT))
                sb.append("-----END CERTIFICATE-----\n")
            }
        } catch (_: Exception) {
            // Нет хранилища — хотя бы системные файлы как есть (в них PEM)
            File("/system/etc/security/cacerts").listFiles().orEmpty().forEach {
                try { sb.append(it.readText()).append('\n') } catch (_: Exception) { }
            }
        }
        out.writeText(sb.toString())
        return out
    }

    /** Строка цифр — порт, иначе null */
    fun parsePort(s: String): Int? = s.trim().toIntOrNull()?.takeIf { it in 1024..65535 }
}
