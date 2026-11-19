package app.ior.android

import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.util.concurrent.TimeUnit

internal object AppUpdate {
    private const val repository = "avov53/ior"
    private val versionPattern = Regex("v?([0-9]+)\\.([0-9]+)\\.([0-9]+)")
    private val client = OkHttpClient.Builder().callTimeout(12, TimeUnit.SECONDS).build()

    fun check(): String? {
        val request = Request.Builder()
            .url("https://api.github.com/repos/$repository/releases/latest")
            .header("Accept", "application/vnd.github+json")
            .build()
        return client.newCall(request).execute().use { response ->
            check(response.isSuccessful)
            val bytes = response.body!!.byteStream().readNBytes(256 * 1024 + 1)
            check(bytes.size <= 256 * 1024)
            newerRelease(JSONObject(bytes.toString(Charsets.UTF_8)), BuildConfig.VERSION_NAME)
        }
    }

    fun newerRelease(release: JSONObject, installed: String): String? {
        if (release.optBoolean("draft") || release.optBoolean("prerelease")) return null
        val tag = release.optString("tag_name")
        fun parts(value: String): List<Int>? { return versionPattern.matchEntire(value)?.groupValues?.drop(1)
            ?.map { it.toIntOrNull() ?: return null } }
        val latest = parts(tag) ?: return null
        val current = parts(installed) ?: return null
        val difference = latest.indices.firstOrNull { latest[it] != current[it] } ?: return null
        if (latest[difference] < current[difference]) return null
        val assets = release.optJSONArray("assets") ?: return null
        if ((0 until assets.length()).none { assets.optJSONObject(it)?.optString("name") == "ior-android-arm64.apk" }) return null
        return "https://github.com/$repository/releases/tag/$tag"
    }
}
