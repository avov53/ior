package app.ior.android

import android.Manifest
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.widget.EditText
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewClientCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executors

class MainActivity:ComponentActivity() {

    private val startupEpoch=java.util.concurrent.atomic.AtomicLong()
    private lateinit var web:WebView
    private lateinit var repo:Repository
    private val worker=Executors.newSingleThreadExecutor()
    private val startupWorker=Executors.newSingleThreadExecutor()
    private val measuring=java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    private val probeGeneration=java.util.concurrent.atomic.AtomicLong()
    private var pingNetworkSignature=""
    private val connectivity by lazy{getSystemService(android.net.ConnectivityManager::class.java)}
    private val pingNetworkCallback=object:android.net.ConnectivityManager.NetworkCallback(){
        override fun onAvailable(network:android.net.Network){runOnUiThread{refreshPingNetwork()}}
        override fun onLost(network:android.net.Network){runOnUiThread{refreshPingNetwork()}}
        override fun onCapabilitiesChanged(network:android.net.Network,capabilities:android.net.NetworkCapabilities){runOnUiThread{refreshPingNetwork()}}
        override fun onLinkPropertiesChanged(network:android.net.Network,properties:android.net.LinkProperties){runOnUiThread{refreshPingNetwork()}}
    }
    private var appCatalogCache:JSONArray?=null
    private val packagesChanged=object:android.content.BroadcastReceiver(){override fun onReceive(context:android.content.Context,intent:Intent){appCatalogCache=null;send("apps-invalidated",JSONObject())}}
    private var probes=Executors.newFixedThreadPool(2)
    private val probeSlots=java.util.concurrent.Semaphore(2,true)
    private var pageReady=false
    @Volatile private var uiVisible=false
    private var pendingImport:String?=null
    private var pendingBackup:ByteArray?=null
    private var pendingDiagnostics:ByteArray?=null
    private val diagnosticsFile=registerForActivityResult(ActivityResultContracts.CreateDocument("application/zip")){uri->val bytes=pendingDiagnostics;pendingDiagnostics=null;if(uri!=null&&bytes!=null)worker.execute{try{contentResolver.openOutputStream(uri,"wt")!!.use{it.write(bytes)};message("Файл сохранён")}catch(_:Exception){message("Не удалось сохранить файл")}}}
    private val observe:()->Unit={if(pageReady&&uiVisible)send("state",repo.snapshot())}
    private val observeEvent:(String,JSONObject)->Unit={kind,data->send(kind,data)}
    private val vpnPermission=registerForActivityResult(ActivityResultContracts.StartActivityForResult()){if(it.resultCode==RESULT_OK)startVpn()else repo.status("off","VPN-разрешение отклонено")}
    private val notificationPermission=registerForActivityResult(ActivityResultContracts.RequestPermission()){}
    private val scanner=registerForActivityResult(ActivityResultContracts.StartActivityForResult()){if(it.resultCode==RESULT_OK)it.data?.getStringExtra("text")?.let{text->send("import",text)}}
    private val backupFile=registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")){uri->val bytes=pendingBackup;pendingBackup=null;if(uri!=null&&bytes!=null)worker.execute{try{contentResolver.openOutputStream(uri,"wt")!!.use{it.write(bytes)};message("Файл сохранён")}catch(_:Exception){message("Не удалось сохранить файл")}}}
    private val restoreFile=registerForActivityResult(ActivityResultContracts.OpenDocument()){uri->if(uri!=null)worker.execute{try{val bytes=contentResolver.openInputStream(uri)!!.use{it.readNBytes(8*1024*1024+1)};val data=JSONObject(String(bytes,Charsets.UTF_8));runOnUiThread{if(data.optBoolean("includesSecrets"))password("Пароль резервной копии"){pass->restore(bytes,pass)}else restore(bytes,null)}}catch(_:Exception){message("Не удалось открыть резервную копию")}}}
    override fun onCreate(savedInstanceState:Bundle?){
        super.onCreate(savedInstanceState)
        androidx.core.view.WindowCompat.setDecorFitsSystemWindows(window,false)
        try{repo=Repository.get(this);NativeCore.stats()}catch(_:Throwable){setContentView(TextView(this).apply{text="Не удалось открыть защищённое хранилище или загрузить ior-core. Данные не заменены."});return}
        web=WebView(this)
        MobileTrafficPolicy.initialize(this)
        worker.execute{val settings=repo.snapshot();Diagnostics.get(this).configure(settings.optInt("journalMaxMb",20),settings.optInt("journalDays",7))}
        val container=android.widget.FrameLayout(this)
        container.addView(web,android.widget.FrameLayout.LayoutParams(-1,-1));setContentView(container)
        web.settings.javaScriptEnabled=true;web.settings.domStorageEnabled=false;web.settings.allowFileAccess=false;web.settings.allowContentAccess=false
        web.settings.mixedContentMode=android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)
        val loader=WebViewAssetLoader.Builder().addPathHandler("/assets/",WebViewAssetLoader.AssetsPathHandler(this)).build()
        web.webViewClient=object:WebViewClientCompat(){
            override fun shouldInterceptRequest(view:WebView,request:WebResourceRequest):WebResourceResponse?=loader.shouldInterceptRequest(request.url) ?: WebResourceResponse("text/plain","UTF-8",403,"Forbidden",emptyMap(),java.io.ByteArrayInputStream(ByteArray(0)))
            override fun shouldOverrideUrlLoading(view:WebView,request:WebResourceRequest):Boolean{if(request.url.host=="appassets.androidplatform.net")return false;if(request.isForMainFrame&&request.url.scheme=="https")try{startActivity(Intent(Intent.ACTION_VIEW,request.url))}catch(_:Exception){};return true}
            override fun onPageFinished(view:WebView,url:String){if(url==LOCAL_PAGE){pageReady=true;send("state",repo.snapshot());pendingImport?.let{send("import",it);pendingImport=null}}}
        }
        web.webChromeClient=object:WebChromeClient(){
            override fun onJsConfirm(view:WebView,url:String,message:String,result:android.webkit.JsResult):Boolean{AlertDialog.Builder(this@MainActivity).setMessage(message.take(500)).setPositiveButton("Да"){_,_->result.confirm()}.setNegativeButton("Отмена"){_,_->result.cancel()}.setOnCancelListener{result.cancel()}.localizedShow();return true}
            override fun onShowFileChooser(view:WebView,callback:android.webkit.ValueCallback<Array<Uri>>,params:FileChooserParams):Boolean{fileCallback?.onReceiveValue(null);fileCallback=callback;importFile.launch(arrayOf("text/*","application/octet-stream","application/json"));return true}
        }
        if(!WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)){setContentView(TextView(this).apply{text="Обновите Android System WebView для работы ior."});return}
        WebViewCompat.addWebMessageListener(web,"iorNative",setOf("https://appassets.androidplatform.net")){_,webMessage,origin,isMainFrame,_->
            if(isMainFrame&&origin.scheme=="https"&&origin.host=="appassets.androidplatform.net"&&origin.port in listOf(-1,443))try{
                val raw=webMessage.data ?: return@addWebMessageListener
                require(raw.length<=1024*1024)
                val command=JSONObject(raw);require(command.optInt("version")==1)
                dispatch(command.getString("action"),command.optJSONObject("data") ?: JSONObject())
            }catch(_:Exception){message("Неверная команда интерфейса")}
        }
        ViewCompat.setOnApplyWindowInsetsListener(container){_,insets->
            val bars=insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val ime=insets.getInsets(WindowInsetsCompat.Type.ime())
            val layout=web.layoutParams as android.widget.FrameLayout.LayoutParams
            layout.setMargins(bars.left,bars.top,bars.right,maxOf(bars.bottom,ime.bottom));web.layoutParams=layout
            send("insets",JSONObject().put("keyboard",insets.isVisible(WindowInsetsCompat.Type.ime())))
            WindowInsetsCompat.CONSUMED
        }
        onBackPressedDispatcher.addCallback(this,object:OnBackPressedCallback(true){override fun handleOnBackPressed(){web.evaluateJavascript("window.iorAndroidBack?.()===true"){if(it!="true"){isEnabled=false;onBackPressedDispatcher.onBackPressed();isEnabled=true}}}})
        repo.observe(observe);repo.observeEvents(observeEvent);receiveIntent(intent);web.loadUrl(LOCAL_PAGE)
        ContextCompat.registerReceiver(this,packagesChanged,android.content.IntentFilter().apply{addAction(Intent.ACTION_PACKAGE_ADDED);addAction(Intent.ACTION_PACKAGE_REMOVED);addAction(Intent.ACTION_PACKAGE_CHANGED);addDataScheme("package")},ContextCompat.RECEIVER_EXPORTED)
        connectivity.registerNetworkCallback(android.net.NetworkRequest.Builder().addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),pingNetworkCallback)
    }
    private fun startupSubscriptions(){
        val epoch=startupEpoch.get()
        val items=repo.subscriptions()
        for(item in items){if(epoch!=startupEpoch.get()||isDestroyed)return;if(item.optBoolean("refreshOnStartup"))try{SubscriptionClient(repo).refresh(item.getString("id"),true)}catch(_:Exception){}}
        for(item in repo.subscriptions())if(item.optBoolean("pingOnStartup")||item.optBoolean("connectOnStartup")&&item.optString("startupSelection")=="lowest"){
            val a=repo.snapshot().getJSONArray("servers");for(i in 0 until a.length())if(a.getJSONObject(i).optString("subscriptionId")==item.getString("id"))measure(a.getJSONObject(i).getString("id"))
        }
        val selectedSub=repo.subscriptions().firstOrNull{it.optBoolean("connectOnStartup")}?:return
        if(repo.connection!="off")return
        val startupGeneration=probeGeneration.get()
        while(measuring.isNotEmpty()){if(epoch!=startupEpoch.get()||startupGeneration!=probeGeneration.get()||isDestroyed)return;Thread.sleep(50)}
        val a=repo.snapshot().getJSONArray("servers");var candidates=(0 until a.length()).map{a.getJSONObject(it)}.filter{it.optString("subscriptionId")==selectedSub.getString("id")}.filter{try{JSONObject(NativeCore.validate(repo.validationRequest(it).toString())).optBoolean("ok")}catch(_:Exception){false}}
        if(selectedSub.optString("startupSelection")=="lowest"){
            val generation=probeGeneration.get()
            while(candidates.any{repo.server(it.getString("id"))?.optBoolean("measuring")==true}){if(epoch!=startupEpoch.get()||generation!=probeGeneration.get()||isDestroyed)return;Thread.sleep(50)}
            candidates=candidates.mapNotNull{repo.server(it.getString("id"))}.filter{!it.optBoolean("pingStale")&&!it.isNull("pingMs")&&it.optLong("pingNetworkGeneration")==generation}.sortedBy{it.getLong("pingMs")}
        }
        val target=when(selectedSub.optString("startupSelection","last")){"random"->candidates.randomOrNull();"lowest"->candidates.firstOrNull();else->candidates.firstOrNull{it.getString("id")==selectedSub.optString("lastUsedServerId")}?:candidates.firstOrNull()}?:run{message("Нет доступного сервера для автоподключения");return}
        runOnUiThread{if(epoch==startupEpoch.get()&&uiVisible&&repo.connection=="off"&&!isDestroyed){repo.select(target.getString("id"));val permission=VpnService.prepare(this);if(permission!=null)vpnPermission.launch(permission)else startVpn()}}
    }
    private var fileCallback:android.webkit.ValueCallback<Array<Uri>>?=null
    private val importFile=registerForActivityResult(ActivityResultContracts.OpenDocument()){uri->
        fileCallback?.onReceiveValue(null);fileCallback=null
        if(uri!=null)worker.execute{try{val raw=contentResolver.openInputStream(uri)!!.use{String(it.readNBytes(1024*1024+1),Charsets.UTF_8)};require(raw.length<=1024*1024);send("import",raw)}catch(_:Exception){message("Не удалось открыть конфигурацию")}}
    }
    private fun dispatch(action:String,data:JSONObject){
        if(action in listOf("connect","select","confirm-server-switch","subscription-settings"))startupEpoch.incrementAndGet()
        when(action){
            "load"->{pageReady=true;send("state",repo.snapshot());SubscriptionWorker.schedule(this,true);if(startupClaimed.compareAndSet(false,true))startupWorker.execute{startupSubscriptions()}}
            "connect"->{cancelPingChecks();if(repo.connection=="on"&&repo.activeId==repo.selectedServer()?.optString("id")){startService(Intent(this,IorVpnService::class.java).setAction(IorVpnService.STOP))}else if(repo.connection in listOf("pending","reconnecting")){startService(Intent(this,IorVpnService::class.java).setAction(IorVpnService.STOP))}else{val permission=VpnService.prepare(this);if(permission!=null)vpnPermission.launch(permission)else startVpn()}}
            "scan"->scanner.launch(Intent(this,QrActivity::class.java))
            "paste"->{val clipboard=getSystemService(ClipboardManager::class.java);clipboard.primaryClip?.getItemAt(0)?.coerceToText(this)?.toString()?.let{send("import",it)}}
            "copy"->{getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("ior",data.getString("text")));message("Скопировано")}
            "share"->{startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT,data.getString("text")),"Отправить конфигурацию"))}
            "backup"->AlertDialog.Builder(this).setTitle("Резервная копия").setItems(arrayOf("Настройки и маршруты без секретов","Все данные: защитить паролем")){_,which->if(which==0)backup(null)else password("Пароль: минимум 10 символов"){backup(it)}}.localizedShow()
            "restore"->restoreFile.launch(arrayOf("application/json","application/octet-stream","text/plain"))
            
            
            "video-stall-marker"->{Diagnostics.get(this).record("VIDEO_PLAYBACK_STALL_REPORTED",JSONObject().put("evidence","USER_REPORTED").put("quality","UNKNOWN"));message("Торможение отмечено в журнале")}
            "export-diagnostics"->AlertDialog.Builder(this).setTitle("Экспорт диагностики").setMessage("Архив содержит события VPN, адреса серверов, домены и сетевые метрики. Конфигурации, пароли и ключи не включаются.").setPositiveButton("Экспортировать"){_,_->worker.execute{try{IorVpnService.flushDiagnostics();pendingDiagnostics=Diagnostics.get(this).export();runOnUiThread{diagnosticsFile.launch("ior-diagnostics.zip")}}catch(_:Exception){message("Не удалось экспортировать диагностику")}}}.setNegativeButton("Отмена",null).localizedShow()
            "vpn-system-settings"->try{startActivity(Intent(android.provider.Settings.ACTION_VPN_SETTINGS))}catch(_:Exception){message("Откройте системные настройки VPN")}
            "ping-group"->{val a=repo.snapshot().getJSONArray("servers");for(i in 0 until a.length()){val s=a.getJSONObject(i);if(s.optString("subscriptionId")==data.getString("id"))measure(s.getString("id"))}}
            "ping-cancel"->cancelPingChecks()
            else->worker.execute{try{when(action){
                "settings"->{repo.saveSettings(data);val settings=repo.snapshot();Diagnostics.get(this).configure(settings.optInt("journalMaxMb",20),settings.optInt("journalDays",7));MobileTrafficPolicy.changed();send("journal-size",Diagnostics.get(this).size())}
                "journal-size"->{val settings=repo.snapshot();Diagnostics.get(this).configure(settings.optInt("journalMaxMb",20),settings.optInt("journalDays",7));send("journal-size",Diagnostics.get(this).size())}
                "lan-settings"->{repo.saveLan(data);if(repo.connection!="on")message("LAN включится после подключения VPN")}
                "subscription-settings"->{repo.saveSubscriptionSettings(data.getString("id"),data);SubscriptionWorker.schedule(this,true)}
                "route-select"->changeRouting{repo.selectRoute(data.getString("id"))}
                "route-enabled"->changeRouting{repo.routingEnabled(data.getBoolean("enabled"))}
                "hwid-mode"->repo.setHwidMode(data.getString("id"),data.getString("mode"))
                "select"->{val id=data.getString("id");repo.select(id);measure(id);if(repo.connection=="on"&&repo.activeId!=id)message("Сервер выбран. Нажмите подключение, чтобы переподключиться")}
                "import"->{if(data.optString("tab")=="subscription"){val item=repo.addSubscription(data.getString("value"),data.optString("name"));refresh(item.getString("id"))}else{repo.addServer(data.getString("value"),data.optString("name"));message("Конфигурация добавлена")}}
                "edit"->{if(data.optString("kind")=="route")repo.saveRoute(data)else repo.addServer(data.getString("raw"),data.getString("name"),data.optString("id").ifBlank{null});send("editor-saved",true)}
                "duplicate"->{val source=repo.server(data.getString("id")) ?: error("Сервер удалён");repo.addServer(source.getString("raw"),source.getString("name")+" · копия",java.util.UUID.randomUUID().toString());message("Создана копия")}
                "delete"->{if(data.optString("kind")=="subscription")androidx.work.WorkManager.getInstance(this).cancelUniqueWork("ior-subscription-"+data.getString("id"));val id=data.getString("id");require(repo.activeId!=id && !(data.getString("kind")=="subscription" && repo.activeId.isNotBlank() && repo.server(repo.activeId)?.optString("subscriptionId")==id)){"Сначала отключите активный сервер"};repo.delete(data.getString("kind"),id)}
                "validate"->{val server=repo.server(data.getString("id")) ?: error("Сервер удалён");val result=JSONObject(NativeCore.validate(repo.request(server).toString()));message(if(result.optBoolean("ok"))"Структура проверена ior-core; сеть не проверялась" else result.optString("code"))}
                "subscription-refresh"->refresh(data.getString("id"))
                "apps"->try{apps()}catch(_:Exception){send("apps-error","Не удалось прочитать приложения")}
                "route-consent"->{if(data.optBoolean("review"))repo.reviewRouteConsent(data.getString("id"))else repo.routeConsent(data.getString("id"),data.getBoolean("accept"))}
                "ui-log"->{repo.log(data.optString("text").take(512))}
                "clear-logs"->{IorVpnService.flushDiagnostics();Diagnostics.get(this).clear();repo.clearLogs();send("journal-size",Diagnostics.get(this).size())}
                "import-route"->{val document=JSONObject(data.getString("raw"));require(document.optString("type")=="ior-android-route"&&document.optInt("version")==1);val route=document.getJSONObject("item").put("id",java.util.UUID.randomUUID().toString());repo.saveRoute(route);message("Маршрут импортирован")}
                "export-route"->{val routes=repo.snapshot().getJSONArray("routes");val route=(0 until routes.length()).map{routes.getJSONObject(it)}.first{it.getString("id")==data.getString("id")};pendingBackup=JSONObject().put("type","ior-android-route").put("version",1).put("item",route).toString().toByteArray();runOnUiThread{backupFile.launch("ior-android-route.json")}}
                else->message("Действие не поддерживается")
            }}catch(error:Exception){message(error.message?.takeIf{it.length<=120&&!it.contains("://")} ?: "Не удалось выполнить действие")}}
        }
    }
    private fun changeRouting(change:()->Unit){
        require(repo.connection in listOf("on","off")){"Дождитесь завершения подключения"}
        val before=repo.snapshot()
        try{change();repo.selectedServer()?.let{repo.request(it)};if(repo.connection=="on")ContextCompat.startForegroundService(this,Intent(this,IorVpnService::class.java).setAction("app.ior.android.ROUTING").putExtra("previousRouting",before.toString()))}
        catch(error:Exception){repo.restoreRouting(before);throw error}
    }
    private fun startVpn(){
        repo.routeForServer()
        try{repo.selectedServer()?.let{repo.request(it)} ?: error("Нет сервера")}catch(_:Exception){message("Выбранный маршрут несовместим с ядром");return}
        if(Build.VERSION.SDK_INT>=33 && ContextCompat.checkSelfPermission(this,Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        ContextCompat.startForegroundService(this,Intent(this,IorVpnService::class.java).setAction(if(repo.connection=="on")"app.ior.android.RECONNECT" else "app.ior.android.CONNECT"))
    }
    @Synchronized private fun measure(id:String){if(isDestroyed||isFinishing)return;if(!measuring.add(id))return;val generation=probeGeneration.get();repo.latency(id,true,null);probes.execute{
        if(generation!=probeGeneration.get())return@execute
        try{probeSlots.acquire()}catch(_:InterruptedException){return@execute}
        try{
        if(generation!=probeGeneration.get())return@execute
        val settings=repo.snapshot();val mode=settings.optString("pingMode","auto");val timeout=if(mode in listOf("auto","head","get"))settings.optInt("proxyPingTimeoutMs",5000).coerceIn(5000,15000)else settings.optInt("pingTimeoutMs",5000)
        val networkSignature=PhysicalNetworks.signature(this)
        val result=try{repo.server(id)?.let{server->
            val network=PhysicalNetworks.select(this)?:return@let PingResult(null,"","PING_NETWORK_UNAVAILABLE")
            val protocol=server.getJSONObject("wire").getString("protocol")
            PingMeasurement.run(protocol,mode,timeout,repo.connection=="on"&&repo.activeId==id,
                physical={method,budget->LatencyProbe.measure(this,server,method,budget,network)},
                https={budget->val response=JSONObject(NativeCore.probe(repo.validationRequest(server).toString(),if(mode=="get")"GET"else"HEAD",settings.optString("pingUrl","https://www.gstatic.com/generate_204"),budget,this));if(response.optBoolean("ok"))PingResult(response.getLong("durationMs"),"https")else PingResult(null,"https",response.optString("code","PING_HTTPS_UNAVAILABLE"))})
        }?:PingResult(null,"","PING_PROFILE_REMOVED")}catch(_:Exception){PingResult(null,"","PING_FAILED")}
        synchronized(this@MainActivity){if(generation==probeGeneration.get()){if(networkSignature==PhysicalNetworks.signature(this))repo.latency(id,false,result.milliseconds,result.method,result.reason,generation)else repo.latency(id,false,null,reason="PING_NETWORK_CHANGED",networkGeneration=generation,stale=true);measuring.remove(id)}}
        }finally{probeSlots.release()}
    }}
    fun protectSocket(fd:Int):Boolean{
        val manager=getSystemService(android.net.ConnectivityManager::class.java)
        if(manager.allNetworks.any{manager.getNetworkCapabilities(it)?.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)==true})return false
        val physical=PhysicalNetworks.select(this) ?: return false
        return try{android.os.ParcelFileDescriptor.fromFd(fd).use{physical.bindSocket(it.fileDescriptor)};true}catch(_:Exception){false}
    }
    private fun refresh(id:String){try{SubscriptionClient(repo).refresh(id);SubscriptionWorker.schedule(this,true);message("Подписка обновлена")}catch(consent:SubscriptionClient.ConsentRequired){runOnUiThread{if(!isFinishing)AlertDialog.Builder(this).setTitle("Разрешить HWID этой подписке?").setMessage("Некоторые сервисы требуют HWID для привязки устройства. Без разрешения загрузка подписки может быть недоступна.\n\n"+consent.origin).setPositiveButton("Разрешить"){_,_->worker.execute{repo.authorizeHwid(id,consent.origin);try{SubscriptionClient(repo).refresh(id);message("Подписка обновлена")}catch(_:Exception){message("Повторное обновление не удалось")}}}.setNegativeButton("Отмена",null).localizedShow()}}}
    private fun apps(){
        appCatalogCache?.let{send("apps",it);return}
        val items=packageManager.getInstalledApplications(0).filter{it.packageName!=packageName}.map{app->
            val system=app.flags and android.content.pm.ApplicationInfo.FLAG_SYSTEM!=0
            JSONObject().put("id",app.packageName).put("name",packageManager.getApplicationLabel(app).toString()).put("system",system)
                .put("category",if(system)"system" else when(app.category){android.content.pm.ApplicationInfo.CATEGORY_GAME->"games";android.content.pm.ApplicationInfo.CATEGORY_VIDEO->"video";android.content.pm.ApplicationInfo.CATEGORY_SOCIAL->"chat";else->"other"}).put("letter",packageManager.getApplicationLabel(app).firstOrNull()?.toString() ?: "?")
                .apply{try{val drawable=packageManager.getApplicationIcon(app);val bitmap=android.graphics.Bitmap.createBitmap(32,32,android.graphics.Bitmap.Config.ARGB_8888);drawable.setBounds(0,0,32,32);drawable.draw(android.graphics.Canvas(bitmap));val bytes=java.io.ByteArrayOutputStream();bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,bytes);put("icon","data:image/png;base64,"+android.util.Base64.encodeToString(bytes.toByteArray(),android.util.Base64.NO_WRAP));bitmap.recycle()}catch(_:Exception){}}
        }.sortedBy{it.getString("name").lowercase()}
        appCatalogCache=JSONArray(items);send("apps",appCatalogCache!!)
    }
    private fun backup(password:CharArray?){worker.execute{try{pendingBackup=BackupCodec.encode(repo.snapshot(),password);runOnUiThread{backupFile.launch("ior-android-backup.json")}}catch(_:Exception){message("Не удалось создать копию; проверьте пароль")}finally{password?.fill('\u0000')}}}
    private fun restore(bytes:ByteArray,password:CharArray?){worker.execute{try{val imported=BackupCodec.decode(bytes,password);val count=imported.getJSONArray("servers").length();runOnUiThread{AlertDialog.Builder(this).setTitle("Заменить данные приложения?").setMessage("Серверов: $count. Существующие конфигурации будут заменены. VPN должен быть отключён.").setPositiveButton("Восстановить"){_,_->worker.execute{try{repo.replace(imported);message("Данные восстановлены")}catch(_:Exception){message("Восстановление не выполнено")}}}.setNegativeButton("Отмена",null).localizedShow()}}catch(_:Exception){message("Неверный пароль или повреждённая копия")}finally{password?.fill('\u0000')}}}
    private fun password(title:String,callback:(CharArray)->Unit){val field=EditText(this).apply{inputType=android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD};AlertDialog.Builder(this).setTitle(title).setView(field).setPositiveButton("Продолжить"){_,_->val pass=field.text.toString().toCharArray();field.text.clear();callback(pass)}.setNegativeButton("Отмена",null).localizedShow()}
    private fun AlertDialog.Builder.localizedShow():AlertDialog {val dialog=show();dialog.window?.decorView?.let{Localization.apply(this@MainActivity,repo.snapshot(),it)};return dialog}
    private fun message(text:String){send("message",Localization.translate(this,repo.snapshot(),text))}
    private fun send(event:String,data:Any){if(!uiVisible&&event in listOf("state","stats","latency","log"))return;val json=JSONObject().put("event",event).put("data",data).toString();runOnUiThread{if(::web.isInitialized&&pageReady&&!isFinishing&&!isDestroyed)web.evaluateJavascript("window.iorAndroidReceive($json)",null)}}
    private fun receiveIntent(intent:Intent?){
        if(intent==null)return
        val stream=if(intent.action==Intent.ACTION_SEND){if(Build.VERSION.SDK_INT>=33)intent.getParcelableExtra(Intent.EXTRA_STREAM,Uri::class.java)else @Suppress("DEPRECATION") intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)}else if(intent.action==Intent.ACTION_VIEW&&intent.data?.scheme=="content")intent.data else null
        if(stream!=null){worker.execute{try{val raw=contentResolver.openInputStream(stream)!!.use{String(it.readNBytes(1024*1024+1),Charsets.UTF_8)};require(raw.length<=1024*1024);runOnUiThread{if(pageReady)send("import",raw)else pendingImport=raw}}catch(_:Exception){message("Не удалось открыть переданный файл")}};return}
        pendingImport=when(intent.action){Intent.ACTION_SEND->intent.getStringExtra(Intent.EXTRA_TEXT);Intent.ACTION_VIEW->intent.dataString;else->null}
    }
    override fun onNewIntent(intent:Intent){super.onNewIntent(intent);receiveIntent(intent);pendingImport?.let{if(pageReady){send("import",it);pendingImport=null}}}
    @Synchronized private fun cancelPingChecks(){probeGeneration.incrementAndGet();NativeCore.cancelProbes();probes.shutdownNow();for(id in measuring)repo.latency(id,false,null,reason="PING_CANCELLED",stale=true);measuring.clear();probes=Executors.newFixedThreadPool(2)}
    @Synchronized private fun refreshPingNetwork(){if(!::repo.isInitialized||isDestroyed||isFinishing)return;val signature=PhysicalNetworks.signature(this);if(signature==pingNetworkSignature)return;pingNetworkSignature=signature;cancelPingChecks();repo.invalidateLatencies(probeGeneration.get())}
    override fun onResume(){super.onResume();uiVisible=true;if(::web.isInitialized){refreshPingNetwork();if(pageReady)send("state",repo.snapshot())}}
    override fun onPause(){uiVisible=false;super.onPause()}
    override fun onDestroy(){startupEpoch.incrementAndGet();startupWorker.shutdownNow();if(::repo.isInitialized){repo.unobserve(observe);repo.unobserveEvents(observeEvent)};try{unregisterReceiver(packagesChanged);connectivity.unregisterNetworkCallback(pingNetworkCallback)}catch(_:Exception){};if(::web.isInitialized){web.stopLoading();web.destroy()};worker.shutdown();probeGeneration.incrementAndGet();NativeCore.cancelProbes();probes.shutdownNow();super.onDestroy()}
    companion object { private val startupClaimed=java.util.concurrent.atomic.AtomicBoolean(false);const val LOCAL_PAGE="https://appassets.androidplatform.net/assets/index.html" }
}


