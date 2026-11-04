package app.ior.android

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureStore(context: Context) {
    private val file = File(context.noBackupFilesDir, "state.enc")
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return (store.getKey("ior-state-v1", null) as? SecretKey) ?: KeyGenerator.getInstance("AES", "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder("ior-state-v1", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
            generateKey()
        }
    }
    @Synchronized fun read(): JSONObject? {
        if (!file.exists()) return null
        val bytes = file.readBytes()
        require(bytes.size in 30..8*1024*1024 && bytes[0] == 1.toByte()) { "Неверное защищённое хранилище" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(1,13)))
        return JSONObject(String(cipher.doFinal(bytes.copyOfRange(13,bytes.size)), Charsets.UTF_8))
    }
    @Synchronized fun write(state: JSONObject) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val bytes = byteArrayOf(1) + cipher.iv + cipher.doFinal(state.toString().toByteArray(Charsets.UTF_8))
        val temporary = File(file.parentFile, "state.enc.tmp")
        temporary.outputStream().use { it.write(bytes); it.fd.sync() }
        require(temporary.renameTo(file)) { "Не удалось сохранить данные" }
    }
}
