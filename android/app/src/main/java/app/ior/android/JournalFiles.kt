package app.ior.android

import java.io.File
import org.json.JSONObject

internal class JournalFiles(private val directory:File) {
    var maxBytes=20L*1024*1024
    var days=7
    private var serial=0L
    private var lastPrune=0L
    fun files()=directory.listFiles()?.filter{it.isFile&&(it.name.endsWith(".jsonl")||it.name.endsWith(".jsonl.previous"))}.orEmpty()
    fun append(text:String){
        val current=File(directory,"current.jsonl")
        if(current.exists()&&current.length()+text.toByteArray(Charsets.UTF_8).size>minOf(1024L*1024,maxBytes/2))
            check(current.renameTo(File(directory,"events-${System.currentTimeMillis()}-${serial++}.jsonl")))
        current.appendText(text,Charsets.UTF_8)
        prune()
    }
    fun prune(force:Boolean=false,now:Long=System.currentTimeMillis()){
        if(!force&&now-lastPrune<60000&&files().sumOf{it.length()}<=maxBytes)return
        lastPrune=now
        val cutoff=java.time.Instant.ofEpochMilli(now).minusSeconds(days*86400L)
        for(file in files()){
            if(file.lastModified()<cutoff.toEpochMilli()){check(file.delete());continue}
            // Filter only files potentially crossing the age boundary.
            val first=file.useLines{it.firstOrNull()}
            val firstAt=try{val row=JSONObject(first?:"{}");java.time.Instant.parse(row.optString("recordedUtc",row.optString("utc")))}catch(_:Exception){null}
            if(firstAt!=null&&firstAt<cutoff){
                val tmp=File(directory,file.name+".tmp")
                tmp.bufferedWriter().use{out->file.forEachLine{line->val keep=try{val row=JSONObject(line);java.time.Instant.parse(row.optString("recordedUtc",row.getString("utc")))>=cutoff}catch(_:Exception){true};if(keep){out.write(line);out.newLine()}}}
                check(file.delete());check(tmp.renameTo(file))
            }
        }
        var total=files().sumOf{it.length()}
        for(file in files().sortedBy{it.lastModified()}){if(total<=maxBytes)break;val size=file.length();check(file.delete());total-=size}
    }
    fun clear(){for(file in files())check(file.delete())}
    fun size()=files().sumOf{it.length()}
}
