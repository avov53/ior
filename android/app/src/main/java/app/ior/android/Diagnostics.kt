package app.ior.android
import android.content.Context
import org.json.JSONObject
import java.io.File
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Executors
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
class Diagnostics internal constructor(private val directory:File,private val maxFileBytes:Int=4*1024*1024):AutoCloseable{
 private constructor(context:Context):this(File(context.noBackupFilesDir,"diagnostics"))
 private sealed class Work {
  class Row(val value:String,val console:Boolean=false):Work()
  class Export(val result:CompletableFuture<ByteArray>):Work()
  class Close(val result:CompletableFuture<Unit>):Work()
  class Control(val action:()->Unit,val result:CompletableFuture<Unit>):Work()
 }
 private val queue=ArrayBlockingQueue<Work>(4096);private val dropped=AtomicLong()
 @Volatile private var session=JSONObject()
 private val files=JournalFiles(directory).apply{maxBytes=if(maxFileBytes==4*1024*1024)20L*1024*1024 else maxFileBytes.toLong()*2}
 private val consoleDirectory=File(directory,"console")
 private val consoleFiles=JournalFiles(consoleDirectory).apply{maxBytes=2L*1024*1024}
 private val writer=Executors.newSingleThreadExecutor{task->Thread(task,"ior-diagnostics-writer").apply{isDaemon=true}}
 init{check(directory.isDirectory||directory.mkdirs());writer.execute{while(true)when(val work=queue.take()){
  is Work.Row->{
   val rows=ArrayList<String>();rows.add(work.value)
   while(rows.size<64){val next=queue.peek();if(next !is Work.Row||next.console!=work.console)break;rows.add((queue.poll() as Work.Row).value)}
   try{val text=rows.joinToString("\n",postfix="\n")
    if(work.console)check(consoleDirectory.isDirectory||consoleDirectory.mkdirs())
    (if(work.console)consoleFiles else files).append(text)
   }catch(_:Exception){dropped.addAndGet(rows.size.toLong())}
  }
  is Work.Export->try{work.result.complete(exportFiles())}catch(error:Exception){work.result.completeExceptionally(error)}
  is Work.Close->{work.result.complete(Unit);return@execute}
  is Work.Control->try{work.action();work.result.complete(Unit)}catch(error:Exception){work.result.completeExceptionally(error)}
 }}}
 fun beginSession(id:String,protocol:String,transport:String){
  session=JSONObject().put("sessionId",id).put("protocol",protocol).put("profileTransport",transport).put("appVersion",BuildConfig.VERSION_NAME).put("versionCode",BuildConfig.VERSION_CODE)
  record("ANDROID_DIAGNOSTIC_SESSION_STARTED",JSONObject().put("evidence","NETWORK_OBSERVATION_ONLY").put("playbackState","UNKNOWN").put("quality","UNKNOWN"))
 }
 fun record(code:String,fields:JSONObject=JSONObject()){
  val merged=JSONObject(fields.toString());val context=session
  if(!merged.has("sessionId")||merged.optString("sessionId")==context.optString("sessionId"))for(key in context.keys())if(!merged.has(key))merged.put(key,context.opt(key))
  val(normalized,safe)=DiagnosticPolicy.normalize(code,merged)
  val recorded=java.time.Instant.now();val eventMillis=safe.optLong("eventUnixMs",0)
  val occurred=if(eventMillis in 946684800000L..4102444800000L)java.time.Instant.ofEpochMilli(eventMillis)else recorded
  val row=JSONObject().put("schemaVersion",2).put("utc",occurred.toString()).put("recordedUtc",recorded.toString()).put("code",normalized).put("fields",safe).put("droppedEvents",dropped.get()).toString();if(!queue.offer(Work.Row(row)))dropped.incrementAndGet()
 }
 fun export():ByteArray{
  val result=CompletableFuture<ByteArray>()
  check(queue.offer(Work.Export(result),5,TimeUnit.SECONDS)){"DIAGNOSTIC_EXPORT_QUEUE_BUSY"}
  return result.get(15,TimeUnit.SECONDS)
 }
 private fun control(action:()->Unit){val result=CompletableFuture<Unit>();check(queue.offer(Work.Control(action,result),5,TimeUnit.SECONDS));result.get(15,TimeUnit.SECONDS)}
 fun console(entry:JSONObject){
  val row=JSONObject(entry.toString()).put("utc",java.time.Instant.now().toString())
  val context=session;for(key in context.keys())row.put(key,context.opt(key))
  if(!queue.offer(Work.Row(row.toString(),true)))dropped.incrementAndGet()
 }
 fun consoleTail():org.json.JSONArray{
  val rows=java.util.ArrayDeque<JSONObject>()
  control{consoleFiles.prune(true);for(file in consoleFiles.files().sortedBy{it.lastModified()})file.forEachLine{line->
   try{rows.addLast(JSONObject(line));if(rows.size>2000)rows.removeFirst()}catch(_:Exception){}
  }}
  return org.json.JSONArray(rows.toList())
 }
 fun configure(mb:Int,days:Int){require(mb in listOf(5,20,50)&&days in listOf(1,7,30));control{val total=mb*1024L*1024;consoleFiles.maxBytes=total/10;files.maxBytes=total-consoleFiles.maxBytes;files.days=days;consoleFiles.days=days;files.prune(true);consoleFiles.prune(true)}}
 fun clear(){control{files.clear();consoleFiles.clear();dropped.set(0)}}
 fun size():Long{var size=0L;control{files.prune(true);consoleFiles.prune(true);size=files.size()+consoleFiles.size()};return size}
 override fun close(){val done=CompletableFuture<Unit>();check(queue.offer(Work.Close(done),5,TimeUnit.SECONDS));done.get(15,TimeUnit.SECONDS);writer.shutdown()}
 private fun exportFiles():ByteArray{val out=java.io.ByteArrayOutputStream();java.util.zip.ZipOutputStream(out).use{zip->
  files.prune(true)
  for(file in files.files().sortedBy{it.lastModified()}){zip.putNextEntry(java.util.zip.ZipEntry(file.name));file.inputStream().use{it.copyTo(zip)};zip.closeEntry()}
  consoleFiles.prune(true)
  for(file in consoleFiles.files().sortedBy{it.lastModified()}){zip.putNextEntry(java.util.zip.ZipEntry("console/"+file.name));file.inputStream().use{it.copyTo(zip)};zip.closeEntry()}
  zip.putNextEntry(java.util.zip.ZipEntry("README.txt"));zip.write(("ior diagnostics schema 2. Times are UTC. Group by sessionId and flowId.\n"+
   "Network metadata only; no decrypted traffic or credentials.\n"+
   "VIDEO_PLAYBACK_STALL_REPORTED is a user report at button-press time.\n"+
   "VIDEO_NETWORK_SAMPLE measures encrypted googlevideo traffic; playback state and quality remain UNKNOWN.\n"+
   "Gaps and low rates may be normal buffering or idle connections, not proof of a stall.\n"+
   "Hysteria losses describe the entire connection, not one video.\n"+
   "Physical link speed estimates are not measured Internet throughput.\n"+
   "Writer dropped events: ${dropped.get()}\n").toByteArray(Charsets.UTF_8));zip.closeEntry()
 };return out.toByteArray()}
 companion object{@Volatile private var instance:Diagnostics?=null;fun get(context:Context):Diagnostics=instance?:synchronized(this){instance?:Diagnostics(context.applicationContext).also{instance=it}}}
}
