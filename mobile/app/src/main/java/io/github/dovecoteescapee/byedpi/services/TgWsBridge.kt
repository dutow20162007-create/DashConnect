package io.github.dovecoteescapee.byedpi.services

import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat
import androidx.preference.PreferenceManager
import io.github.dovecoteescapee.byedpi.data.START_ACTION
import io.github.dovecoteescapee.byedpi.data.STOP_ACTION
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.io.File
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom

/**
 * The phone port of the PC tg-ws-proxy: a bundled native helper ([binaryName], built from the
 * open-source Flowseal/amurcanov Go proxy, shipped as a per-ABI executable in jniLibs) that runs a
 * LOCAL MTProto proxy on 127.0.0.1:1443 and tunnels Telegram to its data-centres over WebSocket/TLS
 * (with Cloudflare fallback) — looks like ordinary HTTPS to DPI, no third-party server. Telegram
 * connects to it via the one-tap [tgLink]; the secret is persisted so the link keeps working.
 *
 * Like the PC, it runs as a subprocess (exec'd from the app's nativeLibraryDir — that path is
 * executable, which is why the manifest sets extractNativeLibs=true). [TgWsBridgeService] owns it.
 */
object TgWsBridge {
    const val PORT = 1443
    const val binaryName = "libtgwsproxy.so"

    fun binaryPath(ctx: Context): String =
        File(ctx.applicationInfo.nativeLibraryDir, binaryName).absolutePath

    fun binaryExists(ctx: Context): Boolean = File(binaryPath(ctx)).exists()

    /** Persisted 32-hex MTProto secret (generated once), so the same tg:// link keeps working. */
    fun secret(ctx: Context): String {
        val p = PreferenceManager.getDefaultSharedPreferences(ctx)
        var s = p.getString("dash_tgws_secret", null)
        if (s == null || s.length != 32 || !s.all { it.isDigit() || it in 'a'..'f' }) {
            val b = ByteArray(16); SecureRandom().nextBytes(b)
            s = b.joinToString("") { "%02x".format(it) }
            p.edit().putString("dash_tgws_secret", s).apply()
        }
        return s
    }

    /** The tg:// link Telegram opens to enable this proxy (one tap to confirm). dd = secure MTProto. */
    fun tgLink(ctx: Context): String = "tg://proxy?server=127.0.0.1&port=$PORT&secret=dd${secret(ctx)}"

    fun start(ctx: Context) {
        ContextCompat.startForegroundService(
            ctx, Intent(ctx, TgWsBridgeService::class.java).setAction(START_ACTION))
    }

    fun stop(ctx: Context) {
        ContextCompat.startForegroundService(
            ctx, Intent(ctx, TgWsBridgeService::class.java).setAction(STOP_ACTION))
    }

    /** Poll the local MTProto port until the bridge is listening (or timeout). */
    suspend fun awaitReady(timeoutMs: Long): Boolean = withContext(Dispatchers.IO) {
        val deadline = timeoutMs / 200
        repeat(deadline.toInt().coerceAtLeast(1)) {
            if (portOpen()) return@withContext true
            delay(200)
        }
        portOpen()
    }

    fun portOpen(): Boolean = try {
        Socket().use { it.connect(InetSocketAddress("127.0.0.1", PORT), 800); it.isConnected }
    } catch (e: Exception) { false }
}
