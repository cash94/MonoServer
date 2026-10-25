package online.torrstream.monoserver

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent

/**
 * Служба спец. возможностей — только чтобы MonoServer жил в фоне.
 *
 * Пока она включена, система держит процесс приложения привязанным к себе и не
 * выгружает его ради памяти, а «оптимизаторы» приставок и телефонов (Xiaomi, Huawei,
 * китайские ТВ-боксы) не убивают его вместе с сервером. После перезагрузки система
 * поднимает её сама — это ещё и автозапуск там, где BOOT_COMPLETED не приходит.
 *
 * С экраном она ничего не делает: события не читает (accessibility_service.xml — без
 * типов событий и без доступа к содержимому окон).
 */
class KeepAliveService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        ServerService.init(applicationContext)
        Autostart.runOnBoot(applicationContext, "спец. возможности")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}
}
