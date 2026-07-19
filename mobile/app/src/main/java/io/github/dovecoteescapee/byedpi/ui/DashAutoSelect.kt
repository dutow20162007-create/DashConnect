package io.github.dovecoteescapee.byedpi.ui

import io.github.dovecoteescapee.byedpi.core.ByeDpiProxy
import io.github.dovecoteescapee.byedpi.core.ByeDpiProxyCmdPreferences
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/**
 * REAL auto-select (the phone analog of the PC StrategySelector). For each strategy it runs ByeDPI on
 * a private SOCKS port and checks that a genuine TLS handshake to the endpoints that are ACTUALLY
 * blocked completes THROUGH the bypass — not a TCP ping (which lies: youtube.com pings fine while the
 * video won't load). Picks the first strategy that clears them all. Run only while disconnected.
 */
object DashAutoSelect {
    private const val BASE_PORT = 10800

    /**
     * @param exhaustive false = stop at the first strategy that fully clears YouTube-video + Discord
     *   (fast). true = test EVERY strategy and return the best-scoring one ("перебрать все стратегии").
     * @param preferredArgs args of the strategy that last worked — tested first.
     */
    suspend fun run(
        strategies: List<DashStrategy>,
        exhaustive: Boolean = false,
        preferredArgs: String? = null,
        onProgress: (String) -> Unit,
    ): DashStrategy? = withContext(Dispatchers.IO) {
        // Try what already worked on this network BEFORE anything else. Previously every connect
        // re-swept the list from the top, so the user sat through a ~20-strategy walk each time even
        // though the answer almost never changes on the same ISP. sortedByDescending is stable, so the
        // remaining strategies keep their curated order as the fallback path.
        val ordered =
            if (preferredArgs.isNullOrBlank()) strategies
            else strategies.sortedByDescending { it.args == preferredArgs }

        var best: DashStrategy? = null
        var bestScore = 0
        for ((i, s) in ordered.withIndex()) {
            val tag = if (exhaustive) "Перебор" else "Проверяю"
            onProgress("$tag «${s.name}» (${i + 1}/${ordered.size})…")
            // Distinct port per strategy so a slow native teardown can't cause a bind race that
            // false-negatives the next (working) strategy.
            val score = testStrategy(s, BASE_PORT + (i % 64))
            if (score > bestScore) { bestScore = score; best = s }
            if (!exhaustive && score >= 2) { // full pass (video + Discord) — good enough, take it
                onProgress("Рабочая стратегия: «${s.name}»")
                return@withContext s
            }
        }
        onProgress(
            when {
                best != null && bestScore >= 2 -> "Лучшая стратегия: «${best.name}»"
                best != null -> "Частично работает: «${best.name}» (пробилось не всё)"
                else -> "Ни одна стратегия не пробила — попробуй другую сеть или режим VPN VLESS"
            }
        )
        best
    }

    /** Returns a score 0..2: +1 if YouTube video passes through it, +1 if the Discord gateway passes. */
    private suspend fun testStrategy(s: DashStrategy, port: Int): Int {
        val proxy = ByeDpiProxy()
        val prefs = ByeDpiProxyCmdPreferences("${s.args} --ip 127.0.0.1 --port $port")
        // Confirm the strategy actually BINDS before we test it (bad args -> fd < 0 -> skip cleanly).
        val fd = try { proxy.createProxy(prefs) } catch (_: Exception) { -1 }
        if (fd < 0) return 0
        val runner = CoroutineScope(Dispatchers.IO).launch {
            try { proxy.runProxy() } catch (_: Exception) {}
        }
        try {
            delay(350) // let the accept loop come up
            // The real battlegrounds (not youtube.com/discord.com CDN fronts which often aren't blocked):
            //   googlevideo = YouTube VIDEO CDN (feed loads, video doesn't → this is what's blocked)
            //   gateway.discord.gg = the Discord gateway. Success = a real TLS handshake completes through it.
            // Probed CONCURRENTLY: they're independent handshakes with a 4 s timeout each, so running them
            // in sequence doubled the worst case per strategy and made a 21-strategy sweep crawl.
            return coroutineScope {
                val video = async { tls("redirector.googlevideo.com", port) }
                val discord = async { tls("gateway.discord.gg", port) }
                (if (video.await()) 1 else 0) + (if (discord.await()) 1 else 0)
            }
        } finally {
            try { proxy.stopProxy() } catch (_: Exception) {}
            // Fully tear down before the next strategy — the native state is process-global.
            // NonCancellable is REQUIRED: `runner` lives in a detached scope, so if the user cancels the
            // sweep a plain join() throws instantly and the native accept loop keeps running on its port.
            // Bounded too, so a wedged runProxy() can't hang the teardown forever.
            withContext(NonCancellable) {
                if (withTimeoutOrNull(2500) { runner.join() } == null) runner.cancel()
            }
        }
    }

    private fun tls(host: String, socksPort: Int, port: Int = 443): Boolean = try {
        val sp = Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", socksPort))
        Socket(sp).use { raw ->
            raw.connect(InetSocketAddress(host, port), 4000)
            raw.soTimeout = 4000
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(null, arrayOf(TrustAll), null)
            (ctx.socketFactory.createSocket(raw, host, port, true) as SSLSocket).use { ssl ->
                ssl.startHandshake() // DPI reset on the ClientHello throws here
                ssl.session.isValid
            }
        }
    } catch (_: Exception) {
        false
    }

    private val TrustAll = object : X509TrustManager {
        override fun checkClientTrusted(c: Array<X509Certificate>?, a: String?) {}
        override fun checkServerTrusted(c: Array<X509Certificate>?, a: String?) {}
        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }
}
