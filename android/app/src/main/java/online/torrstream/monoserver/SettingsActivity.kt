package online.torrstream.monoserver

import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Настройки: порт сервера (приложения) и настройки самой службы — те же BTSets, что у
 * TorrServer MatriX (service/TorrSettings.cs).
 *
 * Сервер работает — через его API (POST /settings get/set/def): почти всё применяется
 * сразу. Остановлен — прямо в settings.json в данных службы: служба прочтёт его при
 * запуске. Порт сервера, порт для пиров, DHT и UPnP служба берёт только при старте —
 * после их смены работающий сервер предлагается перезапустить.
 */
class SettingsActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var status: TextView
    private lateinit var port: Ui.NumberRow
    private lateinit var cache: Ui.NumberRow
    private lateinit var readAhead: Ui.NumberRow
    private lateinit var preload: Ui.NumberRow
    private lateinit var connections: Ui.NumberRow
    private lateinit var disconnect: Ui.NumberRow
    private lateinit var peersPort: Ui.NumberRow
    private lateinit var dht: Ui.ToggleRow
    private lateinit var upnp: Ui.ToggleRow
    private lateinit var download: Ui.NumberRow
    private lateinit var upload: Ui.NumberRow
    private lateinit var retrackers: Ui.ToggleRow
    private lateinit var responsive: Ui.ToggleRow
    private lateinit var saveBtn: Button
    /** Как пришло от службы или из файла: незнакомые ключи сохраняем как были */
    private var loaded = JSONObject()
    private var busy = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ServerService.init(this)
        setContentView(buildLayout())
        load()
    }

    private fun dp(v: Int) = Ui.dp(this, v)

    private fun buildLayout(): View {
        val tv = Ui.isTv(this)
        val wide = Ui.wide(this)
        Ui.compact = wide
        val side = dp(if (tv) 48 else 16)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(side, dp(if (tv) 24 else 16), side, dp(if (tv) 24 else 16))
        }

        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val back = Ui.button(this, "←") { finish() }.apply { minWidth = dp(56) }
        bar.addView(back, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        bar.addView(Ui.text(this, 22f, bold = true).apply {
            text = "Настройки"
            setPadding(dp(16), 0, 0, 0)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(bar)
        status = Ui.text(this, 13f, Ui.MUTED).apply {
            setPadding(dp(4), dp(10), 0, 0)
            setLineSpacing(0f, 1.15f)
        }
        root.addView(status)

        // На широком экране — две колонки, как на главном
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val right = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val gap = dp(12)
        if (wide) {
            val cols = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            cols.addView(left, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { rightMargin = gap / 2 })
            cols.addView(right, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = gap / 2 })
            root.addView(cols, Ui.matchWidth(dp(12)))
        } else {
            root.addView(left, Ui.matchWidth(dp(12)))
            root.addView(right, Ui.matchWidth(gap))
        }

        val server = Ui.card(this, "Сервер")
        port = Ui.NumberRow(this, "Порт сервера", "Адрес для клиентов — http://<IP>:порт. 8090 обычно занят оригинальным TorrServer",
            1024, 65535, 1)
        server.addView(port, Ui.matchWidth())
        left.addView(server, Ui.matchWidth())

        val cacheCard = Ui.card(this, "Кэш и чтение")
        cache = Ui.NumberRow(this, "Кэш на торрент, МБ", "Только в памяти. На слабых приставках — 32–64",
            8, 1024, 8)
        cacheCard.addView(cache, Ui.matchWidth())
        readAhead = Ui.NumberRow(this, "Упреждающее чтение, %", "Доля кэша впереди позиции просмотра", 5, 100, 5, format = { "$it%" })
        cacheCard.addView(readAhead, Ui.matchWidth())
        preload = Ui.NumberRow(this, "Предзагрузка, %", "Сколько кэша набрать перед началом просмотра", 0, 100, 5, format = { "$it%" })
        cacheCard.addView(preload, Ui.matchWidth())
        responsive = Ui.ToggleRow(this, "Отдавать данные сразу", "Плееру — блоки по мере прихода, не дожидаясь проверки всего куска") { }
        cacheCard.addView(responsive, Ui.matchWidth(dp(6)))
        left.addView(cacheCard, Ui.matchWidth(gap))

        val net = Ui.card(this, "Сеть")
        connections = Ui.NumberRow(this, "Подключений на торрент", "Пиров одновременно", 5, 200, 5)
        net.addView(connections, Ui.matchWidth())
        disconnect = Ui.NumberRow(this, "Закрывать торрент через, с", "После того как его перестали смотреть", 5, 3600, 5)
        net.addView(disconnect, Ui.matchWidth())
        peersPort = Ui.NumberRow(this, "Порт для пиров", "0 — выбрать случайный. Входящие подключения", 0, 65535, 1)
        net.addView(peersPort, Ui.matchWidth())
        download = Ui.NumberRow(this, "Ограничение загрузки, КБ/с", "0 — без ограничения", 0, 1_000_000, 256, format = { if (it == 0L) "нет" else "$it" })
        net.addView(download, Ui.matchWidth())
        upload = Ui.NumberRow(this, "Ограничение отдачи, КБ/с", "0 — без ограничения", 0, 1_000_000, 256, format = { if (it == 0L) "нет" else "$it" })
        net.addView(upload, Ui.matchWidth())
        dht = Ui.ToggleRow(this, "DHT", "Поиск пиров без трекеров") { }
        net.addView(dht, Ui.matchWidth(dp(6)))
        upnp = Ui.ToggleRow(this, "UPnP", "Открыть порт для пиров на роутере") { }
        net.addView(upnp, Ui.matchWidth(dp(8)))
        retrackers = Ui.ToggleRow(this, "Добавлять ретрекеры", "Список ngosang/trackerslist к каждому торренту") { }
        net.addView(retrackers, Ui.matchWidth(dp(8)))
        right.addView(net, Ui.matchWidth())

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        saveBtn = Ui.button(this, "Сохранить", Ui.Kind.PRIMARY) { save() }
        actions.addView(saveBtn, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        actions.addView(Ui.button(this, "По умолчанию") { confirmDefaults() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(10) })
        root.addView(actions, Ui.matchWidth(dp(14)))

        if (tv) handler.post { saveBtn.requestFocus() }
        return ScrollView(this).apply {
            setBackgroundColor(Ui.BG)
            isFillViewport = true
            isVerticalScrollBarEnabled = !tv
            addView(root)
        }
    }

    // ==================== ЗАГРУЗКА / СОХРАНЕНИЕ ====================

    /** Сервер работает и отвечает — тогда настройки через его API */
    private fun live(): Boolean = ServerService.running && ServerService.echo(Env.port(this)) != null

    private fun load() {
        status.text = "Загрузка…"
        val ctx = applicationContext
        Thread({
            val viaApi = live()
            val o = try {
                if (viaApi) JSONObject(api("""{"action":"get"}"""))
                else readFile(ctx)
            } catch (e: Exception) {
                ServerService.log("Настройки: ${e.message}")
                readFile(ctx)
            }
            handler.post { show(o, viaApi) }
        }, "settings-load").start()
    }

    private fun show(o: JSONObject, viaApi: Boolean) {
        loaded = o
        port.value = Env.port(this).toLong()
        cache.value = o.optLong("CacheSize", DEF_CACHE) / MB
        readAhead.value = o.optLong("ReaderReadAHead", 95)
        preload.value = o.optLong("PreloadCache", 50)
        connections.value = o.optLong("ConnectionsLimit", 25)
        disconnect.value = o.optLong("TorrentDisconnectTimeout", 30)
        peersPort.value = o.optLong("PeersListenPort", 0)
        download.value = o.optLong("DownloadRateLimit", 0)
        upload.value = o.optLong("UploadRateLimit", 0)
        dht.checked = o.optBoolean("EnableDHT", true)
        upnp.checked = o.optBoolean("EnableUPNP", true)
        retrackers.checked = o.optInt("RetrackersMode", 1) == 1
        responsive.checked = o.optBoolean("ResponsiveMode", true)
        status.text = if (viaApi) "Сервер работает — изменения применяются сразу (порты, DHT и UPnP — после перезапуска)."
        else "Сервер остановлен — настройки вступят в силу при запуске."
    }

    private fun collect(): JSONObject = JSONObject().apply {
        put("CacheSize", cache.value * MB)
        put("ReaderReadAHead", readAhead.value)
        put("PreloadCache", preload.value)
        put("ConnectionsLimit", connections.value)
        put("TorrentDisconnectTimeout", disconnect.value)
        put("PeersListenPort", peersPort.value)
        put("DownloadRateLimit", download.value)
        put("UploadRateLimit", upload.value)
        put("EnableDHT", dht.checked)
        put("EnableUPNP", upnp.checked)
        put("RetrackersMode", if (retrackers.checked) 1 else 0)
        put("ResponsiveMode", responsive.checked)
    }

    private fun save() {
        if (busy) return
        busy = true
        val ctx = applicationContext
        val sets = collect()
        val newPort = port.value.toInt()
        val portChanged = newPort != Env.port(ctx)
        // Это служба берёт только при запуске движка
        val needRestart = portChanged ||
            sets.optLong("PeersListenPort") != loaded.optLong("PeersListenPort", 0) ||
            sets.optBoolean("EnableDHT") != loaded.optBoolean("EnableDHT", true) ||
            sets.optBoolean("EnableUPNP") != loaded.optBoolean("EnableUPNP", true)
        Thread({
            val result = try {
                if (live()) api(JSONObject().put("action", "set").put("sets", sets).toString())
                else writeFile(ctx, sets)
                if (portChanged) Env.setPort(ctx, newPort)
                null
            } catch (e: Exception) {
                e.message ?: e.javaClass.simpleName
            }
            handler.post {
                busy = false
                if (result != null) {
                    Toast.makeText(this, "Не сохранилось: $result", Toast.LENGTH_LONG).show()
                    return@post
                }
                ServerService.log("Настройки сохранены")
                loaded = JSONObject(loaded.toString()).also { o -> sets.keys().forEach { k -> o.put(k, sets.get(k)) } }
                if (needRestart && ServerService.running) askRestart()
                else Toast.makeText(this, "Сохранено", Toast.LENGTH_SHORT).show()
            }
        }, "settings-save").start()
    }

    private fun askRestart() {
        AlertDialog.Builder(this, R.style.DialogTheme)
            .setTitle("Перезапустить сервер?")
            .setMessage("Порт, DHT и UPnP применятся после перезапуска. Идущие просмотры прервутся.")
            .setPositiveButton("Перезапустить") { _, _ ->
                ServerService.restart(applicationContext)
                finish()
            }
            .setNegativeButton("Позже", null)
            .show()
    }

    private fun confirmDefaults() {
        AlertDialog.Builder(this, R.style.DialogTheme)
            .setTitle("Вернуть настройки по умолчанию?")
            .setMessage("Порт сервера станет ${Env.DEFAULT_PORT}; порт для пиров останется прежним.")
            .setPositiveButton("Вернуть") { _, _ -> resetDefaults() }
            .setNegativeButton("Отмена", null)
            .show()
    }

    private fun resetDefaults() {
        val ctx = applicationContext
        val portChanged = Env.port(ctx) != Env.DEFAULT_PORT
        Thread({
            try {
                if (live()) api("""{"action":"def"}""")
                else {
                    val f = Env.settingsFile(ctx)
                    val peers = readFile(ctx).optLong("PeersListenPort", 0)
                    f.writeText(JSONObject().put("PeersListenPort", peers).put("TorrStreamSettingsVersion", SETTINGS_VERSION).toString())
                }
                Env.setPort(ctx, Env.DEFAULT_PORT)
            } catch (e: Exception) {
                ServerService.log("Настройки по умолчанию: ${e.message}")
            }
            handler.post {
                load()
                if (portChanged && ServerService.running) askRestart()
            }
        }, "settings-def").start()
    }

    /** POST /settings у работающей службы; ответ — текстом */
    private fun api(body: String): String {
        val c = URL("http://127.0.0.1:${Env.port(this)}/settings").openConnection() as HttpURLConnection
        c.connectTimeout = 3000
        c.readTimeout = 5000
        c.requestMethod = "POST"
        c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json")
        // Ключ приложения (служба пишет его в local.key при запуске): с ним настройки
        // меняются и при включённом входе — пароль admin приложению не нужен
        try {
            val key = File(Env.dataDir(this), "local.key").readText().trim()
            if (key.isNotEmpty()) c.setRequestProperty("X-MonoServer-Local", key)
        } catch (_: Exception) {
        }
        try {
            c.outputStream.use { it.write(body.toByteArray()) }
            if (c.responseCode != 200) throw java.io.IOException("HTTP ${c.responseCode}")
            return c.inputStream.bufferedReader().readText()
        } finally {
            c.disconnect()
        }
    }

    private fun readFile(ctx: android.content.Context): JSONObject = try {
        JSONObject(Env.settingsFile(ctx).readText())
    } catch (_: Exception) {
        JSONObject()
    }

    /** Остановленная служба: поверх файла (незнакомые ключи — как были) */
    private fun writeFile(ctx: android.content.Context, sets: JSONObject) {
        val f: File = Env.settingsFile(ctx)
        val o = readFile(ctx)
        sets.keys().forEach { k -> o.put(k, sets.get(k)) }
        // Без версии набора умолчаний служба приняла бы файл за старый (TorrSettings.Load)
        if (!o.has("TorrStreamSettingsVersion")) o.put("TorrStreamSettingsVersion", SETTINGS_VERSION)
        val tmp = File(f.parentFile, f.name + ".tmp")
        tmp.writeText(o.toString(1))
        if (!tmp.renameTo(f)) throw java.io.IOException("не удалось записать ${f.name}")
    }

    companion object {
        private const val MB = 1L shl 20
        private const val DEF_CACHE = 64L shl 20
        /** TorrSettings.SettingsVersion службы */
        private const val SETTINGS_VERSION = 4
    }
}
