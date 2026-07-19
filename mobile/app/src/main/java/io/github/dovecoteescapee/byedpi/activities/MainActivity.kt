package io.github.dovecoteescapee.byedpi.activities

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.mutableStateOf
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import androidx.preference.PreferenceManager
import androidx.core.content.FileProvider
import io.github.dovecoteescapee.byedpi.ui.DashAutoSelect
import io.github.dovecoteescapee.byedpi.ui.UpdateChecker
import io.github.dovecoteescapee.byedpi.ui.UpdateInfo
import kotlinx.coroutines.launch
import io.github.dovecoteescapee.byedpi.BuildConfig
import io.github.dovecoteescapee.byedpi.data.AppStatus
import io.github.dovecoteescapee.byedpi.data.FAILED_BROADCAST
import io.github.dovecoteescapee.byedpi.data.Mode
import io.github.dovecoteescapee.byedpi.data.STARTED_BROADCAST
import io.github.dovecoteescapee.byedpi.data.STOPPED_BROADCAST
import io.github.dovecoteescapee.byedpi.services.ServiceManager
import io.github.dovecoteescapee.byedpi.services.TgWsBridge
import io.github.dovecoteescapee.byedpi.services.appStatus
import io.github.dovecoteescapee.byedpi.ui.DashScreen
import io.github.dovecoteescapee.byedpi.ui.DashStrategies
import io.github.dovecoteescapee.byedpi.ui.DashTheme

/**
 * Compose dashboard, styled 1:1 with the PC app (dark glass + particle field), driving the real
 * ByeDPI VpnService underneath — Connect brings up the whole-device DPI bypass.
 */
class MainActivity : ComponentActivity() {

    private val running = mutableStateOf(false)
    private val connecting = mutableStateOf(false)
    private val strategyArgs = mutableStateOf("")
    private val telegramStatus = mutableStateOf("")
    private val autoOnConnect = mutableStateOf(true)
    private val autoSelectStatus = mutableStateOf("")
    private val connMode = mutableStateOf("dpi")
    private val vlessUrl = mutableStateOf("")
    private val amneziaConf = mutableStateOf("")
    private val updateVersion = mutableStateOf<String?>(null)
    private val updateStatus = mutableStateOf("")
    private var pendingUpdate: UpdateInfo? = null

