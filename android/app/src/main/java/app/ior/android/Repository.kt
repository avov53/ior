package app.ior.android

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

class Repository private constructor(context:Context):SubscriptionStore {
    private val storage=SecureStore(context)
    init { RouteCodec.geodata={type,name->context.assets.open("geodata/$type-$name.json").use{JSONArray(it.bufferedReader().readText())}} }
    private val savedState=storage.read()
    private var state=savedState ?: JSONObject().put("schema",1).put("theme","dark").put("reduced",false).put("backgroundMotion",true)
        .put("servers",JSONArray()).put("subscriptions",JSONArray()).put("routes",JSONArray()).put("profiles",JSONArray())
        .put("selected","").put("selectedRoute","").put("closed",JSONArray()).put("logs",JSONArray())
        .put("hwidEnabled",false).put("installationId",UUID.randomUUID().toString())
        .put("appRouting",JSONObject().put("enabled",false).put("mode","vpn").put("ids",JSONArray()))
    init{val subscriptions=state.optJSONArray("subscriptions")?:JSONArray();for(i in 0 until subscriptions.length()){HwidPolicy.migrate(subscriptions.getJSONObject(i),state.optBoolean("hwidEnabled"));SubscriptionOptions.migrate(subscriptions.getJSONObject(i),state.optBoolean("autoUpdateSubscriptions"));};val routes=state.optJSONArray("routes")?:JSONArray();for(i in 0 until routes.length())RouteSource.normalize(routes.getJSONObject(i))}
    init{if(!state.has("lan"))state.put("lan",JSONObject().put("enabled",false).put("authEnabled",true).put("port",2080).put("username","ior").put("password",java.util.UUID.randomUUID().toString().replace("-","")))}
    @Volatile var lanStatus="Отключён"
    init{if(!state.has("realityUdp443Enabled"))state.put("realityUdp443Enabled",false);if(!state.has("saveMobileTraffic"))state.put("saveMobileTraffic",false);if(!state.has("journalMaxMb"))state.put("journalMaxMb",20);if(!state.has("journalDays"))state.put("journalDays",7)}
    private val journal=Diagnostics.get(context).also{it.configure(state.optInt("journalMaxMb",20),state.optInt("journalDays",7))}
    init{val retained=journal.consoleTail();if(retained.length()>0)state.put("logs",retained)else{val legacy=state.optJSONArray("logs")?:JSONArray();for(i in 0 until legacy.length())journal.console(legacy.getJSONObject(i))}}
    init{RepositoryDefaults.apply(state,savedState==null)}
    private val observers=CopyOnWriteArrayList<()->Unit>()
    private val eventObservers=CopyOnWriteArrayList<(String,JSONObject)->Unit>()
    private val main=Handler(Looper.getMainLooper())
    init{for(item in subscriptions()){item.put("routeConsent","accepted");item.optJSONObject("pendingRoute")?.let{try{synchronizeRoute(item,it,true)}catch(_:Exception){item.put("routingWarning","Не удалось восстановить маршрут подписки. Обновите подписку.")}};updateSubscription(item,null,false)}}
    @Volatile var connection="off"
    @Volatile var error=""
    @Volatile var activeId=""
    @Volatile var stats=JSONObject()
    companion object {
        @Volatile private var instance:Repository?=null
        fun get(context:Context):Repository=instance ?: synchronized(this){instance ?: Repository(context.applicationContext).also{instance=it}}
    }
    fun observe(callback:()->Unit){observers.add(callback)}
    fun unobserve(callback:()->Unit){observers.remove(callback)}
    fun observeEvents(callback:(String,JSONObject)->Unit){eventObservers.add(callback)}
    fun unobserveEvents(callback:(String,JSONObject)->Unit){eventObservers.remove(callback)}
    @Synchronized fun debugLogging():Boolean=state.optString("logLevel","INFO")=="DEBUG"
    fun event(kind:String,data:JSONObject){main.post{eventObservers.forEach{it(kind,data)}}}
    private fun changed(){main.post{observers.forEach{it()}}}
    private fun blocked(s:JSONObject)=s.optString("protocol").lowercase() in listOf("vmess","shadowsocks","trojan")||s.optString("raw").substringBefore("://").lowercase() in listOf("vmess","ss","trojan")
    @Synchronized override fun snapshot():JSONObject=JSONObject(state.toString()).apply{val all=getJSONArray("servers");put("servers",JSONArray((0 until all.length()).map{all.getJSONObject(it)}.filterNot{blocked(it)}));put("unsupportedStoredCount",(0 until all.length()).count{blocked(all.getJSONObject(it))})}.put("systemVpn",IorVpnService.systemProtection()).put("connection",connection).put("connectionError",error).put("activeId",activeId).put("stats",stats).put("lanInterfaces",LanInterfaces.list()).put("lanStatus",lanStatus).put("systemLanguage",if(java.util.Locale.getDefault().language=="en")"en" else "ru")
    @Synchronized fun server(id:String):JSONObject?=(0 until state.getJSONArray("servers").length()).map{state.getJSONArray("servers").getJSONObject(it)}.find{it.optString("id")==id}?.let{JSONObject(it.toString())}
    @Synchronized fun selectedServer():JSONObject?=server(state.optString("selected"))
    @Synchronized override fun subscriptions():List<JSONObject>{val a=state.getJSONArray("subscriptions");return (0 until a.length()).map{JSONObject(a.getJSONObject(it).toString())}}
    @Synchronized fun saveSettings(input:JSONObject){
        if(input.has("journalMaxMb"))require(input.getInt("journalMaxMb") in listOf(5,20,50))
        if(input.has("journalDays"))require(input.getInt("journalDays") in listOf(1,7,30))
        for(key in listOf("journalMaxMb","journalDays","saveMobileTraffic","realityUdp443Enabled"))if(input.has(key))state.put(key,input.get(key))
        if(input.has("pingMode"))require(input.getString("pingMode") in listOf("auto","icmp","tcp","head","get"))
        if(input.has("proxyPingTimeoutMs"))require(input.getInt("proxyPingTimeoutMs") in 5000..15000)
        if(input.has("language"))require(input.getString("language") in listOf("system","ru","en"))
        if(input.has("logLevel"))require(input.getString("logLevel") in listOf("INFO","DEBUG"))
        if(input.has("pingUrl")){val uri=java.net.URI(input.getString("pingUrl"));require(uri.scheme=="https"&&uri.host!=null&&uri.userInfo==null)}
        for(key in listOf("theme","reduced","backgroundMotion","closed","drafts","appRouting","selectedRoute","hwidEnabled","autoUpdateSubscriptions","pingMode","pingUrl","pingTimeoutMs","proxyPingTimeoutMs","language","logLevel")) if(input.has(key))state.put(key,input.get(key))
        persist()
    }
    @Synchronized fun select(id:String){require(server(id)!=null&&!blocked(server(id)!!)){"Выберите поддерживаемый сервер"};state.put("selected",id);persist()}
    @Synchronized fun setVpnWanted(value:Boolean){state.put("vpnWanted",value);persist()}
    @Synchronized fun latency(id:String,pending:Boolean,ms:Long?,method:String="",reason:String="",networkGeneration:Long=0,stale:Boolean=false){
        val a=state.getJSONArray("servers");for(i in 0 until a.length()){val s=a.getJSONObject(i);if(s.optString("id")==id){
            s.put("measuring",pending).put("pingMs",ms?:JSONObject.NULL).put("pingMethod",method).put("pingReason",reason).put("pingNetworkGeneration",networkGeneration).put("pingStale",stale)
            if(!pending&&!stale)storage.write(state)
            event("latency",JSONObject().put("id",id).put("measuring",pending).put("pingMs",ms?:JSONObject.NULL).put("pingMethod",method).put("pingReason",reason).put("pingNetworkGeneration",networkGeneration).put("pingStale",stale));break
        }}
    }
    @Synchronized fun invalidateLatencies(generation:Long){val a=state.getJSONArray("servers");for(i in 0 until a.length()){val s=a.getJSONObject(i);s.put("measuring",false).put("pingStale",true).put("pingReason","PING_NETWORK_CHANGED").put("pingNetworkGeneration",generation);event("latency",JSONObject(s.toString()))}}
    @Synchronized fun addServer(raw:String,name:String,id:String?=null,subscription:String=""):JSONObject{
        val server=ProfileCodec.parse(raw,name)
        if(id!=null)server.put("id",id)
        server.put("subscriptionId",subscription)
        val validation=JSONObject(NativeCore.validate(validationRequest(server).toString()))
        require(validation.optBoolean("ok")){validation.optString("code","Не удалось проверить конфигурацию")}
        val items=state.getJSONArray("servers")
        val existing=(0 until items.length()).indexOfFirst{items.getJSONObject(it).optString("raw")==raw.trim() && items.getJSONObject(it).optString("subscriptionId")==subscription}
        if(id==null && existing>=0)return items.getJSONObject(existing)
        val index=(0 until items.length()).indexOfFirst{items.getJSONObject(it).optString("id")==server.getString("id")}
        if(index>=0)items.put(index,server)else items.put(server)
        if(state.optString("selected").isBlank())state.put("selected",server.getString("id"))
        persist();return server
    }
    @Synchronized fun saveRoute(route:JSONObject){require(route.optString("id")!="none"){"Системный маршрут none нельзя изменить"};RouteSource.normalize(route);val rules=RouteCodec.rules(route);require(JSONObject(NativeCore.validateRoutes(rules.toString())).optBoolean("ok")){"Маршрут отклонён ядром"};if(route.optString("id").isBlank())route.put("id",UUID.randomUUID().toString());val a=state.getJSONArray("routes");val index=(0 until a.length()).indexOfFirst{a.getJSONObject(it).optString("id")==route.getString("id")};if(index>=0)a.put(index,route)else a.put(route);persist()}
    @Synchronized fun delete(kind:String,id:String){
        require(kind!="route"||id!="none"){"Системный маршрут none нельзя удалить"}
        val key=when(kind){"server"->"servers";"subscription"->"subscriptions";"route"->"routes";else->error("Неверный объект")}
        fun filtered(array:JSONArray,predicate:(JSONObject)->Boolean)=JSONArray((0 until array.length()).map{array.getJSONObject(it)}.filter(predicate))
        state.put(key,filtered(state.getJSONArray(key)){it.optString("id")!=id})
        if(kind=="route"&&state.optString("selectedRoute")==id)state.put("selectedRoute","")
        if(kind=="subscription")state.put("servers",filtered(state.getJSONArray("servers")){it.optString("subscriptionId")!=id})
        if(server(state.optString("selected"))==null)state.put("selected","")
        persist()
    }
    @Synchronized fun addSubscription(url:String,name:String):JSONObject{
        require(java.net.URI(url).scheme=="https"){"Подписка должна использовать HTTPS"}
        subscriptions().find{it.optString("url")==url}?.let{return it}
        val item=JSONObject().put("id",UUID.randomUUID().toString()).put("name",name.ifBlank{java.net.URI(url).host}).put("url",url).put("routeConsent","ask").put("status","Ожидает загрузки").put("hwidOrigins",JSONArray()).put("hwidMode","required")
        SubscriptionOptions.migrate(item);state.getJSONArray("subscriptions").put(item);persist();return item
    }
    @Synchronized override fun updateSubscription(item:JSONObject,servers:List<JSONObject>?,success:Boolean){
        val id=item.getString("id");val subscriptions=state.getJSONArray("subscriptions");val index=(0 until subscriptions.length()).indexOfFirst{subscriptions.getJSONObject(it).getString("id")==id}
        if(index<0)return
        val currentItem=subscriptions.getJSONObject(index);if(currentItem.has("routeConsent"))item.put("routeConsent",currentItem.get("routeConsent"));SubscriptionOptions.keys.forEach{if(currentItem.has(it))item.put(it,currentItem.get(it))}
        if(currentItem.has("lastUsedServerId"))item.put("lastUsedServerId",currentItem.get("lastUsedServerId"))
        val currentMode=subscriptions.getJSONObject(index).optString("hwidMode")
        if(currentMode in HwidPolicy.modes)item.put("hwidMode",currentMode)
        if(success && servers!=null){
            val old=state.getJSONArray("servers");val replacements=servers.map { candidate->
                val previous=(0 until old.length()).map{old.getJSONObject(it)}.find{it.optString("subscriptionId")==id && it.optString("raw")==candidate.optString("raw")}
                candidate.put("id",previous?.optString("id") ?: candidate.getString("id")).put("subscriptionId",id)
            }
            state.put("servers",JSONArray((0 until old.length()).map{old.getJSONObject(it)}.filter{it.optString("subscriptionId")!=id}+replacements))
            item.put("updatedAt",System.currentTimeMillis())
            if(server(state.optString("selected"))==null)state.put("selected",replacements.firstOrNull()?.getString("id") ?: "")
        }
        subscriptions.put(index,item);persist()
    }
    @Synchronized fun saveLan(input:JSONObject){require(input.optInt("port",2080) in 1..65535);require(input.optString("username","ior").isNotBlank()&&!input.optString("username","ior").contains(':'));require(input.optString("password").length in 8..128);LanInterfaces.config(input);state.put("lan",JSONObject(input.toString()));persist()}
    @Synchronized fun saveSubscriptionSettings(id:String,input:JSONObject){val a=state.getJSONArray("subscriptions");val item=(0 until a.length()).map{a.getJSONObject(it)}.first{it.getString("id")==id};SubscriptionOptions.save(item,input);if(item.optBoolean("connectOnStartup"))for(i in 0 until a.length())if(a.getJSONObject(i)!==item)a.getJSONObject(i).put("connectOnStartup",false);persist()}
    @Synchronized fun selectRoute(id:String){val a=state.getJSONArray("routes");val route=(0 until a.length()).map{a.getJSONObject(it)}.first{it.getString("id")==id};require(!route.has("error")){"Маршрут содержит неподдержанные правила"};require(JSONObject(NativeCore.validateRoutes(RouteCodec.rules(route).toString())).optBoolean("ok")){"Маршрут несовместим с ядром"};state.put("selectedRoute",id);persist()}
    @Synchronized fun routeForServer(){val server=selectedServer();val sub=subscriptions().firstOrNull{it.optString("id")==server?.optString("subscriptionId")};state.put("selectedRoute",sub?.optString("routingId").orEmpty());persist()}
    @Synchronized fun routingEnabled(value:Boolean){state.put("routingEnabled",value);persist()}
    @Synchronized fun restoreRouting(value:JSONObject){state.put("routingEnabled",value.optBoolean("routingEnabled",true));state.put("selectedRoute",value.optString("selectedRoute"));persist()}
    @Synchronized fun recordSuccessfulServer(id:String){val subId=server(id)?.optString("subscriptionId")?:return;val a=state.getJSONArray("subscriptions");for(i in 0 until a.length())if(a.getJSONObject(i).optString("id")==subId)a.getJSONObject(i).put("lastUsedServerId",id);persist()}
    @Synchronized fun setHwidMode(id:String,mode:String){require(mode in HwidPolicy.modes);val items=state.getJSONArray("subscriptions");val index=(0 until items.length()).first{items.getJSONObject(it).getString("id")==id};items.getJSONObject(index).put("hwidMode",mode);persist()}
    @Synchronized fun authorizeHwid(id:String,origin:String){val item=subscriptions().first{it.getString("id")==id};val origins=item.optJSONArray("hwidOrigins") ?: JSONArray();if((0 until origins.length()).none{origins.getString(it)==origin})origins.put(origin);item.put("hwidOrigins",origins).put("hwidPolicy","allow");updateSubscription(item,null,false)}
    private fun setRouteChoice(item:JSONObject,value:String){val a=state.getJSONArray("subscriptions");for(i in 0 until a.length())if(a.getJSONObject(i).optString("id")==item.optString("id"))a.getJSONObject(i).put("routeConsent",value);item.put("routeConsent",value)}
    @Synchronized fun reviewRouteConsent(id:String){val item=subscriptions().first{it.getString("id")==id};setRouteChoice(item,"ask");updateSubscription(item,null,false)}
    @Synchronized fun routeConsent(id:String,accept:Boolean){val item=subscriptions().first{it.getString("id")==id};setRouteChoice(item,if(accept)"accepted"else"declined");if(accept){item.optJSONObject("pendingRoute")?.let{synchronizeRoute(item,it,true);state.put("selectedRoute",item.optString("routingId","none"))}}else if(state.optString("selectedRoute")==item.optString("routingId"))state.put("selectedRoute","");updateSubscription(item,null,false)}
    @Synchronized override fun synchronizeRoute(item:JSONObject,route:JSONObject?,enabled:Boolean){
        val current=subscriptions().firstOrNull{it.optString("id")==item.optString("id")};if(current?.has("routeConsent")==true)item.put("routeConsent",current.get("routeConsent"));if(current?.has("routingId")==true)item.put("routingId",current.get("routingId"))
        val id=item.optString("routingId");val routes=state.getJSONArray("routes")
        val index=(0 until routes.length()).indexOfFirst{routes.getJSONObject(it).optString("id")==id}
        if(!enabled){item.remove("pendingRoute");if(index>=0&&routes.getJSONObject(index).optString("managedBy")==item.getString("id"))routes.remove(index);if(state.optString("selectedRoute")==id)state.put("selectedRoute","");item.remove("routingId");persist();return}
        require(route!=null)
        item.put("pendingRoute",JSONObject(route.toString()))
        item.put("routeConsent","accepted")
        RouteSource.normalize(route.put("id",id.ifBlank{UUID.randomUUID().toString()}).put("managedBy",item.getString("id")))
        try{require(JSONObject(NativeCore.validateRoutes(RouteCodec.rules(route).toString())).optBoolean("ok"))}catch(_:Exception){
            if(index>=0){item.put("routingWarning","Не удалось обновить маршрут; прежний сохранён");return}
            route.put("error","Маршрут содержит неподдержанные правила")
        }
        if(index>=0)routes.put(index,route)else routes.put(route)
        item.put("routingId",route.getString("id"));item.remove("routingWarning")

        persist()
    }
    @Synchronized override fun request(server:JSONObject):JSONObject=buildRequest(server,true)
    @Synchronized override fun validationRequest(server:JSONObject):JSONObject=buildRequest(server,false)
    private fun buildRequest(server:JSONObject,includeRoute:Boolean):JSONObject {
        require(!blocked(server)){"Протокол пока не поддерживается; выберите другой сервер"}
        val routeArray=state.getJSONArray("routes");val route=if(includeRoute&&state.optBoolean("routingEnabled",true))(0 until routeArray.length()).map{routeArray.getJSONObject(it)}.find{it.optString("id")==state.optString("selectedRoute")}else null
        require(route?.has("error")!=true){"Выбранный маршрут несовместим с ядром"}
        val dns=server.optJSONArray("dns") ?: JSONArray()
        val resolvers=if(dns.length()>0)dns else JSONArray(listOf("8.8.8.8","1.1.1.1"))
        return JSONObject().put("apiVersion",3).put("requestId",UUID.randomUUID().toString()).put("operation","start").put("profile",RealityUdpPolicy.effective(server.getJSONObject("wire"),state.optBoolean("realityUdp443Enabled",false)))
            .put("routes",RouteCodec.rules(route)).put("dns",JSONObject().put("bootstrap","1.1.1.1").put("upstream",resolvers.getString(0)).put("upstreams",resolvers).put("ipv4Only",false))
    }
    fun status(value:String,message:String="",id:String=activeId){connection=value;error=message;activeId=id;if(value=="on")recordSuccessfulServer(id);changed()}
    fun progress(value:JSONObject){stats=value;event("stats",value)}
    @Synchronized fun log(code:String){val logs=state.optJSONArray("logs") ?: JSONArray();val entry=JSONObject().put("time",java.text.SimpleDateFormat("HH:mm:ss",java.util.Locale.ROOT).format(java.util.Date())).put("text",code.take(512));journal.console(entry);logs.put(entry);while(logs.length()>2000)logs.remove(0);state.put("logs",logs);event("log",entry)}
    @Synchronized fun clearLogs(){state.put("logs",JSONArray());persist()}
    @Synchronized fun replace(imported:JSONObject){
        require(imported.optInt("schema")==1)
        require(connection=="off"){"Отключите VPN перед восстановлением"}
        for(i in 0 until imported.getJSONArray("servers").length()){val server=imported.getJSONArray("servers").getJSONObject(i);if(!blocked(server))server.put("wire",ProfileCodec.parse(server.getString("raw"),server.getString("name")).getJSONObject("wire"))}
        val routes=imported.getJSONArray("routes");for(i in 0 until routes.length())RouteCodec.rules(routes.getJSONObject(i))
        imported.put("installationId",state.getString("installationId"));imported.put("hwidEnabled",false).put("autoUpdateSubscriptions",false)
        imported.put("lan",JSONObject(state.getJSONObject("lan").toString()).put("enabled",false))
        val subscriptions=imported.getJSONArray("subscriptions");for(i in 0 until subscriptions.length())subscriptions.getJSONObject(i).put("hwidOrigins",JSONArray()).put("hwidPolicy","inherit").put("hwidMode","never").put("hwidRequiredOrigins",JSONArray())
        val previous=state
        state=JSONObject(imported.toString());RepositoryDefaults.apply(state,false)
        try{persist()}catch(error:Exception){state=previous;throw error}
    }
    private fun persist(){storage.write(state);changed()}
}

