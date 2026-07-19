package io.github.dovecoteescapee.byedpi.services

import android.app.Notification
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import androidx.lifecycle.lifecycleScope
import io.github.dovecoteescapee.byedpi.R
import io.github.dovecoteescapee.byedpi.data.*
import io.github.dovecoteescapee.byedpi.ui.TunnelConfig
import io.github.dovecoteescapee.byedpi.utility.createConnectionNotification
import io.github.dovecoteescapee.byedpi.utility.getPreferences
import io.github.dovecoteescapee.byedpi.utility.registerNotificationChannel
import io.nekohasekai.libbox.BoxService
import io.nekohasekai.libbox.InterfaceUpdateListener
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.NetworkInterfaceIterator
import io.nekohasekai.libbox.PlatformInterface
import io.nekohasekai.libbox.RoutePrefixIterator
import io.nekohasekai.libbox.SetupOptions
import io.nekohasekai.libbox.StringIterator
import io.nekohasekai.libbox.TunOptions
import io.nekohasekai.libbox.WIFIState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.NetworkInterface as JavaNetworkInterface
import io.nekohasekai.libbox.NetworkInterface as LibboxNetworkInterface
import io.nekohasekai.libbox.Notification as LibboxNotification

/**
 * The real VLESS / WireGuard tunnel engine — the phone analog of the PC app's sing-box engine, driven
 * by the sing-box `libbox` (built from source with gomobile, all 4 ABIs). This is a genuine full-device
 * VPN: libbox parses the config, opens the TUN through [openTun], and routes everything through the
 * VLESS (or WireGuard) outbound. Not a stub.
 *
 * We implement [PlatformInterface] so libbox can drive the Android VpnService: build the TUN, protect
 * outbound sockets from the tunnel loop, and follow the default network as it changes (WiFi <-> mobile).
 */
class SingBoxVpnService : LifecycleVpnService(), PlatformInterface {

    private var boxService: BoxService? = null
    private var tunFd: ParcelFileDescriptor? = null
    private val mutex = Mutex()

    private val connectivity by lazy { getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager }
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    companion object {
        private val TAG = SingBoxVpnService::class.java.simpleName
        private const val FOREGROUND_SERVICE_ID = 3
        private const val NOTIFICATION_CHANNEL_ID = "DashTunnel"
        private var status = ServiceStatus.Disconnected
        @Volatile private var didSetup = false
    }

    override fun onCreate() {
        super.onCreate()
        registerNotificationChannel(this, NOTIFICATION_CHANNEL_ID, R.string.vpn_channel_name)
        ensureSetup()
    }