    private val vpnRegister =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
            if (it.resultCode == RESULT_OK) {
                launchSelected()
            } else {
                connecting.value = false
                sync()
            }
        }

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                STARTED_BROADCAST, STOPPED_BROADCAST, FAILED_BROADCAST -> {
                    connecting.value = false
                    sync()
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val filter = IntentFilter().apply {
            addAction(STARTED_BROADCAST)
            addAction(STOPPED_BROADCAST)
            addAction(FAILED_BROADCAST)
        }
        ContextCompat.registerReceiver(this, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        // Ship a strong desync strategy by default (the weak ByeDPI default doesn't beat RU DPI).
        DashStrategies.ensureDefaults(this)
        strategyArgs.value = DashStrategies.currentArgs(this)
        val prefs = PreferenceManager.getDefaultSharedPreferences(this)
        autoOnConnect.value = prefs.getBoolean("dash_auto_on_connect", true)
        connMode.value = prefs.getString("dash_conn_mode", "dpi") ?: "dpi"
        vlessUrl.value = prefs.getString("dash_vless_url", "") ?: ""
        amneziaConf.value = prefs.getString("dash_amnezia_conf", "") ?: ""

        sync()
        checkForUpdates()
        setContent {
            DashTheme {
                DashScreen(
                    connected = running.value,
                    connecting = connecting.value,
                    onToggle = ::toggle,
                    onOpenEngineSettings = { startActivity(Intent(this, SettingsActivity::class.java)) },
                    versionName = BuildConfig.VERSION_NAME,
                    strategies = DashStrategies.all,
                    currentArgs = strategyArgs.value,
                    onSelectStrategy = { s ->
                        DashStrategies.select(this, s)
                        strategyArgs.value = s.args
                    },
                    telegramStatus = telegramStatus.value,
                    onSetupTelegram = ::setupTelegram,
                    autoOnConnect = autoOnConnect.value,
                    onToggleAutoOnConnect = ::setAutoOnConnect,
                    autoSelectStatus = autoSelectStatus.value,
                    onAutoSelectNow = { autoSelectNow(exhaustive = false) },
                    onExhaustiveSelectNow = { autoSelectNow(exhaustive = true) },
                    connMode = connMode.value,
                    onSetConnMode = ::setConnMode,
                    vlessUrl = vlessUrl.value,
                    onSetVlessUrl = { vlessUrl.value = it; savePref("dash_vless_url", it) },
                    amneziaConf = amneziaConf.value,
                    onSetAmneziaConf = { amneziaConf.value = it; savePref("dash_amnezia_conf", it) },
                    updateVersion = updateVersion.value,
                    updateStatus = updateStatus.value,
                    onUpdate = ::startUpdate,
                )
            }
        }
    }

    /** Silent update check on launch — mirrors the PC app. Shows a card only if a newer release exists. */
    private fun checkForUpdates() {
        lifecycleScope.launch {
            val info = UpdateChecker.check(BuildConfig.VERSION_NAME) ?: return@launch
            pendingUpdate = info
            updateVersion.value = info.version
        }
    }

    /** Downloads the release APK and hands it to the system installer. */
    private fun startUpdate() {
        val info = pendingUpdate ?: return
        updateStatus.value = "Скачиваю обновление…"
        lifecycleScope.launch {
            val file = UpdateChecker.download(this@MainActivity, info.apkUrl) { p ->
                updateStatus.value = "Скачиваю обновление… $p%"
            }
            if (file == null) {
                updateStatus.value = "Не удалось скачать. Открой страницу релизов вручную."
                return@launch
            }
            updateStatus.value = "Запускаю установку…"
            try {
                val uri = FileProvider.getUriForFile(
                    this@MainActivity, "$packageName.fileprovider", file)
                startActivity(Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(uri, "application/vnd.android.package-archive")
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                })
            } catch (e: Exception) {
                updateStatus.value = "Не удалось открыть установщик: ${e.message}"
            }
        }
    }

    private fun setAutoOnConnect(v: Boolean) {
        autoOnConnect.value = v
        PreferenceManager.getDefaultSharedPreferences(this).edit().putBoolean("dash_auto_on_connect", v).apply()
    }

    private fun setConnMode(v: String) {
        connMode.value = v
        savePref("dash_conn_mode", v)
    }

    private fun savePref(key: String, value: String) {
        PreferenceManager.getDefaultSharedPreferences(this).edit().putString(key, value).apply()
    }

    /** Real auto-select: run each strategy on a private SOCKS port, keep the one that truly passes. */
    private fun autoSelectNow(exhaustive: Boolean) {
        if (running.value) { autoSelectStatus.value = "Сначала отключись, потом подбирай"; return }
        lifecycleScope.launch {
            val best = DashAutoSelect.run(
                DashStrategies.all, exhaustive, preferredArgs = strategyArgs.value,
            ) { autoSelectStatus.value = it }
            if (best != null) { DashStrategies.select(this@MainActivity, best); strategyArgs.value = best.args }
        }
    }

    /**
     * Raises the local tg-ws-proxy bridge ([TgWsBridge]) — the phone port of the PC bridge: a bundled
     * native MTProto proxy on 127.0.0.1:1443 that tunnels Telegram to its DCs over WebSocket/TLS (looks
     * like HTTPS, no third-party server). Then hands Telegram the one-tap tg:// link so it connects.
     */
    private fun setupTelegram() {
        if (!TgWsBridge.binaryExists(this)) {
            telegramStatus.value = "Мост недоступен в этой сборке."
            return
        }
        telegramStatus.value = "Поднимаю Telegram-мост…"
        TgWsBridge.start(this)
        lifecycleScope.launch {
            if (TgWsBridge.awaitReady(9000)) {
                telegramStatus.value = "Мост поднят. В Telegram нажми «Подключить прокси»."
                try {
                    startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(TgWsBridge.tgLink(this@MainActivity))))
                } catch (e: Exception) {
                    telegramStatus.value = "Мост работает на 127.0.0.1:1443 — открой Telegram, прокси уже добавлен."
                }
            } else {
                telegramStatus.value = "Мост не поднялся — проверь интернет и попробуй ещё раз."
            }
        }
    }

    override fun onResume() {
        super.onResume()
        sync()
    }

    override fun onDestroy() {
        super.onDestroy()
        unregisterReceiver(receiver)
    }

    private fun sync() {
        running.value = appStatus.first == AppStatus.Running
    }

    private fun toggle() {
        val (status, _) = appStatus
        if (status == AppStatus.Running) {
            connecting.value = true
            ServiceManager.stop(this)
        } else {
            connecting.value = true
            // Auto-select only applies to the DPI-bypass engine (it tunes ByeDPI strategies).
            if (connMode.value == "dpi" && autoOnConnect.value) {
                lifecycleScope.launch {
                    autoSelectStatus.value = "Подбираю рабочую стратегию…"
                    val best = DashAutoSelect.run(
                        DashStrategies.all, preferredArgs = strategyArgs.value,
                    ) { autoSelectStatus.value = it }
                    if (best != null) {
                        DashStrategies.select(this@MainActivity, best)
                        strategyArgs.value = best.args
                    }
                    startEngine()
                }
            } else {
                startEngine()
            }
        }
    }

    private fun startEngine() {
        val prepare = VpnService.prepare(this)
        if (prepare != null) vpnRegister.launch(prepare) else launchSelected()
    }

    /** Start whichever engine the user picked: DPI-bypass (ByeDPI) or the real VLESS/Amnezia tunnel. */
    private fun launchSelected() {
        when (connMode.value) {
            "vless", "amnezia" -> ServiceManager.startTunnel(this)
            else -> ServiceManager.start(this, Mode.VPN)
        }
    }
}
