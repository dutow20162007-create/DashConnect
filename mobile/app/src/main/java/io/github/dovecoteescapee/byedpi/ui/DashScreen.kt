package io.github.dovecoteescapee.byedpi.ui

import android.content.Intent
import android.net.Uri
import android.os.SystemClock
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.dovecoteescapee.byedpi.R
import io.github.dovecoteescapee.byedpi.services.connectedSince
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun DashScreen(
    connected: Boolean,
    connecting: Boolean,
    onToggle: () -> Unit,
    onOpenEngineSettings: () -> Unit,
    versionName: String,
    strategies: List<DashStrategy>,
    currentArgs: String,
    onSelectStrategy: (DashStrategy) -> Unit,
    telegramStatus: String,
    onSetupTelegram: () -> Unit,
    autoOnConnect: Boolean,
    onToggleAutoOnConnect: (Boolean) -> Unit,
    autoSelectStatus: String,
    onAutoSelectNow: () -> Unit,
    onExhaustiveSelectNow: () -> Unit,
    connMode: String,
    onSetConnMode: (String) -> Unit,
    vlessUrl: String,
    onSetVlessUrl: (String) -> Unit,
    amneziaConf: String,
    onSetAmneziaConf: (String) -> Unit,
    updateVersion: String? = null,
    updateStatus: String = "",
    onUpdate: () -> Unit = {},
) {
    var tab by remember { mutableStateOf(0) }

    Box(
        Modifier.fillMaxSize().background(
            Brush.verticalGradient(listOf(Color(0xFF121214), Dash.Bg, Color(0xFF07110B)))
        )
    ) {
        ParticleField(Modifier.matchParentSize())

        Column(Modifier.fillMaxSize()) {
            // Header (logo + title) — same brand block as the PC sidebar.
            Row(
                Modifier.fillMaxWidth().padding(start = 18.dp, end = 18.dp, top = 20.dp, bottom = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Image(
                    painter = painterResource(R.mipmap.ic_launcher),
                    contentDescription = null,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(10.dp)),
                )
                Spacer(Modifier.width(11.dp))
                Column(Modifier.weight(1f)) {
                    Text("Dash Connect", color = Dash.Text, fontSize = 21.sp, fontWeight = FontWeight.Bold,
                        maxLines = 1)
                    Text("обход блокировок · Discord · YouTube · Telegram", color = Dash.Muted,
                        fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                // Update button — appears in the header (right of the title) only when a newer release
                // exists. Same flow as before: download the APK, hand it to the system installer.
                if (updateVersion != null) {
                    Spacer(Modifier.width(8.dp))
                    Box(
                        Modifier
                            .clip(RoundedCornerShape(14.dp))
                            .background(Dash.AccentDim.copy(alpha = 0.35f))
                            .border(1.dp, Dash.Accent, RoundedCornerShape(14.dp))
                            .clickable { onUpdate() }
                            .padding(horizontal = 12.dp, vertical = 7.dp),
                    ) {
                        Text(
                            updateStatus.ifEmpty { "Обновить" },
                            color = Dash.Accent, fontSize = 12.sp,
                            fontWeight = FontWeight.SemiBold, maxLines = 1,
                        )
                    }
                }
            }

            // Content
            Box(Modifier.weight(1f).fillMaxWidth().padding(horizontal = 18.dp)) {
                if (tab == 0) ConnectTab(connected, connecting, connMode, autoSelectStatus, onToggle)
                else SettingsTab(
                    strategies, currentArgs, onSelectStrategy,
                    autoOnConnect, onToggleAutoOnConnect, autoSelectStatus, onAutoSelectNow, onExhaustiveSelectNow,
                    telegramStatus, onSetupTelegram, onOpenEngineSettings, versionName,
                    connMode, onSetConnMode, vlessUrl, onSetVlessUrl, amneziaConf, onSetAmneziaConf,
                )
            }

            // Bottom navigation (tabs at the BOTTOM, per request).
            BottomNav(tab) { tab = it }
        }
    }
}

@Composable
private fun BottomNav(selected: Int, onSelect: (Int) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 12.dp)
            .clip(RoundedCornerShape(24.dp))
            .background(Dash.Surface)
            .border(1.dp, Dash.Border, RoundedCornerShape(24.dp))
            .padding(6.dp),
    ) {
        NavItem("⚡", "Подключение", selected == 0, Modifier.weight(1f)) { onSelect(0) }
        NavItem("⚙", "Настройки", selected == 1, Modifier.weight(1f)) { onSelect(1) }
    }
}

