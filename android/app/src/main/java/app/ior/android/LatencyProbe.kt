package app.ior.android
import android.content.Context
import android.net.ConnectivityManager
import android.net.DnsResolver
import android.net.Network
import android.os.CancellationSignal
import org.json.JSONObject
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

object LatencyProbe {
 private val callbacks=Executors.newSingleThreadExecutor()
 private fun addresses(network:Network,host:String,timeout:Int):List<InetAddress>{
  if(host.contains(':')||host.matches(Regex("[0-9]+(\\.[0-9]+){3}")))return listOf(InetAddress.getByName(host))
  val done=CountDownLatch(1);var result:List<InetAddress> = emptyList();val cancellation=CancellationSignal()
  try{
   DnsResolver.getInstance().query(network,host,0,callbacks,cancellation,object:DnsResolver.Callback<List<InetAddress>>{
    override fun onAnswer(answer:List<InetAddress>,rcode:Int){if(rcode==0)result=answer;done.countDown()}
    override fun onError(error:DnsResolver.DnsException){done.countDown()}
   })
   if(!done.await(timeout.toLong(),TimeUnit.MILLISECONDS))return emptyList()
   return result
  }finally{cancellation.cancel()}
 }
 fun measure(context:Context,server:JSONObject,mode:String,timeoutMs:Int,network:Network):PingResult {
  val deadline=System.nanoTime()/1000000+timeoutMs
  fun remaining()=maxOf(0,(deadline-System.nanoTime()/1000000).toInt())
  val profile=server.getJSONObject("wire")
  val addresses=addresses(network,profile.getString("server"),remaining())
  if(addresses.isEmpty())return PingResult(null,mode,"PING_DNS_FAILED")
  if(mode=="tcp"){
   for(address in addresses){val budget=remaining();if(budget<=0)break
    try{val start=System.nanoTime();network.socketFactory.createSocket().use{it.connect(InetSocketAddress(address,profile.getInt("port")),budget)};return PingResult((System.nanoTime()-start)/1000000,"tcp")}catch(_:java.io.IOException){}
   };return PingResult(null,"tcp","PING_TCP_UNAVAILABLE")
  }
  val iface=context.getSystemService(ConnectivityManager::class.java).getLinkProperties(network)?.interfaceName?:return PingResult(null,"icmp","PING_NETWORK_UNAVAILABLE")
  for(address in addresses){val budget=remaining();if(budget<=0)break
   val program=if(address.address.size==16)"/system/bin/ping6"else"/system/bin/ping"
   val process=try{ProcessBuilder(program,"-n","-c","1","-W",maxOf(1,(budget+999)/1000).toString(),"-I",iface,address.hostAddress!!).redirectErrorStream(true).start()}catch(_:java.io.IOException){return PingResult(null,"icmp","PING_ICMP_UNAVAILABLE")}
   try{if(process.waitFor(budget.toLong(),TimeUnit.MILLISECONDS)&&process.exitValue()==0){val ms=Regex("time[=<]([0-9.]+)").find(process.inputStream.bufferedReader().readText().take(4096))?.groupValues?.get(1)?.toDoubleOrNull()?.toLong();if(ms!=null)return PingResult(ms,"icmp")}}finally{process.destroyForcibly()}
  }
  return PingResult(null,"icmp","PING_ICMP_UNAVAILABLE")
 }
}
