package app.ior.android

import org.junit.Assert.*
import org.junit.Test
import org.json.JSONArray
import org.json.JSONObject

class CodecTests {
    @Test fun realityUdpSettingPreservesImportedFlowAndOriginalProfile(){
        for(flow in listOf("xtls-rprx-vision","xtls-rprx-vision-udp443"))for(enabled in listOf(false,true)){
            val raw="vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&pbk=synthetic&sni=cover.example&type=tcp&flow="+flow
            val server=ProfileCodec.parse(raw);val original=server.getJSONObject("wire");val before=original.toString()
            val restored=JSONObject(original.toString());val effective=RealityUdpPolicy.effective(restored,enabled)
            assertEquals(if(enabled)"xtls-rprx-vision-udp443"else flow,effective.getString("flow"));assertEquals(before,original.toString())
        }
        val hy=JSONObject().put("protocol","hysteria2").put("flow",JSONObject.NULL)
        assertTrue(RealityUdpPolicy.effective(hy,true).isNull("flow"))
    }

    @Test fun salamanderImportsWithoutDisablingTls(){
        val base="hysteria2://test-password@edge.example:443?sni=edge.example&obfs=salamander&obfs-password=test-obfs"
        val options=ProfileCodec.parse(base).getJSONObject("wire").getJSONObject("options")
        assertEquals("salamander",options.getString("obfs"));assertEquals("test-obfs",options.getString("obfsPassword"))
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse(base+"&insecure=true")}
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse(base.replace("salamander","unknown"))}
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse(base.replace("test-obfs","ab"))}
    }

    @Test fun realityHybridShareLinkImportsWithoutChangingSecurity(){
        val base="vless://11111111-1111-1111-1111-111111111111@edge.example:443?encryption=none&flow=xtls-rprx-vision&fp=firefox&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&security=reality&sid=3195cbb9875637&sni=sni.example&spx=%2F8bc8d8243e4044b&type=tcp"
        val baseline=ProfileCodec.parse(base).getJSONObject("wire")
        for(flag in listOf("true","1")){
            val profile=ProfileCodec.parse("$base&support-x25519mlkem768=$flag#Home")
            assertEquals(baseline.toString(),profile.getJSONObject("wire").toString())
            assertEquals("Home",profile.getString("name"))
            assertTrue(profile.getString("raw").contains("support-x25519mlkem768=$flag"))
        }
        for(flag in listOf("false","0","invalid"))assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("$base&support-x25519mlkem768=$flag")}
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse(base.replace("fp=firefox","fp=safari")+"&support-x25519mlkem768=true")}
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("$base&unknown-security-option=true")}
    }
    @Test fun grpcModesArePreservedAndUnknownModesRejected(){
        val base="vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls&type=grpc"
        for(mode in listOf("gun","multi")){
            val transport=ProfileCodec.parse("$base&mode=$mode").getJSONObject("wire").getJSONObject("options").getJSONObject("vlessTransport")
            assertEquals(mode=="multi",transport.getBoolean("multiMode"));assertFalse(transport.has("mode"))
        }
        assertTrue(ProfileCodec.parse("$base&multiMode=true").getJSONObject("wire").getJSONObject("options").getJSONObject("vlessTransport").getBoolean("multiMode"))
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("$base&mode=unknown")}
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("$base&multiMode=unknown")}
    }
    @Test fun vlessServerNameAliasAndSniPrecedence(){
        val base="vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls&type=grpc"
        assertEquals("alias.example",ProfileCodec.parse("$base&serverName=alias.example").getJSONObject("wire").getString("serverName"))
        assertEquals("peer.example",ProfileCodec.parse("$base&serverName=alias.example&peer=peer.example").getJSONObject("wire").getString("serverName"))
        assertEquals("sni.example",ProfileCodec.parse("$base&serverName=alias.example&peer=peer.example&sni=sni.example").getJSONObject("wire").getString("serverName"))
    }
    @Test fun xhttpDownloadPreservesEndpointHeadersAndAlpn(){
        val download=JSONObject().put("address","download.example").put("port",8443).put("security","tls")
            .put("tlsSettings",JSONObject().put("serverName","download-sni.example").put("alpn",JSONArray().put("h2")))
            .put("xhttpSettings",JSONObject().put("path","/down").put("host","authority.example").put("extra",JSONObject().put("headers",JSONObject().put("x-test","value"))))
        val extra=java.net.URLEncoder.encode(JSONObject().put("downloadSettings",download).toString(),"UTF-8")
        val profile=ProfileCodec.parse("vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls&type=xhttp&extra=$extra")
        val child=profile.getJSONObject("wire").getJSONObject("options").getJSONObject("vlessTransport").getJSONObject("download")
        assertEquals("download.example",child.getString("server"));assertEquals(8443,child.getInt("port"));assertEquals("download-sni.example",child.getString("serverName"))
        val options=child.getJSONObject("options")
        assertEquals("/down",options.getString("path"));assertEquals("authority.example",options.getString("host"));assertEquals("h2",options.getJSONArray("alpn").getString(0))
        assertEquals("value",options.getJSONObject("vlessTransport").getJSONObject("headers").getString("x-test"))
        download.getJSONObject("xhttpSettings").put("unsupportedOption",true)
        val unsupported=java.net.URLEncoder.encode(JSONObject().put("downloadSettings",download).toString(),"UTF-8")
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls&type=xhttp&extra=$unsupported")}
    }
    @Test fun vlessFiveTransportsAndSecurityArePreserved(){
        for(transport in listOf("raw","ws","httpupgrade","grpc","xhttp")){
            val profile=ProfileCodec.parse("vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls&type=$transport&sni=edge.example&path=%2Flab&serviceName=service")
            val wire=profile.getJSONObject("wire");assertEquals("vless_tls",wire.getString("protocol"));assertTrue(wire.isNull("publicKey"));assertEquals(transport,wire.getJSONObject("options").getString("transport"));assertEquals("/lab",wire.getJSONObject("options").getString("path"))
        }
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse("vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&type=ws&pbk=synthetic")}
    }
    @Test fun routeSourceDoesNotChangeNormalizedRules(){
        val route=SubscriptionRoutes.decode("{\"GlobalProxy\":true,\"DirectSites\":[\"local.test\"]}",okhttp3.OkHttpClient())
        assertEquals("happ",route.getString("sourceFormat"));assertEquals("ior",route.getString("format"))
        val legacy=JSONObject().put("id","stable").put("managedBy","sub").put("format","ior").put("raw","MATCH,PROXY")
        assertEquals("unknown",RouteSource.normalize(legacy).getString("sourceFormat"));assertEquals("stable",legacy.getString("id"));assertEquals("MATCH,PROXY",legacy.getString("raw"))
        assertEquals("clash",RouteSource.normalize(JSONObject().put("format","clash")).getString("sourceFormat"))
    }
    @Test fun autoPingUsesRemainingBudgetAndLabelsHttps(){
        var clock=0L;var method="";var httpBudget=0
        val result=PingMeasurement.run("hysteria2","auto",5000,false,{m,t->method=m;assertEquals(1000,t);clock=1000;PingResult(null,m,"PING_ICMP_UNAVAILABLE")},{t->httpBudget=t;PingResult(88,"https")},{clock})
        assertEquals("icmp",method);assertEquals(4000,httpBudget);assertEquals("https",result.method);assertEquals(88L,result.milliseconds)
    }
    @Test fun autoAwgCannotStartSecondPeerAndExplicitIcmpDoesNotFallback(){
        for((protocol,mode) in listOf("amneziawg" to "auto","hysteria2" to "icmp")){
            val result=PingMeasurement.run(protocol,mode,5000,false,{m,_->PingResult(null,m,"PING_ICMP_UNAVAILABLE")},{error("HTTPS must not start")})
            assertNull(result.milliseconds);assertEquals("icmp",result.method)
        }
        val result=PingMeasurement.run("vless_reality","auto",5000,false,{m,_->assertEquals("tcp",m);PingResult(25,m)},{error("Working TCP must not fall back")})
        assertEquals("tcp",result.method)
    }
    @Test fun unknownRoutingJsonIsNotSilentlyConvertedToProxy(){
        assertThrows(IllegalArgumentException::class.java){SubscriptionRoutes.decode("{\"unknown\":true}",okhttp3.OkHttpClient())}
    }
    @Test fun noResolveIsPreservedAndUnknownModifiersAreRejected(){
        val rules=RouteCodec.rules(JSONObject().put("raw","IP-CIDR,10.0.0.0/8,DIRECT,no-resolve"))
        assertTrue(rules.getJSONObject(0).getBoolean("noResolve"))
        assertThrows(IllegalArgumentException::class.java){RouteCodec.rules(JSONObject().put("raw","IP-CIDR,10.0.0.0/8,DIRECT,unknown"))}
    }
    @Test fun windowsHappAddLinkAndExplicitActionOrderArePreserved(){
        val text="{\"RouteOrder\":\"direct-proxy-block\",\"DirectSites\":[\"same.test\"],\"ProxySites\":[\"same.test\"]}"
        val encoded=java.util.Base64.getEncoder().withoutPadding().encodeToString(text.toByteArray())
        val route=SubscriptionRoutes.decode("happ://routing/onadd/"+java.net.URLEncoder.encode(encoded,"UTF-8"),okhttp3.OkHttpClient())
        val rules=RouteCodec.rules(route);assertEquals("DIRECT",rules.getJSONObject(0).getString("target"));assertEquals("PROXY",rules.getJSONObject(1).getString("target"));assertEquals("DIRECT",rules.getJSONObject(2).getString("target"))
    }
    @Test fun happRoutingDecodesWithoutLosingOrder(){
        val text="{\"GlobalProxy\":true,\"BlockDomains\":[\"bad.test\"],\"DirectDomains\":[\"local.test\"]}"
        val encoded="happ://routing/"+java.util.Base64.getEncoder().encodeToString(text.toByteArray())
        val route=SubscriptionRoutes.decode(encoded,okhttp3.OkHttpClient())
        val rules=RouteCodec.rules(route)
        assertEquals("REJECT",rules.getJSONObject(0).getString("target"));assertEquals("DIRECT",rules.getJSONObject(1).getString("target"));assertEquals("PROXY",rules.getJSONObject(2).getString("target"))
    }
    @Test fun geositeAndIpv6NormalizeToSupportedCoreRules(){
        val old=RouteCodec.geodata
        try{RouteCodec.geodata={_,_->JSONArray().put(JSONObject().put("kind","DOMAIN-SUFFIX").put("value","test.ru"))}
            val rules=RouteCodec.rules(JSONObject().put("raw","GEOSITE,ru,DIRECT\nIP-CIDR6,2001:db8::/32,PROXY\nMATCH,PROXY"))
            assertEquals("DOMAIN-SUFFIX",rules.getJSONObject(0).getString("kind"));assertEquals("IP-CIDR",rules.getJSONObject(1).getString("kind"))
        }finally{RouteCodec.geodata=old}
    }
    @Test fun realityAndHysteriaKeepCredentialsAndTransport(){
        val reality=ProfileCodec.parse("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality&sni=example.org&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=aa&flow=xtls-rprx-vision#Home")
        assertEquals("vless_reality",reality.getJSONObject("wire").getString("protocol"))
        assertEquals("xtls-rprx-vision",reality.getJSONObject("wire").getString("flow"))
        assertEquals("a+b",ProfileCodec.parse("hy2://a%2Bb@example.com:443").getJSONObject("wire").getString("credential"))
    }
    @Test fun awgPreservesAllParametersAndRejectsUnknown(){
        val key=java.util.Base64.getEncoder().encodeToString(ByteArray(32){7})
        val raw="[Interface]\nPrivateKey = $key\nAddress = 10.7.0.2/32\nJc = 2\nH1 = 100-110\n[Peer]\nPublicKey = $key\nEndpoint = example.com:1234\nAllowedIPs = 0.0.0.0/0"
        val profile=ProfileCodec.parse(raw)
        assertEquals("100-110",profile.getJSONObject("wire").getJSONObject("options").getJSONObject("awg").getString("h1"))
        assertThrows(IllegalArgumentException::class.java){ProfileCodec.parse(raw.replace("Jc = 2","NotAWG = 2"))}
    }
    @Test fun routesRetainOrderAndRejectUnknownActions(){
        val rules=RouteCodec.rules(JSONObject().put("raw","DOMAIN-SUFFIX,ru,DIRECT\nMATCH,PROXY"))
        assertEquals("DIRECT",rules.getJSONObject(0).getString("target"))
        assertEquals("PROXY",rules.getJSONObject(1).getString("target"))
        assertThrows(IllegalArgumentException::class.java){RouteCodec.rules(JSONObject().put("raw","MATCH,SILENT_DIRECT"))}
    }
    @Test fun hwidIsStablePerOriginAndDifferentBetweenOrigins(){
        assertEquals(SubscriptionClient.hwid("seed","https://a.test"),SubscriptionClient.hwid("seed","https://a.test"))
        assertNotEquals(SubscriptionClient.hwid("seed","https://a.test"),SubscriptionClient.hwid("seed","https://b.test"))
        assertThrows(IllegalArgumentException::class.java){SubscriptionClient.origin("http://example.org/sub")}
    }
    @Test fun backupWithoutSecretsAndEncryptedRoundtrip(){
        val state=JSONObject().put("schema",1).put("servers",JSONArray().put(JSONObject().put("raw","secret"))).put("subscriptions",JSONArray().put(JSONObject().put("url","https://a.test/secret"))).put("routes",JSONArray()).put("installationId","device-secret")
        val public=String(BackupCodec.encode(state,null));assertFalse(public.contains("device-secret"));assertFalse(public.contains("https://a.test"))
        val encrypted=BackupCodec.encode(state,"long-password".toCharArray());assertFalse(String(encrypted).contains("https://a.test"))
        assertEquals("secret",BackupCodec.decode(encrypted,"long-password".toCharArray()).getJSONArray("servers").getJSONObject(0).getString("raw"))
        assertThrows(Exception::class.java){BackupCodec.decode(encrypted,"wrong-password".toCharArray())}
    }
}
