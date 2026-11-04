package app.ior.android
import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
object PhysicalNetworks {
 fun select(context:Context):Network? {
  val manager=context.getSystemService(ConnectivityManager::class.java)
  fun usable(n:Network)=manager.getNetworkCapabilities(n)?.let{it.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)&&it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)}==true&&manager.getLinkProperties(n)!=null
  val candidates=manager.allNetworks.filter(::usable)
  IorVpnService.currentPhysicalNetwork?.let{if(usable(it))return it}
  val active=manager.activeNetwork
  if(active!=null&&usable(active))return active
  val validated=candidates.filter{manager.getNetworkCapabilities(it)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)==true}
  return validated.firstOrNull{manager.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)==true}?:validated.firstOrNull()?:candidates.singleOrNull()
 }
 fun signature(context:Context):String {
  val network=select(context)?:return "none"
  val links=context.getSystemService(ConnectivityManager::class.java).getLinkProperties(network)?:return "none"
  return network.networkHandle.toString()+"|"+links.interfaceName+"|"+links.linkAddresses.map{it.toString()}.sorted().joinToString()
 }
}
