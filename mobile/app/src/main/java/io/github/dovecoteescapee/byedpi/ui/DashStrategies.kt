package io.github.dovecoteescapee.byedpi.ui

import android.content.Context
import androidx.preference.PreferenceManager

/**
 * ByeDPI desync strategies — the phone analog of the PC zapret presets. Every command below was
 * adversarially verified against the bundled native parser (cpp/byedpi + cpp/utils.c `parse_args`):
 * ByeDPI has NO "ignore unknown flag" behaviour — a SINGLE bad token makes socket creation return -1,
 * so the whole proxy fails to bind. These are all valid syntax and drawn from strategies reported
 * working on RU ISPs (Rostelecom/Megafon/Beeline/MTS/Tele2/Yota/MGTS). DPI is ISP-specific, so the
 * app auto-selects the working one (see [DashAutoSelect]) or lets the user pick.
 *
 * The byedpi listener is pinned to 127.0.0.1:1080 ([BIND]) so it always matches the tun2socks dial
 * port; strategies that include `-a`/UDP-fakes also cover YouTube QUIC + Discord voice.
 */
data class DashStrategy(val name: String, val hint: String, val args: String)

object DashStrategies {
    /** Appended to every strategy so the byedpi SOCKS listener is always on the port tun2socks dials. */
    private const val BIND = "-p 1080"

    val all = listOf(
        DashStrategy("Универсальный (авто)", "адаптивно: десинхрон при сбросе соединения",
            "--auto=torst -d1+s -o1+s -f-1 -t3"),
        DashStrategy("Базовый", "сплит + OOB + дизордер",
            "-s1 -o1 -d1"),
        DashStrategy("SNI-сплит + fake", "разрез по SNI + поддельный пакет",
            "-s1+s -f-1 -S -b661"),
        DashStrategy("Fake-SNI + авто", "поддельный SNI google + авто-триггеры",
            "--tls-sni=google.com -o2 --auto=t,r,a,s -d2"),
        DashStrategy("OOB YouTube", "быстрое видео, двойной OOB + TLSrec",
            "-o1 -o25+s -T3 -At -r1+s"),
        DashStrategy("Мегафон (сплит+fake)", "сплит + авто-профили + fake",
            "-s1 -o1 -Ar -o1 -At -f-1 -r1+s"),
        DashStrategy("Мульти-десинхрон + UDP", "тяжёлый, стойкий TSPU + UDP-фейк",
            "-d1 -d3+s -s6+s -d9+s -s12+s -d15+s -s20+s -d25+s -s30+s -d35+s -r1+s -S -a1"),
        DashStrategy("Discord голос (UDP)", "голос Discord + видео (UDP-фейки)",
            "-o1 -a2 -f-1 -d6+s -o2 -a1 -q4+h -t7 -f6 -q2 -d11 -f9+h -o3 -a2"),
        DashStrategy("Мегафон/Билайн", "fake-SNI + OOB + авто (TLS)",
            "-n www.google.com -o1 -f-1 -Ar,s -o0+s -At -r1+s -f-1 -t6 -Kt"),
        DashStrategy("Агрессивный (МТС)", "максимальный десинхрон, стойкий DPI",
            "-n google.com -f-204 -s1+s -a1 -As -d1 -s3+s -s5+s -q7 -a1 -o2 -f-43 -r5 -Mh"),
        DashStrategy("Лёгкий (torst)", "минимальный, низкий пинг",
            "--auto=torst -d1+s -o1+s"),
        DashStrategy("Дизордер+фейк (Ростелеком)", "реордер по SNI + фейк с низким TTL",
            "-d1+s -f-1 -t4 -S"),
        DashStrategy("Двойной TLS-рекорд", "фрагментация TLS-записи в двух точках",
            "-r1+s -r2+s -s2+s -f-1 -t5"),
        DashStrategy("Disoob (Discord)", "дизордер-OOB по SNI и Host + фейк",
            "-q1+s -q5+h -f-1 -t6 -S"),
        DashStrategy("OOB со своим байтом", "OOB-сплит по SNI с кастомным байтом",
            "-o1+s -e! -f-1 -t4 -S"),
        DashStrategy("Drop-SACK + сплит", "дроп SACK-пакетов + сплит/дизордер",
            "-Y -s1+s -d2+s -f-1 -t5"),
        DashStrategy("Замедленный десинхрон", "сплит с задержкой отправки сегментов",
            "-w2 -s1+s -d2+s -f-1 -t4"),
        DashStrategy("QUIC/голос (UDP-фейки)", "TLS-сплит + 4 UDP-фейка для видео/голоса",
            "-s1+s -f-1 -t5 -a4 -Kt,u"),
        DashStrategy("Сплит по концу ClientHello", "фрагментация по END-смещениям + TLS-рекорд",
            "-s2+s -d4+e -s8+e -f-1 -t5 -r1+s"),
        DashStrategy("Авто + fake SNI vk.com", "фейк-SNI vk.com (не блокируется в РФ) + авто",
            "--tls-sni=vk.com -s1+s -f-1 --auto=s,r -d2+s -q3+s -t5"),
        DashStrategy("HTTP-мод + сплит", "модификация HTTP + сплит по SNI + фейк",
            "-s1+s -d3+s -Mh,d,r -f-1 -t5"),
    )

    val default get() = all.first()

    /** Ensures the app runs in ByeDPI command-line mode with a strong, port-pinned strategy at first run. */
    fun ensureDefaults(ctx: Context) {
        val p = PreferenceManager.getDefaultSharedPreferences(ctx)
        // Bump the marker so devices that shipped the old (weaker/mis-tuned) defaults get re-seeded.
        if (p.getInt("dash_defaults_ver", 0) < 2) {
            p.edit()
                .putBoolean("byedpi_enable_cmd_settings", true)
                .putString("byedpi_cmd_args", withBind(default.args))
                .putString("byedpi_proxy_ip", "127.0.0.1")
                .putString("byedpi_proxy_port", "1080")
                .putInt("dash_defaults_ver", 2)
                .apply()
        }
    }

    fun current(ctx: Context): String =
        PreferenceManager.getDefaultSharedPreferences(ctx).getString("byedpi_cmd_args", withBind(default.args))
            ?: withBind(default.args)

    /** The pure desync args (bind flags stripped) matching the stored strategy, for UI highlighting. */
    fun currentArgs(ctx: Context): String = stripBind(current(ctx))

    fun select(ctx: Context, args: String) {
        PreferenceManager.getDefaultSharedPreferences(ctx).edit()
            .putBoolean("byedpi_enable_cmd_settings", true)
            .putString("byedpi_cmd_args", withBind(args))
            .apply()
    }

    fun select(ctx: Context, s: DashStrategy) = select(ctx, s.args)

    private fun withBind(args: String): String = "${args.trim()} $BIND"
    private fun stripBind(stored: String): String = stored.trim().removeSuffix(BIND).trim()
}
