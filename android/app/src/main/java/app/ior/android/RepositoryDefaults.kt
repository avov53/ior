package app.ior.android
import org.json.JSONArray
import org.json.JSONObject
internal object RepositoryDefaults {
    fun apply(state:JSONObject,newInstall:Boolean){
        if(!state.has("saveMobileTraffic"))state.put("saveMobileTraffic",false)
        val routes=state.optJSONArray("routes")?:JSONArray().also{state.put("routes",it)}
        if(!state.has("routingEnabled"))state.put("routingEnabled",newInstall||state.optString("selectedRoute")!="none")
        for(i in routes.length()-1 downTo 0)if(routes.getJSONObject(i).optString("id")=="none")routes.remove(i)
        if(state.optString("selectedRoute")=="none")state.put("selectedRoute","")
    }
}
