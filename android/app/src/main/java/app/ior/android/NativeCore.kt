package app.ior.android

object NativeCore {
    init { System.loadLibrary("ior_android") }
    external fun validate(request: String): String
    external fun validateRoutes(rules: String): String
    external fun start(request: String, tunFd: Int, mtu: Int, callback: IorVpnService): String
    external fun configureLan(settings:String):String
    external fun stop(): String
    external fun stats(): String
    external fun events(): String
    external fun cancelProbes()
    external fun probe(request:String,method:String,url:String,timeoutMs:Int,callback:MainActivity):String
}
