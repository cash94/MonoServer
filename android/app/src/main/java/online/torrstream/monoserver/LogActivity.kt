package online.torrstream.monoserver

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Typeface
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * Журнал службы отдельным экраном: последние строки ServerService, обновляются
 * раз в секунду. Пока прокрутка внизу — едет за новыми строками; отмотали вверх —
 * стоит на месте.
 *
 * На телевизоре фокус на самой области: вверх/вниз листают журнал, влево/вправо —
 * длинные строки; из начала журнала «вверх» переходит к кнопкам. «Назад» — выход.
 */
class LogActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var logView: TextView
    private lateinit var scroll: ScrollView
    private var shown = ""

    private val tick = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ServerService.init(this)
        val tv = Ui.isTv(this)
        val dp = { v: Int -> Ui.dp(this, v) }
        val side = dp(if (tv) 48 else 16)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Ui.BG)
            setPadding(side, dp(if (tv) 24 else 16), side, dp(if (tv) 24 else 12))
        }

        // Широкий экран — всё одной строкой; телефон — заголовок, под ним кнопки
        // поровну: в одну строку с заголовком они не помещаются
        val wide = Ui.wide(this)
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val back = Ui.button(this, "←") { finish() }.apply { minWidth = dp(56) }
        bar.addView(back, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        bar.addView(Ui.text(this, 22f, bold = true).apply {
            text = "Журнал"
            setPadding(dp(16), 0, 0, 0)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(bar)

        val actions = if (wide) bar else LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        if (!wide) root.addView(actions, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) })
        val gap = dp(8)
        fun add(btn: android.widget.Button, first: Boolean) {
            val lp = if (wide) LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            else LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            if (wide || !first) lp.leftMargin = gap
            // Треть ширины телефона — поля поуже, иначе «Копировать» переносится
            if (!wide) btn.setPadding(dp(6), btn.paddingTop, dp(6), btn.paddingBottom)
            actions.addView(btn, lp)
        }
        add(Ui.button(this, "Копировать") { copy() }, true)
        add(Ui.button(this, "Отправить") { share() }, false)
        add(Ui.button(this, "Очистить", Ui.Kind.DANGER) {
            ServerService.clearLog()
            refresh()
        }, false)

        logView = Ui.text(this, if (tv) 13f else 11f, android.graphics.Color.parseColor("#C8C8D0")).apply {
            typeface = Typeface.MONOSPACE
            setPadding(dp(14), dp(12), dp(14), dp(12))
            // Длинные строки (пути, ffmpeg) не переносим — их видно прокруткой вбок
            setHorizontallyScrolling(true)
            // Выделение — только на телефоне: на телевизоре выделяемый текст
            // забирал бы фокус у области прокрутки, и стрелки перестали бы листать
            if (!tv) setTextIsSelectable(true)
        }
        val hscroll = HorizontalScrollView(this).apply {
            isFocusable = false
            addView(logView)
        }
        scroll = ScrollView(this).apply {
            background = Ui.focusable(this@LogActivity, Ui.CARD, Ui.CARD, dp(14))
            isFocusable = true
            isFillViewport = true
            addView(hscroll)
        }
        root.addView(scroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = dp(14) })
        setContentView(root)
        if (tv) scroll.requestFocus()
        // Влево/вправо — прокрутка длинных строк: фокус на вертикальной области,
        // и вложенный HorizontalScrollView стрелок сам не получает
        scroll.setOnKeyListener { _, code, ev ->
            if (ev.action != KeyEvent.ACTION_DOWN) return@setOnKeyListener false
            when (code) {
                KeyEvent.KEYCODE_DPAD_LEFT -> { hscroll.smoothScrollBy(-dp(120), 0); true }
                KeyEvent.KEYCODE_DPAD_RIGHT -> { hscroll.smoothScrollBy(dp(120), 0); true }
                else -> false
            }
        }
    }

    override fun onResume() {
        super.onResume()
        handler.post(tick)
    }

    override fun onPause() {
        handler.removeCallbacks(tick)
        super.onPause()
    }

    private fun refresh() {
        val text = ServerService.logText().ifEmpty { "Журнал пуст" }
        if (text == shown) return
        // Внизу (или журнал короче экрана) — после обновления остаёмся внизу
        val child = scroll.getChildAt(0)
        val atBottom = child == null || scroll.scrollY + scroll.height >= child.height - Ui.dp(this, 24)
        shown = text
        logView.text = text
        if (atBottom) scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
    }

    private fun copy() {
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("MonoServer", ServerService.logText()))
        Toast.makeText(this, "Журнал скопирован", Toast.LENGTH_SHORT).show()
    }

    /** Весь server.log, а не только экран — для разбора проблем */
    private fun share() {
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "Журнал MonoServer")
            putExtra(Intent.EXTRA_TEXT, ServerService.logFileText().takeLast(200_000))
        }
        try {
            startActivity(Intent.createChooser(send, "Отправить журнал"))
        } catch (_: Exception) {
            Toast.makeText(this, "Нет приложения, чтобы отправить журнал", Toast.LENGTH_SHORT).show()
        }
    }
}
