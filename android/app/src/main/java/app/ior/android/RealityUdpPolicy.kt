package app.ior.android
import org.json.JSONObject
internal object RealityUdpPolicy {
    fun effective(profile:JSONObject,enabled:Boolean):JSONObject=JSONObject(profile.toString()).apply {
        if(enabled && optString("protocol")=="vless_reality" && optString("flow")=="xtls-rprx-vision")put("flow","xtls-rprx-vision-udp443")
    }
}
