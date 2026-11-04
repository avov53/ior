package app.ior.android

import com.google.zxing.*
import com.google.zxing.common.HybridBinarizer

/** Owned by the single analysis executor; no camera data is stored. */
class QrDecoder {
    private val reader=MultiFormatReader().apply{setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),DecodeHintType.TRY_HARDER to true))}
    fun decode(input:LuminanceSource):String? {
        // QR finder patterns determine orientation. PlanarYUVLuminanceSource
        // cannot rotate, and rotating it on every failed frame wastes copies.
        return try{reader.decodeWithState(BinaryBitmap(HybridBinarizer(input))).text}
        catch(_:ReaderException){null}
        finally{reader.reset()}
    }
}