@Composable
private fun NavItem(glyph: String, label: String, active: Boolean, modifier: Modifier, onClick: () -> Unit) {
    Row(
        modifier
            .clip(RoundedCornerShape(20.dp))
            .background(if (active) Dash.AccentDim.copy(alpha = 0.32f) else Color.Transparent)
            .clickable { onClick() }
            .padding(vertical = 11.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(glyph, color = if (active) Dash.Accent else Dash.Muted, fontSize = 16.sp)
        Spacer(Modifier.width(7.dp))
        Text(label, color = if (active) Dash.Text else Dash.Muted, fontSize = 13.sp,
            fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal)
    }
}

@Composable
private fun ConnectTab(
    connected: Boolean, connecting: Boolean, connMode: String,
    autoSelectStatus: String, onToggle: () -> Unit,
) {
    // Elapsed time is derived from the GLOBAL connect start time (connectedSince), so re-opening the
    // app shows the real duration instead of restarting from 00:00:00.
    var elapsed by remember { mutableStateOf(0L) }
    LaunchedEffect(connected) {
        while (connected) {
            val since = connectedSince
            elapsed = if (since > 0) (SystemClock.elapsedRealtime() - since) / 1000 else 0
            delay(1000)
        }
    }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(28.dp))
        val ring = if (connected) Dash.Accent else Dash.Border
        val fill = if (connected)
            Brush.verticalGradient(listOf(Dash.Accent.copy(alpha = 0.45f), Dash.AccentDim.copy(alpha = 0.28f)))
        else
            Brush.verticalGradient(listOf(Dash.SurfaceAlt, Dash.Surface))
        // Soft outer glow (only meaningful when connected).
        Box(Modifier.size(228.dp), contentAlignment = Alignment.Center) {
            if (connected) {
                Box(Modifier.size(210.dp).clip(CircleShape)
                    .background(Dash.Accent.copy(alpha = 0.14f)).blur(26.dp))
            }
            Box(
                Modifier.size(184.dp).clip(CircleShape).background(fill)
                    .border(2.dp, ring, CircleShape)
                    .clickable(enabled = !connecting) { onToggle() },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    when { connecting -> "…"; connected -> "Отключить"; else -> "Подключить" },
                    color = Dash.Text, fontSize = 20.sp, fontWeight = FontWeight.SemiBold,
                )
            }
        }
        Spacer(Modifier.height(20.dp))
        Text(if (connected) "Защищено" else "Отключено",
            color = if (connected) Dash.Accent else Dash.Muted, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(4.dp))
        Text("Режим: ${connModeLabel(connMode)}", color = Dash.Muted, fontSize = 11.sp)
        if (connected) {
            Spacer(Modifier.height(4.dp))
            Text(formatTime(elapsed), color = Dash.Muted, fontSize = 12.sp)
        }
        // Auto-select progress is shown HERE too (not only in Settings) so the user sees which strategy
        // is being tried while connecting.
        if (connecting && autoSelectStatus.isNotEmpty()) {
            Spacer(Modifier.height(8.dp))
            Text(autoSelectStatus, color = Dash.Accent, fontSize = 12.sp,
                fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 24.dp))
        }
        Spacer(Modifier.height(22.dp))
        DiagnosticsCard(connected, connMode)
        Spacer(Modifier.height(18.dp))
    }
}

