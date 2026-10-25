package online.torrstream.monoserver

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.TimeUnit

/**
 * MonoServer: служба TorrServer (C#, MonoTorrent) под Mono — дочерний процесс
 * `libmono.so MonoServer.exe <порт> <данные>`, к нему подключаются TorrStream, Lampa и
 * другие клиенты TorrServer по http://<IP>:8099.
 *
 * Foreground-сервис с уведомлением, плюс WakeLock и WifiLock: с погасшим экраном
 * устройство иначе усыпляет процессор и Wi-Fi, и клиент теряет сервер посреди фильма.
 * Упавшая служба перезапускается, но не чаще [MAX_RESTARTS] раз за [RESTART_WINDOW_MS].
 *
 * Остановка — строкой «stop» в stdin: служба закрывает торренты и пишет кэш DHT. Если
 * процесс приложения убит, stdin закрывается, и служба выходит сама — порт не остаётся
 * занятым осиротевшим процессом.
 */
class ServerService : Service() {

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIF_ID, buildNotification())
        if (!running) {
            running = true
            acquireLocks()
            Thread({ startServer() }, "server-start").start()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        val p = proc
        proc = null
        state = "Остановлен"
        releaseLocks()
        if (p != null) Thread({ stopProcess(p) }, "server-stop").start()
        log("■ Остановлено")
        super.onDestroy()
    }

    // ==================== ЗАПУСК ====================

    private fun startServer() {
        val ctx = applicationContext
        if (proc != null) return
        state = "Запуск…"
        val port = Env.port(ctx)
        try {
            Env.prepareLibs(ctx)
            if (Env.prepareRuntime(ctx)) log("Служба распакована: ${Env.versions(ctx).first ?: "?"}")
            Env.writeCerts(ctx)
        } catch (e: Exception) {
            state = "Не удалось подготовить службу: ${e.message}"
            log("Подготовка: $e")
            return
        }
        val mono = Env.mono(ctx)
        if (!mono.exists()) {
            state = "Нет mono в APK"
            log("Нет ${mono.path}")
            return
        }
        // Порт занят — скорее всего, служба от прошлого запуска ещё закрывается (или
        // порт взяла другая программа): ждём немного, иначе Mono упадёт на занятом порту
        for (i in 0 until 10) {
            if (echo(port) == null) break
            if (i == 0) log("Порт $port занят — жду освобождения")
            Thread.sleep(1000)
            if (!running) return
        }

        val root = Env.monoRoot(ctx)
        val home = File(ctx.filesDir, "home").apply { mkdirs() }
        val cmd = listOf(mono.absolutePath, Env.serviceExe(ctx).absolutePath, port.toString(), Env.dataDir(ctx).absolutePath)
        val pb = ProcessBuilder(cmd).directory(Env.dataDir(ctx)).redirectErrorStream(true)
        pb.environment().apply {
            put("LD_LIBRARY_PATH", Env.libDir(ctx).absolutePath)
            // Mono из Termux ищет сборки и конфиги в каталоге Termux, которого у нас нет:
            // mscorlib и остальное — по MONO_PATH, machine.config — в MONO_CFG_DIR,
            // dllmap (без $mono_libdir) — MONO_CONFIG
            put("MONO_PATH", "${root}/lib/mono/4.5:${root}/lib/mono/4.5/Facades")
            put("MONO_CFG_DIR", "${root}/etc")
            put("MONO_CONFIG", "${root}/etc/mono/config")
            put("SSL_CERT_FILE", Env.certFile(ctx).absolutePath)
            put("HOME", home.absolutePath)
            put("TMPDIR", ctx.cacheDir.absolutePath)
        }
        val p = try {
            pb.start()
        } catch (e: Exception) {
            state = "Не запустился: ${e.message}"
            log("Не запустился: ${e.message}")
            return
        }
        if (!running) { stopProcess(p); return }
        proc = p
        state = "Запуск…"
        log("Служба запущена, порт $port")
        pump(p) { code -> onExit(p, code) }
        // «Работает» — когда служба ответила по HTTP
        Thread({
            for (i in 0 until 60) {
                if (proc !== p) return@Thread
                if (echo(port) != null) {
                    state = "Работает"
                    return@Thread
                }
                Thread.sleep(500)
            }
        }, "server-wait").start()
    }

    /** Вывод процесса — в журнал (его обязательно вычитывать: полный буфер останавливает процесс) */
    private fun pump(p: Process, onDone: (Int) -> Unit) {
        Thread({
            try {
                p.inputStream.bufferedReader().forEachLine { log(it) }
            } catch (_: Exception) { }
            val code = try { p.waitFor() } catch (_: InterruptedException) { -1 }
            onDone(code)
        }, "server-output").start()
    }

    private fun onExit(p: Process, code: Int) {
        if (!running || p !== proc) return
        proc = null
        log("Служба завершилась, код $code")
        // Код 2 — служба не открыла порт (MonoMain): перезапуск тут не поможет
        if (code == 2) {
            state = "Не запустился: порт ${Env.port(applicationContext)} занят другой программой?"
            return
        }
        state = "Перезапуск…"
        val now = System.currentTimeMillis()
        synchronized(restarts) {
            restarts.removeAll { now - it > RESTART_WINDOW_MS }
            if (restarts.size >= MAX_RESTARTS) {
                state = "Падает — остановлена, см. журнал"
                log("Служба падает раз за разом — больше не перезапускаем")
                return
            }
            restarts += now
        }
        Thread.sleep(RESTART_DELAY_MS)
        if (running) startServer()
    }

    // ==================== СОН ====================

    @SuppressLint("WakelockTimeout")
    private fun acquireLocks() {
        if (wakeLock != null) return
        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "MonoServer:server").apply { acquire() }
            @Suppress("DEPRECATION")
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            @Suppress("DEPRECATION")
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "MonoServer:server").apply { acquire() }
        } catch (e: Exception) {
            log("Не удалось удержать процессор/Wi-Fi: ${e.message}")
        }
    }

    private fun releaseLocks() {
        try { wakeLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) { }
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) { }
        wakeLock = null
        wifiLock = null
    }

    // ==================== УВЕДОМЛЕНИЕ ====================

    private fun buildNotification(): Notification {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (nm.getNotificationChannel(CHANNEL_ID) == null) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW)
                        .apply { setShowBadge(false) }
                )
            }
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0
        )
        val addr = Env.lanAddresses().firstOrNull()?.let { "http://$it:${Env.port(this)}" } ?: "нет сети"
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(getString(R.string.notif_text, addr))
            .setSmallIcon(R.drawable.ic_notify)
            .setContentIntent(open)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setOngoing(true)
            .setShowWhen(false)
            .build()
    }

    companion object {
        private const val TAG = "MonoServer"
        private const val CHANNEL_ID = "server"
        private const val NOTIF_ID = 8099
        private const val MAX_RESTARTS = 3
        private const val RESTART_WINDOW_MS = 5 * 60 * 1000L
        private const val RESTART_DELAY_MS = 3000L
        private const val STOP_WAIT_MS = 6000L
        private const val LOG_LINES = 500
        private const val LOG_FILE_MAX = 2L * 1024 * 1024

        @Volatile var running = false; private set
        @Volatile var state = "Остановлен"; private set
        @Volatile private var proc: Process? = null
        private val restarts = ArrayList<Long>()

        private val lines = ArrayDeque<String>()
        private var logFile: File? = null

        fun init(ctx: Context) {
            if (logFile == null) logFile = Env.serverLog(ctx.applicationContext)
        }

        fun log(line: String) {
            Log.i(TAG, line)
            synchronized(lines) {
                lines.addLast(line)
                while (lines.size > LOG_LINES) lines.removeFirst()
                logFile?.let { f ->
                    try {
                        if (f.length() > LOG_FILE_MAX) f.delete()
                        FileOutputStream(f, true).use { it.write((line + "\n").toByteArray()) }
                    } catch (_: Exception) { }
                }
            }
        }

        fun logText(): String = synchronized(lines) { lines.joinToString("\n") }

        /** Очистка экрана журнала; файл server.log не трогаем — он для разбора проблем */
        fun clearLog() = synchronized(lines) { lines.clear() }

        /** Весь server.log (до 2 МБ) — чтобы отправить разработчику */
        fun logFileText(): String = try {
            logFile?.takeIf { it.isFile }?.readText() ?: logText()
        } catch (_: Exception) {
            logText()
        }

        fun start(ctx: Context) {
            init(ctx)
            synchronized(restarts) { restarts.clear() }
            val intent = Intent(ctx, ServerService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ctx.startForegroundService(intent)
            else ctx.startService(intent)
        }

        fun stop(ctx: Context) {
            ctx.stopService(Intent(ctx, ServerService::class.java))
        }

        /** Перезапуск — после смены порта или настроек, которые служба берёт только при старте */
        fun restart(ctx: Context) {
            if (!running) return
            stop(ctx)
            // Порт освобождается не сразу — startServer подождёт его сам
            Handler(Looper.getMainLooper()).postDelayed({ start(ctx) }, 1500)
        }

        /** «stop» в stdin, через [STOP_WAIT_MS] — SIGTERM, ещё через 2 с — SIGKILL */
        private fun stopProcess(p: Process) {
            try {
                p.outputStream.write("stop\n".toByteArray())
                p.outputStream.flush()
            } catch (_: IOException) { }
            if (waitExit(p, STOP_WAIT_MS)) return
            p.destroy()
            if (waitExit(p, 2000)) return
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) p.destroyForcibly()
        }

        private fun waitExit(p: Process, ms: Long): Boolean {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) return p.waitFor(ms, TimeUnit.MILLISECONDS)
            val until = System.currentTimeMillis() + ms
            while (System.currentTimeMillis() < until) {
                try { p.exitValue(); return true } catch (_: IllegalThreadStateException) { Thread.sleep(100) }
            }
            return false
        }

        /** Ответ службы на /echo (версия) или null */
        fun echo(port: Int): String? = try {
            val c = URL("http://127.0.0.1:$port/echo").openConnection() as HttpURLConnection
            c.connectTimeout = 1500
            c.readTimeout = 1500
            try {
                if (c.responseCode == 200) c.inputStream.bufferedReader().readText().trim().take(80) else null
            } finally {
                c.disconnect()
            }
        } catch (_: Exception) {
            null
        }
    }
}
