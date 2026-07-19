package io.github.dovecoteescapee.byedpi.ui

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import kotlin.random.Random

private class Dot(var x: Float, var y: Float, val vx: Float, val vy: Float, val r: Float, val a: Float)

/**
 * Soft white dots drifting up on black — the PC app's particle field. Capped at ~30&#160;fps and
 * drawn in one Canvas pass.
 *
 * Driven by [withFrameNanos] (the Compose frame clock) rather than a `delay()` loop ON PURPOSE: the
 * frame clock stops producing frames when the window isn't visible, so the animation pauses by itself
 * in the background. The old timer loop kept waking the main thread ~30x/second forever — the single
 * biggest avoidable battery drain while the VPN sat connected in the user's pocket.
 */
@Composable
fun ParticleField(modifier: Modifier = Modifier, count: Int = 70) {
    val rnd = remember { Random(7) }
    val dots = remember { ArrayList<Dot>() }
    var w by remember { mutableStateOf(0f) }
    var h by remember { mutableStateOf(0f) }
    var frame by remember { mutableIntStateOf(0) } // Int state: no boxing on every tick

    LaunchedEffect(w, h) {
        if (w <= 0f || h <= 0f) return@LaunchedEffect
        if (dots.isEmpty()) {
            repeat(count) {
                dots.add(
                    Dot(
                        x = rnd.nextFloat() * w,
                        y = rnd.nextFloat() * h,
                        vx = (rnd.nextFloat() - 0.5f) * 6f,
                        vy = -(4f + rnd.nextFloat() * 11f),
                        r = 1.3f + rnd.nextFloat() * 2.6f,
                        a = 0.10f + rnd.nextFloat() * 0.34f,
                    )
                )
            }
        }
        var last = 0L
        while (true) {
            withFrameNanos { now ->
                if (last == 0L) last = now
                val elapsed = now - last
                // Still cap at ~30 fps: on a 120 Hz panel the frame clock would otherwise tick 4x more
                // often than the motion needs, burning CPU for no visible difference.
                if (elapsed >= 30_000_000L) {
                    val dt = (elapsed / 1_000_000_000f).coerceAtMost(0.05f) // clamp after a pause
                    last = now
                    for (p in dots) {
                        p.x += p.vx * dt
                        p.y += p.vy * dt
                        if (p.y < -p.r) { p.y = h + p.r; p.x = rnd.nextFloat() * w }
                        if (p.x < -p.r) p.x += w + p.r else if (p.x > w + p.r) p.x -= w + p.r
                    }
                    frame++
                }
            }
        }
    }

    Canvas(modifier.onSizeChanged { w = it.width.toFloat(); h = it.height.toFloat() }) {
        @Suppress("UNUSED_EXPRESSION") frame // read so the draw recomposes each tick
        for (p in dots) {
            drawCircle(color = Color.White.copy(alpha = p.a), radius = p.r, center = Offset(p.x, p.y))
        }
    }
}
