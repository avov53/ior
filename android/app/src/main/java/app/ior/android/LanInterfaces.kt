package app.ior.android
import org.json.JSONArray
import org.json.JSONObject
import java.net.NetworkInterface
import java.net.Inet4Address

object LanInterfaces {
    fun list():JSONArray {
        val result=JSONArray()
        try{NetworkInterface.getNetworkInterfaces().toList().filter{it.isUp&&!it.isLoopback&&!it.name.startsWith("tun")&&!it.name.startsWith("rmnet")&&!it.name.startsWith("v4-rmnet")}.forEach{network->network.interfaceAddresses.forEach{address->val ip=address.address;if(ip is Inet4Address&&(ip.isSiteLocalAddress||ip.isLinkLocalAddress))result.put(JSONObject().put("id",network.name).put("name",network.displayName).put("address",ip.hostAddress).put("prefix",address.networkPrefixLength.toInt()))}}}catch(_:Exception){}
        return result
    }
    fun config(settings:JSONObject):JSONObject {
        val enabled=settings.optBoolean("enabled");val a=list();val choices=(0 until a.length()).map{a.getJSONObject(it)}
        val item=if(settings.optString("interfaceId").isBlank())choices.firstOrNull()else choices.firstOrNull{it.getString("id")==settings.optString("interfaceId")}
        require(!enabled||item!=null){"LAN_NETWORK_UNAVAILABLE"}
        return JSONObject().put("enabled",enabled).put("address",item?.getString("address")?:"192.168.0.1").put("prefix",item?.getInt("prefix")?:24).put("port",settings.optInt("port",2080)).put("auth",if(settings.optBoolean("authEnabled",true))JSONObject().put("username",settings.optString("username","ior")).put("password",settings.getString("password"))else JSONObject.NULL)
    }
}
