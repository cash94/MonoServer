package online.torrstream.monoserver

import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings

/**
 * Автозапуск — переключатели на главном экране:
 * - запускать сервер при включении устройства (BootReceiver, а на приставках, где
 *   BOOT_COMPLETED не приходит, — ещё и служба спец. возможностей KeepAliveService);
 * - открывать приложение при включении устройства;
 * - запускать сервер, когда приложение открылось.
 */
object Autostart {
    private const val PREFS = "autostart"
    private const val SERVER_ON_BOOT = "server_on_boot"
    private const val APP_ON_BOOT = "app_on_boot"
    private const val SERVER_ON_OPEN = "server_on_open"

    private fun prefs(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun serverOnBoot(ctx: Context) = prefs(ctx).getBoolean(SERVER_ON_BOOT, true)
    fun setServerOnBoot(ctx: Context, on: Boolean) = prefs(ctx).edit().putBoolean(SERVER_ON_BOOT, on).apply()

    fun appOnBoot(ctx: Context) = prefs(ctx).getBoolean(APP_ON_BOOT, false)
    fun setAppOnBoot(ctx: Context, on: Boolean) = prefs(ctx).edit().putBoolean(APP_ON_BOOT, on).apply()

    fun serverOnOpen(ctx: Context) = prefs(ctx).getBoolean(SERVER_ON_OPEN, true)
    fun setServerOnOpen(ctx: Context, on: Boolean) = prefs(ctx).edit().putBoolean(SERVER_ON_OPEN, on).apply()

    /** При открытии приложения */
    fun runOnOpen(ctx: Context) {
        if (serverOnOpen(ctx) && !ServerService.running) ServerService.start(ctx)
    }

    /** При включении устройства: из BootReceiver или из службы спец. возможностей — что раньше */
    fun runOnBoot(ctx: Context, from: String) {
        if (serverOnBoot(ctx) && !ServerService.running) {
            ServerService.init(ctx)
            ServerService.log("Автозапуск ($from)")
            ServerService.start(ctx)
        }
    }

    /**
     * Можно ли открыть приложение из фона (после загрузки). С Android 10 система
     * открывает активность из фона, только если у приложения есть разрешение
     * «Поверх других приложений»; без него запуск молча не происходит.
     */
    fun canOpenFromBackground(ctx: Context) =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || Settings.canDrawOverlays(ctx)

    /** Экран выдачи разрешения «Поверх других приложений»; false — его на устройстве нет */
    fun requestOverlay(ctx: Context): Boolean = open(ctx,
        Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${ctx.packageName}")),
        Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION),
    )

    /** Включена ли служба спец. возможностей MonoServer */
    fun accessibilityOn(ctx: Context): Boolean {
        val me = ComponentName(ctx, KeepAliveService::class.java)
        val list = Settings.Secure.getString(ctx.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        return list.split(':').any { ComponentName.unflattenFromString(it) == me }
    }

    /** Экран спец. возможностей; false — его на устройстве нет (часть приставок прячет) */
    fun openAccessibility(ctx: Context): Boolean = open(ctx,
        Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS),
        Intent("com.android.settings.ACCESSIBILITY_SETTINGS_ACTIVITY"),
        Intent(Settings.ACTION_SETTINGS),
    )

    fun open(ctx: Context, vararg intents: Intent): Boolean {
        for (i in intents) {
            try {
                ctx.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (_: Exception) {
            }
        }
        return false
    }
}

/**
 * Включение устройства: запустить сервер и, если включено, открыть приложение.
 * Сервер — сразу и без окна: на Android 10+ окно из фона может не открыться.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        if (action != Intent.ACTION_BOOT_COMPLETED &&
            action != "android.intent.action.QUICKBOOT_POWERON" &&
            action != "com.htc.intent.action.QUICKBOOT_POWERON" &&
            action != Intent.ACTION_MY_PACKAGE_REPLACED
        ) return
        val ctx = context.applicationContext
        ServerService.init(ctx)
        // Обновили приложение — сервер был запущен до обновления: поднимаем снова
        if (action == Intent.ACTION_MY_PACKAGE_REPLACED) {
            Autostart.runOnBoot(ctx, "после обновления")
            return
        }
        Autostart.runOnBoot(ctx, "включение устройства")
        if (!Autostart.appOnBoot(ctx)) return
        try {
            ctx.startActivity(
                Intent(ctx, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED)
            )
        } catch (e: Exception) {
            ServerService.log("Не удалось открыть приложение: ${e.message}")
        }
    }
}
