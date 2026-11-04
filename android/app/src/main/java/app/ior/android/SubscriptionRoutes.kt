package app.ior.android
import org.json.JSONArray
import org.json.JSONObject
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.Base64
object SubscriptionRoutes {
 fun decode(value:String,http:OkHttpClient):JSONObject {
  var raw=value.trim().trim('"','\'')
  require(raw.toByteArray().size<=1024*1024)
  val links=raw.lineSequence().map{it.trim()}.filter{it.isNotEmpty()}.toList()
  if(!raw.startsWith('{')&&!raw.startsWith('[')&&links.size>1){
   val merged=JSONArray();var global=false;val sources=mutableSetOf<String>()
   for(link in links){val decoded=decode(link,http);sources.add(decoded.optString("sourceFormat","unknown"));val part=RouteCodec.rules(decoded);for(i in 0 until part.length()){val rule=part.getJSONObject(i);if(rule.getString("kind")=="MATCH")global=global||rule.getString("target")=="PROXY"else merged.put(rule)}}
   merged.put(JSONObject().put("kind","MATCH").put("value","").put("target",if(global)"PROXY" else "DIRECT"))
   return JSONObject().put("name","Маршрут подписки").put("format","ior").put("sourceFormat",sources.singleOrNull()?:"unknown").put("raw",merged.toString())
  }
  if(raw.startsWith("https://")){
   var address=raw;var found=false
   repeat(6){if(!found){require(SubscriptionClient.origin(address).startsWith("https://"));http.newCall(Request.Builder().url(address).build()).execute().use{r->if(r.code in 300..399){address=r.request.url.resolve(r.header("Location") ?: error("Неверный редирект маршрута"))?.toString() ?: error("Неверный маршрут")}else{require(r.isSuccessful){"Ошибка загрузки маршрута"};raw=r.body!!.byteStream().use{String(it.readNBytes(1024*1024+1),Charsets.UTF_8)};require(raw.length<=1024*1024);found=true}}}}
   require(found){"Слишком много редиректов маршрута"}
  }
  val happLink=raw.startsWith("happ://routing/",true)
  if(happLink){
   val marker=if(raw.contains("/onadd/",true))"/onadd/"else if(raw.contains("/add/",true))"/add/"else "happ://routing/"
   val payload=raw.substring(raw.indexOf(marker,ignoreCase=true)+marker.length)
   // URLDecoder is form-oriented; protect literal '+' used in ordinary Base64.
   raw=java.net.URLDecoder.decode(payload.replace("+","%2B"),"UTF-8")
  }
  if(!raw.startsWith('{')&&!raw.startsWith('['))raw=String(Base64.getDecoder().decode(raw.replace('-','+').replace('_','/')),Charsets.UTF_8)
  val route=JSONObject().put("name","Маршрут подписки").put("format","ior")
  if(raw.startsWith('['))return route.put("sourceFormat",if(happLink)"happ"else if(JSONArray(raw).opt(0) is String)"clash"else "ior").put("raw",raw)
  val data=JSONObject(raw);val rules=JSONArray()
  route.put("sourceFormat",if(happLink||data.keys().asSequence().any{it.equals("GlobalProxy",true)||it.equals("RouteOrder",true)||it.matches(Regex("(?i)(Block|Direct|Proxy)(Sites|Domains|Keywords|Ip)"))})"happ"else if(data.optJSONArray("rules")?.opt(0) is String)"clash"else "ior")
  require(data.keys().asSequence().any{it.equals("rules",true)||it.equals("GlobalProxy",true)||it.matches(Regex("(?i)(Block|Direct|Proxy)(Sites|Domains|Keywords|Ip)"))}){"Неподдерживаемый формат маршрута"}
  fun field(name:String)=data.keys().asSequence().firstOrNull{it.equals(name,true)}?.let{data.optJSONArray(it)} ?: JSONArray()
  fun add(kind:String,value:String,target:String){rules.put(JSONObject().put("kind",kind).put("value",value).put("target",target))}
  val orderKey=data.keys().asSequence().firstOrNull{it.equals("RouteOrder",true)}
  val order=if(orderKey==null||data.optString(orderKey).isBlank())listOf("block","proxy","direct")else data.getString(orderKey).split('-').map{it.trim().lowercase()}.filter{it.isNotEmpty()}
  require(order.all{it in listOf("block","proxy","direct")}){"Неподдерживаемый RouteOrder"}
  for(action in order){val prefix=mapOf("block" to "Block","proxy" to "Proxy","direct" to "Direct").getValue(action);val target=mapOf("block" to "REJECT","proxy" to "PROXY","direct" to "DIRECT").getValue(action)
   for((suffix,kind) in listOf("Sites" to "DOMAIN-SUFFIX","Domains" to "DOMAIN","Keywords" to "DOMAIN-KEYWORD","Ip" to "IP-CIDR")){
    val a=field(prefix+suffix);for(i in 0 until a.length()){val text=a.getString(i);val parts=text.split(':',limit=2);val known=mapOf("geosite" to "GEOSITE","geoip" to "GEOIP","domain" to "DOMAIN-SUFFIX","full" to "DOMAIN","regexp" to "REGEXP","keyword" to "DOMAIN-KEYWORD");val marked=known[parts[0].lowercase()];add(marked ?: kind,if(marked!=null)parts[1] else text,target)}
   }
  }
  val rulesKey=data.keys().asSequence().firstOrNull{it.equals("rules",true)}
  if(rulesKey!=null)return route.put("raw",data.getJSONArray(rulesKey).toString())
  val globalKey=data.keys().asSequence().firstOrNull{it.equals("GlobalProxy",true)}
  val global=globalKey?.let{val v=data.opt(it);v==true||v is Number&&v.toInt()!=0||v is String&&v.equals("true",true)} ?: false
  add("MATCH","",if(global)"PROXY" else "DIRECT")
  return route.put("raw",rules.toString())
 }
}
