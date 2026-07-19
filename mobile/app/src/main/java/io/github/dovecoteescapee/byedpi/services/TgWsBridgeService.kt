package io.github.dovecoteescapee.byedpi.services

import android.app.Notification
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import io.github.dovecoteescapee.byedpi.R
import io.github.dovecoteescapee.byedpi.data.START_ACTION
import io.github.dovecoteescapee.byedpi.data.STOP_ACTION
import io.github.dovecoteescapee.byedpi.utility.createConnectionNotification
import io.github.dovecoteescapee.byedpi.utility.registerNotificationChannel
import java.io.File
import kotlin.concurrent.thread

/**
 * Foreground service that runs the bundled tg-ws-proxy binary (see [TgWsBridge]) as a subprocess so
 * the local Telegram MTProto bridge survives while the user switches to Telegram. Exec'd from the
 * app's nativeLibraryDir (executable). Drains the process output so its pipe never blocks.
 */
class TgWsBridgeService : Service() {
    private var proc: Process? = null

    companion object {
        private val TAG = TgWsBridgeService::class.java.simpleName
        private const val FOREGROUND_SERVICE_ID = 4
        private const val NOTIFICATION_CHANNEL_ID = "DashTgBridge"
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        registerNotificationChannel(this, NOTIFICATION_CHANNEL_ID, R.string.proxy_channel_name)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            START_ACTION -> { setEnabled(true); startBridge() }
            STOP_ACTION -> { setEnabled(false); stopBridge(); stopForegroundCompat(); stopSelf() }
            // null action = START_STICKY restart after the OS killed us (or the app was swiped away).
            // Re-raise the bridge if the user had it on — so it doesn't need a manual restart each time.
            null -> if (isEnabled()) startBridge() else { stopForegroundCompat(); stopSelf() }
            else -> Log.w(TAG, "Unknown action: ${intent.action}")
        }
        return START_STICKY
    }

    private fun isEnabled(): Boolean =
        androidx.preference.PreferenceManager.getDefaultSharedPreferences(this)
            .getBoolean("dash_tg_bridge_on", false)

    private fun setEnabled(on: Boolean) =
        androidx.preference.PreferenceManager.getDefaultSharedPreferences(this)
            .edit().putBoolean("dash_tg_bridge_on", on).apply()

    private fun startBridge() {
        if (proc?.isAlive == true) return
        startForegroundNotif()

        val bin = TgWsBridge.binaryPath(this)
        if (!File(bin).exists()) { Log.e(TAG, "tg-ws-proxy binary missing: $bin"); return }
        val secret = TgWsBridge.secret(this)
        val cache = File(cacheDir, "tgws").apply { mkdirs() }.absolutePath

        // Kill our previous instance if any, then launch. The binary itself binds 127.0.0.1:1443.
        stopBridge()
        thread(name = "tgws-proc") {
            try {
                val p = ProcessBuilder(
                    bin, "--port", TgWsBridge.PORT.toString(),
                    "--secret", secret, "--cf-cache-dir", cache,
                    // Keep more warm WebSocket connections per DC than the default 4 — Telegram feels
                    // noticeably snappier (media/history load without waiting on a cold connect).
                    "--pool", "8",
                ).redirectErrorStream(true).start()
                proc = p
                // Drain output so the OS pipe buffer never fills (which would block the proxy).
                p.inputStream.bufferedReader().useLines { lines ->
                    lines.forEach { Log.i(TAG, it) }
                }
                Log.i(TAG, "tg-ws-proxy exited: ${runCatching { p.exitValue() }.getOrNull()}")
            } catch (e: Exception) {
                Log.e(TAG, "tg-ws-proxy launch failed", e)
            }
        }
    }

    private fun stopBridge() {
        try { proc?.destroy() } catch (_: Exception) {}
        try {
            if (proc?.isAlive == true && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                proc?.destroyForcibly()
        } catch (_: Exception) {}
        proc = null
    }

    private fun startForegroundNotif() {
        val n: Notification = createConnectionNotification(
            this, NOTIFICATION_CHANNEL_ID, R.string.notification_title,
            R.string.proxy_notification_content, TgWsBridgeService::class.java,
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
            startForeground(FOREGROUND_SERVICE_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(FOREGROUND_SERVICE_ID, n)
    }

    @Suppress("DEPRECATION")
    private fun stopForegroundCompat() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) stopForeground(STOP_FOREGROUND_REMOVE)
        else stopForeground(true)
    }

    override fun onDestroy() {
        stopBridge()
        super.onDestroy()
    }
}
