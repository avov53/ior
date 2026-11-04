package app.ior.android
data class PingResult(val milliseconds:Long?,val method:String,val reason:String="")
object PingMeasurement {
 fun run(protocol:String,mode:String,timeout:Int,allowAwgHttps:Boolean,
         physical:(String,Int)->PingResult,https:(Int)->PingResult,now:()->Long={System.nanoTime()/1000000}):PingResult {
  val started=now()
  if(mode in listOf("head","get"))return https(timeout)
  val first=if(mode=="auto")if(protocol in listOf("vless_reality","vless_tls"))"tcp"else"icmp"else mode
  val result=physical(first,if(mode=="auto")minOf(1000,timeout)else timeout)
  if(result.milliseconds!=null||mode!="auto"||Thread.currentThread().isInterrupted)return result
  if(protocol=="amneziawg"&&!allowAwgHttps)return PingResult(null,first,"AWG_ACTIVE_TRANSPORT_REQUIRED")
  val remaining=(timeout-(now()-started)).toInt()
  if(remaining<100)return PingResult(null,first,"PING_TIMEOUT")
  return https(remaining)
 }
}
