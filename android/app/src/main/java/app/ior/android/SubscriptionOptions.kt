package app.ior.android
import org.json.JSONObject

object SubscriptionOptions {
    val keys=listOf("autoRefreshEnabled","refreshIntervalHours","refreshOnStartup","pingOnStartup","connectOnStartup","startupSelection","requestTimeoutMs")
    fun migrate(item:JSONObject,legacyEnabled:Boolean=true){
        val defaults=JSONObject().put("autoRefreshEnabled",legacyEnabled).put("refreshIntervalHours",6).put("refreshOnStartup",false).put("pingOnStartup",false).put("connectOnStartup",false).put("startupSelection","last").put("requestTimeoutMs",10000)
        keys.forEach{if(!item.has(it))item.put(it,defaults.get(it))}
    }
    fun save(item:JSONObject,input:JSONObject){
        val required=item.optInt("serverRefreshIntervalHours",0)
        for(key in listOf("autoRefreshEnabled","refreshOnStartup","pingOnStartup","connectOnStartup"))if(input.has(key))require(input.get(key) is Boolean)
        if(input.has("refreshIntervalHours"))require(input.getInt("refreshIntervalHours") in 1..168)
        if(input.has("requestTimeoutMs"))require(input.getInt("requestTimeoutMs") in 5000..15000)
        if(input.has("startupSelection"))require(input.getString("startupSelection") in listOf("last","lowest","random"))
        if(required>0){require(!input.has("autoRefreshEnabled")||input.getBoolean("autoRefreshEnabled"));require(!input.has("refreshIntervalHours")||input.getInt("refreshIntervalHours")==required)}
        keys.forEach{if(input.has(it)&&!(required>0&&it=="refreshIntervalHours"))item.put(it,input.get(it))}
    }
    fun interval(item:JSONObject)=item.optInt("serverRefreshIntervalHours",0).takeIf{it in 1..168}?:item.optInt("refreshIntervalHours",6)
    fun enabled(item:JSONObject)=item.optInt("serverRefreshIntervalHours",0)>0||item.optBoolean("autoRefreshEnabled",true)
}
