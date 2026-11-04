package app.ior.android

import org.json.JSONArray
import org.json.JSONObject
import org.yaml.snakeyaml.Yaml
import org.yaml.snakeyaml.LoaderOptions
import org.yaml.snakeyaml.constructor.SafeConstructor

object RouteCodec {
    private val kinds=setOf("DOMAIN","DOMAIN-SUFFIX","DOMAIN-KEYWORD","IP-CIDR","IP-SET","REGEXP","MATCH")
    var geodata:((String,String)->JSONArray)?=null
    fun rules(route:JSONObject?):JSONArray {
        val result=JSONArray()
        if(route!=null) {
            val raw=route.optString("raw").trim()
            if(raw.isNotEmpty()) {
                when(route.optString("format","ior")) {
                    "clash"-> {
                        val yaml=Yaml(SafeConstructor(LoaderOptions().apply { codePointLimit=1024*1024;maxAliasesForCollections=20 }))
                        val data=yaml.load<Any>(raw)
                        val lines=if(data is Map<*,*>) data["rules"] as? List<*> else data as? List<*>
                        require(lines!=null){"В Clash не найден rules"}
                        lines.forEach { addLine(result,it.toString()) }
                    }
                    "sing-box"->error("Импорт sing-box-маршрутов пока не реализован; используйте правила ior или Clash")
                    else->if(raw.startsWith('[')) {
                        val array=JSONArray(raw)
                        for(i in 0 until array.length()){val item=array.get(i);if(item is String)addLine(result,item)else{val rule=item as JSONObject;add(result,rule.getString("kind"),rule.optString("value"),rule.getString("target"),rule.optBoolean("noResolve",false))}}
                    } else raw.lineSequence().filter { it.trim().isNotEmpty() && !it.trim().startsWith('#') }.forEach { addLine(result,it) }
                }
            } else {
                for((field,target) in listOf("block" to "REJECT","direct" to "DIRECT","proxy" to "PROXY")) route.optString(field).lineSequence().filter{it.isNotBlank()}.forEach {
                    val text=it.trim();val kind=when{ text.contains('/')&&text.contains(':')->"IP-CIDR6";text.contains('/')->"IP-CIDR";else->"DOMAIN-SUFFIX" }
                    add(result,kind,text.removePrefix("*."),target)
                }
            }
        }
        val hasMatch=(0 until result.length()).any { result.getJSONObject(it).getString("kind")=="MATCH" }
        if(!hasMatch)add(result,"MATCH","",route?.optString("defaultAction","proxy")?.uppercase()?.let{if(it=="BLOCK")"REJECT" else it} ?: "PROXY")
        return result
    }
    private fun addLine(result:JSONArray,text:String){
        val parts=text.split(',').map{it.trim()}
        if(parts[0].uppercase()=="MATCH"){require(parts.size==2);add(result,"MATCH","",parts[1])}
        else{require(parts.size in 3..4);require(parts.size==3||parts[3].equals("no-resolve",true)){"Неподдерживаемый модификатор правила"};add(result,parts[0],parts[1],parts[2],parts.size==4)}
    }
    private fun add(result:JSONArray,kind:String,value:String,target:String,noResolve:Boolean=false){
        var k=kind.uppercase();val t=target.uppercase()
        if(k=="IP-CIDR6")k="IP-CIDR"
        if(k in setOf("GEOSITE","GEOIP")){
            require(value.matches(Regex("[a-zA-Z0-9_-]{1,80}"))){"Геоправило с инверсией или атрибутами не поддерживается"}
            val expanded=geodata?.invoke(k.lowercase(),value.lowercase()) ?: error("Геобаза недоступна")
            for(i in 0 until expanded.length()){val item=expanded.getJSONObject(i);add(result,item.getString("kind"),item.getString("value"),t,noResolve)};return
        }
        require(k in kinds && t in setOf("DIRECT","PROXY","REJECT")){"Правило или действие не поддерживается ядром Android"}
        result.put(JSONObject().put("kind",k).put("value",value).put("target",t).put("noResolve",noResolve).put("source","android"))
    }
}
