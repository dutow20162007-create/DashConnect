package io.github.dovecoteescapee.byedpi.services

import android.os.SystemClock
import io.github.dovecoteescapee.byedpi.data.AppStatus
import io.github.dovecoteescapee.byedpi.data.Mode

var appStatus = AppStatus.Halted to Mode.VPN
    private set

/**
 * Monotonic timestamp (elapsedRealtime) of when the current connection came up, or 0 when not
 * connected. The UI computes the elapsed timer from THIS, so it survives re-opening the app instead of
 * restarting from zero on every recomposition.
 */
var connectedSince = 0L
    private set

fun setStatus(status: AppStatus, mode: Mode) {
    appStatus = status to mode
    connectedSince = when {
        status == AppStatus.Running && connectedSince == 0L -> SystemClock.elapsedRealtime()
        status == AppStatus.Running -> connectedSince // keep the original start time
        else -> 0L
    }
}
