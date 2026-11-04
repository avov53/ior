package app.ior.android

import android.os.Build
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import org.yaml.snakeyaml.Yaml
import org.yaml.snakeyaml.LoaderOptions
import org.yaml.snakeyaml.constructor.SafeConstructor
import java.net.URI
import java.util.Base64
import java.util.UUID
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

interface SubscriptionStore {
    fun snapshot():JSONObject
    fun subscriptions():List<JSONObject>
    fun request(server:JSONObject):JSONObject
    fun validationRequest(server:JSONObject):JSONObject=request(server)
    fun updateSubscription(item:JSONObject,servers:List<JSONObject>?,success:Boolean)
    fun synchronizeRoute(item:JSONObject,route:JSONObject?,enabled:Boolean) {}
}
class SubscriptionClient(
    private val repo:SubscriptionStore,
    private val http:OkHttpClient=OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).callTimeout(25,TimeUnit.SECONDS).build(),
    private val validate:(JSONObject)->Boolean={JSONObject(NativeCore.validate(repo.validationRequest(it).toString())).optBoolean("ok")},
    private val deviceHeaders:()->Map<String,String> = {mapOf("x-device-os" to "Android","x-ver-os" to Build.VERSION.RELEASE,"x-device-model" to Build.MODEL.take(80),"x-app-version" to BuildConfig.VERSION_NAME)}
) {
    class ConsentRequired(val id:String,val origin:String):Exception("HWID_CONSENT_REQUIRED")
    fun refresh(id:String,automatic:Boolean=false) {
        val pending=java.util.concurrent.CompletableFuture<Unit>();val existing=refreshing.putIfAbsent(id,pending)
        if(existing!=null){try{existing.get()}catch(e:java.util.concurrent.ExecutionException){if(!automatic&&e.cause is MobileTrafficPolicy.Deferred){refreshing.remove(id,existing);refresh(id,false);return};throw e.cause?:e};return}
        try{refreshOnce(id,automatic);pending.complete(Unit)}catch(e:Throwable){pending.completeExceptionally(e);throw e}finally{refreshing.remove(id,pending)}
    }
    private fun refreshOnce(id:String,automatic:Boolean) {
        val item=repo.subscriptions().first{it.getString("id")==id}
        val deadline=System.nanoTime()+item.optInt("requestTimeoutMs",10000).coerceIn(5000,15000)*1000000L
        try {
            MobileTrafficPolicy.check(automatic)
            val requestHttp=http.newBuilder().addInterceptor{chain->MobileTrafficPolicy.check(automatic);chain.proceed(chain.request())}.eventListener(object:okhttp3.EventListener(){
                override fun callStart(call:okhttp3.Call){if(automatic)MobileTrafficPolicy.track(call)}
                override fun callEnd(call:okhttp3.Call){MobileTrafficPolicy.release(call)}
                override fun callFailed(call:okhttp3.Call,ioe:java.io.IOException){MobileTrafficPolicy.release(call)}
            }).build()
            var url=item.getString("url")
            val initial=origin(url)
            val approved=item.optJSONArray("hwidOrigins") ?: JSONArray()
            fun allowed(o:String):Boolean {
                val currentItem=repo.subscriptions().firstOrNull{it.optString("id")==id} ?: error("Подписка удалена")
                require(currentItem.optString("hwidMode")==item.optString("hwidMode")){"HWID_POLICY_CHANGED"}
                val mode=item.optString("hwidMode")
                if(mode in HwidPolicy.modes){
                    if(mode=="never")return false
                    val authorized=o==initial||HwidPolicy.approved(item,o)
                    return authorized&&(mode=="always"||HwidPolicy.required(item,o)||HwidPolicy.approved(item,o))
                }
                return item.optString("hwidPolicy")!="deny" && ((repo.snapshot().optBoolean("hwidEnabled")&&o==initial)||(0 until approved.length()).any{approved.getString(it)==o})
            }
            var attempt=0
            var redirects=0
            while(true) {
                val current=origin(url)
                val request=Request.Builder().url(url).header("User-Agent","ior-android/${BuildConfig.VERSION_NAME}").apply {
                    if(allowed(current)) {
                        header("x-hwid",hwid(repo.snapshot().getString("installationId"),current))
                        deviceHeaders().forEach{(name,value)->header(name,value)}
                    }
                }.build()
                val remaining=(deadline-System.nanoTime())/1000000;require(remaining>0){"SUBSCRIPTION_TIMEOUT"}
                requestHttp.newBuilder().callTimeout(remaining,TimeUnit.MILLISECONDS).build().newCall(request).execute().use { response->
                    if(response.code in 300..399) {
                        require(++redirects<=5){"Слишком много перенаправлений"}
                        url=response.request.url.resolve(response.header("Location") ?: error("Редирект без адреса"))?.toString() ?: error("Неверный редирект")
                        origin(url) // reject downgrade before sending anything
                        return@use
                    }
                    val limitReached=response.header("x-hwid-max-devices-reached")?.let{it.lowercase() !in listOf("","0","false")}==true
                    val limit=response.header("x-hwid-limit")?.let{it.lowercase() in listOf("1","true","yes","on")}==true
                    require(!limitReached&&!limit){"Достигнут лимит устройств подписки"}
                    val needs=response.header("subscription-always-hwid-enable")?.let{it.lowercase() !in listOf("","0","false")}==true || response.header("x-hwid-not-supported")?.let{it.lowercase() !in listOf("","0","false")}==true
                    if(needs && !allowed(current)){
                        if(item.optString("hwidMode")=="never")error("Подписка требует HWID; измените режим в разделе HWID")
                        if(item.optString("hwidMode")=="required"&&(current==initial||HwidPolicy.approved(item,current))){
                            HwidPolicy.rememberRequirement(item,current);repo.updateSubscription(item,null,false)
                            return@use
                        }
                        throw ConsentRequired(id,current)
                    }
                    require(response.isSuccessful){"Ошибка HTTP ${response.code}"}
                    val bytes=response.body?.byteStream()?.use{it.readNBytes(5*1024*1024+1)} ?: error("Пустая подписка")
                    require(bytes.size<=5*1024*1024){"Подписка слишком велика"}
                    val servers=parse(String(bytes,Charsets.UTF_8))
                    if(skipped.isNotEmpty())item.put("importWarning","Пропущены записи: "+skipped.entries.joinToString{it.key+": "+it.value})else item.remove("importWarning")
                    if(servers.isEmpty()&&skipped.isNotEmpty()){item.put("status",item.getString("importWarning"));repo.updateSubscription(item,null,false);return}
                    require(servers.isNotEmpty()){ "Нет поддерживаемых конфигураций в подписке" }
                    for(server in servers)require(validate(server)){"Подписка содержит неверную конфигурацию"}
                    val interval=response.header("Profile-Update-Interval")?.toIntOrNull()?.takeIf{it in 1..168};if(interval==null)item.remove("serverRefreshIntervalHours")else item.put("serverRefreshIntervalHours",interval)
                    val info=response.header("subscription-userinfo")
                    if(info!=null){val metadata=JSONObject();info.split(';').forEach{val parts=it.trim().split('=',limit=2);if(parts.size==2&&parts[0] in listOf("upload","download","total","expire"))parts[1].toLongOrNull()?.let{n->metadata.put(parts[0],n)}};item.put("metadata",metadata)}
                    response.header("Announce")?.takeIf{it.length<=4096}?.let{encoded->item.put("announcement",try{String(Base64.getDecoder().decode(encoded),Charsets.UTF_8).take(2048)}catch(_:Exception){encoded.take(2048)})}
                    val routingFlag=response.header("Routing-Enable")?.lowercase()
                    if(routingFlag in listOf("true","1")){
                        try {val supplied=response.header("Routing") ?: error("Routing header пуст");repo.synchronizeRoute(item,SubscriptionRoutes.decode(supplied,requestHttp),true)}catch(_:Exception){MobileTrafficPolicy.check(automatic);item.put("routingWarning","Не удалось обновить маршрут; прежний сохранён")}
                    }else if(routingFlag in listOf("false","0"))repo.synchronizeRoute(item,null,false)
                    else if(String(bytes,Charsets.UTF_8).contains("proxies:")){
                        val yaml=Yaml(SafeConstructor(LoaderOptions().apply{codePointLimit=5*1024*1024;maxAliasesForCollections=20})).load<Map<String,Any>>(String(bytes,Charsets.UTF_8))
                        if(yaml.containsKey("rules"))repo.synchronizeRoute(item,JSONObject().put("name","Маршрут Clash").put("format","clash").put("sourceFormat","clash").put("raw",String(bytes,Charsets.UTF_8)),true)
                    }
                    MobileTrafficPolicy.check(automatic)
                    item.put("waitingForWifi",false).put("status",if(item.has("routingWarning"))"Обновлена · проверьте маршрут" else "Обновлена");repo.updateSubscription(item,servers,true);return
                }
                require(++attempt<=6){"Не удалось загрузить подписку"}
            }
        } catch(error:Exception){if(automatic&&(error is MobileTrafficPolicy.Deferred||!MobileTrafficPolicy.allowed())){item.put("waitingForWifi",true).put("status","Ожидает Wi-Fi");repo.updateSubscription(item,null,false);throw MobileTrafficPolicy.Deferred()};item.put("status",if(error is ConsentRequired)"Требуется разрешение HWID" else error.message?.takeIf{!it.contains("://")&&it.length<120} ?: "Ошибка загрузки");repo.updateSubscription(item,null,false);throw error}
    }
    private val skipped=linkedMapOf<String,Int>()
    private fun excluded(type:String):Boolean{val name=when(type.lowercase()){ "vmess"->"VMess";"ss","shadowsocks"->"Shadowsocks";"trojan"->"Trojan";else->return false};skipped[name]=(skipped[name]?:0)+1;return true}
    private fun parse(raw:String):List<JSONObject> {
        skipped.clear()
        var text=raw.trim()
        if(!text.contains("://") && !text.startsWith('[') && !text.startsWith('{') && !text.contains("proxies:"))try{text=String(Base64.getDecoder().decode(text.filterNot{it.isWhitespace()}),Charsets.UTF_8)}catch(_:Exception){}
        if(text.startsWith("[Interface]"))return listOf(ProfileCodec.parse(text))
        if(text.contains("proxies:")) {
            val yaml=Yaml(SafeConstructor(LoaderOptions().apply{codePointLimit=5*1024*1024;maxAliasesForCollections=20}))
            val map=yaml.load<Map<String,Any>>(text)
            return (map["proxies"] as? List<*>)?.mapNotNull{entry->try{fromMap(entry as Map<*,*>)}catch(_:Exception){null}} ?: emptyList()
        }
        if(text.startsWith('[')||text.startsWith('{')){
            val json=if(text.startsWith('['))JSONArray(text)else JSONObject(text).optJSONArray("proxies") ?: JSONObject(text).optJSONArray("outbounds") ?: JSONArray()
            fun native(value:Any?):Any?=when(value){is JSONObject->value.keys().asSequence().associateWith{native(value.get(it))};is JSONArray->(0 until value.length()).map{native(value.get(it))};else->value}
            return (0 until json.length()).mapNotNull{try{val item=json.getJSONObject(it);if(item.has("raw")){require(!excluded(item.getString("raw").substringBefore("://"))){"Протокол пока не поддерживается"};ProfileCodec.parse(item.getString("raw"),item.optString("name"))}else fromMap(native(item) as Map<*,*>)}catch(_:Exception){null}}
        }
        return text.lineSequence().map{it.trim()}.filter{it.isNotEmpty()&&!it.startsWith('#')}.mapNotNull{
            val scheme=it.substringBefore("://").lowercase()
            if(excluded(scheme))null else if(scheme in setOf("vless","hy2","hysteria2","awg","amneziawg","vpn","wireguard"))try{ProfileCodec.parse(it)}catch(_:Exception){val label="Некорректные или неподдерживаемые конфигурации";skipped[label]=(skipped[label]?:0)+1;null} else null
        }.toList()
    }
    private fun fromMap(map:Map<*,*>):JSONObject {
        fun value(vararg names:String)=names.firstNotNullOfOrNull{map[it]?.toString()}.orEmpty()
        val type=value("type","protocol").lowercase()
        require(!excluded(type)){"Протокол пока не поддерживается"}
        val stream=map["transport"] as? Map<*,*>
        val network=value("network").ifBlank{stream?.get("type")?.toString().orEmpty().ifBlank{"tcp"}}
        require(network in listOf("tcp","raw","ws","websocket","grpc","httpupgrade","xhttp")){"Транспорт не поддерживается"}
        require(value("obfs").isBlank() && value("skip-cert-verify")!="true"){"Параметры безопасности не поддерживаются"}
        val host=value("server");val authority=if(host.contains(':'))"[$host]" else host;val port=value("port","server_port").ifBlank{"443"}
        val tls=map["tls"] as? Map<*,*>
        require(tls?.get("insecure")!=true){"Отключение проверки TLS не поддерживается"}
        val reality=(map["reality-opts"] ?: tls?.get("reality")) as? Map<*,*>
        val encode:(String)->String={java.net.URLEncoder.encode(it,"UTF-8")}
        val fields=(map[when(network){"ws","websocket"->"ws-opts";"grpc"->"grpc-opts";"xhttp"->"xhttp-opts";else->"httpupgrade-opts"}] as? Map<*,*>) ?: stream ?: emptyMap<Any,Any>()
        require(fields.keys.all{it.toString() in listOf("type","path","host","authority","headers","service_name","grpc-service-name","multiMode","multi-mode","mode","extra")}){"Не поддержаны параметры транспорта"}
        val headers=fields["headers"] as? Map<*,*> ?: emptyMap<Any,Any>()
        val transportHost=(fields["host"] ?: fields["authority"])?.toString() ?: headers.entries.firstOrNull{it.key.toString().equals("host",true)}?.value?.toString().orEmpty()
        val extra=JSONObject().put("headers",JSONObject(headers.entries.filterNot{it.key.toString().equals("host",true)}.associate{it.key.toString() to it.value}))
        (fields["extra"] as? Map<*,*>)?.let{for((key,v) in it)extra.put(key.toString(),JSONObject.wrap(v))}
        val link=when(type){
            "hysteria2","hy2"->"hysteria2://${encode(value("password"))}@$authority:$port?sni=${encode(value("sni").ifBlank{tls?.get("server_name")?.toString().orEmpty().ifBlank{host}})}"
            "vless"->"vless://${value("uuid")}@$authority:$port?security=${if(reality!=null)"reality"else"tls"}&type=$network&sni=${encode(value("servername","sni").ifBlank{tls?.get("server_name")?.toString().orEmpty().ifBlank{host}})}&pbk=${encode(reality?.get("public-key")?.toString() ?: reality?.get("public_key")?.toString().orEmpty())}&sid=${encode(reality?.get("short-id")?.toString() ?: reality?.get("short_id")?.toString().orEmpty())}&flow=${encode(value("flow"))}&path=${encode(fields["path"]?.toString().orEmpty())}&host=${encode(transportHost)}&serviceName=${encode((fields["grpc-service-name"] ?: fields["service_name"])?.toString().orEmpty())}&mode=${encode(fields["mode"]?.toString().orEmpty())}&multiMode=${fields["multiMode"]==true}&extra=${encode(extra.toString())}"
            "amneziawg","wireguard"->{
                if(value("config").isNotBlank())return ProfileCodec.parse(value("config"),value("name","tag"))
                val awg=(map["awg"] as? Map<*,*>) ?: map.filterKeys{it.toString().lowercase() in setOf("jc","jmin","jmax","s1","s2","s3","s4","h1","h2","h3","h4","i1","i2","i3","i4","i5","header_protection_key","content_padding_addition","rekey_after_time","reject_after_time","rekey_timeout","keepalive_timeout","max_handshake_attempts","random_trailers","disable_cookies")}
                require(awg.isNotEmpty()){ "Это обычный WireGuard" }
                val addresses=map["local_address"] as? List<*> ?: listOf(value("ip").let{if(it.contains('/'))it else "$it/32"})
                val allowed=map["allowed-ips"] as? List<*> ?: map["allowed_ips"] as? List<*> ?: listOf("0.0.0.0/0")
                val conf=StringBuilder("[Interface]\nPrivateKey = ${value("private-key","private_key")}\nAddress = ${addresses.joinToString(",")}\nMTU = ${value("mtu").ifBlank{"1280"}}\n")
                (map["dns"] as? List<*>)?.let{conf.append("DNS = ${it.joinToString(",")}\n")}
                for((key,v) in awg)conf.append("$key = $v\n")
                conf.append("[Peer]\nPublicKey = ${value("public-key","public_key")}\nEndpoint = $authority:$port\nAllowedIPs = ${allowed.joinToString(",")}\n")
                value("pre-shared-key","preshared_key").takeIf{it.isNotBlank()}?.let{conf.append("PresharedKey = $it\n")}
                return ProfileCodec.parse(conf.toString(),value("name","tag"))
            }
            else->error("Протокол не поддерживается")
        }
        val completeLink=if(type=="vless"){
            val alpn=map["alpn"] ?: tls?.get("alpn")
            require(alpn==null || alpn is List<*>){"Неверный ALPN VLESS"}
            val fingerprint=value("client-fingerprint","fingerprint").ifBlank{((tls?.get("utls") as? Map<*,*>)?.get("fingerprint"))?.toString().orEmpty()}.ifBlank{"chrome"}
            val multi=fields["multiMode"] ?: fields["multi-mode"]
            require(multi==null || multi.toString() in listOf("true","false","1","0")){"Неверный multiMode gRPC"}
            val hybrid=reality?.get("support-x25519mlkem768")
            link+"&fp=${encode(fingerprint)}"+(if(alpn!=null)"&alpn=${encode((alpn as List<*>).joinToString(","))}"else "")+(if(multi!=null)"&multiMode=${encode(multi.toString())}"else "")+(if(hybrid!=null)"&support-x25519mlkem768=${encode(hybrid.toString())}"else "")
        }else if(type in listOf("hy2","hysteria2")){
            val obfs=map["obfs"];val obfsMap=obfs as? Map<*,*>
            val kind=obfsMap?.get("type")?.toString() ?: obfs?.toString().orEmpty()
            val password=obfsMap?.get("password")?.toString() ?: value("obfs-password")
            val insecure=map["skip-cert-verify"] ?: tls?.get("insecure") ?: map["insecure"]
            link+"&obfs=${encode(kind)}&obfs-password=${encode(password)}"+(if(insecure!=null)"&insecure=$insecure" else "")
        }else link
        return ProfileCodec.parse(completeLink,value("name","tag"))
    }
    companion object {
        private val refreshing=java.util.concurrent.ConcurrentHashMap<String,java.util.concurrent.CompletableFuture<Unit>>()
        fun origin(url:String):String{val uri=URI(url);require(uri.scheme=="https" && uri.host!=null && uri.userInfo==null){"Подписка требует HTTPS"};return "https://${uri.host.lowercase()}"+if(uri.port in listOf(-1,443))"" else ":${uri.port}"}
        fun hwid(seed:String,origin:String):String {
            val mac=Mac.getInstance("HmacSHA256");mac.init(SecretKeySpec(seed.toByteArray(Charsets.UTF_8),"HmacSHA256"));val bytes=mac.doFinal(("ior-hwid-v1:"+origin).toByteArray(Charsets.UTF_8)).copyOf(16)
            bytes[6]=((bytes[6].toInt() and 15) or 0x50).toByte();bytes[8]=((bytes[8].toInt() and 63) or 0x80).toByte()
            val buffer=java.nio.ByteBuffer.wrap(bytes);return UUID(buffer.long,buffer.long).toString()
        }
    }
}
