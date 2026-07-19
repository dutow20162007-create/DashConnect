package io.github.dovecoteescapee.byedpi.services

import android.app.Notification
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import androidx.lifecycle.lifecycleScope
import io.github.dovecoteescapee.byedpi.R
import io.github.dovecoteescapee.byedpi.activities.MainActivity
import io.github.dovecoteescapee.byedpi.core.ByeDpiProxy
import io.github.dovecoteescapee.byedpi.core.ByeDpiProxyPreferences
import io.github.dovecoteescapee.byedpi.core.TProxyService
import io.github.dovecoteescapee.byedpi.data.*
import io.github.dovecoteescapee.byedpi.utility.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File

class ByeDpiVpnService : LifecycleVpnService() {
    private val byeDpiProxy = ByeDpiProxy()
    private var proxyJob: Job? = null
    private var tunFd: ParcelFileDescriptor? = null
    private val mutex = Mutex()
    private var stopping: Boolean = false

    companion object {
        private val TAG: String = ByeDpiVpnService::class.java.simpleName
        private const val FOREGROUND_SERVICE_ID: Int = 1
        private const val NOTIFICATION_CHANNEL_ID: String = "ByeDPIVpn"

        /// Single source of truth for the TUN MTU — the VpnService builder AND the tun2socks config
        /// must agree, otherwise throughput suffers.
        private const val TUN_MTU: Int = 8500

        private var status: ServiceStatus = ServiceStatus.Disconnected
    }

