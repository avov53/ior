package app.ior.android

import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.tls.HandshakeCertificates
import okhttp3.tls.HeldCertificate
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SubscriptionTests {
    @Test fun mixedAndUnsupportedOnlyRefreshKeepSupportedServers(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString())
            server.enqueue(MockResponse().setBody("vmess://invalid\nss://invalid\ntrojan://invalid\nhy2://secret@example.com:443"));client(store,http).refresh("sub")
            assertEquals(1,store.servers.size);assertTrue(store.item.getString("importWarning").contains("VMess"))
            server.enqueue(MockResponse().setBody("vmess://invalid"));client(store,http).refresh("sub");assertEquals(1,store.servers.size)
        }finally{server.shutdown()}
    }
    @Test fun realityHybridShareLinkSubscriptionImports(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString())
            val link="vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&type=tcp&flow=xtls-rprx-vision&fp=firefox&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=aa&sni=sni.example&support-x25519mlkem768=true"
            server.enqueue(MockResponse().setBody(java.util.Base64.getEncoder().encodeToString(link.toByteArray())))
            client(store,http).refresh("sub")
            val wire=store.servers.single().getJSONObject("wire")
            assertEquals("vless_reality",wire.getString("protocol"));assertEquals("firefox",wire.getString("fingerprint"));assertEquals("xtls-rprx-vision",wire.getString("flow"));assertEquals("sni.example",wire.getString("serverName"))
        }finally{server.shutdown()}
    }
    @Test fun vlessSubscriptionPreservesGrpcSecurityAndAuthority(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString())
            val profile=JSONObject().put("type","vless").put("server","edge.example").put("port",443).put("uuid","11111111-1111-1111-1111-111111111111")
                .put("network","grpc").put("servername","sni.example").put("alpn",JSONArray().put("h2")).put("client-fingerprint","firefox")
                .put("reality-opts",JSONObject().put("public-key","AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").put("short-id","aa").put("support-x25519mlkem768",true))
                .put("grpc-opts",JSONObject().put("grpc-service-name","service").put("authority","authority.example").put("multi-mode",true))
            server.enqueue(MockResponse().setBody(JSONArray().put(profile).toString()));client(store,http).refresh("sub")
            val wire=store.servers.single().getJSONObject("wire");val options=wire.getJSONObject("options")
            assertEquals("vless_reality",wire.getString("protocol"));assertEquals("firefox",wire.getString("fingerprint"));assertEquals("sni.example",wire.getString("serverName"))
            assertEquals("grpc",options.getString("transport"));assertEquals("h2",options.getJSONArray("alpn").getString(0));assertEquals("authority.example",options.getString("host"));assertEquals("service",options.getString("serviceName"));assertTrue(options.getJSONObject("vlessTransport").getBoolean("multiMode"))
        }finally{server.shutdown()}
    }
    @Test fun serverIntervalSurvivesFailureAndClearsOnlyAfterSuccess(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setHeader("Profile-Update-Interval","1").setBody("hy2://secret@example.com:443"));client(store,http).refresh("sub");assertEquals(1,store.item.getInt("serverRefreshIntervalHours"));server.enqueue(MockResponse().setResponseCode(500));assertThrows(IllegalArgumentException::class.java){client(store,http).refresh("sub")};assertEquals(1,store.item.getInt("serverRefreshIntervalHours"));server.enqueue(MockResponse().setBody("hy2://secret@example.com:443"));client(store,http).refresh("sub");assertFalse(store.item.has("serverRefreshIntervalHours"))}finally{server.shutdown()}
    }
    @Test fun simultaneousRefreshesShareOneDownload(){
        val(server,http)=fixture();val pool=java.util.concurrent.Executors.newFixedThreadPool(2);try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setBody("hy2://secret@example.com:443").setBodyDelay(500,java.util.concurrent.TimeUnit.MILLISECONDS));val first=pool.submit{client(store,http).refresh("sub")};server.takeRequest();val second=pool.submit{client(store,http).refresh("sub")};first.get(3,java.util.concurrent.TimeUnit.SECONDS);second.get(3,java.util.concurrent.TimeUnit.SECONDS);assertEquals(1,server.requestCount)}finally{pool.shutdownNow();server.shutdown()}
    }
    @Test fun migrationPreservesDeniedAndApprovedSubscriptions(){
        val denied=JSONObject().put("hwidPolicy","deny");HwidPolicy.migrate(denied,true);assertEquals("never",denied.getString("hwidMode"))
        val approved=JSONObject().put("hwidOrigins",JSONArray(listOf("https://example.com")));HwidPolicy.migrate(approved,false);assertEquals("always",approved.getString("hwidMode"))
        val empty=JSONObject();HwidPolicy.migrate(empty,false);assertEquals("required",empty.getString("hwidMode"))
    }
    @Test fun requiredModeRetriesAndRemembersOnlyAfterExplicitRequirement(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString());store.item.put("hwidMode","required")
            server.enqueue(MockResponse().setResponseCode(403).setHeader("subscription-always-hwid-enable","true"))
            server.enqueue(MockResponse().setBody("hy2://secret@example.com:443"))
            client(store,http).refresh("sub")
            assertNull(server.takeRequest().getHeader("x-hwid"));val hwid=server.takeRequest().getHeader("x-hwid");assertNotNull(hwid)
            server.enqueue(MockResponse().setBody("hy2://secret@example.com:443"));client(store,http).refresh("sub");assertEquals(hwid,server.takeRequest().getHeader("x-hwid"))
        }finally{server.shutdown()}
    }
    @Test fun neverModeCannotBeOverriddenByServerRequirement(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString());store.item.put("hwidMode","never");store.enabled=true
            server.enqueue(MockResponse().setResponseCode(403).setHeader("subscription-always-hwid-enable","true"))
            assertThrows(IllegalStateException::class.java){client(store,http).refresh("sub")};assertNull(server.takeRequest().getHeader("x-hwid"));assertEquals("old-server",store.servers[0].getString("raw"));assertEquals(1,server.requestCount)
        }finally{server.shutdown()}
    }
    @Test fun requiredModeDoesNotAuthorizeDifferentRedirectOrigin(){
        val(first,http)=fixture();val(second,_)=fixture();try{
            val store=Store(first.url("/sub").toString());store.item.put("hwidMode","required")
            first.enqueue(MockResponse().setResponseCode(302).setHeader("Location",second.url("/sub")))
            second.enqueue(MockResponse().setResponseCode(403).setHeader("subscription-always-hwid-enable","true"))
            assertThrows(SubscriptionClient.ConsentRequired::class.java){client(store,http).refresh("sub")};assertNull(first.takeRequest().getHeader("x-hwid"));assertNull(second.takeRequest().getHeader("x-hwid"))
        }finally{first.shutdown();second.shutdown()}
    }
    @Test fun alwaysModeSendsWithoutRequirement(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());store.item.put("hwidMode","always");server.enqueue(MockResponse().setBody("hy2://secret@example.com:443"));client(store,http).refresh("sub");assertNotNull(server.takeRequest().getHeader("x-hwid"))}finally{server.shutdown()}
    }
    private val certificate=HeldCertificate.Builder().addSubjectAlternativeName("localhost").addSubjectAlternativeName("127.0.0.1").build()
    private class Store(url:String):SubscriptionStore {
        var enabled=false
        val item=JSONObject().put("id","sub").put("url",url).put("name","Test").put("hwidOrigins",JSONArray()).put("updatedAt",123L)
        var servers=listOf(JSONObject().put("raw","old-server"))
        var route=JSONObject().put("raw","MATCH,DIRECT")
        override fun synchronizeRoute(item:JSONObject,route:JSONObject?,enabled:Boolean){if(enabled)this.route=route!! else this.route=JSONObject()}
        override fun snapshot()=JSONObject().put("hwidEnabled",enabled).put("installationId","seed")
        override fun subscriptions()=listOf(item)
        override fun request(server:JSONObject)=server
        override fun updateSubscription(item:JSONObject,servers:List<JSONObject>?,success:Boolean){if(success){this.servers=servers!!;this.item.put("updatedAt",456L)}}
    }
    private fun fixture():Pair<MockWebServer,OkHttpClient>{
        val serverCert=HandshakeCertificates.Builder().heldCertificate(certificate).build()
        val clientCert=HandshakeCertificates.Builder().addTrustedCertificate(certificate.certificate).build()
        val server=MockWebServer().apply{useHttps(serverCert.sslSocketFactory(),false);start()}
        val client=OkHttpClient.Builder().sslSocketFactory(clientCert.sslSocketFactory(),clientCert.trustManager).followRedirects(false).followSslRedirects(false).build()
        return server to client
    }
    private fun client(store:Store,http:OkHttpClient)=SubscriptionClient(store,http,{true},{mapOf("x-device-os" to "Android")})
    @Test fun routeHeaderImportsAndMalformedRefreshKeepsPrevious(){
        val(server,http)=fixture();try{
            val store=Store(server.url("/sub").toString())
            val encoded="happ://routing/"+java.util.Base64.getEncoder().encodeToString("{\"DirectDomains\":[\"local.test\"],\"GlobalProxy\":true}".toByteArray())
            server.enqueue(MockResponse().setHeader("Routing-Enable","true").setHeader("Routing",encoded).setBody("hy2://secret@example.com:443"))
            client(store,http).refresh("sub");val before=store.route.toString();assertTrue(before.contains("local.test"))
            server.enqueue(MockResponse().setHeader("Routing-Enable","true").setHeader("Routing","broken").setBody("hy2://secret@example.com:443"))
            client(store,http).refresh("sub");assertEquals(before,store.route.toString());assertTrue(store.item.has("routingWarning"))
        }finally{server.shutdown()}
    }
    @Test fun requirementNeedsConsentAndRetryIsExplicit(){
        val(server,http)=fixture()
        try{
            val store=Store(server.url("/sub").toString())
            server.enqueue(MockResponse().setResponseCode(403).setHeader("subscription-always-hwid-enable","true"))
            assertThrows(SubscriptionClient.ConsentRequired::class.java){client(store,http).refresh("sub")}
            assertNull(server.takeRequest().getHeader("x-hwid"));assertEquals(123L,store.item.getLong("updatedAt"));assertEquals("old-server",store.servers[0].getString("raw"))
            store.item.getJSONArray("hwidOrigins").put(SubscriptionClient.origin(store.item.getString("url")))
            server.enqueue(MockResponse().setBody("hy2://secret@example.com:443"))
            client(store,http).refresh("sub")
            assertNotNull(server.takeRequest().getHeader("x-hwid"));assertEquals(456L,store.item.getLong("updatedAt"));assertEquals(1,store.servers.size)
        }finally{server.shutdown()}
    }
    @Test fun deviceLimitPreservesPreviousServersAndTimestamp(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setResponseCode(403).setHeader("x-hwid-max-devices-reached","true"));assertThrows(IllegalArgumentException::class.java){client(store,http).refresh("sub")};assertEquals(123L,store.item.getLong("updatedAt"));assertEquals("old-server",store.servers[0].getString("raw"))}finally{server.shutdown()}
    }
    @Test fun malformedRecordDoesNotBlockSupportedProfile(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setBody("hy2://secret@example.com:443\nhy2://secret@example.com:70000"));client(store,http).refresh("sub");assertEquals(1,store.servers.size);assertTrue(store.item.getString("importWarning").contains("1"));assertFalse(store.item.getString("importWarning").contains("secret"))}finally{server.shutdown()}
    }
    @Test fun salamanderSubscriptionPreservesPasswordAndSkipsInvalidRecord(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setBody("hy2://auth@example.com:443?obfs=salamander&obfs-password=test-obfs\nhy2://auth@example.com:443?obfs=unknown"));client(store,http).refresh("sub");assertEquals(1,store.servers.size);assertEquals("test-obfs",store.servers.single().getJSONObject("wire").getJSONObject("options").getString("obfsPassword"));assertFalse(store.item.getString("importWarning").contains("test-obfs"))}finally{server.shutdown()}
    }
    @Test fun falseHwidHintDoesNotRequestConsent(){
        val(server,http)=fixture();try{val store=Store(server.url("/sub").toString());server.enqueue(MockResponse().setHeader("x-hwid-not-supported","false").setBody("hy2://secret@example.com:443"));client(store,http).refresh("sub");assertNull(server.takeRequest().getHeader("x-hwid"));assertEquals(456L,store.item.getLong("updatedAt"))}finally{server.shutdown()}
    }
    @Test fun redirectToAnotherOriginDoesNotCarryHwid(){
        val(first,http)=fixture()
        val(second,_)=fixture()
        try{
            val store=Store(first.url("/sub").toString());store.enabled=true
            first.enqueue(MockResponse().setResponseCode(302).setHeader("Location",second.url("/sub")))
            second.enqueue(MockResponse().setBody("hy2://secret@example.com:443"))
            client(store,http).refresh("sub")
            assertNotNull(first.takeRequest().getHeader("x-hwid"))
            assertNull(second.takeRequest().getHeader("x-hwid"))
            assertEquals(456L,store.item.getLong("updatedAt"))
        }finally{first.shutdown();second.shutdown()}
    }
}
