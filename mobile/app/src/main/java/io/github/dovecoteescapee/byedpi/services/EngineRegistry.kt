package io.github.dovecoteescapee.byedpi.services

/**
 * Which engine is currently the active VPN. Android allows only one VpnService at a time, so the
 * DPI-bypass engine (ByeDPI) and the tunnel engine (sing-box VLESS / WireGuard) are mutually
 * exclusive. [ServiceManager] reads this to route Stop to the correct service.
 */
enum class Engine { DPI, TUNNEL }

var runningEngine: Engine? = null