@Composable
private fun DiagnosticsCard(connected: Boolean, connMode: String) {
    val scope = rememberCoroutineScope()
    var probes by remember { mutableStateOf<List<Probe>>(emptyList()) }
    var running by remember { mutableStateOf(false) }

    // While the DPI VPN is up the app is excluded from the tunnel, so probe through the byedpi SOCKS
    // (127.0.0.1:1080) — the same desync path real traffic takes — otherwise the check falsely reads
    // "blocked" while YouTube actually plays.
    val socksPort = if (connected && connMode == "dpi") 1080 else 0

    fun refresh() {
        if (running) return
        running = true
        scope.launch {
            // pingAll probes all four endpoints at once; the old per-target map was sequential.
            try { probes = Diagnostics.pingAll(socksPort) } finally { running = false }
        }
    }
    LaunchedEffect(connected) { refresh() }

    GlassCard {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("ДОСТУПНОСТЬ (реальная проверка)", color = Dash.Muted, fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            Text(if (running) "…" else "Обновить", color = Dash.Text, fontSize = 12.sp,
                modifier = Modifier.clickable { refresh() }.padding(4.dp))
        }
        Spacer(Modifier.height(10.dp))
        val items = if (probes.isEmpty()) Diagnostics.targets.map { Probe(it.label, false, -1L) } else probes
        items.chunked(2).forEach { row ->
            Row(Modifier.fillMaxWidth()) {
                row.forEach { p -> Box(Modifier.weight(1f).padding(3.dp)) { Chip(p) } }
                if (row.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun Chip(p: Probe) {
    val color = when { p.ms < 0L -> Dash.Muted; p.ok -> Dash.Accent; else -> Dash.Danger }
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Dash.SurfaceAlt)
            .border(1.dp, Dash.Border, RoundedCornerShape(14.dp)).padding(horizontal = 10.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(if (p.ok) "●" else if (p.ms < 0) "…" else "×", color = color, fontSize = 13.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.width(7.dp))
        Column {
            Text(p.label, color = Dash.Text, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
            Text(when { p.ms < 0 -> "проверка…"; p.ok -> "работает · ${p.ms} мс"; else -> "заблокировано" },
                color = if (p.ok) Dash.Muted else color, fontSize = 10.5.sp)
        }
    }
}

@Composable
private fun SettingsTab(
    strategies: List<DashStrategy>,
    currentArgs: String,
    onSelectStrategy: (DashStrategy) -> Unit,
    autoOnConnect: Boolean,
    onToggleAutoOnConnect: (Boolean) -> Unit,
    autoSelectStatus: String,
    onAutoSelectNow: () -> Unit,
    onExhaustiveSelectNow: () -> Unit,
    telegramStatus: String,
    onSetupTelegram: () -> Unit,
    onOpenEngineSettings: () -> Unit,
    versionName: String,
    connMode: String,
    onSetConnMode: (String) -> Unit,
    vlessUrl: String,
    onSetVlessUrl: (String) -> Unit,
    amneziaConf: String,
    onSetAmneziaConf: (String) -> Unit,
) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        Spacer(Modifier.height(6.dp))
        GlassCard {
            Text("Режим подключения", color = Dash.Text, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(4.dp))
            Text("Как ходит трафик. DPI-обход — без своего сервера (Discord/YouTube). VLESS/Amnezia — полноценный VPN через твой сервер (как на ПК).",
                color = Dash.Muted, fontSize = 11.5.sp)
            Spacer(Modifier.height(10.dp))
            ModeRow("DPI-обход (Discord/YouTube)", "без VPN-сервера, локальный обход", connMode == "dpi") { onSetConnMode("dpi") }
            Spacer(Modifier.height(6.dp))
            ModeRow("VPN VLESS", "весь трафик через твой vless:// сервер", connMode == "vless") { onSetConnMode("vless") }
            Spacer(Modifier.height(6.dp))
            ModeRow("VPN Amnezia (.conf)", "WireGuard/AmneziaWG из .conf", connMode == "amnezia") { onSetConnMode("amnezia") }

            if (connMode == "vless") {
                Spacer(Modifier.height(10.dp))
                ConfigField(vlessUrl, onSetVlessUrl, "vless://uuid@host:port?...", singleLine = true)
            }
            if (connMode == "amnezia") {
                Spacer(Modifier.height(10.dp))
                ConfigField(amneziaConf, onSetAmneziaConf, "Вставь весь текст .conf сюда", singleLine = false)
                Spacer(Modifier.height(6.dp))
                Text("Примечание: обфускация AmneziaWG (Jc/S1/H1…) на телефоне пока идёт как обычный WireGuard.",
                    color = Dash.Muted, fontSize = 10.5.sp)
            }
        }
        Spacer(Modifier.height(12.dp))
        GlassCard {
            Text("Стратегия обхода", color = Dash.Text, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(6.dp))
            ToggleRow("Автоподбор при подключении",
                "перед подключением сам найдёт рабочую стратегию (проверяет YouTube-видео и Discord по-настоящему)",
                autoOnConnect, onToggleAutoOnConnect)
            Spacer(Modifier.height(10.dp))
            GhostButton("Подобрать стратегию (быстро)", onAutoSelectNow)
            Spacer(Modifier.height(6.dp))
            GhostButton("Перебрать ВСЕ стратегии", onExhaustiveSelectNow)
            Text("быстрый — берёт первую рабочую; перебор — проверяет все и выбирает лучшую (дольше)",
                color = Dash.Muted, fontSize = 10.5.sp, modifier = Modifier.padding(top = 4.dp))
            if (autoSelectStatus.isNotEmpty()) {
                Spacer(Modifier.height(8.dp)); Text(autoSelectStatus, color = Dash.Muted, fontSize = 11.sp)
            }
            Spacer(Modifier.height(12.dp))
            Text("или выбери вручную:", color = Dash.Muted, fontSize = 11.sp)
            Spacer(Modifier.height(8.dp))
            strategies.forEach { s ->
                StrategyRow(s, s.args == currentArgs) { onSelectStrategy(s) }
                Spacer(Modifier.height(6.dp))
            }
        }
        Spacer(Modifier.height(12.dp))
        GlassCard {
            Text("Telegram (MTProto-мост)", color = Dash.Text, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(4.dp))
            Text("Локальный мост поднимает MTProto-прокси прямо на телефоне (127.0.0.1) и гонит Telegram к его дата-центрам через WebSocket/TLS — выглядит как обычный HTTPS, без чужого сервера. Нажми — мост поднимется и Telegram настроится в один тап.",
                color = Dash.Muted, fontSize = 12.sp)
            Spacer(Modifier.height(10.dp))
            GhostButton("Поднять мост и настроить Telegram", onSetupTelegram)
            if (telegramStatus.isNotEmpty()) {
                Spacer(Modifier.height(8.dp)); Text(telegramStatus, color = Dash.Muted, fontSize = 11.sp)
            }
        }
        Spacer(Modifier.height(12.dp))
        GlassCard {
            Text("Движок", color = Dash.Text, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(4.dp))
            Text("Весь трафик идёт через локальный VPN (ByeDPI) и десинхронизируется. Тонкая настройка — ниже.",
                color = Dash.Muted, fontSize = 12.sp)
            Spacer(Modifier.height(10.dp))
            GhostButton("Все настройки движка", onOpenEngineSettings)
        }
        Spacer(Modifier.height(12.dp))
        GlassCard {
            val ctx = LocalContext.current
            Text("О приложении", color = Dash.Text, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(4.dp))
            Text("Dash Connect для Android · v$versionName", color = Dash.Muted, fontSize = 12.sp)
            Spacer(Modifier.height(4.dp))
            Text(
                "Telegram-канал: t.me/HUGOVSYKAYA",
                color = Dash.Accent, fontSize = 12.sp, fontWeight = FontWeight.SemiBold,
                textDecoration = TextDecoration.Underline,
                modifier = Modifier.clickable {
                    try {
                        ctx.startActivity(
                            Intent(Intent.ACTION_VIEW, Uri.parse("https://t.me/HUGOVSYKAYA"))
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        )
                    } catch (_: Exception) {}
                },
            )
        }
        Spacer(Modifier.height(20.dp))
    }
}

@Composable
private fun ToggleRow(title: String, hint: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(end = 10.dp)) {
            Text(title, color = Dash.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
            Text(hint, color = Dash.Muted, fontSize = 11.sp)
        }
        Switch(
            checked = checked, onCheckedChange = onChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = Dash.White, checkedTrackColor = Dash.Accent,
                uncheckedThumbColor = Dash.Muted, uncheckedTrackColor = Dash.SurfaceAlt,
                uncheckedBorderColor = Dash.Border,
            ),
        )
    }
}

@Composable
private fun ModeRow(title: String, hint: String, active: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp))
            .background(if (active) Dash.AccentDim.copy(alpha = 0.30f) else Dash.SurfaceAlt)
            .border(1.dp, if (active) Dash.Accent else Dash.Border, RoundedCornerShape(14.dp))
            .clickable { onClick() }.padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(if (active) "◉" else "○", color = if (active) Dash.Accent else Dash.Muted, fontSize = 14.sp)
        Spacer(Modifier.width(9.dp))
        Column(Modifier.weight(1f)) {
            Text(title, color = Dash.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
            Text(hint, color = Dash.Muted, fontSize = 10.5.sp)
        }
    }
}

