package app.ior.android

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DiagnosticTests {
    @Test fun defaultsPreserveExistingPreferencesAndRestoreSystemRoute(){
        val fresh=JSONObject().put("routes",org.json.JSONArray());RepositoryDefaults.apply(fresh,true)
        assertFalse(fresh.getBoolean("saveMobileTraffic"));assertTrue(fresh.getBoolean("routingEnabled"));assertEquals(0,fresh.getJSONArray("routes").length())
        val old=JSONObject().put("saveMobileTraffic",true).put("selectedRoute","personal").put("routes",org.json.JSONArray().put(JSONObject().put("id","personal")))
        RepositoryDefaults.apply(old,false);RepositoryDefaults.apply(old,false)
        assertTrue(old.getBoolean("saveMobileTraffic"));assertEquals("personal",old.getString("selectedRoute"));assertEquals(1,old.getJSONArray("routes").length());val none=JSONObject().put("selectedRoute","none").put("routes",org.json.JSONArray().put(JSONObject().put("id","none")));RepositoryDefaults.apply(none,false);assertFalse(none.getBoolean("routingEnabled"));assertEquals("",none.getString("selectedRoute"))
    }
    @Test fun consoleSurvivesProtocolSwitchAndProcessRestartUntilExplicitClear(){
        val dir=java.nio.file.Files.createTempDirectory("ior-console-").toFile()
        try{
            Diagnostics(dir).use{d->d.configure(5,7);d.beginSession("awg","amneziawg","packet");repeat(350){d.console(JSONObject().put("time","12:00:00").put("text","AWG-$it"))};d.beginSession("vless","vless_reality","raw");d.console(JSONObject().put("time","12:01:00").put("text","VLESS"));assertEquals(351,d.consoleTail().length());val zip=archive(d.export());assertTrue(zip.getValue("console/current.jsonl").contains("AWG-0"))}
            Diagnostics(dir).use{d->val tail=d.consoleTail();assertEquals(351,tail.length());assertEquals("AWG-0",tail.getJSONObject(0).getString("text"));assertEquals("amneziawg",tail.getJSONObject(0).getString("protocol"));assertEquals("VLESS",tail.getJSONObject(350).getString("text"));d.clear();assertEquals(0,d.consoleTail().length())}
        }finally{dir.deleteRecursively()}
    }
    @Test fun automaticTrafficPolicyAllowsWifiAndManualOverrideButDefersMobileAndUnknown(){
        assertTrue(MobileTrafficPolicy.permitsAutomatic(true,"wifi"));assertTrue(MobileTrafficPolicy.permitsAutomatic(true,"ethernet"));assertFalse(MobileTrafficPolicy.permitsAutomatic(true,"cellular"));assertFalse(MobileTrafficPolicy.permitsAutomatic(true,null));assertTrue(MobileTrafficPolicy.permitsAutomatic(false,"cellular"))
    }
    @Test fun clearAndPolicyChangesAreSerializedWithPendingWrites(){
        val directory=java.nio.file.Files.createTempDirectory("ior-clear-policy-").toFile()
        try{Diagnostics(directory).use{d->repeat(300){d.record("BEFORE_CLEAR")};d.clear();d.configure(5,1);d.record("AFTER_CLEAR");val files=archive(d.export());val rows=files.filterKeys{it.endsWith(".jsonl")}.values.joinToString();assertFalse(rows.contains("BEFORE_CLEAR"));assertTrue(rows.contains("AFTER_CLEAR"));assertTrue(d.size()<=5*1024*1024)}}finally{directory.listFiles()?.forEach{it.delete()};directory.delete()}
    }
    @Test fun journalPrunesAgeAndTotalSizeWithoutTouchingExports(){
        val dir=java.nio.file.Files.createTempDirectory("ior-retention-").toFile()
        try{val files=JournalFiles(dir);files.maxBytes=2048;files.days=1
            val old=java.io.File(dir,"events-old.jsonl");old.writeText("{}\n");old.setLastModified(System.currentTimeMillis()-2*86400000L)
            val export=java.io.File(dir,"saved.zip");export.writeText("user archive")
            repeat(12){files.append(JSONObject().put("utc",java.time.Instant.now().toString()).put("data","x".repeat(400)).toString()+"\n")}
            files.prune(true);assertFalse(old.exists());assertTrue(files.size()<=2048);assertEquals("user archive",export.readText());files.clear();assertEquals(0L,files.size());assertTrue(export.exists())
        }finally{dir.listFiles()?.forEach{it.delete()};dir.delete()}
    }
    @Test fun hysteriaQueueLossIsLinkedToItsSessionWithoutRepeatingOldWarnings(){
        val fields=JSONObject().put("udpSessionId",17).put("rxQueuedDatagrams",4).put("rxQueueCapacity",1024).put("rxQueueHighWater",64).put("receiveQueueDrops",3).put("newReceiveQueueDrops",3)
        val first=DiagnosticPolicy.normalize("HYSTERIA2_UDP_FLOW_METRICS",fields).second
        assertEquals("WARN",first.getString("severity"));assertEquals("LOCAL_UDP_QUEUE_OVERFLOW",first.getString("classification"));assertEquals(17,first.getInt("udpSessionId"));assertEquals(64,first.getInt("rxQueueHighWater"))
        fields.put("newReceiveQueueDrops",0)
        assertEquals("INFO",DiagnosticPolicy.normalize("HYSTERIA2_UDP_FLOW_METRICS",fields).second.getString("severity"))
        assertEquals("WARN",DiagnosticPolicy.normalize("HYSTERIA2_UDP_FLOW_CLOSED",fields).second.getString("severity"))
    }
    @Test fun virtualDnsTlsRefusalIsExpectedButExternalConnectionFailuresRemainWarnings(){
        val local=DiagnosticPolicy.normalize("TUN_VIRTUAL_DNS_TLS_UNAVAILABLE",JSONObject().put("classification","EXPECTED_LOCAL_SERVICE_UNAVAILABLE").put("phase","local_dns_policy").put("destination","172.19.0.2:853")).second
        assertEquals("INFO",local.getString("severity"));assertEquals("local_dns_policy",local.getString("phase"))
        val external=DiagnosticPolicy.normalize("TUN_TCP_FAILED",JSONObject().put("destination","1.1.1.1:853").put("kind","ConnectionRefused")).second
        assertEquals("WARN",external.getString("severity"))
    }
    private fun archive(bytes:ByteArray):Map<String,String>{
        val entries=linkedMapOf<String,String>()
        java.util.zip.ZipInputStream(bytes.inputStream()).use{zip->while(true){val entry=zip.nextEntry ?: break;entries[entry.name]=zip.readBytes().toString(Charsets.UTF_8);zip.closeEntry()}}
        return entries
    }
    @Test fun exportBarrierIncludesQueuedTailAndKeepsLateEventsInTheirSession(){
        val directory=java.nio.file.Files.createTempDirectory("ior-diagnostic-test-").toFile()
        try{Diagnostics(directory).use{diagnostics->
            diagnostics.beginSession("old","amneziawg","packet")
            diagnostics.beginSession("new","hysteria2","quic")
            diagnostics.record("LATE_EVENT",JSONObject().put("sessionId","old").put("protocol","amneziawg").put("eventUnixMs",1000000000000L))
            repeat(1000){diagnostics.record("SAMPLE",JSONObject().put("count",it))}
            diagnostics.record("VIDEO_PLAYBACK_STALL_REPORTED",JSONObject().put("evidence","USER_REPORTED"))
            val files=archive(diagnostics.export());val rows=files.getValue("current.jsonl").lineSequence().filter{it.isNotBlank()}.map{JSONObject(it)}.toList()
            assertEquals("VIDEO_PLAYBACK_STALL_REPORTED",rows.last().getString("code"));assertEquals(1004,rows.size)
            val late=rows.first{it.getString("code")=="LATE_EVENT"}.getJSONObject("fields")
            assertEquals("old",late.getString("sessionId"));assertEquals("amneziawg",late.getString("protocol"));assertFalse(late.has("profileTransport"))
            assertEquals("2001-09-09T01:46:40Z",rows.first{it.getString("code")=="LATE_EVENT"}.getString("utc"))
            assertTrue(files.getValue("README.txt").contains("quality remain UNKNOWN"))
        }}finally{directory.listFiles()?.forEach{assertTrue(it.delete())};assertTrue(directory.delete())}
    }
    @Test fun rotationAndExportAreSerializedAndPreserveLastMarker(){
        val directory=java.nio.file.Files.createTempDirectory("ior-diagnostic-rotation-").toFile()
        try{Diagnostics(directory,1024).use{diagnostics->
            repeat(20){diagnostics.record("SAMPLE",JSONObject().put("count",it));diagnostics.export()}
            diagnostics.record("VIDEO_PLAYBACK_STALL_REPORTED")
            val files=archive(diagnostics.export())
            assertTrue(files.keys.any{it.startsWith("events-")});assertTrue(files.getValue("current.jsonl").contains("VIDEO_PLAYBACK_STALL_REPORTED"))
            for(name in files.keys.filter{it.endsWith(".jsonl")})files.getValue(name).lineSequence().filter{it.isNotBlank()}.forEach{JSONObject(it)}
        }}finally{directory.listFiles()?.forEach{assertTrue(it.delete())};assertTrue(directory.delete())}
    }
    @Test fun negativeDnsAndPolicyBlocksAreNotTransportFailures(){
        for(reason in listOf("DNS_ADDRESS_NODATA","TUN_DNS_ADDRESS_NODATA","DNS_ADDRESS_NXDOMAIN")){
            val(code,fields)=DiagnosticPolicy.normalize("DNS_RESOLVER_FAILED",JSONObject().put("reason",reason))
            assertEquals("DNS_RESOLVER_NEGATIVE_ANSWER",code);assertEquals("INFO",fields.getString("severity"))
        }
        val(code,fields)=DiagnosticPolicy.normalize("TUN_UDP_FAILED",JSONObject().put("reason","VISION_UDP443_BLOCKED"))
        assertEquals("TUN_UDP_POLICY_BLOCKED",code);assertEquals("EXPECTED_POLICY_BLOCK",fields.getString("classification"))
        assertEquals("WARN",DiagnosticPolicy.normalize("DNS_RESOLVER_FAILED",JSONObject().put("reason","TUN_DNS_TIMEOUT")).second.getString("severity"))
    }
    @Test fun remoteBrokenPipeIsKeptAndLocalClosureIsExplained(){
        val local=JSONObject().put("side","application").put("kind","BrokenPipe").put("receivedBytes",2048)
        val(code,fields)=DiagnosticPolicy.normalize("TUN_TCP_IO_FAILED",local)
        assertEquals("TUN_TCP_APPLICATION_CLOSED",code);assertEquals(2048,fields.getInt("receivedBytes"))
        val remote=DiagnosticPolicy.normalize("TUN_TCP_IO_FAILED",JSONObject().put("side","transport").put("kind","BrokenPipe"))
        assertEquals("TUN_TCP_IO_FAILED",remote.first);assertEquals("WARN",remote.second.getString("severity"))
    }
    @Test fun diagnosticMetadataSurvivesForEveryAndroidProtocolAndSecretsAreDropped(){
        for(protocol in listOf("amneziawg","hysteria2","vless_tls","vless_reality")){
            val event=JSONObject().put("sessionId","session-1").put("protocol",protocol).put("destination","[2001:db8::1]:443")
                .put("classification","TRANSPORT_IO_FAILURE").put("phase","outbound_connect").put("destinationName","r.googlevideo.com")
                .put("credential","secret").put("raw","vless://secret@example.test").put("privateKey","secret").put("path","/private?token=secret")
            val safe=DiagnosticPolicy.normalize("TUN_TCP_FAILED",event).second
            assertEquals(protocol,safe.getString("protocol"));assertEquals("session-1",safe.getString("sessionId"))
            assertEquals("[2001:db8::1]:443",safe.getString("destination"));assertEquals("outbound_connect",safe.getString("phase"))
            for(secret in listOf("credential","raw","privateKey","path"))assertFalse(safe.has(secret))
        }
        assertFalse(DiagnosticPolicy.normalize("TEST",JSONObject().put("destination","https://secret@example.test/path")).second.has("destination"))
    }
    @Test fun consoleFilteringDoesNotPromoteNormalDnsToError(){
        val(code,fields)=DiagnosticPolicy.normalize("DNS_RESOLVER_FAILED",JSONObject().put("reason","DNS_ADDRESS_NODATA"))
        assertFalse(DiagnosticPolicy.console(code,fields,false));assertTrue(DiagnosticPolicy.console(code,fields,true))
        val failed=DiagnosticPolicy.normalize("TUN_TCP_FAILED",JSONObject().put("kind","TimedOut"))
        assertTrue(DiagnosticPolicy.console(failed.first,failed.second,false))
    }
    private fun video(elapsed:Long,received:Long,session:String="one")=JSONObject().put("sessionId",session).put("flowId",4).put("destinationName","r.googlevideo.com").put("elapsedMs",elapsed).put("receivedBytes",received).put("sentBytes",100)
    @Test fun videoRatesUseWindowsAndCannotClaimPlaybackStallOrQuality(){
        val sampler=VideoNetworkSampler()
        val first=sampler.observe("RESOURCE_TCP_PROGRESS",video(5000,5000000))!!
        assertEquals(1000000.0,first.getDouble("receiveBytesPerSecond"),0.001)
        val idle=sampler.observe("RESOURCE_TCP_PROGRESS",video(10000,5000000).put("lastReceivedAgoMs",5000))!!
        assertEquals(0.0,idle.getDouble("receiveBytesPerSecond"),0.0);assertEquals("REMOTE_DATA_GAP_OBSERVED",idle.getString("hint"))
        assertEquals("UNKNOWN",idle.getString("playbackState"));assertEquals("UNKNOWN",idle.getString("quality"))
        assertEquals("NETWORK_OBSERVATION_ONLY",idle.getString("evidence"))
        val newSession=sampler.observe("RESOURCE_TCP_PROGRESS",video(5000,5000000,"two"))!!
        assertEquals(1000000.0,newSession.getDouble("receiveBytesPerSecond"),0.001)
    }
    @Test fun videoDoesNotLabelUnknownUdpOrFalseSuffixAsYoutube(){
        val sampler=VideoNetworkSampler()
        for(name in listOf("notgooglevideo.com","googlevideo.com.attacker.test","","youtube.com"))assertNull(sampler.observe("RESOURCE_PACKET_PROGRESS",video(5000,5).put("destinationName",name)))
        assertNull(sampler.observe("TUN_TCP_PENDING",video(5000,5)))
    }
    @Test fun videoDistinguishesLocalDeliveryAndOutboundWaits(){
        val sampler=VideoNetworkSampler()
        val local=sampler.observe("RESOURCE_PACKET_PROGRESS",video(5000,100).put("receiveWaitUs",2000000))!!
        assertEquals("LOCAL_TUN_DELIVERY_WAIT_OBSERVED",local.getString("hint"))
        val outgoing=sampler.observe("RESOURCE_UDP_PROGRESS",video(5000,100).put("sendWaitUs",1500000))!!
        assertEquals("OUTBOUND_BACKPRESSURE_OBSERVED",outgoing.getString("hint"))
        val absent=sampler.observe("RESOURCE_TCP_PROGRESS",video(5000,0).put("nameSource","dns_candidates"))!!
        assertEquals("NO_REMOTE_RESPONSE_OBSERVED",absent.getString("hint"));assertEquals("DNS_CANDIDATE_NETWORK_OBSERVATION",absent.getString("evidence"))
    }
    @Test fun trafficCountersResetWithoutNegativeRates(){
        val sampler=TrafficSampler()
        assertNull(sampler.sample(JSONObject().put("receivedBytes",0),1000))
        assertEquals(1000.0,sampler.sample(JSONObject().put("receivedBytes",5000),6000)!!.getDouble("receiveBytesPerSecond"),0.001)
        assertNull(sampler.sample(JSONObject().put("receivedBytes",1),11000))
        sampler.reset();assertNull(sampler.sample(JSONObject().put("receivedBytes",9000),12000))
    }
}
