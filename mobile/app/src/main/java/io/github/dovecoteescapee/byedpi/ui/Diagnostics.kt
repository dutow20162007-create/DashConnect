package io.github.dovecoteescapee.byedpi.ui

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

data class Target(val label: String, val host: String, val port: Int = 443)
data class Probe(val label: String, val ok: Boolean, val ms: Long)

/**
 * HONEST reachability probes: a real TLS handshake (not a TCP ping) to the endpoints that actually get
 * blocked. A TCP connect to youtube.com succeeds even when the video won't load — so we probe
 * googlevideo (the video CDN) and the Discord gateway, where the DPI reset actually happens.
 *
 * When the DPI VPN is up the app itself is EXCLUDED from the tunnel (to avoid a routing loop), so a
 * raw probe would still hit the blocked network and falsely read "blocked" even while YouTube (another
 * app, which IS tunneled) plays fine. So while connected we route the probe through the local byedpi
 * SOCKS (127.0.0.1:1080) — the same desync path real traffic takes — for an accurate reading.
 */
object Diagnostics {
    val targets = listOf(
        Target("YouTube видео", "redirector.googlevideo.com"),
        Target("Discord шлюз", "gateway.discord.gg"),
        Target("Telegram", "web.telegram.org"),
        Target("YouTube", "www.youtube.com"),
    )

    private val trustAll = object : X509TrustManager {
        override fun checkClientTrusted(c: Array<X509Certificate>?, a: String?) {}
        override fun checkServerTrusted(c: Array<X509Certificate>?, a: String?) {}
        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }

    /**
     * Probes every target CONCURRENTLY. Serially this took up to ~8&#160;s per target (4&#160;s connect +
     * 4&#160;s handshake), so on a heavily blocked network the availability card sat on "проверка…" for
     * over half a minute — long enough that users read it as the app being frozen.
     */
    suspend fun pingAll(socksPort: Int = 0): List<Probe> = coroutineScope {
        targets.map { async { ping(it, socksPort) } }.awaitAll()
    }

    /** [socksPort] > 0 routes the probe through that local SOCKS proxy (the byedpi desync path). */
    suspend fun ping(t: Target, socksPort: Int = 0): Probe = withContext(Dispatchers.IO) {
        val start = System.nanoTime()
        try {
            val socket = if (socksPort > 0)
                Socket(Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", socksPort)))
            else Socket()
            socket.use { raw ->
                raw.connect(InetSocketAddress(t.host, t.port), 4000)
                raw.soTimeout = 4000
                val ctx = SSLContext.getInstance("TLS")
                ctx.init(null, arrayOf(trustAll), null)
                (ctx.socketFactory.createSocket(raw, t.host, t.port, true) as SSLSocket).use { ssl ->
                    ssl.startHandshake() // DPI reset on the ClientHello throws here — the honest signal
                }
            }
            Probe(t.label, true, (System.nanoTime() - start) / 1_000_000)
        } catch (e: Exception) {
            Probe(t.label, false, 0)
        }
    }
}
