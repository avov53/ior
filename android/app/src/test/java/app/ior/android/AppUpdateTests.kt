package app.ior.android

import org.json.JSONObject
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class AppUpdateTests {
    @Test fun offersOnlyNewerStableReleaseWithAndroidAsset() {
        val release = JSONObject("""{"tag_name":"v1.2.7","assets":[{"name":"ior-android-arm64.apk"}]}""")
        assertNotNull(AppUpdate.newerRelease(release, "1.2.6"))
        assertNull(AppUpdate.newerRelease(release, "1.2.7"))
        release.put("tag_name", "v1.2.8")
        assertNotNull(AppUpdate.newerRelease(release, "1.2.7"))
        assertNull(AppUpdate.newerRelease(release.put("prerelease", true), "1.2.7"))
        release.put("prerelease", false).remove("assets")
        assertNull(AppUpdate.newerRelease(release, "1.2.7"))
    }
}
