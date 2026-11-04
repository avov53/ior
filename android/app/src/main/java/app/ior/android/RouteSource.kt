package app.ior.android
import org.json.JSONObject
object RouteSource {
 fun normalize(route:JSONObject):JSONObject {
  if(route.optString("sourceFormat") !in listOf("happ","clash","ior","unknown")){
   val format=route.optString("format","ior")
   route.put("sourceFormat",if(format in listOf("clash","happ"))format else if(route.optString("managedBy").isNotBlank())"unknown"else "ior")
  }
  return route
 }
}
