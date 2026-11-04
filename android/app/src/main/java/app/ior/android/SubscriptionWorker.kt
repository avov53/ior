package app.ior.android

import android.content.Context
import androidx.work.Worker
import androidx.work.WorkerParameters
import androidx.work.WorkManager
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.Constraints
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.ExistingWorkPolicy
import java.util.concurrent.TimeUnit

class SubscriptionWorker(context:Context,parameters:WorkerParameters):Worker(context,parameters) {
    override fun doWork():Result {
        MobileTrafficPolicy.initialize(applicationContext)
        val repo=try{Repository.get(applicationContext)}catch(_:Exception){return Result.failure()}
        val id=inputData.getString("subscriptionId")?:return Result.success()
        val item=repo.subscriptions().firstOrNull{it.optString("id")==id}?:return Result.success()
        if(!SubscriptionOptions.enabled(item))return Result.success()
        val due=System.currentTimeMillis()-item.optLong("updatedAt",0)>=SubscriptionOptions.interval(item)*3600000L
        if(inputData.getBoolean("wifiRetry",false)&&!item.optBoolean("waitingForWifi")&&!due)return Result.success()
        for(item in listOf(item))try{SubscriptionClient(repo).refresh(item.getString("id"),true)}catch(_:MobileTrafficPolicy.Deferred){
            if(inputData.getBoolean("wifiRetry",false))return Result.retry()
            WorkManager.getInstance(applicationContext).enqueueUniqueWork("ior-wifi-"+id,ExistingWorkPolicy.KEEP,OneTimeWorkRequestBuilder<SubscriptionWorker>().setInputData(androidx.work.Data.Builder().putString("subscriptionId",id).putBoolean("wifiRetry",true).build()).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.UNMETERED).build()).build())
            return Result.success()
        }catch(_:Exception){}
        // Errors/consent remain on cards. No background dialogs or VPN startup.
        schedule(applicationContext,true);return Result.success()
    }
    companion object {
        fun schedule(context:Context,enabled:Boolean){
            val manager=WorkManager.getInstance(context)
            manager.cancelUniqueWork("ior-subscriptions")
            for(item in Repository.get(context).subscriptions()){
                val key="ior-subscription-"+item.getString("id")
                if(!SubscriptionOptions.enabled(item)){manager.cancelUniqueWork(key);continue}
                val period=TimeUnit.HOURS.toMillis(SubscriptionOptions.interval(item).toLong())
                val now=System.currentTimeMillis()
                val delay=(item.optLong("updatedAt",now)+period-now).coerceIn(0,period)
                manager.enqueueUniquePeriodicWork(key,ExistingPeriodicWorkPolicy.UPDATE,
                    PeriodicWorkRequestBuilder<SubscriptionWorker>(SubscriptionOptions.interval(item).toLong(),TimeUnit.HOURS).setInitialDelay(delay,TimeUnit.MILLISECONDS).setInputData(androidx.work.Data.Builder().putString("subscriptionId",item.getString("id")).build()).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build())
            }
        }
    }
}
