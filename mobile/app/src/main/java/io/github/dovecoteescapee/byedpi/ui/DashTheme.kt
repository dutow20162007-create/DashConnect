package io.github.dovecoteescapee.byedpi.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** Palette lifted 1:1 from the PC app (monochrome dark + translucent "glass"). */
object Dash {
    val Bg = Color(0xFF0A0A0A)
    val Surface = Color(0xA8141414)     // translucent glass card
    val SurfaceAlt = Color(0xA81E1E1E)
    val Inset = Color(0xFF0A0A0A)
    val Border = Color(0x3DFFFFFF)      // subtle glass rim
    val Text = Color(0xFFF2F2F2)
    val Muted = Color(0xFF8A8A8A)
    val Accent = Color(0xFF22C55E)      // connected = green
    val AccentDim = Color(0xFF166534)
    val Danger = Color(0xFFEF4444)
    val Warn = Color(0xFFF59E0B)
    val White = Color(0xFFFFFFFF)
}

@Composable
fun DashTheme(content: @Composable () -> Unit) {
    @Suppress("UNUSED_EXPRESSION") isSystemInDarkTheme() // app is always dark, like the PC version
    val scheme = darkColorScheme(
        primary = Dash.Accent,
        background = Dash.Bg,
        surface = Dash.Surface,
        onPrimary = Dash.White,
        onBackground = Dash.Text,
        onSurface = Dash.Text,
    )
    MaterialTheme(colorScheme = scheme, content = content)
}
