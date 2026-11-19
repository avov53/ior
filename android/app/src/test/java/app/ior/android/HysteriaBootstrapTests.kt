package app.ior.android

import java.net.InetAddress
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class HysteriaBootstrapTests {
    private val request=JSONObject("""{"profile":{"protocol":"hysteria2","server":"vpn.example","serverName":"sni.example","options":{"hysteriaCertificatePin":"test"}}}""")
    private val timeout=JSONObject().put("code","BOOTSTRAP_DNS_TIMEOUT")
    @Test fun endpointOnlyChangesInTemporaryRequest(){
        val result=HysteriaBootstrap.retryRequest(request,timeout){assertEquals("vpn.example",it);InetAddress.getByAddress(byteArrayOf(10,0,0,2))}!!
        val profile=result.getJSONObject("profile")
        assertEquals("10.0.0.2",profile.getString("server"))
        assertEquals("sni.example",profile.getString("serverName"))
        assertEquals("test",profile.getJSONObject("options").getString("hysteriaCertificatePin"))
        assertEquals("vpn.example",request.getJSONObject("profile").getString("server"))
    }
    @Test fun mobileEndpointResolutionPreservesProfileForEachProtocol(){
        for(protocol in listOf("hysteria2","vless_reality","amneziawg")){
            val profile=request.getJSONObject("profile").put("protocol",protocol)
            val copy=HysteriaBootstrap.resolvedRequest(request){InetAddress.getByAddress(byteArrayOf(10,0,0,3))}!!
            assertEquals("10.0.0.3",copy.getJSONObject("profile").getString("server"))
            assertEquals("vpn.example",profile.getString("server"))
            assertNull(HysteriaBootstrap.resolvedRequest(request){null})
        }
    }
    @Test fun otherErrorsAndLiteralsSkipFallback(){
        assertNull(HysteriaBootstrap.retryRequest(request,JSONObject().put("code","HYSTERIA2_QUIC_ERROR")){error("unexpected DNS")})
        request.getJSONObject("profile").put("server","192.0.2.1")
        assertNull(HysteriaBootstrap.retryRequest(request,timeout){error("unexpected DNS")})
    }
}
