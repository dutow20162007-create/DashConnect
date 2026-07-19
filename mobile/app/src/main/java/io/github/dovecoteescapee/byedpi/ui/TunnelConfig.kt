package io.github.dovecoteescapee.byedpi.ui

import org.json.JSONArray
import org.json.JSONObject
import java.net.URLDecoder

/**
 * FOUNDATION for the phone VLESS / Amnezia tunnel (the phone analog of the PC sing-box / AmneziaWG
 * engine). This is the platform-independent config layer — pure Kotlin, unit-testable, ported from the
 * PC app. It turns a `vless://` link into a sing-box outbound + full TUN config, and validates an
 * AmneziaWG `.conf`.
 *
 * What still needs a native engine (a Go library built with gomobile — see ANDROID-VPN-PLAN.md):
 *   - VLESS:   sing-box `libbox` runs [buildSingboxConfig] against the VpnService TUN fd.
 *   - Amnezia: `amneziawg-android`'s GoBackend runs the parsed `.conf`.
 * Android allows only ONE VpnService, so the tunnel mode replaces the ByeDPI DPI-bypass mode.
 */
object TunnelConfig {

    // ---------- VLESS link -> sing-box outbound ----------

    /** Parse a vless:// share link into a sing-box "vless" outbound object. Returns null if malformed. */
    fun parseVless(url: String): JSONObject? = try {
        val body = url.removePrefix("vless://")
        val hash = body.indexOf('#')
        val noName = if (hash >= 0) body.substring(0, hash) else body
        val q = noName.indexOf('?')
        val core = if (q >= 0) noName.substring(0, q) else noName
        val query = if (q >= 0) noName.substring(q + 1) else ""
        val at = core.indexOf('@')
        val uuid = core.substring(0, at)
        val (host, port) = splitHostPort(core.substring(at + 1))
        val p = parseQuery(query)

        JSONObject().apply {
            put("type", "vless")
            put("tag", "proxy")
            put("server", host)
            put("server_port", port)
            put("uuid", uuid)
            p["flow"]?.takeIf { it.isNotEmpty() }?.let { put("flow", it) }

            val security = (p["security"] ?: "none").lowercase()
            if (security == "tls" || security == "reality") {
                val tls = JSONObject().apply {
                    put("enabled", true)
                    put("server_name", p["sni"] ?: p["peer"] ?: host)
                    p["fp"]?.takeIf { it.isNotEmpty() }?.let {
                        put("utls", JSONObject().put("enabled", true).put("fingerprint", it))
                    }
                    if (security == "reality") {
                        put("reality", JSONObject().apply {
                            put("enabled", true)
                            p["pbk"]?.let { put("public_key", it) }
                            p["sid"]?.let { put("short_id", it) }
                        })
                    }
                }
                put("tls", tls)
            }
            buildTransport(p["type"] ?: "tcp", p, host)?.let { put("transport", it) }
        }
    } catch (e: Exception) {
        null
    }

    private fun buildTransport(type: String, p: Map<String, String>, host: String): JSONObject? =
        when (type.lowercase()) {
            "ws" -> JSONObject().apply {
                put("type", "ws")
                p["path"]?.takeIf { it.isNotEmpty() }?.let { put("path", it) }
                put("headers", JSONObject().put("Host", p["host"] ?: host))
            }
            "grpc" -> JSONObject().apply {
                put("type", "grpc"); put("service_name", p["serviceName"] ?: "")
            }
            else -> null
        }

