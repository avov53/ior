package app.ior.android

import android.content.Context
import android.net.*
import okhttp3.Call
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

object MobileTrafficPolicy {
    class Deferred:java.io.IOException("Ожидает Wi-Fi")
    @Volatile private var context:Context?=null
    private val calls=ConcurrentHashMap.newKeySet<Call>()
    private val worker=Executors.newSingleThreadExecutor()
    @Volatile private var wasAllowed=false
    internal fun permitsAutomatic(saving:Boolean,transport:String?)=!saving||transport in listOf("wifi","ethernet")
    @Synchronized fun initialize(value:Context){
        if(context!=null)return
        context=value.applicationContext
        val manager=value.getSystemService(ConnectivityManager::class.java)
        manager.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),object:ConnectivityManager.NetworkCallback(){
            override fun onCapabilitiesChanged(n:Network,c:NetworkCapabilities){changed()}
            override fun onLost(n:Network){changed()}
        })
        changed()
    }
    fun allowed():Boolean {
        val ctx=context?:return true // pure codec tests have no Android environment
        if(!Repository.get(ctx).snapshot().optBoolean("saveMobileTraffic",false))return true
        val network=PhysicalNetworks.select(ctx)?:return false
        val caps=ctx.getSystemService(ConnectivityManager::class.java).getNetworkCapabilities(network)?:return false
        return permitsAutomatic(true,when{caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)->"wifi";caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)->"ethernet";caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)->"cellular";else->null})
    }
    fun check(automatic:Boolean){if(automatic&&!allowed())throw Deferred()}
    fun track(call:Call){calls.add(call);if(!allowed())call.cancel()}
    fun release(call:Call){calls.remove(call)}
    fun changed(){
        // Cancel from the callback itself: the refresh worker may currently be
        // blocked reading a subscription and cannot process its own cancellation.
        if(!allowed())calls.forEach{it.cancel()}
        worker.execute{
        val available=allowed()
        if(!available)calls.forEach{it.cancel()}
        val newlyAvailable=available&&!wasAllowed;wasAllowed=available
        if(newlyAvailable){val ctx=context?:return@execute;val repo=Repository.get(ctx)
            for(item in repo.subscriptions())if(SubscriptionOptions.enabled(item)&&(item.optBoolean("waitingForWifi")||System.currentTimeMillis()-item.optLong("updatedAt",0)>=SubscriptionOptions.interval(item)*3600000L)){
                if(!allowed())break
                try{SubscriptionClient(repo).refresh(item.getString("id"),true)}catch(_:Exception){}
            }
        }
    }}
}
