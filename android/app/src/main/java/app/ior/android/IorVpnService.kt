package app.ior.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import androidx.core.app.NotificationCompat
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

class IorVpnService:VpnService() {
    private val worker=Executors.newSingleThreadScheduledExecutor()
    private val diagnosticWorker=Executors.newSingleThreadScheduledExecutor()
    private val generation=AtomicLong()
    private lateinit var repo:Repository
    private lateinit var connectivity:ConnectivityManager
    @Volatile private var network:Network?=null
    @Volatile private var tun:ParcelFileDescriptor?=null
    @Volatile private var wanted=false
    @Volatile private var linkSignature=""
    private var activeServer:JSONObject?=null
    private var activeRequest:JSONObject?=null
    private var activeRouting:JSONObject?=null
    private val videoSamples=VideoNetworkSampler()
    private val trafficSamples=TrafficSampler()
    private var lastTrafficSample=0L
    companion object {
        const val STOP="app.ior.android.STOP"
        @Volatile internal var currentPhysicalNetwork:Network?=null
        @Volatile private var diagnosticService:IorVpnService?=null
        fun flushDiagnostics(){diagnosticService?.drainEvents()}
        fun systemProtection():JSONObject=JSONObject().put("alwaysOn",diagnosticService?.isAlwaysOn==true).put("lockdown",diagnosticService?.isLockdownEnabled==true)
    }
    private val callback=object:ConnectivityManager.NetworkCallback(){
        override fun onAvailable(n:Network){if(wanted && network==null) reconnect(n)}
        override fun onLost(n:Network){if(n==network){Diagnostics.get(this@IorVpnService).record("ANDROID_PHYSICAL_NETWORK_LOST",networkFields());network=null;currentPhysicalNetwork=null;if(wanted){generation.incrementAndGet();repo.status("reconnecting","Ожидание сети",activeServer?.optString("id").orEmpty());setUnderlyingNetworks(emptyArray());NativeCore.stop();getSystemService(NotificationManager::class.java).notify(1,notification("Ожидание сети",activeServer?.optString("name").orEmpty()));PhysicalNetworks.select(this@IorVpnService)?.let{reconnect(it)}}}}
        override fun onLinkPropertiesChanged(n:Network,properties:android.net.LinkProperties){val signature=properties.linkAddresses.map{it.toString()}.sorted().joinToString();if(wanted&&n==network&&linkSignature.isNotBlank()&&signature!=linkSignature)reconnect(n)}
        override fun onCapabilitiesChanged(n:Network,c:NetworkCapabilities){if(wanted && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)&&c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)&&n!=network)reconnect(n)}
    }
    private var lanSignature=""
    private fun updateLan(){try{val config=LanInterfaces.config(repo.snapshot().getJSONObject("lan"));val signature=config.toString();if(signature!=lanSignature){lanSignature=signature;val result=JSONObject(NativeCore.configureLan(signature));repo.lanStatus=if(result.optBoolean("ok")){if(config.optBoolean("enabled"))config.getString("address")+":"+config.getInt("port")else"Отключён"}else result.optString("code","LAN_CONFIGURATION_FAILED");repo.event("lan",JSONObject().put("status",repo.lanStatus))}}catch(_:Exception){lanSignature="";try{NativeCore.configureLan("{\"enabled\":false,\"address\":\"192.168.0.1\",\"prefix\":24,\"port\":2080,\"auth\":null}")}catch(_:Exception){};repo.lanStatus="LAN_NETWORK_UNAVAILABLE";repo.event("lan",JSONObject().put("status",repo.lanStatus))}}
    override fun onCreate(){
        super.onCreate();repo=Repository.get(this);connectivity=getSystemService(ConnectivityManager::class.java);diagnosticService=this
        MobileTrafficPolicy.initialize(this)
        worker.execute{val settings=repo.snapshot();Diagnostics.get(this).configure(settings.optInt("journalMaxMb",20),settings.optInt("journalDays",7))}
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("vpn","VPN ior",NotificationManager.IMPORTANCE_LOW))
        connectivity.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),callback)
        diagnosticWorker.scheduleAtFixedRate({drainEvents()},0,1,TimeUnit.SECONDS)
        worker.scheduleAtFixedRate({if(repo.connection=="on")try{val stats=JSONObject(NativeCore.stats());if(!stats.optBoolean("running")){if(wanted)network?.let{reconnect(it)}}else{repo.progress(stats);updateLan()}}catch(_:Exception){if(wanted)network?.let{reconnect(it)}}},1,1,TimeUnit.SECONDS)
    }
    private var previousRouting:JSONObject?=null
    private var previousRequest:JSONObject?=null
    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int {
        if(intent?.action==STOP){stopConnection();return START_NOT_STICKY}
        if(intent==null&&!repo.snapshot().optBoolean("vpnWanted")&&!isAlwaysOn)return START_NOT_STICKY
        if(!wanted)repo.routeForServer()
        val server=repo.selectedServer() ?: run{stopSelf();return START_NOT_STICKY}
        val request=try{repo.request(server)}catch(_:Exception){repo.log("ANDROID_ROUTE_NOT_SUPPORTED");if(!wanted){repo.status("off","ANDROID_ROUTE_NOT_SUPPORTED","");stopSelf()};return START_NOT_STICKY}
        startForeground(1,notification("Подключение",server.optString("name")))
        if(wanted){if(intent?.action in listOf("app.ior.android.RECONNECT","app.ior.android.ROUTING")){if(intent?.action=="app.ior.android.ROUTING"){previousRouting=JSONObject(intent.getStringExtra("previousRouting")!!);previousRequest=activeRequest};activeServer=server;activeRequest=request;activeRouting=repo.snapshot().optJSONObject("appRouting");worker.execute{NativeCore.stop();tun?.close();tun=null;network?.let{reconnect(it)}}};return START_STICKY}
        activeServer=server;activeRequest=request;activeRouting=repo.snapshot().optJSONObject("appRouting");wanted=true
        repo.setVpnWanted(true)
        val physical=connectivity.allNetworks.firstOrNull{n->connectivity.getNetworkCapabilities(n)?.let{it.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)&&it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)}==true}
        if(physical==null){repo.status("reconnecting","Ожидание сети",server.getString("id"));worker.execute{try{ensureTun(null)}catch(error:Exception){stopConnection(safeError(error))}}}else reconnect(physical)
        return START_STICKY
    }
    private fun reconnect(physical:Network){
        if(!wanted)return
        val previous=network
        // Prefer Wi-Fi over cellular while both remain available.
        if(previous!=null && connectivity.getNetworkCapabilities(previous)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)==true && connectivity.getNetworkCapabilities(physical)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)!=true)return
        network=physical
        currentPhysicalNetwork=physical
        MobileTrafficPolicy.changed()
        linkSignature=connectivity.getLinkProperties(physical)?.linkAddresses?.map{it.toString()}?.sorted()?.joinToString().orEmpty()
        val revision=generation.incrementAndGet()
        Diagnostics.get(this).record("ANDROID_NETWORK_CHANGED");repo.log("ANDROID_NETWORK_CHANGED")
        repo.status(if(tun==null)"pending" else "reconnecting",id=activeServer?.optString("id").orEmpty())
        worker.execute {
            if(!wanted||revision!=generation.get())return@execute
            try {
                ensureTun(physical)
                NativeCore.stop();drainEvents()
                val server=activeServer ?: error("Нет сервера")
                val request=JSONObject(activeRequest!!.toString()).put("requestId",java.util.UUID.randomUUID().toString())
                val profile=request.getJSONObject("profile")
                val protocol=profile.getString("protocol")
                val transport=profile.getJSONObject("options").optString("transport").ifBlank{when(protocol){"amneziawg"->"packet";"hysteria2"->"quic";else->"tcp"}}
                synchronized(this){videoSamples.reset();trafficSamples.reset();lastTrafficSample=0
                    Diagnostics.get(this).beginSession(request.getString("requestId"),protocol,transport)}
                Diagnostics.get(this).record("ANDROID_PHYSICAL_NETWORK",networkFields())
                val mtu=server.getJSONObject("wire").getJSONObject("options").optInt("mtu",1500)
                val descriptor=ensureTun(physical)
                if(!wanted||revision!=generation.get())return@execute
                val result=JSONObject(NativeCore.start(request.toString(),descriptor.fd,mtu,this))
                drainEvents()
                if(!wanted||revision!=generation.get()){NativeCore.stop();return@execute}
                require(result.optBoolean("ok")){result.optString("code","ANDROID_CORE_START_FAILED")}
                previousRouting=null;previousRequest=null;lanSignature="";repo.status("on",id=server.getString("id"));updateLan();repo.log("VPN_CONNECTED · "+server.optString("protocol"));Diagnostics.get(this).record("VPN_CONNECTED")
                getSystemService(NotificationManager::class.java).notify(1,notification("Подключено",server.getString("name")))
            } catch(error:Exception){
                if(wanted&&revision==generation.get()){
                    previousRouting?.let{repo.restoreRouting(it);activeRequest=previousRequest;previousRouting=null;previousRequest=null;repo.log("ROUTING_APPLY_FAILED_PREVIOUS_RESTORED")}
                    repo.status("reconnecting",safeError(error),activeServer?.optString("id").orEmpty())
                    Diagnostics.get(this).record("ANDROID_VPN_RETRY",JSONObject().put("reason","TRANSPORT_UNAVAILABLE"))
                    worker.schedule({if(wanted&&revision==generation.get())network?.let{reconnect(it)}},5,TimeUnit.SECONDS)
                }
            }
        }
    }
    @Synchronized private fun ensureTun(physical:Network?):ParcelFileDescriptor {
        check(wanted)
        tun?.let{setUnderlyingNetworks(physical?.let{arrayOf(it)}?:emptyArray());return it}
        val server=activeServer?:error("Нет сервера")
        val mtu=server.getJSONObject("wire").getJSONObject("options").optInt("mtu",1500)
        val builder=Builder().setSession("ior · "+server.getString("name")).setMtu(mtu)
                    .addAddress("172.19.0.1",30).addAddress("fd19::1",126)
                    .addRoute("0.0.0.0",0).addRoute("::",0).addDnsServer("172.19.0.2")
                    .setBlocking(false).setMetered(connectivity.getNetworkCapabilities(physical)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)!=true)
                builder.setUnderlyingNetworks(physical?.let{arrayOf(it)}?:emptyArray())
                val routing=activeRouting ?: JSONObject()
                val ids=routing.optJSONArray("ids")
                if(routing.optBoolean("enabled") && ids!=null && ids.length()>0){
                    var applied=0
                    for(i in 0 until ids.length())try {
                        val id=ids.getString(i)
                        if(id==packageName)continue
                        if(routing.optString("mode")=="direct")builder.addDisallowedApplication(id)else builder.addAllowedApplication(id)
                        applied++
                    }catch(_:android.content.pm.PackageManager.NameNotFoundException){}
                    require(applied>0 || routing.optString("mode")=="direct"){"Выбранные приложения не установлены"}
                }
                val descriptor=builder.establish() ?: error("VPN-разрешение отозвано")
                tun=descriptor
                Diagnostics.get(this).record("ANDROID_TUN_CREATED");repo.log("ANDROID_TUN_CREATED")
                return descriptor
    }
    // Called by native Rust worker threads before any external connect/send.
    fun protectLanSocket(fd:Int):Boolean = wanted&&protect(fd)
    fun protectSocket(fd:Int):Boolean {
        if(!wanted)return false
        val physical=network ?: return false
        if(!protect(fd))return false
        return try{ParcelFileDescriptor.fromFd(fd).use{physical.bindSocket(it.fileDescriptor)};true}catch(_:Exception){false}
    }
    private fun notification(status:String,name:String):Notification {
        val open=PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop=PendingIntent.getService(this,1,Intent(this,IorVpnService::class.java).setAction(STOP),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return NotificationCompat.Builder(this,"vpn").setSmallIcon(android.R.drawable.ic_lock_lock).setContentTitle("ior · "+Localization.translate(this,repo.snapshot(),status)).setContentText(name)
            .setContentIntent(open).setOngoing(true).addAction(0,Localization.translate(this,repo.snapshot(),"Отключить"),stop).build()
    }
    private fun stopConnection(message:String=""){
        Diagnostics.get(this).record(if(message.isBlank())"VPN_STOPPED"else"VPN_ERROR",JSONObject().put("reason",message.takeIf{it.matches(Regex("[A-Za-z0-9_-]{1,80}"))} ?: if(message.isBlank())"USER_STOP"else"ANDROID_VPN_FAILED"))
        wanted=false;val stoppedGeneration=generation.incrementAndGet();tun?.close();tun=null
        repo.setVpnWanted(false)
        currentPhysicalNetwork=null
        repo.status("off",message,"");repo.log(if(message.isBlank())"VPN_STOPPED" else "VPN_ERROR · $message")
        Thread({try{NativeCore.stop()}finally{android.os.Handler(mainLooper).post{if(!wanted&&generation.get()==stoppedGeneration){stopForeground(STOP_FOREGROUND_REMOVE);stopSelf()}}}},"ior-stop").start()
    }
    override fun onRevoke(){stopConnection("VPN_PERMISSION_REVOKED");super.onRevoke()}
    private fun networkFields():JSONObject {
        val capabilities=network?.let{connectivity.getNetworkCapabilities(it)} ?: return JSONObject().put("physicalTransport","unavailable")
        return JSONObject().put("physicalTransport",when{capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)->"wifi";capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)->"cellular";capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)->"ethernet";else->"other"})
            .put("validated",capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)).put("metered",!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
            .put("linkDownstreamKbps",capabilities.linkDownstreamBandwidthKbps).put("linkUpstreamKbps",capabilities.linkUpstreamBandwidthKbps)
    }
    @Synchronized private fun drainEvents(){try{
        val diagnostics=Diagnostics.get(this)
        val events=org.json.JSONArray(NativeCore.events());val debug=repo.debugLogging()
        for(i in 0 until events.length()){
            val event=events.getJSONObject(i);val(code,safe)=DiagnosticPolicy.normalize(event.optString("code"),event)
            // File evidence is independent of the screen's INFO/DEBUG filter.
            diagnostics.record(code,safe)
            videoSamples.observe(code,event)?.let{diagnostics.record("VIDEO_NETWORK_SAMPLE",it)}
            if(DiagnosticPolicy.console(code,safe,debug))repo.log("["+safe.optString("severity")+"] "+code+listOf("sessionId","flowId","phase","destination","destinationName","durationMs","reason","classification").filter{safe.has(it)}.joinToString("",transform={" · "+it+"="+safe.opt(it)}))
        }
        val now=android.os.SystemClock.elapsedRealtime()
        if(repo.connection=="on" && now-lastTrafficSample>=5000){
            lastTrafficSample=now
            val stats=JSONObject(NativeCore.stats())
            if(stats.optBoolean("running"))trafficSamples.sample(stats,now)?.let{sample->val physical=networkFields();for(key in physical.keys())sample.put(key,physical.opt(key));diagnostics.record("VPN_TRAFFIC_SAMPLE",sample)}
        }
    }catch(error:Exception){Diagnostics.get(this).record("ANDROID_DIAGNOSTIC_DRAIN_FAILED",JSONObject().put("reason",error.javaClass.simpleName))}}
    override fun onDestroy(){if(diagnosticService===this)diagnosticService=null;wanted=false;currentPhysicalNetwork=null;generation.incrementAndGet();if(repo.connection!="off")repo.status("off","VPN_SERVICE_STOPPED","");try{connectivity.unregisterNetworkCallback(callback)}catch(_:Exception){};tun?.close();tun=null;worker.shutdownNow();diagnosticWorker.shutdownNow();NativeCore.stop();super.onDestroy()}
    private fun safeError(error:Exception):String=error.message?.takeIf{it.length<=100 && !it.contains("://") && !it.contains("PrivateKey")} ?: "ANDROID_VPN_FAILED"
}
