package io.github.dovecoteescapee.byedpi.services

import android.content.Context
import android.content.Intent
import android.util.Log
import androidx.core.content.ContextCompat
import io.github.dovecoteescapee.byedpi.data.Mode
import io.github.dovecoteescapee.byedpi.data.START_ACTION
import io.github.dovecoteescapee.byedpi.data.STOP_ACTION

object ServiceManager {
    private val TAG: String = ServiceManager::class.java.simpleName

    fun start(context: Context, mode: Mode) {
        runningEngine = Engine.DPI
        when (mode) {
            Mode.VPN -> {
                Log.i(TAG, "Starting VPN")
                val intent = Intent(context, ByeDpiVpnService::class.java)
                intent.action = START_ACTION
                ContextCompat.startForegroundService(context, intent)
            }

            Mode.Proxy -> {
                Log.i(TAG, "Starting proxy")
                val intent = Intent(context, ByeDpiProxyService::class.java)
                intent.action = START_ACTION
                ContextCompat.startForegroundService(context, intent)
            }
        }
    }

    /** Start the real VLESS / WireGuard tunnel (sing-box libbox engine). */
    fun startTunnel(context: Context) {
        Log.i(TAG, "Starting tunnel")
        runningEngine = Engine.TUNNEL
        val intent = Intent(context, SingBoxVpnService::class.java)
        intent.action = START_ACTION
        ContextCompat.startForegroundService(context, intent)
    }

    fun stop(context: Context) {
        if (runningEngine == Engine.TUNNEL) {
            Log.i(TAG, "Stopping tunnel")
            val intent = Intent(context, SingBoxVpnService::class.java)
            intent.action = STOP_ACTION
            ContextCompat.startForegroundService(context, intent)
            return
        }
        val (_, mode) = appStatus
        when (mode) {
            Mode.VPN -> {
                Log.i(TAG, "Stopping VPN")
                val intent = Intent(context, ByeDpiVpnService::class.java)
                intent.action = STOP_ACTION
                ContextCompat.startForegroundService(context, intent)
            }

            Mode.Proxy -> {
                Log.i(TAG, "Stopping proxy")
                val intent = Intent(context, ByeDpiProxyService::class.java)
                intent.action = STOP_ACTION
                ContextCompat.startForegroundService(context, intent)
            }
        }
    }
}
