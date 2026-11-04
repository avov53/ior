package app.ior.android

import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

object BackupCodec {
    private const val ITERATIONS=600000
    fun encode(state:JSONObject,password:CharArray?):ByteArray {
        val data=JSONObject(state.toString())
        for(key in listOf("connection","connectionError","stats","activeId","installationId","logs","drafts","lanInterfaces","lanStatus"))data.remove(key)
        data.put("hwidEnabled",false)
        val subscriptions=data.getJSONArray("subscriptions")
        for(i in 0 until subscriptions.length()){subscriptions.getJSONObject(i).apply{remove("hwidOrigins");remove("hwidRequiredOrigins");put("hwidMode","never")}}
        val envelope=JSONObject().put("type","ior-android-backup").put("version",1)
        if(password==null) {
            data.remove("lan") // LAN password is a secret, just like subscription URLs.
            // Server links and subscription URLs can both contain credentials.
            data.put("servers",JSONArray()).put("subscriptions",JSONArray()).put("selected","")
            envelope.put("includesSecrets",false).put("data",data)
        }else {
            require(password.size>=10){"Пароль должен содержать не менее 10 символов"}
            val salt=ByteArray(16).also{SecureRandom().nextBytes(it)}
            val cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key(password,salt))
            cipher.updateAAD("ior-android-backup-v1".toByteArray())
            val encode:(ByteArray)->String={Base64.getEncoder().encodeToString(it)}
            envelope.put("includesSecrets",true).put("iterations",ITERATIONS).put("salt",encode(salt)).put("iv",encode(cipher.iv)).put("encrypted",encode(cipher.doFinal(data.toString().toByteArray(Charsets.UTF_8))))
        }
        return envelope.toString().toByteArray(Charsets.UTF_8)
    }
    fun decode(bytes:ByteArray,password:CharArray?):JSONObject {
        require(bytes.size<=8*1024*1024)
        val envelope=JSONObject(String(bytes,Charsets.UTF_8));require(envelope.optString("type")=="ior-android-backup"&&envelope.optInt("version")==1){"Неверная версия резервной копии"}
        if(!envelope.optBoolean("includesSecrets"))return envelope.getJSONObject("data")
        require(password!=null && envelope.getInt("iterations")==ITERATIONS)
        val salt=Base64.getDecoder().decode(envelope.getString("salt"));val iv=Base64.getDecoder().decode(envelope.getString("iv"));require(salt.size==16&&iv.size==12)
        val cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(password,salt),GCMParameterSpec(128,iv));cipher.updateAAD("ior-android-backup-v1".toByteArray())
        return JSONObject(String(cipher.doFinal(Base64.getDecoder().decode(envelope.getString("encrypted"))),Charsets.UTF_8))
    }
    private fun key(password:CharArray,salt:ByteArray):SecretKeySpec {val spec=PBEKeySpec(password,salt,ITERATIONS,256);return try{SecretKeySpec(SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded,"AES")}finally{spec.clearPassword()}}
}
