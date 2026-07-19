package io.github.dovecoteescapee.byedpi.utility

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.os.Build
import androidx.annotation.StringRes
import androidx.core.app.NotificationCompat
import io.github.dovecoteescapee.byedpi.R
import io.github.dovecoteescapee.byedpi.activities.MainActivity
import io.github.dovecoteescapee.byedpi.data.STOP_ACTION

private const val NOTIFICATION_GROUP = "dashconnect"

fun registerNotificationChannel(context: Context, id: String, @StringRes name: Int) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return

        // IMPORTANCE_MIN keeps these ongoing service notifications OUT of the status bar (no icon at the
        // top) and collapsed at the bottom of the shade — the app runs 1-2 foreground services (DPI VPN
        // + Telegram bridge) and users complained about the pile of icons up top.
        val channel = NotificationChannel(
            id,
            context.getString(name),
            NotificationManager.IMPORTANCE_MIN
        )
        channel.enableLights(false)
        channel.enableVibration(false)
        channel.setShowBadge(false)

        manager.createNotificationChannel(channel)
    }
}

fun createConnectionNotification(
    context: Context,
    channelId: String,
    @StringRes title: Int,
    @StringRes content: Int,
    service: Class<*>,
): Notification =
    NotificationCompat.Builder(context, channelId)
        .setSmallIcon(R.drawable.ic_notification)
        .setLargeIcon(BitmapFactory.decodeResource(context.resources, R.mipmap.ic_launcher))
        .setSilent(true)
        .setPriority(NotificationCompat.PRIORITY_MIN)
        .setGroup(NOTIFICATION_GROUP) // collapse the app's ongoing notifications together
            .setContentTitle(context.getString(title))
            .setContentText(context.getString(content))
            .addAction(0, "Отключить",
                PendingIntent.getService(
                    context,
                    0,
                    Intent(context, service).setAction(STOP_ACTION),
                    PendingIntent.FLAG_IMMUTABLE,
                )
            )
            .setContentIntent(
                PendingIntent.getActivity(
                    context,
                    0,
                    Intent(context, MainActivity::class.java),
                    PendingIntent.FLAG_IMMUTABLE,
                )
            )
        .build()
