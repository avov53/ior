package app.ior.android

import android.Manifest
import android.content.Intent
import android.os.Bundle
import android.content.pm.PackageManager
import android.graphics.*
import android.util.Size
import android.view.View
import android.view.MotionEvent
import android.widget.*
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.*
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.camera.view.transform.CoordinateTransform
import androidx.camera.view.transform.ImageProxyTransformFactory
import androidx.camera.view.transform.OutputTransform
import androidx.core.content.ContextCompat
import com.google.zxing.*
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.TimeUnit

class QrActivity:ComponentActivity() {
    private val worker=Executors.newSingleThreadExecutor()
    private val scanned=AtomicBoolean(false)
    private lateinit var previewView:PreviewView
    private var camera:androidx.camera.core.Camera?=null
    private var provider:ProcessCameraProvider?=null
    private val geometry=AtomicReference<Pair<OutputTransform,RectF>?>()
    private val decoder=QrDecoder()
    private val transformFactory=ImageProxyTransformFactory().apply{isUsingCropRect=false;isUsingRotationDegrees=false}
    private var luma=ByteArray(0)
    private var row=ByteArray(0)
    private var torch=false
    private val en get()=Repository.get(this).snapshot().let{it.optString("language")=="en"||it.optString("language","system")=="system"&&it.optString("systemLanguage")=="en"}
    private val permission=registerForActivityResult(ActivityResultContracts.RequestPermission()){if(it)startCamera()else{Toast.makeText(this,if(en)"Camera permission denied"else"Нет разрешения камеры",Toast.LENGTH_LONG).show();finish()}}
    override fun onCreate(savedInstanceState:Bundle?){
        super.onCreate(savedInstanceState)
        val box=FrameLayout(this);previewView=PreviewView(this).apply{implementationMode=PreviewView.ImplementationMode.COMPATIBLE}
        box.addView(previewView,FrameLayout.LayoutParams(-1,-1))
        val guide=object:View(this){
            val paint=Paint(Paint.ANTI_ALIAS_FLAG)
            override fun onDraw(canvas:Canvas){super.onDraw(canvas);val rect=frame(width,height)
                paint.color=0x99000000.toInt();paint.style=Paint.Style.FILL
                val shade=Path().apply{fillType=Path.FillType.EVEN_ODD;addRect(0f,0f,width.toFloat(),height.toFloat(),Path.Direction.CW);addRoundRect(rect,20f,20f,Path.Direction.CW)};canvas.drawPath(shade,paint)
                paint.color=Color.WHITE;paint.style=Paint.Style.STROKE;paint.strokeWidth=3*resources.displayMetrics.density;canvas.drawRoundRect(rect,20f,20f,paint)
                previewView.outputTransform?.let{geometry.set(it to rect)}
            }
        }
        box.addView(guide,FrameLayout.LayoutParams(-1,-1))
        val controls=LinearLayout(this).apply{orientation=LinearLayout.VERTICAL;setPadding(24,24,24,32);setBackgroundColor(0xb0000000.toInt())}
        controls.addView(TextView(this).apply{text=if(en)"Place QR inside the frame"else"Поместите QR-код в рамку";setTextColor(Color.WHITE);textSize=17f})
        val buttons=LinearLayout(this)
        buttons.addView(Button(this).apply{text=if(en)"Back"else"Назад";setOnClickListener{finish()}})
        val light=Button(this).apply{text=if(en)"Flashlight"else"Фонарик";isEnabled=false;setOnClickListener{torch=!torch;camera?.cameraControl?.enableTorch(torch);isSelected=torch}}
        buttons.addView(light);controls.addView(buttons)
        val zoom=SeekBar(this).apply{max=100;contentDescription=if(en)"Zoom"else"Увеличение";setOnSeekBarChangeListener(object:SeekBar.OnSeekBarChangeListener{override fun onProgressChanged(bar:SeekBar?,value:Int,user:Boolean){if(user)camera?.cameraControl?.setLinearZoom(value/100f)};override fun onStartTrackingTouch(bar:SeekBar?){};override fun onStopTrackingTouch(bar:SeekBar?){} })}
        controls.addView(zoom)
        box.addView(controls,FrameLayout.LayoutParams(-1,-2,android.view.Gravity.BOTTOM))
        box.setOnApplyWindowInsetsListener{_,insets->val bars=insets.getInsets(android.view.WindowInsets.Type.systemBars() or android.view.WindowInsets.Type.displayCutout());box.setPadding(bars.left,bars.top,bars.right,bars.bottom);insets}
        previewView.setOnTouchListener{_,event->if(event.action==MotionEvent.ACTION_UP){focus(event.x,event.y);true}else true}
        previewView.addOnLayoutChangeListener{_,_,_,_,_,_,_,_,_->guide.invalidate();previewView.post{focus(previewView.width/2f,previewView.height*.43f)}}
        setContentView(box)
        torchButton=light;guideView=guide
        previewView.previewStreamState.observe(this){if(it==PreviewView.StreamState.STREAMING){guide.invalidate();focus(previewView.width/2f,previewView.height*.43f)}}
        if(ContextCompat.checkSelfPermission(this,Manifest.permission.CAMERA)==PackageManager.PERMISSION_GRANTED)startCamera()else permission.launch(Manifest.permission.CAMERA)
    }
    private var torchButton:Button?=null
    private var guideView:View?=null
    private fun frame(width:Int,height:Int):RectF{val side=minOf(width*.8f,height*.46f);val x=(width-side)/2;val y=(height*.43f-side/2).coerceAtLeast(16f);return RectF(x,y,x+side,y+side)}
    private fun focus(x:Float,y:Float){val cam=camera?:return;val point=previewView.meteringPointFactory.createPoint(x,y);cam.cameraControl.startFocusAndMetering(FocusMeteringAction.Builder(point,FocusMeteringAction.FLAG_AF or FocusMeteringAction.FLAG_AE).setAutoCancelDuration(3,TimeUnit.SECONDS).build())}
    private fun startCamera(){
        val future=ProcessCameraProvider.getInstance(this)
        future.addListener({try{
            if(isFinishing||isDestroyed)return@addListener
            val viewport=previewView.viewPort?:run{previewView.post{startCamera()};return@addListener}
            val owner=future.get();provider=owner
            val preview=Preview.Builder().build().apply{setSurfaceProvider(previewView.surfaceProvider)}
            val analysis=ImageAnalysis.Builder().setResolutionSelector(ResolutionSelector.Builder().setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY).setResolutionStrategy(ResolutionStrategy(Size(1280,720),ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER)).build()).setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
            analysis.setAnalyzer(worker){image->try{
                if(scanned.get())return@setAnalyzer
                val currentGeometry=geometry.get()?:return@setAnalyzer
                val plane=image.planes[0];val length=image.width*image.height
                if(luma.size!=length)luma=ByteArray(length)
                if(row.size<plane.rowStride)row=ByteArray(plane.rowStride)
                val buffer=plane.buffer.duplicate()
                for(y in 0 until image.height){buffer.position(y*plane.rowStride);val count=minOf(plane.rowStride,buffer.remaining());buffer.get(row,0,count);if(plane.pixelStride==1)System.arraycopy(row,0,luma,y*image.width,image.width)else for(x in 0 until image.width)luma[y*image.width+x]=row[x*plane.pixelStride]}
                val roi=currentGeometry.let{(output,rect)->RectF(rect).also{CoordinateTransform(output,transformFactory.getOutputTransform(image)).mapRect(it)}}
                val crop=image.cropRect
                val left=roi.left.toInt().coerceIn(crop.left,crop.right-1);val top=roi.top.toInt().coerceIn(crop.top,crop.bottom-1)
                val right=roi.right.toInt().coerceIn(left+1,crop.right);val bottom=roi.bottom.toInt().coerceIn(top+1,crop.bottom)
                val source:LuminanceSource=PlanarYUVLuminanceSource(luma,image.width,image.height,left,top,right-left,bottom-top,false)
                val result=decoder.decode(source)
                if(result!=null&&scanned.compareAndSet(false,true))runOnUiThread{analysis.clearAnalyzer();owner.unbindAll();setResult(RESULT_OK,Intent().putExtra("text",result));finish()}
            }catch(_:Exception){}finally{image.close()}}
            val group=UseCaseGroup.Builder().setViewPort(viewport).addUseCase(preview).addUseCase(analysis).build()
            camera=owner.bindToLifecycle(this,CameraSelector.DEFAULT_BACK_CAMERA,group)
            torchButton?.isEnabled=camera?.cameraInfo?.hasFlashUnit()==true
            camera?.cameraInfo?.zoomState?.observe(this){guideView?.invalidate()}
            previewView.post{guideView?.invalidate();focus(previewView.width/2f,previewView.height*.43f)}
        }catch(_:Exception){Toast.makeText(this,if(en)"Camera unavailable"else"Камера недоступна",Toast.LENGTH_LONG).show();finish()}},ContextCompat.getMainExecutor(this))
    }
    override fun onDestroy(){provider?.unbindAll();worker.shutdownNow();super.onDestroy()}
}