    override fun onCreate() {
        super.onCreate()
        registerNotificationChannel(
            this,
            NOTIFICATION_CHANNEL_ID,
            R.string.vpn_channel_name,
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        return when (val action = intent?.action) {
            START_ACTION -> {
                lifecycleScope.launch { start() }
                START_STICKY
            }

            STOP_ACTION -> {
                lifecycleScope.launch { stop() }
                START_NOT_STICKY
            }

            else -> {
                Log.w(TAG, "Unknown action: $action")
                START_NOT_STICKY
            }
        }
    }

    override fun onRevoke() {
        Log.i(TAG, "VPN revoked")
        lifecycleScope.launch { stop() }
    }

    private suspend fun start() {
        Log.i(TAG, "Starting")

        if (status == ServiceStatus.Connected) {
            Log.w(TAG, "VPN already connected")
            return
        }

        // Go foreground FIRST, before any real work. Android gives a service started via
        // startForegroundService() roughly 5 seconds to reach startForeground(); the native SOCKS bind
        // plus the TUN establish below can exceed that on a cold start or a slow phone, and the system
        // then kills the process with ForegroundServiceDidNotStartInTimeException — which the user just
        // sees as "VPN crashes when I press connect".
        startForeground()

        try {
            mutex.withLock {
                startProxy()        // now CONFIRMS the SOCKS bind before returning
                startTun2Socks()    // only runs once the proxy is proven up
            }
            updateStatus(ServiceStatus.Connected)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start VPN", e)
            updateStatus(ServiceStatus.Failed)
            stop()
        }
    }

    private fun startForeground() {
        val notification: Notification = createNotification()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                FOREGROUND_SERVICE_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
            )
        } else {
            startForeground(FOREGROUND_SERVICE_ID, notification)
        }
    }

    private suspend fun stop() {
        Log.i(TAG, "Stopping")

        mutex.withLock {
            stopping = true
            try {
                stopTun2Socks()
                stopProxy()
            } catch (e: Exception) {
                Log.e(TAG, "Failed to stop VPN", e)
            } finally {
                stopping = false
            }
        }

        updateStatus(ServiceStatus.Disconnected)
        stopSelf()
    }

    private suspend fun startProxy() {
        Log.i(TAG, "Starting proxy")

        if (proxyJob != null) {
            Log.w(TAG, "Proxy fields not null")
            throw IllegalStateException("Proxy fields not null")
        }

        val preferences = getByeDpiPreferences()

        // 1) CONFIRM the SOCKS listener is bound before anyone raises the TUN. A failed bind (bad
        //    strategy args, port in use) throws here — start()'s catch tears everything down, so we
        //    never leave a live TUN over a dead proxy (which black-holes all traffic).
        val fd = withContext(Dispatchers.IO) { byeDpiProxy.createProxy(preferences) }
        if (fd < 0) {
            throw IllegalStateException("Не удалось запустить обход (стратегия не приняла аргументы)")
        }

        // 2) Run the event loop in the background. If it exits ON ITS OWN (not a user stop), the native
        //    loop has ALREADY returned — so tear down DIRECTLY here. We must NOT call stop()/stopProxy()
        //    from inside proxyJob: stopProxy() does proxyJob.join(), so the coroutine would join itself
        //    → permanent deadlock while holding the mutex (VPN stuck "Connected", every later stop hangs).
        proxyJob = lifecycleScope.launch(Dispatchers.IO) {
            val code = byeDpiProxy.runProxy()

            withContext(Dispatchers.Main) {
                if (stopping) return@withContext   // a user-initiated stop() already owns teardown
                mutex.withLock {
                    if (status != ServiceStatus.Disconnected) {
                        proxyJob = null            // detach FIRST so nothing can self-join
                        stopTun2Socks()            // the proxy is already dead; just drop the tunnel
                    }
                }
                updateStatus(if (code != 0) ServiceStatus.Failed else ServiceStatus.Disconnected)
                stopSelf()
            }
        }

        Log.i(TAG, "Proxy bound and running")
    }

    private suspend fun stopProxy() {
        Log.i(TAG, "Stopping proxy")

        if (status == ServiceStatus.Disconnected) {
            Log.w(TAG, "Proxy already disconnected")
            return
        }

        byeDpiProxy.stopProxy()
        proxyJob?.join() ?: throw IllegalStateException("ProxyJob field null")
        proxyJob = null

        Log.i(TAG, "Proxy stopped")
    }

    /**
     * Suspending + [Dispatchers.IO] ON PURPOSE: this writes a temp config to disk, makes a binder call
     * to the framework ([android.net.VpnService.Builder.establish]) and starts the native tun2socks
     * loop. It used to run inline on the main thread (lifecycleScope defaults to Main), which froze the
     * UI for the whole connect and could trip the ANR watchdog on slower phones.
     */
    private suspend fun startTun2Socks() = withContext(Dispatchers.IO) {
        Log.i(TAG, "Starting tun2socks")

        if (tunFd != null) {
            throw IllegalStateException("VPN field not null")
        }

        val sharedPreferences = getPreferences()
        // In cmd mode the strategy pins the byedpi listener to 1080 (see DashStrategies), so dial 1080
        // too — otherwise the tun2socks dial port and the byedpi bind port can silently diverge.
        val cmdMode = sharedPreferences.getBoolean("byedpi_enable_cmd_settings", false)
        val port = if (cmdMode) 1080
            else sharedPreferences.getString("byedpi_proxy_port", null)?.toIntOrNull() ?: 1080
        val dns = sharedPreferences.getStringNotNull("dns_ip", "1.1.1.1")
        val ipv6 = sharedPreferences.getBoolean("ipv6_enable", false)

        val tun2socksConfig = """
        | misc:
        |   task-stack-size: 81920
        | socks5:
        |   mtu: $TUN_MTU
        |   address: 127.0.0.1
        |   port: $port
        |   udp: udp
        """.trimMargin("| ")

        val configPath = try {
            File.createTempFile("config", "tmp", cacheDir).apply {
                writeText(tun2socksConfig)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to create config file", e)
            throw e
        }

        val fd = createBuilder(dns, ipv6).establish()
            ?: throw IllegalStateException("VPN connection failed")

        this@ByeDpiVpnService.tunFd = fd

        TProxyService.TProxyStartService(configPath.absolutePath, fd.fd)

        Log.i(TAG, "Tun2Socks started")
    }

    /** Off the main thread for the same reason as [startTun2Socks] — native stop + fd close block. */
    private suspend fun stopTun2Socks() = withContext(Dispatchers.IO) {
        Log.i(TAG, "Stopping tun2socks")

        TProxyService.TProxyStopService()

        try {
            File(cacheDir, "config.tmp").delete()
        } catch (e: SecurityException) {
            Log.e(TAG, "Failed to delete config file", e)
        }

        tunFd?.close() ?: Log.w(TAG, "VPN not running")
        tunFd = null

        Log.i(TAG, "Tun2socks stopped")
    }

    private fun getByeDpiPreferences(): ByeDpiProxyPreferences =
        ByeDpiProxyPreferences.fromSharedPreferences(getPreferences())

    private fun updateStatus(newStatus: ServiceStatus) {
        Log.d(TAG, "VPN status changed from $status to $newStatus")

        status = newStatus

        setStatus(
            when (newStatus) {
                ServiceStatus.Connected -> AppStatus.Running

                ServiceStatus.Disconnected,
                ServiceStatus.Failed -> AppStatus.Halted
            },
            Mode.VPN
        )

        val intent = Intent(
            when (newStatus) {
                ServiceStatus.Connected -> STARTED_BROADCAST
                ServiceStatus.Disconnected -> STOPPED_BROADCAST
                ServiceStatus.Failed -> FAILED_BROADCAST
            }
        )
        intent.putExtra(SENDER, Sender.VPN.ordinal)
        intent.setPackage(packageName) // only our own app should receive engine-state broadcasts
        sendBroadcast(intent)
    }

    private fun createNotification(): Notification =
        createConnectionNotification(
            this,
            NOTIFICATION_CHANNEL_ID,
            R.string.notification_title,
            R.string.vpn_notification_content,
            ByeDpiVpnService::class.java,
        )

    private fun createBuilder(dns: String, ipv6: Boolean): Builder {
        Log.d(TAG, "DNS: $dns")
        val builder = Builder()
        builder.setSession("Dash Connect")
        // Match the tun2socks MTU (see startTun2Socks: mtu 8500). Without this the TUN used Android's
        // default (~1500) while hev-socks5-tunnel assumed 8500 — a mismatch that costs throughput
        // (many more small reads/syscalls). A large TUN MTU is safe here because the SOCKS proxy
        // re-originates normal TCP to the real network, so the physical MTU still applies downstream.
        builder.setMtu(TUN_MTU)
        builder.setConfigureIntent(
            PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE,
            )
        )

        builder.addAddress("10.10.10.10", 32)
            .addRoute("0.0.0.0", 0)

        if (ipv6) {
            builder.addAddress("fd00::1", 128)
                .addRoute("::", 0)
        }

        if (dns.isNotBlank()) {
            builder.addDnsServer(dns)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            builder.setMetered(false)
        }

        builder.addDisallowedApplication(applicationContext.packageName)

        return builder
    }
}
