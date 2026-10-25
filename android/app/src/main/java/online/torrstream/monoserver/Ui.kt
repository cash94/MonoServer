package online.torrstream.monoserver

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.StateListDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView

/**
 * Общий вид экранов: тёмные карточки, крупные кнопки и заметный фокус.
 *
 * Фокус — главное для Android TV: там нет касаний, только пульт, и выбранный
 * элемент обязан быть виден издалека. Поэтому у каждой кнопки и переключателя
 * состояние focused рисуется оранжевой рамкой (как цвет фокуса в TorrStream),
 * а на телефоне его не видно вовсе — касание фокус не ставит.
 * Оранжевый — как цвет фокуса в TorrStream и значок MonoServer.
 */
object Ui {
    val BG = Color.parseColor("#0B0B0F")
    val CARD = Color.parseColor("#17171D")
    val CARD_LINE = Color.parseColor("#24242C")
    val TEXT = Color.WHITE
    val MUTED = Color.parseColor("#9A9AA6")
    val ACCENT = Color.parseColor("#FF8C00")
    val OK = Color.parseColor("#3DDC84")
    val WARN = Color.parseColor("#FFB020")
    val ERR = Color.parseColor("#FF5A5A")
    val IDLE = Color.parseColor("#6B6B78")

    /**
     * Плотная раскладка — для широкого экрана (телевизор): там всё должно влезть
     * в один экран без прокрутки, иначе нижние карточки не видно вовсе.
     * Ставит MainActivity до сборки разметки.
     */
    var compact = false

