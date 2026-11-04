package app.ior.android
import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import android.widget.EditText
import org.json.JSONObject
object Localization {
 private var dictionary:JSONObject?=null
 fun english(state:JSONObject)=state.optString("language","system")=="en" || state.optString("language","system")=="system"&&java.util.Locale.getDefault().language=="en"
 fun translate(context:Context,state:JSONObject,text:String):String{if(!english(state))return text;val map=dictionary?:context.assets.open("locales/en.json").use{JSONObject(it.bufferedReader().readText())}.also{dictionary=it};return map.optString(text,text)}
 fun apply(context:Context,state:JSONObject,view:View){
  if(view is TextView && view !is EditText)view.text=translate(context,state,view.text.toString())
  view.contentDescription?.let{view.contentDescription=translate(context,state,it.toString())}
  if(view is ViewGroup)for(i in 0 until view.childCount)apply(context,state,view.getChildAt(i))
 }
}
