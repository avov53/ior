package app.ior.android

import org.json.JSONObject
import java.net.InetAddress

internal object HysteriaBootstrap {
    fun retryRequest(request:JSONObject, failure:JSONObject, resolve:(String)->InetAddress):JSONObject? {
        if(failure.optBoolean("ok") || failure.optString("code")!="BOOTSTRAP_DNS_TIMEOUT") return null
        return resolvedRequest(request,resolve)
    }

    fun resolvedRequest(request:JSONObject, resolve:(String)->InetAddress?):JSONObject? {
        val profile=request.getJSONObject("profile")
        if(profile.optString("protocol") !in setOf("hysteria2","vless_reality","amneziawg")) return null
        val host=profile.getString("server")
        if(host.isBlank() || host.contains(':') || host.all{it.isDigit() || it=='.'}) return null
        val address=resolve(host)?.hostAddress ?: return null
        return JSONObject(request.toString()).also{it.getJSONObject("profile").put("server",address)}
    }
}
