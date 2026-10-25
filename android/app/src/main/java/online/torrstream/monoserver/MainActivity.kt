package online.torrstream.monoserver

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * Главный экран: адрес сервера, запуск/остановка, работа в фоне, автозапуск и
 * обслуживание (настройки, журнал). Состояние с версией — значком в шапке.
 *
 * Разметка собирается кодом (Ui.kt). На широком экране — телевизоре или планшете —
 * две колонки, на телефоне одна. Всё, что нажимается, принимает фокус пульта.
 */
class MainActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var statePill: TextView
    private lateinit var stateDot: View
    private lateinit var addressView: TextView
    private lateinit var localView: TextView
    private lateinit var startStop: Button
    private lateinit var serverError: TextView
    private lateinit var batteryRow: Ui.StatusRow
    private lateinit var batteryBtn: Button
    private lateinit var accessRow: Ui.StatusRow
    private lateinit var accessBtn: Button
    private lateinit var bootHint: TextView
    private var lastRunning: Boolean? = null

    private val tick = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ServerService.init(this)
        setContentView(buildLayout())
        // Автозапуск при открытии — только при настоящем открытии, не при повороте экрана
        if (savedInstanceState == null) {
            Autostart.runOnOpen(this)
            handler.postDelayed({ refresh() }, 300)
        }
        // На телевизоре фокус сразу на главной кнопке — иначе первое нажатие
        // пульта уходит на поиск, куда его поставить
        if (Ui.isTv(this)) startStop.requestFocus()
    }

    override fun onResume() {
        super.onResume()
        handler.post(tick)
    }

    override fun onPause() {
        handler.removeCallbacks(tick)
        super.onPause()
    }

    private fun dp(v: Int) = Ui.dp(this, v)

    private fun buildLayout(): View {
        val tv = Ui.isTv(this)
        val wide = Ui.wide(this)
        Ui.compact = wide
        // Телевизоры обрезают края кадра (overscan) — поля шире
        val side = dp(if (tv) 40 else 18)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(side, dp(if (wide) 18 else 22), side, dp(if (wide) 14 else 24))
        }

        // ── Шапка: название и состояние ──
        val header = LinearLayout(this).apply {
            orientation = if (wide) LinearLayout.HORIZONTAL else LinearLayout.VERTICAL
            gravity = if (wide) Gravity.CENTER_VERTICAL else Gravity.START
        }
        val titles = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        titles.addView(Ui.text(this, if (wide) 22f else 26f, bold = true).apply { text = getString(R.string.app_name) })
        titles.addView(Ui.text(this, if (wide) 13f else 14f, Ui.MUTED).apply {
            text = "TorrServer на C# (Mono) для TorrStream, Lampa и других клиентов"
            setPadding(0, dp(2), 0, 0)
        })
        header.addView(titles, if (wide) LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
        else LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        val pill = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = Ui.rounded(Ui.CARD, dp(20), Ui.CARD_LINE, dp(1))
            setPadding(dp(14), dp(8), dp(16), dp(8))
        }
        stateDot = View(this)
        pill.addView(stateDot, LinearLayout.LayoutParams(dp(10), dp(10)).apply { rightMargin = dp(8) })
        statePill = Ui.text(this, 14f, bold = true)
        pill.addView(statePill)
        header.addView(pill, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            .apply { if (!wide) topMargin = dp(12) })
        root.addView(header)

        // ── Колонки ──
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val right = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val gap = dp(if (wide) 12 else 16)
        if (wide) {
            val cols = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            cols.addView(left, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { rightMargin = gap / 2 })
            cols.addView(right, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = gap / 2 })
            root.addView(cols, Ui.matchWidth(dp(14)))
        } else {
            root.addView(left, Ui.matchWidth(dp(20)))
            root.addView(right, Ui.matchWidth(gap))
        }

        // Адрес
        val addrCard = Ui.card(this, "Адрес сервера")
        addressView = Ui.text(this, if (wide) 24f else 26f, Ui.ACCENT, bold = true).apply {
            // Выделять/копировать адрес — только на телефоне: на телевизоре
            // выделяемый текст забирал бы фокус пульта
            if (!tv) setTextIsSelectable(true)
        }
        addrCard.addView(addressView)
        localView = Ui.text(this, if (wide) 12f else 13f, Ui.MUTED).apply {
            setPadding(0, dp(6), 0, dp(if (wide) 12 else 16))
            setLineSpacing(0f, 1.15f)
        }
        addrCard.addView(localView)
        startStop = Ui.button(this, "Запустить сервер", Ui.Kind.PRIMARY, big = true) {
            if (ServerService.running) ServerService.stop(this) else ServerService.start(this)
            handler.postDelayed({ refresh() }, 300)
        }
        addrCard.addView(startStop, Ui.matchWidth())
        // Не запустился / падает — причина здесь, подробности в журнале
        serverError = Ui.text(this, 13f, Ui.ERR).apply {
            setPadding(dp(4), dp(8), 0, 0)
            visibility = View.GONE
        }
        addrCard.addView(serverError)
        left.addView(addrCard, Ui.matchWidth())

        // Обслуживание
        val toolsCard = Ui.card(this, "Обслуживание")
        val toolsRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        toolsRow.addView(Ui.button(this, "Настройки") {
            startActivity(Intent(this, SettingsActivity::class.java))
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        toolsRow.addView(Ui.button(this, "Журнал") {
            startActivity(Intent(this, LogActivity::class.java))
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(10) })
        toolsCard.addView(toolsRow, Ui.matchWidth())
        // Страница самой службы: торренты, состояние движка. На ТВ браузера может не быть
        toolsCard.addView(Ui.button(this, "Веб-интерфейс сервера") {
            val url = "http://127.0.0.1:${Env.port(this)}/"
            if (!Autostart.open(this, Intent(Intent.ACTION_VIEW, Uri.parse(url)))) {
                Toast.makeText(this, "Нет браузера — откройте $url на другом устройстве по адресу сервера", Toast.LENGTH_LONG).show()
            }
        }, Ui.matchWidth(dp(10)))
        left.addView(toolsCard, Ui.matchWidth(gap))

        // Работа в фоне: без этого телефоны и приставки выгружают сервер
        val bgCard = Ui.card(this, "Работа в фоне")
        bgCard.addView(Ui.text(this, if (wide) 12f else 13f, Ui.MUTED).apply {
            text = "Чтобы система не выгружала сервер, отключите для приложения экономию " +
                "батареи и включите его службу в «Спец. возможностях»."
            setPadding(0, 0, 0, dp(if (wide) 6 else 8))
            setLineSpacing(0f, 1.15f)
        })
        batteryRow = Ui.StatusRow(this, null)
        bgCard.addView(batteryRow, Ui.matchWidth())
        batteryBtn = Ui.button(this, "Отключить экономию батареи") { requestIgnoreBattery() }
        bgCard.addView(batteryBtn, Ui.matchWidth(dp(6)))
        accessRow = Ui.StatusRow(this, null)
        bgCard.addView(accessRow, Ui.matchWidth(dp(8)))
        accessBtn = Ui.button(this, "Открыть спец. возможности") {
            if (Autostart.openAccessibility(this)) {
                Toast.makeText(this, "Найдите «${getString(R.string.accessibility_label)}» и включите", Toast.LENGTH_LONG).show()
            } else {
                Toast.makeText(this, "На устройстве нет экрана спец. возможностей", Toast.LENGTH_LONG).show()
            }
        }
        bgCard.addView(accessBtn, Ui.matchWidth(dp(6)))
        right.addView(bgCard, Ui.matchWidth())

        // Автозапуск
        val autoCard = Ui.card(this, "Автозапуск")
        autoCard.addView(Ui.ToggleRow(this, "Запускать сервер при включении устройства") { on ->
            Autostart.setServerOnBoot(this, on)
        }.apply { checked = Autostart.serverOnBoot(this@MainActivity) }, Ui.matchWidth())
        autoCard.addView(Ui.ToggleRow(this, "Запускать сервер при открытии приложения") { on ->
            Autostart.setServerOnOpen(this, on)
        }.apply { checked = Autostart.serverOnOpen(this@MainActivity) }, Ui.matchWidth(dp(8)))
        autoCard.addView(Ui.ToggleRow(this, "Открывать приложение при включении устройства") { on ->
            Autostart.setAppOnBoot(this, on)
            // Android 10+: окно из фона откроется только с разрешением «Поверх других
            // приложений» — сразу ведём туда
            if (on && !Autostart.canOpenFromBackground(this) && !Autostart.requestOverlay(this)) {
                Toast.makeText(this, "На устройстве нет экрана этого разрешения — " +
                    "сервер всё равно запустится при включении", Toast.LENGTH_LONG).show()
            }
            refresh()
        }.apply { checked = Autostart.appOnBoot(this@MainActivity) }, Ui.matchWidth(dp(8)))
        bootHint = Ui.text(this, 12f, Ui.WARN).apply {
            text = "Чтобы приложение открывалось само, разрешите ему «Поверх других приложений» " +
                "(Android 10 и новее). Сервер запустится и без этого."
            setPadding(dp(4), dp(6), 0, dp(4))
            setLineSpacing(0f, 1.15f)
            visibility = View.GONE
        }
        autoCard.addView(bootHint)
        right.addView(autoCard, Ui.matchWidth(gap))

        return ScrollView(this).apply {
            setBackgroundColor(Ui.BG)
            isFillViewport = true
            // Прокрутка идёт за фокусом пульта сама; полоса только мешает
            isVerticalScrollBarEnabled = !tv
            addView(root)
        }
    }

    @SuppressLint("SetTextI18n")
    private fun refresh() {
        val running = ServerService.running
        val port = Env.port(this)
        val ips = Env.lanAddresses()
        val addr = if (ips.isEmpty()) "Нет сети — подключите Wi-Fi или кабель"
        else ips.joinToString("\n") { "http://$it:$port" }
        if (addressView.text.toString() != addr) addressView.text = addr
        val local = "Укажите его как адрес TorrServer в TorrStream, Lampa и других клиентах " +
            "в той же сети. На этом устройстве — http://127.0.0.1:$port"
        if (localView.text.toString() != local) localView.text = local

        val state = ServerService.state
        val failed = state.startsWith("Не ") || state.startsWith("Падает") || state.startsWith("Нет ")
        val ok = running && state.startsWith("Работает")
        val pillText = when {
            ok -> "Работает" + (Env.versions(this).first?.let { " · $it" } ?: "")
            running && failed -> "Ошибка"
            running -> "Запуск…"
            else -> "Остановлен"
        }
        if (statePill.text.toString() != pillText) statePill.text = pillText
        stateDot.background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(when {
                failed -> Ui.ERR
                ok -> Ui.OK
                running -> Ui.WARN
                else -> Ui.IDLE
            })
        }
        val err = if (failed) state else ""
        if (serverError.text.toString() != err) serverError.text = err
        show(serverError, failed)

        if (lastRunning != running) {
            lastRunning = running
            startStop.text = if (running) "Остановить сервер" else "Запустить сервер"
            Ui.style(startStop, if (running) Ui.Kind.DANGER else Ui.Kind.PRIMARY)
        }

        val focused = currentFocus
        val battery = ignoringBattery()
        batteryRow.set(if (battery) "Экономия батареи отключена" else "Экономия батареи может остановить сервер",
            if (battery) Ui.OK else Ui.WARN)
        show(batteryBtn, !battery)
        val access = Autostart.accessibilityOn(this)
        accessRow.set(if (access) "Спец. возможности: служба включена" else "Спец. возможности: служба выключена",
            if (access) Ui.OK else Ui.IDLE)
        accessBtn.text = if (access) "Спец. возможности" else "Открыть спец. возможности"
        show(bootHint, Autostart.appOnBoot(this) && !Autostart.canOpenFromBackground(this))
        // Кнопка под фокусом пульта пропала (экономию батареи отключили) — без
        // этого фокус повис бы в пустоте
        if (focused != null && !focused.isShown) startStop.requestFocus()
    }

    private fun show(v: View, on: Boolean) {
        val want = if (on) View.VISIBLE else View.GONE
        if (v.visibility != want) v.visibility = want
    }

    private fun ignoringBattery(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return true
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        return pm.isIgnoringBatteryOptimizations(packageName)
    }

    @SuppressLint("BatteryLife")
    private fun requestIgnoreBattery() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return
        if (!Autostart.open(this,
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")),
                Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))) {
            Toast.makeText(this, "На устройстве нет настройки экономии батареи", Toast.LENGTH_LONG).show()
        }
    }
}