    private fun ensureSetup() {
        if (didSetup) return
        try {
            Libbox.setup(SetupOptions().apply {
                basePath = filesDir.absolutePath
                workingPath = filesDir.resolve("singbox").absolutePath
                tempPath = cacheDir.resolve("singbox").absolutePath
                isTVOS = false
                fixAndroidStack = false
            })
            didSetup = true
        } catch (e: Exception) {
            Log.e(TAG, "libbox setup failed", e)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        when (intent?.action) {
            START_ACTION -> lifecycleScope.launch { start() }
            STOP_ACTION -> lifecycleScope.launch { stop() }
            else -> Log.w(TAG, "Unknown action: ${intent?.action}")
        }
        return START_STICKY
    }

    override fun onRevoke() {
        lifecycleScope.launch { stop() }
    }

    private suspend fun start() = mutex.withLock {
        if (status == ServiceStatus.Connected) return
        // Foreground FIRST — see the same fix in ByeDpiVpnService. Bringing up a VLESS/Amnezia tunnel
        // (config check + libbox service start + TUN) routinely takes longer than the ~5 s the framework
        // allows after startForegroundService(), and blowing that budget kills the process with
        // ForegroundServiceDidNotStartInTimeException instead of just connecting slowly.
        startForegroundNotif()
        try {
            val outbound = buildOutboundFromPrefs()
                ?: throw IllegalStateException("Нет или неверный конфиг (VLESS/Amnezia)")
            val config = TunnelConfig.buildLibboxConfig(outbound)
            withContext(Dispatchers.IO) {
                Libbox.checkConfig(config)      // fail early with a clear error on a bad config
                ensureSetup()
                val svc = Libbox.newService(config, this@SingBoxVpnService)
                svc.start()                     // triggers openTun() on this platform
                boxService = svc
            }
            updateStatus(ServiceStatus.Connected)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start tunnel", e)
            updateStatus(ServiceStatus.Failed)
            stopInternal()
            stopSelf()
        }
    }

    private suspend fun stop() {
        mutex.withLock { stopInternal() }
        updateStatus(ServiceStatus.Disconnected)
        stopSelf()
    }

    private fun stopInternal() {
        try { boxService?.close() } catch (e: Exception) { Log.e(TAG, "close box", e) }
        boxService = null
        try { networkCallback?.let { connectivity.unregisterNetworkCallback(it) } } catch (_: Exception) {}
        networkCallback = null
        try { tunFd?.close() } catch (_: Exception) {}
        tunFd = null
    }

    private fun buildOutboundFromPrefs() =
        getPreferences().let { p ->
            when (p.getString("dash_conn_mode", "dpi")) {
                "amnezia" -> TunnelConfig.parseAmneziaOutbound(p.getString("dash_amnezia_conf", "") ?: "")
                else -> TunnelConfig.parseVless(p.getString("dash_vless_url", "") ?: "")
            }
        }

    private fun startForegroundNotif() {
        val n = createConnectionNotification(
            this, NOTIFICATION_CHANNEL_ID, R.string.notification_title,
            R.string.vpn_notification_content, SingBoxVpnService::class.java,
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
            startForeground(FOREGROUND_SERVICE_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(FOREGROUND_SERVICE_ID, n)
    }

    private fun updateStatus(newStatus: ServiceStatus) {
        status = newStatus
        setStatus(
            if (newStatus == ServiceStatus.Connected) AppStatus.Running else AppStatus.Halted,
            Mode.VPN,
        )
        runningEngine = if (newStatus == ServiceStatus.Connected) Engine.TUNNEL else null
        val action = when (newStatus) {
            ServiceStatus.Connected -> STARTED_BROADCAST
            ServiceStatus.Disconnected -> STOPPED_BROADCAST
            ServiceStatus.Failed -> FAILED_BROADCAST
        }
        // Restrict to our own package so no other app can spoof engine-state broadcasts.
        sendBroadcast(Intent(action).setPackage(packageName).putExtra(SENDER, Sender.VPN.ordinal))
    }

    // ============================ PlatformInterface ============================

    override fun openTun(options: TunOptions): Int {
        val builder = Builder()
        builder.setSession("DashConnect VPN")
        builder.setMtu(options.getMTU())

        addPrefixes(options.inet4Address) { a, p -> builder.addAddress(a, p) }
        addPrefixes(options.inet6Address) { a, p -> builder.addAddress(a, p) }

        var routes = 0
        addPrefixes(options.inet4RouteAddress) { a, p -> builder.addRoute(a, p); routes++ }
        addPrefixes(options.inet6RouteAddress) { a, p -> builder.addRoute(a, p); routes++ }
        if (routes == 0) {                      // full tunnel fallback
            builder.addRoute("0.0.0.0", 0)
            builder.addRoute("::", 0)
        }

        try { options.getDNSServerAddress()?.value?.let { builder.addDnsServer(it) } }
        catch (_: Exception) { builder.addDnsServer("1.1.1.1") }

        // Keep the tunnel's own outbound out of the tunnel (avoid a routing loop).
        try { builder.addDisallowedApplication(packageName) } catch (_: Exception) {}
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) builder.setMetered(false)

        val pfd = builder.establish() ?: throw IllegalStateException("VpnService.establish() failed")
        tunFd = pfd
        return pfd.fd
    }

    override fun usePlatformAutoDetectInterfaceControl(): Boolean = true

    override fun autoDetectInterfaceControl(fd: Int) {
        if (!protect(fd)) throw IllegalStateException("protect($fd) failed")
    }

    override fun useProcFS(): Boolean = false

    override fun writeLog(message: String) { Log.i(TAG, message) }

    override fun underNetworkExtension(): Boolean = false

    override fun includeAllNetworks(): Boolean = false

    override fun clearDNSCache() {}

    override fun readWIFIState(): WIFIState? = null

    override fun sendNotification(notification: LibboxNotification) {}

    override fun findConnectionOwner(
        ipProtocol: Int, sourceAddress: String, sourcePort: Int,
        destinationAddress: String, destinationPort: Int,
    ): Int = throw UnsupportedOperationException("not implemented")

    override fun packageNameByUid(uid: Int): String = throw UnsupportedOperationException("not implemented")

    override fun uidByPackageName(packageName: String): Int = throw UnsupportedOperationException("not implemented")

    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = report(network, listener)
            override fun onLinkPropertiesChanged(network: Network, lp: LinkProperties) = report(network, listener)
            override fun onLost(network: Network) =
                listener.updateDefaultInterface("", -1, false, false)
        }
        networkCallback = cb
        val req = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).build()
        connectivity.registerNetworkCallback(req, cb)
        // Report the current default immediately so libbox doesn't wait for the first change.
        connectivity.activeNetwork?.let { report(it, listener) }
    }

    private fun report(network: Network, listener: InterfaceUpdateListener) {
        val lp = connectivity.getLinkProperties(network) ?: return
        val name = lp.interfaceName ?: return
        val index = try { JavaNetworkInterface.getByName(name)?.index ?: -1 } catch (_: Exception) { -1 }
        val caps = connectivity.getNetworkCapabilities(network)
        val expensive = caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == false
        listener.updateDefaultInterface(name, index, expensive, false)
    }

    override fun closeDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        try { networkCallback?.let { connectivity.unregisterNetworkCallback(it) } } catch (_: Exception) {}
        networkCallback = null
    }

    override fun getInterfaces(): NetworkInterfaceIterator {
        val list = ArrayList<LibboxNetworkInterface>()
        for (ni in JavaNetworkInterface.getNetworkInterfaces().iterator()) {
            val item = LibboxNetworkInterface()
            item.name = ni.name
            item.index = ni.index
            item.setMTU(try { ni.mtu } catch (_: Exception) { 0 })
            item.addresses = StringIter(ni.interfaceAddresses.map {
                "${it.address.hostAddress}/${it.networkPrefixLength}"
            })
            var flags = 0
            try {
                if (ni.isUp) flags = flags or 1 or 32          // FlagUp | FlagRunning
                if (ni.supportsMulticast()) flags = flags or 16 // FlagMulticast
                if (ni.isLoopback) flags = flags or 4           // FlagLoopback
                if (ni.isPointToPoint) flags = flags or 8       // FlagPointToPoint
            } catch (_: Exception) {}
            item.flags = flags
            item.type = interfaceType(ni.name)
            item.setDNSServer(StringIter(emptyList()))
            item.metered = false
            list.add(item)
        }
        return NetIfaceIter(list)
    }

    private fun interfaceType(name: String): Int = when {
        name.startsWith("wlan") -> Libbox.InterfaceTypeWIFI
        name.startsWith("rmnet") || name.startsWith("ccmni") || name.startsWith("radio") ||
            name.startsWith("pdp") || name.startsWith("clat") -> Libbox.InterfaceTypeCellular
        name.startsWith("eth") -> Libbox.InterfaceTypeEthernet
        else -> Libbox.InterfaceTypeOther
    }

    private inline fun addPrefixes(it: RoutePrefixIterator, add: (String, Int) -> Unit) {
        while (it.hasNext()) { val p = it.next(); add(p.address(), p.prefix()) }
    }

    // --- gomobile iterator adapters ---

    private class StringIter(private val items: List<String>) : StringIterator {
        private var i = 0
        override fun hasNext(): Boolean = i < items.size
        override fun next(): String = items[i++]
        override fun len(): Int = items.size
    }

    private class NetIfaceIter(private val items: List<LibboxNetworkInterface>) : NetworkInterfaceIterator {
        private var i = 0
        override fun hasNext(): Boolean = i < items.size
        override fun next(): LibboxNetworkInterface = items[i++]
    }
}
