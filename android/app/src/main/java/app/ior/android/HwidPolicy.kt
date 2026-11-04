package app.ior.android

import org.json.JSONArray
import org.json.JSONObject

object HwidPolicy {
    val modes=setOf("always","required","never")
    fun migrate(item:JSONObject,globalEnabled:Boolean) {
        if(item.optString("hwidMode") in modes)return
        val mode=when {
            item.optString("hwidPolicy")=="deny"->"never"
            globalEnabled||item.optString("hwidPolicy")=="allow"||(item.optJSONArray("hwidOrigins")?.length()?:0)>0->"always"
            else->"required"
        }
        item.put("hwidMode",mode)
    }
    fun approved(item:JSONObject,origin:String):Boolean {
        val origins=item.optJSONArray("hwidOrigins")?:JSONArray()
        return (0 until origins.length()).any{origins.optString(it)==origin}
    }
    fun required(item:JSONObject,origin:String):Boolean {
        val origins=item.optJSONArray("hwidRequiredOrigins")?:JSONArray()
        return (0 until origins.length()).any{origins.optString(it)==origin}
    }
    fun rememberRequirement(item:JSONObject,origin:String) {
        val origins=item.optJSONArray("hwidRequiredOrigins")?:JSONArray()
        if(!required(item,origin))origins.put(origin)
        item.put("hwidRequiredOrigins",origins)
    }
}
