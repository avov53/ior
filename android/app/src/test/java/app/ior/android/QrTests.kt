package app.ior.android

import com.google.zxing.*
import com.google.zxing.qrcode.QRCodeWriter
import org.junit.Assert.*
import org.junit.Test

class QrTests {
    @Test fun recognizesSubscriptionAndProfileAtDifferentSizesAndRotations(){
        val decoder=QrDecoder()
        for(text in listOf("https://example.test/subscription","hy2://secret@example.test:443"))for(size in listOf(128,256,512)){
            val bits=QRCodeWriter().encode(text,BarcodeFormat.QR_CODE,size,size)
            val bytes=ByteArray(size*size){i->if(bits.get(i%size,i/size))0 else 255.toByte()}
            var image=bytes
            repeat(4){assertEquals(text,decoder.decode(PlanarYUVLuminanceSource(image,size,size,0,0,size,size,false)));val next=ByteArray(size*size);for(y in 0 until size)for(x in 0 until size)next[(size-1-x)*size+y]=image[y*size+x];image=next}
        }
        assertNull(decoder.decode(PlanarYUVLuminanceSource(ByteArray(128*128){255.toByte()},128,128,0,0,128,128,false)))
    }
}