    fun dp(ctx: Context, v: Int) =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), ctx.resources.displayMetrics).toInt()

    /** Экран шире 720 dp — телевизор или планшет: две колонки вместо одной */
    fun wide(ctx: Context): Boolean {
        val m = ctx.resources.displayMetrics
        return m.widthPixels / m.density >= 720
    }

    fun rounded(color: Int, radius: Int, strokeColor: Int = 0, strokeWidth: Int = 0) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = radius.toFloat()
        if (strokeWidth > 0) setStroke(strokeWidth, strokeColor)
    }

    /** Фон с рамкой фокуса: обычный, нажатый и в фокусе пульта */
    fun focusable(ctx: Context, color: Int, pressed: Int, radius: Int = dp(ctx, 12), ringColor: Int = ACCENT): Drawable {
        val ring = dp(ctx, 3)
        return StateListDrawable().apply {
            addState(intArrayOf(-android.R.attr.state_enabled), rounded(darker(color, 0.55f), radius))
            addState(intArrayOf(android.R.attr.state_focused), rounded(pressed, radius, ringColor, ring))
            addState(intArrayOf(android.R.attr.state_pressed), rounded(pressed, radius))
            addState(intArrayOf(), rounded(color, radius))
        }
    }

    fun darker(c: Int, k: Float) = Color.rgb((Color.red(c) * k).toInt(), (Color.green(c) * k).toInt(), (Color.blue(c) * k).toInt())

    fun text(ctx: Context, size: Float, color: Int = TEXT, bold: Boolean = false) = TextView(ctx).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, size)
        setTextColor(color)
        if (bold) setTypeface(Typeface.DEFAULT, Typeface.BOLD)
    }

    enum class Kind { PRIMARY, DANGER, SECONDARY }

    fun button(ctx: Context, label: String, kind: Kind = Kind.SECONDARY, big: Boolean = false, onClick: () -> Unit) = Button(ctx).apply {
        text = label
        isAllCaps = false
        setTextSize(TypedValue.COMPLEX_UNIT_SP, if (big) 18f else 15f)
        setTypeface(Typeface.DEFAULT, if (big) Typeface.BOLD else Typeface.NORMAL)
        stateListAnimator = null
        minHeight = dp(ctx, if (big) (if (compact) 52 else 60) else (if (compact) 44 else 48))
        minimumHeight = minHeight
        setPadding(dp(ctx, 18), dp(ctx, 10), dp(ctx, 18), dp(ctx, 10))
        isFocusable = true
        style(this, kind)
        setOnClickListener { onClick() }
    }

    /** Цвета кнопки; меняются на ходу — «Запустить» оранжевая, «Остановить» красная */
    fun style(b: Button, kind: Kind) {
        val (bg, pressed, fg) = when (kind) {
            Kind.PRIMARY -> Triple(ACCENT, Color.parseColor("#E07800"), Color.BLACK)
            Kind.DANGER -> Triple(Color.parseColor("#3A1E22"), Color.parseColor("#4A262B"), Color.parseColor("#FF8A8A"))
            Kind.SECONDARY -> Triple(Color.parseColor("#26262E"), Color.parseColor("#32323C"), TEXT)
        }
        // На оранжевой кнопке оранжевая рамка фокуса не видна — у неё белая
        b.background = focusable(b.context, bg, pressed, ringColor = if (kind == Kind.PRIMARY) TEXT else ACCENT)
        b.setTextColor(ColorStateList(
            arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf()),
            intArrayOf(MUTED, fg)
        ))
    }

    /** Телевизор: управление пультом, касаний нет */
    fun isTv(ctx: Context): Boolean {
        val ui = ctx.getSystemService(Context.UI_MODE_SERVICE) as android.app.UiModeManager
        return ui.currentModeType == android.content.res.Configuration.UI_MODE_TYPE_TELEVISION ||
            ctx.packageManager.hasSystemFeature("android.software.leanback")
    }

    /** Карточка-раздел: заголовок и содержимое столбиком */
    fun card(ctx: Context, title: String?): LinearLayout = LinearLayout(ctx).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(CARD, dp(ctx, 16), CARD_LINE, dp(ctx, 1))
        val p = dp(ctx, if (compact) 14 else 18)
        setPadding(p + dp(ctx, 2), p, p + dp(ctx, 2), p)
        if (title != null) addView(text(ctx, if (compact) 12f else 13f, MUTED, bold = true).apply {
            text = title.uppercase()
            letterSpacing = 0.08f
            setPadding(0, 0, 0, dp(ctx, if (compact) 6 else 10))
        })
    }

    /** Строка «подпись — значение» с цветной точкой состояния; без подписи — точка и текст */
    class StatusRow(ctx: Context, label: String?) : LinearLayout(ctx) {
        private val dot = View(ctx)
        private val size = if (compact) 14f else 15f
        val value: TextView = text(ctx, size)

        init {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            val v = dp(ctx, if (compact) 3 else 6)
            setPadding(0, v, 0, v)
            val d = dp(ctx, 10)
            addView(dot, LayoutParams(d, d).apply { rightMargin = dp(ctx, 12) })
            if (label != null) addView(text(ctx, size, MUTED).apply { text = label }, LayoutParams(dp(ctx, 130), ViewGroup.LayoutParams.WRAP_CONTENT))
            addView(value, LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }

        fun set(text: String, color: Int) {
            if (value.text.toString() != text) value.text = text
            dot.background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
        }
    }

    /**
     * Переключатель строкой целиком: фокус пульта и нажатие — на всей строке,
     * сам SwitchCompat только показывает состояние. Иначе на телевизоре фокус
     * вставал бы на маленький ползунок, а рамку вокруг него не разглядеть.
     */
    class ToggleRow(ctx: Context, label: String, hint: String? = null, private val onChange: (Boolean) -> Unit) : LinearLayout(ctx) {
        private val sw = androidx.appcompat.widget.SwitchCompat(ctx).apply {
            isFocusable = false
            isClickable = false
            thumbTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(ACCENT, Color.parseColor("#C8C8D0"))
            )
            trackTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(darker(ACCENT, 0.6f), Color.parseColor("#3A3A44"))
            )
        }

        var checked: Boolean
            get() = sw.isChecked
            set(v) { sw.isChecked = v }

        init {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = dp(ctx, 52)
            setPadding(dp(ctx, 16), dp(ctx, 6), dp(ctx, 10), dp(ctx, 6))
            background = focusable(ctx, Color.parseColor("#1F1F26"), Color.parseColor("#2A2A33"))
            isFocusable = true
            isClickable = true
            addView(labelBlock(ctx, label, hint), LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(sw)
            setOnClickListener {
                sw.isChecked = !sw.isChecked
                onChange(sw.isChecked)
            }
        }
    }

    /** Подпись строки настройки и пояснение под ней мелким шрифтом */
    private fun labelBlock(ctx: Context, label: String, hint: String?) = LinearLayout(ctx).apply {
        orientation = LinearLayout.VERTICAL
        addView(text(ctx, 15f).apply { text = label })
        if (hint != null) addView(text(ctx, 12f, MUTED).apply {
            text = hint
            setPadding(0, dp(ctx, 2), dp(ctx, 8), 0)
            setLineSpacing(0f, 1.1f)
        })
    }

    /**
     * Число строкой: подпись, «−», значение, «+». На пульте влево/вправо — между
     * кнопками; нажатие на значение — ввод числа с клавиатуры.
     */
    class NumberRow(
        ctx: Context, label: String, hint: String?,
        private val min: Long, private val max: Long, private val step: Long,
        private val format: (Long) -> String = { it.toString() },
        private val onChange: () -> Unit = {},
    ) : LinearLayout(ctx) {
        private val valueBtn: Button
        var value: Long = min
            set(v) {
                field = v.coerceIn(min, max)
                valueBtn.text = format(field)
            }

        init {
            // Телефон: кнопки под подписью — рядом с ними подпись сжалась бы в столбик
            val wide = wide(ctx)
            orientation = if (wide) HORIZONTAL else VERTICAL
            gravity = if (wide) Gravity.CENTER_VERTICAL else Gravity.END
            setPadding(0, dp(ctx, 6), 0, dp(ctx, 6))
            addView(labelBlock(ctx, label, hint), if (wide) LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            else LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            val controls = LinearLayout(ctx).apply { orientation = HORIZONTAL }
            val minus = button(ctx, "−") { change(-step) }.apply { minWidth = dp(ctx, 52); minimumWidth = minWidth }
            valueBtn = button(ctx, "") { input(ctx, label) }.apply { minWidth = dp(ctx, 96); minimumWidth = minWidth }
            val plus = button(ctx, "+") { change(step) }.apply { minWidth = dp(ctx, 52); minimumWidth = minWidth }
            for (b in listOf(minus, valueBtn, plus)) {
                b.setPadding(dp(ctx, 8), b.paddingTop, dp(ctx, 8), b.paddingBottom)
                controls.addView(b, LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = dp(ctx, 6) })
            }
            addView(controls, LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { if (!wide) topMargin = dp(ctx, 6) })
            value = min
        }

        private fun change(d: Long) {
            // К ближайшему кратному шагу: 64 → 72 → 80, а не 64 → 73 после ввода 65
            val next = if (d > 0) (value / step + 1) * step else ((value + step - 1) / step - 1) * step
            value = next
            onChange()
        }

        private fun input(ctx: Context, label: String) {
            val edit = EditText(ctx).apply {
                inputType = InputType.TYPE_CLASS_NUMBER
                setText(value.toString())
                setSelection(text.length)
            }
            androidx.appcompat.app.AlertDialog.Builder(ctx, R.style.DialogTheme)
                .setTitle(label)
                .setMessage("От $min до $max")
                .setView(edit)
                .setPositiveButton("OK") { _, _ ->
                    edit.text.toString().trim().toLongOrNull()?.let { value = it; onChange() }
                }
                .setNegativeButton("Отмена", null)
                .show()
        }
    }

    fun matchWidth(topMargin: Int = 0) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
    ).apply { this.topMargin = topMargin }
}
