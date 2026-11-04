package app.ior.android

import org.json.JSONArray
import org.json.JSONObject
import java.net.URI
import java.net.URLDecoder
import java.util.Base64
import java.util.UUID
import java.util.zip.InflaterInputStream
import java.io.ByteArrayInputStream

object ProfileCodec {
    private val awgNames = mapOf("jc" to "jc", "jmin" to "jmin", "jmax" to "jmax", "s1" to "s1", "s2" to "s2", "s3" to "s3", "s4" to "s4",
        "h1" to "h1", "h2" to "h2", "h3" to "h3", "h4" to "h4", "i1" to "i1", "i2" to "i2", "i3" to "i3", "i4" to "i4", "i5" to "i5",
        "headerprotectionkey" to "header_protection_key", "contentpaddingaddition" to "content_padding_addition", "rekeyaftertime" to "rekey_after_time",
        "rejectaftertime" to "reject_after_time", "rekeytimeout" to "rekey_timeout", "keepalivetimeout" to "keepalive_timeout",
        "maxhandshakeattempts" to "max_handshake_attempts", "randomtrailers" to "random_trailers", "disablecookies" to "disable_cookies")
    private fun decode(value: String) = URLDecoder.decode(value.replace("+", "%2B"), "UTF-8")
    private fun base64(value: String): ByteArray = Base64.getDecoder().decode(value.replace('-', '+').replace('_', '/').filterNot { it.isWhitespace() })
    fun parse(input: String, name: String = ""): JSONObject {
        require(input.length in 1..1024*1024) { "Размер конфигурации недопустим" }
        val raw = input.trim()
        if (raw.startsWith("[Interface]", true)) return awg(raw, name, raw, false)
        val scheme = raw.substringBefore("://").lowercase()
        require(scheme !in listOf("vmess","ss","shadowsocks","trojan")){"VMess, Shadowsocks и Trojan пока не поддерживаются"}
        if (scheme in listOf("awg", "amneziawg", "vpn", "wireguard")) {
            val bytes = base64(decode(raw.substringAfter("://").substringBefore('#')))
            val text = if (bytes.size > 6 && bytes[4] == 0x78.toByte()) {
                InflaterInputStream(ByteArrayInputStream(bytes,4,bytes.size-4)).use { String(it.readNBytes(1024*1024+1), Charsets.UTF_8) }
            } else String(bytes, Charsets.UTF_8)
            require(text.length <= 1024*1024) { "Распакованная конфигурация слишком велика" }
            val config = if (text.trim().startsWith('{')) extractConfig(JSONObject(text),0) ?: error("В ссылке нет AWG-конфигурации") else text
            return awg(config, name.ifBlank { decode(raw.substringAfter('#', "")) }, raw, scheme in listOf("awg","amneziawg"))
        }
        require(scheme in listOf("vless","hy2","hysteria2")) { "Этот протокол пока не поддерживается Android" }
        val uri = URI(raw)
        val host = uri.host?.removeSurrounding("[", "]") ?: error("Неверный адрес сервера")
        val port = if (uri.port == -1) 443 else uri.port
        require(port in 1..65535)
        val credential = decode(uri.rawUserInfo ?: error("Не указаны данные доступа"))
        require(credential.isNotBlank()){ "Не указаны данные доступа" }
        val params = (uri.rawQuery ?: "").split('&').filter { it.isNotEmpty() }.associate { it.substringBefore('=') to decode(it.substringAfter('=',"")) }
        val isVless = scheme == "vless"
        val network=params["type"] ?: params["network"] ?: "tcp"
        val isReality = isVless && params["security"] == "reality"
        if (isVless) {
            require(params["security"] in listOf("tls","reality")) { "VLESS требует TLS или Reality" }
            require(network in listOf("","tcp","raw","ws","websocket","httpupgrade","grpc","xhttp")) { "Транспорт VLESS не поддерживается" }
            require(!isReality || network in listOf("","tcp","raw","grpc","xhttp")) { "Этот транспорт несовместим с Reality" }
            UUID.fromString(credential)
            require(!isReality || !params["pbk"].isNullOrBlank()) { "Не указан ключ Reality" }
            require(params["insecure"] !in listOf("1","true")&&params["allowInsecure"] !in listOf("1","true")){"Отключение проверки TLS не поддерживается"}
            require(params["encryption"].orEmpty() in listOf("","none")&&params["packetEncoding"].orEmpty() in listOf("","xudp")){"Параметры кодека VLESS не поддерживаются"}
            require(params.keys.all{it in listOf("security","type","network","sni","peer","serverName","pbk","sid","flow","fp","host","authority","path","serviceName","mode","multiMode","extra","encryption","packetEncoding","spx","insecure","allowInsecure","alpn","support-x25519mlkem768")}){"Не поддержан параметр VLESS"}
            params["support-x25519mlkem768"]?.let { flag ->
                require(flag in listOf("true","false","1","0")){"Неверный support-x25519mlkem768"}
                // The native Reality provider already offers the hybrid group for Chrome/Firefox.
                // Do not silently claim to disable it or enable it for the X25519-only Safari provider.
                require(isReality && flag in listOf("true","1") && params["fp"].orEmpty() in listOf("","chrome","firefox")){"support-x25519mlkem768 требует Reality, true и fingerprint chrome/firefox"}
            }
        } else {
            require(params["insecure"].orEmpty().lowercase() !in listOf("1","true") && params["allowInsecure"].orEmpty().lowercase() !in listOf("1","true")){"Отключение проверки TLS не поддерживается"}
            val obfs=params["obfs"].orEmpty().lowercase();val password=params["obfs-password"].orEmpty()
            require(obfs in listOf("","none","salamander")){"Тип obfs Hysteria2 не поддерживается"}
            require(if(obfs=="salamander")password.toByteArray(Charsets.UTF_8).size in 4..4096 else password.isEmpty()){"Неверный пароль obfs Hysteria2"}
        }
        val options = JSONObject()
        if(!isVless && params["obfs"].orEmpty().lowercase()=="salamander")options.put("obfs","salamander").put("obfsPassword",params["obfs-password"])
        if(isVless){
            options.put("transport",params["type"] ?: params["network"] ?: "tcp").put("host",params["host"] ?: params["authority"] ?: JSONObject.NULL).put("path",params["path"] ?: JSONObject.NULL).put("serviceName",params["serviceName"] ?: JSONObject.NULL)
            params["alpn"]?.let{options.put("alpn",JSONArray(it.split(',').map{v->v.trim()}.filter{v->v.isNotEmpty()}))}
            val extra=params["extra"]?.let{JSONObject(it)} ?: JSONObject()
            require(extra.keys().asSequence().all{it in listOf("headers","download","downloadSettings")}){"Не поддержаны обязательные параметры extra VLESS"}
            if(network=="grpc"){
                require(params["mode"].orEmpty() in listOf("","gun","multi")){"Режим gRPC не поддерживается"}
                require(params["multiMode"].orEmpty() in listOf("","true","false","1","0")){"Неверный multiMode gRPC"}
            }
            val transport=JSONObject().put("mode",params["mode"]?.takeIf{it.isNotBlank()} ?: JSONObject.NULL).put("multiMode",params["multiMode"] in listOf("true","1") || params["mode"]=="multi")
                .put("headers",extra.optJSONObject("headers") ?: JSONObject()).put("download",extra.optJSONObject("download") ?: JSONObject.NULL)
            if(network=="grpc")transport.remove("mode")
            options.put("vlessTransport",transport)
        }
        val wire = JSONObject().put("protocol",if (isReality) "vless_reality" else if(isVless) "vless_tls" else "hysteria2")
            .put("server",host).put("port",port).put("credential",credential)
            .put("serverName",params["sni"] ?: params["peer"] ?: params["serverName"] ?: host)
            .put("publicKey",if(isReality)params["pbk"] ?: JSONObject.NULL else JSONObject.NULL).put("shortId",if(isReality)params["sid"] ?: JSONObject.NULL else JSONObject.NULL)
            .put("flow",params["flow"] ?: JSONObject.NULL).put("fingerprint",if(isReality)params["fp"] ?: "chrome" else JSONObject.NULL).put("options",options)
        if(isVless)params["extra"]?.let{JSONObject(it).optJSONObject("downloadSettings")?.let{download->
            val security=download.optString("security",if(isReality)"reality"else"tls");require(security in listOf("tls","reality"));require(download.optString("network","xhttp")=="xhttp")
            require(download.keys().asSequence().all{it in listOf("address","port","network","security","tlsSettings","realitySettings","xhttpSettings")}){"Не поддержаны параметры downloadSettings"}
            val child=JSONObject(wire.toString());val tls=download.optJSONObject(if(security=="reality")"realitySettings"else"tlsSettings") ?: JSONObject();val stream=download.optJSONObject("xhttpSettings") ?: JSONObject()
            require(tls.keys().asSequence().all{it in listOf("serverName","publicKey","password","shortId","fingerprint","alpn")}){"Не поддержаны параметры безопасности downloadSettings"}
            tls.optJSONArray("alpn")?.let{values->require((0 until values.length()).all{values.getString(it)=="h2"}){"Download этого выпуска требует HTTP/2"}}
            require(stream.keys().asSequence().all{it in listOf("host","path","mode","extra")}){"Не поддержаны параметры xhttpSettings downloadSettings"}
            val extra=stream.optJSONObject("extra") ?: JSONObject();require(extra.keys().asSequence().all{it=="headers"}){"Не поддержаны параметры extra downloadSettings"}
            val childOptions=JSONObject().put("transport","xhttp").put("host",stream.opt("host") ?: JSONObject.NULL).put("path",stream.opt("path") ?: params["path"] ?: "/")
                .put("vlessTransport",JSONObject().put("mode",stream.opt("mode") ?: JSONObject.NULL).put("headers",extra.optJSONObject("headers") ?: JSONObject()))
            tls.optJSONArray("alpn")?.let{childOptions.put("alpn",JSONArray(it.toString()))}
            child.put("protocol",if(security=="reality")"vless_reality"else"vless_tls").put("server",download.optString("address",host)).put("port",download.optInt("port",port)).put("serverName",tls.optString("serverName",wire.getString("serverName"))).put("flow",JSONObject.NULL).put("options",childOptions)
                .put("publicKey",if(security=="reality")tls.opt("publicKey") ?: tls.opt("password") ?: wire.opt("publicKey") else JSONObject.NULL).put("shortId",if(security=="reality")tls.opt("shortId") ?: wire.opt("shortId") else JSONObject.NULL).put("fingerprint",if(security=="reality")tls.optString("fingerprint","chrome")else JSONObject.NULL)
            options.getJSONObject("vlessTransport").put("download",child)
        }}
        return server(name.ifBlank { decode(uri.rawFragment.orEmpty()).ifBlank { host } }, raw, wire)
    }
    private fun extractConfig(value: Any?, depth: Int): String? {
        if (depth > 8) return null
        if (value is String && value.trim().startsWith("[Interface]")) return value
        if (value is String && value.trim().startsWith('{')) try { extractConfig(JSONObject(value),depth+1)?.let{return it} } catch(_:Exception) {}
        if (value is JSONObject) for (key in value.keys()) extractConfig(value.opt(key),depth+1)?.let { return it }
        if (value is JSONArray) for (i in 0 until value.length()) extractConfig(value.opt(i),depth+1)?.let { return it }
        return null
    }
    private fun awg(text: String, name: String, raw: String, forced: Boolean): JSONObject {
        var section = ""
        var peers = 0
        val values = mutableMapOf<String,String>()
        for (line in text.lineSequence()) {
            val clean = line.substringBefore('#').trim()
            if (clean.startsWith('[')) { section=clean.lowercase(); if (section=="[peer]") peers++; continue }
            if (clean.isBlank()) continue
            require(clean.contains('=')) { "Неверная строка AWG" }
            val key=clean.substringBefore('=').trim().lowercase().replace("_","")
            val value=clean.substringAfter('=').trim()
            require(section in listOf("[interface]","[peer]")) { "Неверная секция AWG" }
            val full=section+key
            require(!values.containsKey(full)) { "Повтор параметра AWG" }
            val known = if(section=="[interface]") key in listOf("privatekey","address","dns","mtu") || awgNames.containsKey(key) else key in listOf("publickey","presharedkey","endpoint","allowedips","persistentkeepalive")
            require(known) { "Неизвестный параметр AWG: $key" }
            values[full]=value
        }
        require(peers==1) { "Поддерживается один Peer" }
        fun value(section:String,key:String)=values["[$section]$key"]
        val options=JSONObject()
        val parameters=JSONObject()
        for ((key,native) in awgNames) value("interface",key)?.let { parameters.put(native,it) }
        require(forced || parameters.length()>0) { "Обычный WireGuard будет добавлен отдельным этапом" }
        val endpoint=value("peer","endpoint") ?: error("Не указан Endpoint")
        val endpointUri=URI("udp://$endpoint")
        require(endpointUri.host!=null && endpointUri.port in 1..65535) { "Неверный Endpoint" }
        val privateKey=value("interface","privatekey") ?: error("Не указан PrivateKey")
        val publicKey=value("peer","publickey") ?: error("Не указан PublicKey")
        require(base64(privateKey).size==32 && base64(publicKey).size==32) { "Неверный ключ AWG" }
        fun list(text:String?)=JSONArray(text?.split(',')?.map { it.trim() }?.filter { it.isNotBlank() } ?: emptyList<String>())
        options.put("awg",parameters).put("awgEngine","rust").put("awgRoutingMode","rules")
            .put("localAddresses",list(value("interface","address"))).put("allowedIps",list(value("peer","allowedips")))
            .put("peerPublicKey",publicKey).put("presharedKey",value("peer","presharedkey") ?: JSONObject.NULL)
            .put("keepalive",value("peer","persistentkeepalive")?.toInt() ?: JSONObject.NULL).put("mtu",value("interface","mtu")?.toInt() ?: 1280)
        val wire=JSONObject().put("protocol","amneziawg").put("server",endpointUri.host.removeSurrounding("[","]")).put("port",endpointUri.port)
            .put("credential",privateKey).put("serverName",endpointUri.host).put("publicKey",JSONObject.NULL).put("shortId",JSONObject.NULL).put("options",options)
        return server(name.ifBlank { "AmneziaWG" },raw,wire).put("dns",list(value("interface","dns")))
    }
    private fun server(name:String,raw:String,wire:JSONObject)=JSONObject().put("id",UUID.randomUUID().toString()).put("name",name)
        .put("protocol",when(wire.getString("protocol")){"amneziawg"->"AmneziaWG";"hysteria2"->"Hysteria2";"vless_tls"->"VLESS TLS";else->"VLESS Reality"})
        .put("address",wire.getString("server")+":"+wire.getInt("port")).put("raw",raw).put("wire",wire).put("subscriptionId","")
}
