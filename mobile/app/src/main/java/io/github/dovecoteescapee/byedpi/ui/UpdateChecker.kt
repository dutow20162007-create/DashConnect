package io.github.dovecoteescapee.byedpi.ui

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** [sha256Url] points at a sibling `<apk>.sha256` (null on older releases without one). */
data class UpdateInfo(val version: String, val apkUrl: String, val notes: String, val sha256Url: String? = null)

/**
 * The phone counterpart of the PC UpdateChecker: looks up the newest GitHub release and reports one
 * only when it is actually newer than the installed build.
 *
 * Two lookup paths, same as the PC app: api.github.com first (gives the real asset URL + notes), then
 * the plain github.com/…/releases/latest redirect as a fallback — the API host is frequently blocked
 * in RU while the normal site still resolves.
 */
object UpdateChecker {
    private const val OWNER = "dutow20162007-create"
    private const val REPO = "DashConnect"

    const val RELEASES_PAGE = "https://github.com/$OWNER/$REPO/releases/latest"

    suspend fun check(currentVersion: String): UpdateInfo? = withContext(Dispatchers.IO) {
        val latest = viaApi() ?: viaRedirect() ?: return@withContext null
        if (isNewer(latest.version, currentVersion)) latest else null
    }

    private fun viaApi(): UpdateInfo? = try {
        val body = httpGet("https://api.github.com/repos/$OWNER/$REPO/releases/latest")
        val o = JSONObject(body)
        val tag = o.optString("tag_name").trimStart('v', 'V')
        var apk = ""
        var sha = ""
        o.optJSONArray("assets")?.let { arr ->
            for (i in 0 until arr.length()) {
                val a = arr.getJSONObject(i)
                val name = a.optString("name")
                if (name.endsWith(".apk.sha256", ignoreCase = true)) sha = a.optString("browser_download_url")
                else if (name.endsWith(".apk", ignoreCase = true)) apk = a.optString("browser_download_url")
            }
        }
        if (tag.isEmpty()) null
        else UpdateInfo(tag, apk.ifEmpty { apkUrlFor(tag) }, o.optString("body"), sha.ifEmpty { null })
    } catch (e: Exception) {
        null
    }

    private fun viaRedirect(): UpdateInfo? = try {
        val c = (URL(RELEASES_PAGE).openConnection() as HttpURLConnection).apply {
            instanceFollowRedirects = false
            connectTimeout = 6000; readTimeout = 6000
            setRequestProperty("User-Agent", "DashConnect")
        }
        val location = c.getHeaderField("Location") ?: ""
        c.disconnect()
        val tag = location.substringAfterLast("/tag/", "").trimStart('v', 'V')
        if (tag.isEmpty()) null else UpdateInfo(tag, apkUrlFor(tag), "", apkUrlFor(tag) + ".sha256")
    } catch (e: Exception) {
        null
    }

    /** Release assets are published as DashConnect-<version>.apk. */
    private fun apkUrlFor(v: String) =
        "https://github.com/$OWNER/$REPO/releases/download/v$v/DashConnect-$v.apk"

    /**
     * Downloads the APK into cache and returns the file, or null on failure. When [sha256Url] is
     * given and the release published a checksum, the file is verified against it and a confirmed
     * MISMATCH fails the download (returns null) so a tampered/corrupt APK is never installed.
     */
    suspend fun download(ctx: Context, url: String, sha256Url: String? = null, onProgress: (Int) -> Unit = {}): File? =
        withContext(Dispatchers.IO) {
            try {
                val dir = File(ctx.cacheDir, "update").apply { mkdirs() }
                val out = File(dir, "DashConnect-update.apk")
                if (out.exists()) out.delete()
                val c = (URL(url).openConnection() as HttpURLConnection).apply {
                    instanceFollowRedirects = true
                    connectTimeout = 15000; readTimeout = 30000
                    setRequestProperty("User-Agent", "DashConnect")
                }
                if (c.responseCode !in 200..299) { c.disconnect(); return@withContext null }
                val total = c.contentLength.toLong()
                var read = 0L
                c.inputStream.use { input ->
                    out.outputStream().use { fout ->
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            fout.write(buf, 0, n)
                            read += n
                            if (total > 0) onProgress(((read * 100) / total).toInt())
                        }
                    }
                }
                c.disconnect()
                if (out.length() <= 0) return@withContext null
                if (!verifyChecksum(out, sha256Url)) { out.delete(); return@withContext null }
                out
            } catch (e: Exception) {
                null
            }
        }

    /** Fail-open: if the checksum can't be fetched/parsed (older release), returns true; only a real
     *  mismatch returns false. */
    private fun verifyChecksum(file: File, sha256Url: String?): Boolean {
        if (sha256Url.isNullOrEmpty()) return true
        val expected = try {
            val c = (URL(sha256Url).openConnection() as HttpURLConnection).apply {
                instanceFollowRedirects = true; connectTimeout = 10000; readTimeout = 15000
                setRequestProperty("User-Agent", "DashConnect")
            }
            if (c.responseCode !in 200..299) { c.disconnect(); return true }
            val text = c.inputStream.bufferedReader().use { it.readText() }
            c.disconnect()
            Regex("[0-9a-fA-F]{64}").find(text)?.value?.lowercase() ?: return true
        } catch (e: Exception) { return true }

        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buf = ByteArray(64 * 1024)
            while (true) { val n = input.read(buf); if (n < 0) break; md.update(buf, 0, n) }
        }
        val actual = md.digest().joinToString("") { "%02x".format(it) }
        return actual == expected
    }

    private fun httpGet(url: String): String {
        val c = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 6000; readTimeout = 6000
            setRequestProperty("User-Agent", "DashConnect")
            setRequestProperty("Accept", "application/vnd.github+json")
        }
        return try { c.inputStream.bufferedReader().readText() } finally { c.disconnect() }
    }

    /** Numeric, segment-wise compare so 1.1.10 correctly beats 1.1.9. */
    fun isNewer(latest: String, current: String): Boolean {
        val a = latest.split('.', '-').mapNotNull { it.toIntOrNull() }
        val b = current.split('.', '-').mapNotNull { it.toIntOrNull() }
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }
}
