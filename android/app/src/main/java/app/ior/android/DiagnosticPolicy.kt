package app.ior.android

import org.json.JSONObject

/** Metadata only: accepts neither profile bodies, credentials, nor URLs. */
internal object DiagnosticPolicy {
    private val scalarKeys=("eventUnixMs stage flowId sessionId protocol profileTransport classification direction phase severity elapsedMs durationMs sentBytes receivedBytes queueWaitUs sendWaitUs readWaitUs waitCount queryType errorType transport side operation readEof writeClosed kind reason rttEstimateMs udpSessionId rxQueuedDatagrams rxQueueCapacity rxQueueHighWater newReceiveQueueDrops proxyQueueDrops tunQueueDrops queuedDatagrams queuedBytes receiveQueueDrops cwndBytes lostPackets sentPackets count droppedEvents lastReceivedAgoMs lastSentAgoMs afterFirstSendMs receiveWaitUs dnsNameCount ttlSeconds tunDeliveredBytes forwardedPackets receivedPackets droppedPackets rxQueuedBytes txQueuedBytes parentFlowId firstResponseUs active fallback maxPacketBytes mtu icmpType icmpCode rxWindowBytes peerWindowBytes rxZeroWindowUs peerZeroWindowUs applicationWaitUs reassemblyDroppedPackets path target addressFamily nameSource sampleWindowMs receiveBytesPerSecond sendBytesPerSecond hint evidence appVersion versionCode physicalTransport validated metered linkDownstreamKbps linkUpstreamKbps playbackState quality").split(' ').toSet()
    private val token=Regex("[A-Za-z0-9_-]{1,80}")
    private val domain=Regex("[A-Za-z0-9._-]{1,253}")
    private val endpoint=Regex("(?:[0-9]{1,3}\\.){3}[0-9]{1,3}:[0-9]{1,5}|\\[[0-9a-fA-F:%]+\\]:[0-9]{1,5}")
    fun normalize(originalCode:String, fields:JSONObject):Pair<String,JSONObject> {
        val safe=JSONObject()
        for(key in fields.keys()){
            val value=fields.opt(key)
            when {
                key in scalarKeys && (value is Boolean || value is Number && value.toDouble().isFinite() || value is String && token.matches(value)) -> safe.put(key,value)
                key in listOf("destinationName","host","domain") && value is String && domain.matches(value) -> safe.put(key,value)
                key in listOf("source","destination","resolver") && value is String && endpoint.matches(value) -> safe.put(key,value)
                key=="appVersion" && value is String && Regex("[0-9.]{1,24}").matches(value) -> safe.put(key,value)
            }
        }
        var code=originalCode.takeIf{token.matches(it)} ?: "INVALID_DIAGNOSTIC_CODE"
        val reason=safe.optString("reason")
        val localClose=safe.optString("side")=="application" && safe.optString("kind") in listOf("BrokenPipe","ConnectionReset","ConnectionAborted")
        when {
            code in setOf("HYSTERIA2_UDP_FLOW_METRICS","HYSTERIA2_UDP_FLOW_CLOSED") && (safe.optLong("newReceiveQueueDrops")>0 || code=="HYSTERIA2_UDP_FLOW_CLOSED" && safe.optLong("receiveQueueDrops")>0) -> safe.put("classification","LOCAL_UDP_QUEUE_OVERFLOW").put("severity","WARN")
            reason=="VISION_UDP443_BLOCKED" -> {code="TUN_UDP_POLICY_BLOCKED";safe.put("classification","EXPECTED_POLICY_BLOCK").put("severity","INFO")}
            code=="DNS_RESOLVER_FAILED" && reason in listOf("DNS_ADDRESS_NODATA","TUN_DNS_ADDRESS_NODATA","DNS_ADDRESS_NXDOMAIN") || code=="DNS_RESOLVER_NEGATIVE_ANSWER" -> {code="DNS_RESOLVER_NEGATIVE_ANSWER";safe.put("classification","DNS_NEGATIVE_ANSWER").put("severity","INFO")}
            localClose && code in listOf("TUN_TCP_IO_FAILED","RESOURCE_TCP_ERROR","TUN_TCP_APPLICATION_CLOSED") -> {code=if(code.startsWith("RESOURCE_"))"RESOURCE_TCP_APPLICATION_CLOSED"else"TUN_TCP_APPLICATION_CLOSED";safe.put("classification","APPLICATION_CONNECTION_CLOSED").put("severity","INFO")}
            code=="TUN_FLOW_CANCELLED" || safe.optString("kind")=="Interrupted" -> safe.put("classification","CANCELLED").put("severity","INFO")
            code=="TUN_TCP_CLOSED" -> safe.put("classification","CONNECTION_CLOSED").put("severity","INFO")
            code=="VPN_ERROR" -> safe.put("severity","ERROR")
            code.contains("FAILED") || code.contains("TIMEOUT") || code.contains("DROPPED") || code.endsWith("_ERROR") -> safe.put("severity","WARN")
            else -> safe.put("severity","INFO")
        }
        return code to safe
    }
    fun console(code:String, fields:JSONObject, debug:Boolean):Boolean = debug ||
        fields.optString("severity") in listOf("WARN","ERROR") ||
        code in setOf("ANDROID_DIAGNOSTIC_SESSION_STARTED","VPN_CONNECTED","VPN_STOPPED","VIDEO_PLAYBACK_STALL_REPORTED") ||
        code.contains("HANDSHAKE") || code.contains("HTTPS_PROBE")
}