    /**
     * Full sing-box config for the libbox engine. libbox itself calls `openTun` on the platform
     * (SingBoxVpnService) — so there is NO `file_descriptor` here and `auto_route` is true so libbox
     * hands us the address/routes/MTU to build the VpnService with. The `dns` block + hijack-dns rule
     * is the exact fix the PC app taught us: sing-box 1.12+ full-tunnel swallows DNS without it.
     */
    fun buildLibboxConfig(outbound: JSONObject): String = JSONObject().apply {
        put("log", JSONObject().put("level", "warn"))
        put("dns", JSONObject().apply {
            put("servers", JSONArray().apply {
                put(JSONObject().put("type", "https").put("tag", "remote-dns").put("server", "1.1.1.1")
                    .put("detour", "proxy").put("domain_resolver", "bootstrap-dns"))
                // No detour on bootstrap-dns — sing-box 1.13 FATALs on "detour to an empty direct
                // outbound"; omitting it makes the bootstrap query go direct by default (what we want).
                put(JSONObject().put("type", "udp").put("tag", "bootstrap-dns").put("server", "1.1.1.1"))
            })
            put("final", "remote-dns"); put("strategy", "prefer_ipv4")
        })
        put("inbounds", JSONArray().put(JSONObject().apply {
            put("type", "tun"); put("tag", "tun-in")
            put("address", JSONArray().put("172.19.0.1/30"))
            put("auto_route", true)      // libbox computes routes -> we read them in openTun()
            put("stack", "gvisor")       // userspace stack: most compatible on Android, no raw sockets
            put("mtu", 9000)
        }))
        put("outbounds", JSONArray().put(outbound)
            .put(JSONObject().put("type", "direct").put("tag", "direct")))
        put("route", JSONObject().apply {
            put("auto_detect_interface", true)
            put("default_domain_resolver", "bootstrap-dns")
            put("final", "proxy")
            put("rules", JSONArray()
                .put(JSONObject().put("action", "sniff"))
                .put(JSONObject().put("protocol", "dns").put("action", "hijack-dns"))
                .put(JSONObject().put("ip_is_private", true).put("outbound", "direct")))
        })
    }.toString()

    // ---------- AmneziaWG / WireGuard .conf ----------

    /**
     * Build a sing-box `wireguard` outbound from a WireGuard/.conf. NOTE: mainline sing-box does not
     * implement AmneziaWG's obfuscation (Jc/S1/H1/…) — those keys are ignored, so an obfuscated conf
     * runs as plain WireGuard (works if the endpoint is reachable, but not DPI-obfuscated).
     */
    fun parseAmneziaOutbound(conf: String): JSONObject? = try {
        val kv = HashMap<String, String>()
        val addresses = ArrayList<String>()
        var section = ""
        for (raw in conf.split('\n')) {
            val line = raw.trim()
            if (line.isEmpty() || line.startsWith('#')) continue
            if (line.startsWith("[")) { section = line.lowercase(); continue }
            val eq = line.indexOf('='); if (eq <= 0) continue
            val k = line.substring(0, eq).trim(); val v = line.substring(eq + 1).trim()
            kv["$section.${k.lowercase()}"] = v
            if (section == "[interface]" && k.equals("Address", true))
                v.split(',').forEach { addresses.add(it.trim()) }
        }
        val endpoint = kv["[peer].endpoint"] ?: error("no endpoint")
        val (host, port) = splitHostPort(endpoint)
        JSONObject().apply {
            put("type", "wireguard")
            put("tag", "proxy")
            put("server", host)
            put("server_port", port)
            put("local_address", JSONArray().apply { addresses.forEach { put(it) } })
            kv["[interface].privatekey"]?.let { put("private_key", it) }
            kv["[peer].publickey"]?.let { put("peer_public_key", it) }
            kv["[peer].presharedkey"]?.let { put("pre_shared_key", it) }
            kv["[interface].mtu"]?.toIntOrNull()?.let { put("mtu", it) }
        }
    } catch (e: Exception) {
        null
    }

    // ---------- helpers ----------

    private fun splitHostPort(hp: String): Pair<String, Int> {
        if (hp.startsWith('[')) {
            val close = hp.indexOf(']')
            return hp.substring(1, close) to hp.substring(close + 2).toInt()
        }
        val c = hp.lastIndexOf(':')
        return hp.substring(0, c) to hp.substring(c + 1).toInt()
    }

    private fun parseQuery(q: String): Map<String, String> = q.split('&')
        .mapNotNull {
            val i = it.indexOf('='); if (i < 0) return@mapNotNull null
            try { URLDecoder.decode(it.substring(0, i), "UTF-8") to URLDecoder.decode(it.substring(i + 1), "UTF-8") }
            catch (e: Exception) { it.substring(0, i) to it.substring(i + 1) }
        }.toMap()
}
