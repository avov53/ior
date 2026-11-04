package app.ior.android
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
class SubscriptionOptionsTests {
 @Test fun defaultsAndLegacyDisabledArePreserved(){val n=JSONObject();SubscriptionOptions.migrate(n);assertTrue(SubscriptionOptions.enabled(n));assertEquals(6,SubscriptionOptions.interval(n));assertEquals(10000,n.getInt("requestTimeoutMs"));val old=JSONObject();SubscriptionOptions.migrate(old,false);assertFalse(SubscriptionOptions.enabled(old))}
 @Test fun serverIntervalLocksSettingsAndKeepsUserInterval(){val n=JSONObject();SubscriptionOptions.migrate(n);n.put("serverRefreshIntervalHours",1);assertTrue(SubscriptionOptions.enabled(n));assertEquals(1,SubscriptionOptions.interval(n));assertThrows(IllegalArgumentException::class.java){SubscriptionOptions.save(n,JSONObject().put("autoRefreshEnabled",false))};SubscriptionOptions.save(n,JSONObject().put("refreshIntervalHours",1).put("requestTimeoutMs",15000));n.remove("serverRefreshIntervalHours");assertEquals(6,SubscriptionOptions.interval(n));assertEquals(15000,n.getInt("requestTimeoutMs"))}
 @Test fun invalidOptionsDoNotPartiallyUpdate(){val n=JSONObject();SubscriptionOptions.migrate(n);val before=n.toString();assertThrows(IllegalArgumentException::class.java){SubscriptionOptions.save(n,JSONObject().put("autoRefreshEnabled",false).put("requestTimeoutMs",100))};assertEquals(before,n.toString())}
}