@Composable
private fun ConfigField(value: String, onChange: (String) -> Unit, placeholder: String, singleLine: Boolean) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        placeholder = { Text(placeholder, color = Dash.Muted, fontSize = 12.sp) },
        singleLine = singleLine,
        modifier = Modifier.fillMaxWidth().let { if (singleLine) it else it.heightIn(min = 120.dp) },
        colors = TextFieldDefaults.colors(
            focusedContainerColor = Dash.SurfaceAlt,
            unfocusedContainerColor = Dash.SurfaceAlt,
            focusedTextColor = Dash.Text,
            unfocusedTextColor = Dash.Text,
            cursorColor = Dash.Accent,
            focusedIndicatorColor = Dash.Accent,
            unfocusedIndicatorColor = Dash.Border,
        ),
    )
}

@Composable
private fun StrategyRow(s: DashStrategy, active: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp))
            .background(if (active) Dash.AccentDim.copy(alpha = 0.30f) else Dash.SurfaceAlt)
            .border(1.dp, if (active) Dash.Accent else Dash.Border, RoundedCornerShape(14.dp))
            .clickable { onClick() }.padding(horizontal = 12.dp, vertical = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(if (active) "●" else "○", color = if (active) Dash.Accent else Dash.Muted, fontSize = 13.sp)
        Spacer(Modifier.width(9.dp))
        Column(Modifier.weight(1f)) {
            Text(s.name, color = Dash.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
            Text(s.hint, color = Dash.Muted, fontSize = 10.5.sp)
        }
    }
}

@Composable
private fun GlassCard(content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(20.dp)).background(Dash.Surface)
            .border(1.dp, Dash.Border, RoundedCornerShape(20.dp)).padding(16.dp),
        content = content,
    )
}

@Composable
private fun GhostButton(label: String, onClick: () -> Unit) {
    Box(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Dash.SurfaceAlt)
            .border(1.dp, Dash.Border, RoundedCornerShape(14.dp)).clickable { onClick() }.padding(vertical = 13.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = Dash.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
    }
}

private fun formatTime(sec: Long): String {
    val h = sec / 3600; val m = (sec % 3600) / 60; val s = sec % 60
    return "%02d:%02d:%02d".format(h, m, s)
}

private fun connModeLabel(mode: String): String = when (mode) {
    "vless" -> "VPN VLESS"
    "amnezia" -> "VPN Amnezia"
    else -> "DPI-обход"
}