/** Observed encrypted traffic is not a player buffer or a 4K bandwidth requirement. */
internal class VideoNetworkSampler {
    private data class Sample(val elapsed:Long,val received:Long,val sent:Long,val writeWait:Long,val deliveryWait:Long)
    private val previous=LinkedHashMap<String,Sample>()
    fun observe(code:String,event:JSONObject):JSONObject? {
        if(code !in setOf("RESOURCE_TCP_PROGRESS","RESOURCE_UDP_PROGRESS","RESOURCE_PACKET_PROGRESS"))return null
        val host=event.optString("destinationName").lowercase()
        if(host!="googlevideo.com" && !host.endsWith(".googlevideo.com"))return null
        if(!event.has("flowId") || !event.has("elapsedMs"))return null
        val key=event.optString("sessionId")+":"+event.optLong("flowId")+":"+code
        val current=Sample(event.optLong("elapsedMs"),event.optLong("receivedBytes"),event.optLong("sentBytes"),event.optLong("sendWaitUs"),event.optLong("receiveWaitUs"))
        val old=previous.put(key,current)
        if(previous.size>512)previous.remove(previous.keys.first())
        val baseline=old ?: Sample(0,0,0,0,0)
        val window=current.elapsed-baseline.elapsed
        if(window<1000 || current.received<baseline.received || current.sent<baseline.sent)return null
        val hint=when {
            current.deliveryWait-baseline.deliveryWait>=1000000 -> "LOCAL_TUN_DELIVERY_WAIT_OBSERVED"
            current.writeWait-baseline.writeWait>=1000000 -> "OUTBOUND_BACKPRESSURE_OBSERVED"
            current.received==0L && current.sent>0L && current.elapsed>=5000 -> "NO_REMOTE_RESPONSE_OBSERVED"
            event.has("lastReceivedAgoMs") && event.optLong("lastReceivedAgoMs")>=5000 -> "REMOTE_DATA_GAP_OBSERVED"
            else -> "NO_SPECIFIC_NETWORK_CAUSE"
        }
        return JSONObject(event.toString()).put("sampleWindowMs",window).put("receiveBytesPerSecond",(current.received-baseline.received)*1000.0/window)
            .put("sendBytesPerSecond",(current.sent-baseline.sent)*1000.0/window).put("hint",hint).put("evidence",if(event.optString("nameSource")=="dns_candidates")"DNS_CANDIDATE_NETWORK_OBSERVATION"else"NETWORK_OBSERVATION_ONLY")
            .put("playbackState","UNKNOWN").put("quality","UNKNOWN")
    }
    fun reset(){previous.clear()}
}

internal class TrafficSampler {
    private var lastTime:Long?=null
    private var sent=0L
    private var received=0L
    fun reset(){lastTime=null;sent=0;received=0}
    fun sample(stats:JSONObject,now:Long):JSONObject? {
        val start=lastTime
        val nextSent=stats.optLong("sentBytes");val nextReceived=stats.optLong("receivedBytes")
        val delta=if(start==null)0 else now-start
        val result=if(delta>=1000 && nextSent>=sent && nextReceived>=received)JSONObject().put("sampleWindowMs",delta)
            .put("sentBytes",nextSent).put("receivedBytes",nextReceived).put("sendBytesPerSecond",(nextSent-sent)*1000.0/delta).put("receiveBytesPerSecond",(nextReceived-received)*1000.0/delta)else null
        lastTime=now;sent=nextSent;received=nextReceived
        return result
    }
}
