// Android import sheet: one compositor animation owns transform at a time.
// Drag updates are coalesced to display frames; scrolling inside the form stays native.
let androidSheetDrag=null,androidSheetMotion=null,androidSheetFrame=0,androidSheetRenderPending=false;
const androidSheetFrameRequest=callback=>window.requestAnimationFrame?window.requestAnimationFrame(callback):setTimeout(callback,16);
const androidSheetFrameCancel=id=>window.cancelAnimationFrame?window.cancelAnimationFrame(id):clearTimeout(id);
const androidSheetTranslate=y=>'translate3d(0,'+y+'px,0)';
function androidSheetTransform(panel){return window.getComputedStyle?.(panel)?.transform||panel.style.transform||androidSheetTranslate(0);}
function androidSheetOffset(panel){
 const transform=androidSheetTransform(panel);
 try{if(window.DOMMatrixReadOnly)return new window.DOMMatrixReadOnly(transform==='none'?undefined:transform).m42;}catch{}
 const translate=transform.match(/translate3d\(0,\s*(-?[\d.]+)px/);return translate?Number(translate[1]):0;
}
function androidSheetFlush(){
 if(androidSheetFrame){androidSheetFrameCancel(androidSheetFrame);androidSheetFrame=0;}
 if(androidSheetDrag)androidSheetDrag.panel.style.transform=androidSheetTranslate(androidSheetDrag.offset);
}
function androidSheetCancelMotion(){
 const current=androidSheetMotion;if(!current)return;
 const transform=androidSheetTransform(current.panel);
 androidSheetMotion=null;
 current.animation.onfinish=current.animation.oncancel=null;
 current.panel.style.transform=transform;
 current.animation.cancel();current.scrimAnimation?.cancel();
}
function androidSheetRenderDeferred(){
 if(androidSheetRenderPending&&!androidSheetDrag&&!androidSheetMotion){androidSheetRenderPending=false;render();}
}
function androidSheetAnimate(panel,from,to,duration,done,closing=false,opening=false){
 androidSheetCancelMotion();
 if(reduceMotion()||!panel?.animate){panel.style.transform=to;done();return;}
 panel.classList.add('android-sheet-moving');
 const animation=panel.animate([{transform:from},{transform:to}],{duration,easing:'cubic-bezier(.2,.8,.2,1)',fill:'both'});
 const current={panel,animation,target:sheet,scrimAnimation:null};androidSheetMotion=current;
 if(closing||opening){const overlay=root.querySelector('.overlay');if(overlay?.animate)current.scrimAnimation=overlay.animate([{opacity:closing?1:0},{opacity:closing?0:1}],{duration,easing:'linear',fill:'both'});}
 const finish=()=>{
  if(androidSheetMotion!==current)return;
  androidSheetMotion=null;panel.style.transform=to;panel.classList.remove('android-sheet-moving');
  animation.onfinish=animation.oncancel=null;animation.cancel();current.scrimAnimation?.cancel();
  done();androidSheetRenderDeferred();
 };
 animation.onfinish=finish;animation.oncancel=finish;
}
const androidSheetOriginalAnimate=animateOnce;
animateOnce=function(key,el,frames,options){
 if(sheet?.kind==='import'&&(key==='sheet'||key==='scrim')){
  if(key==='sheet'&&el){androidSheetAnimate(el,androidSheetTranslate(48),androidSheetTranslate(0),240,()=>{el.style.transform='';},false,true);}
  return null;
 }
 return androidSheetOriginalAnimate(key,el,frames,options);
};
const androidSheetOriginalBack=back;
back=function(fromHistory=false){
 if(sheet?.kind!=='import')return androidSheetOriginalBack(fromHistory);
 if(sheet.closing)return;
 const panel=root.querySelector('.import-sheet');
 if(!panel)return backWithoutTransitions(fromHistory);
 const target=sheet;target.closing=true;
 androidSheetFlush();androidSheetDrag=null;
 androidSheetCancelMotion();
 const from=androidSheetTransform(panel),height=panel.getBoundingClientRect?.().height||innerHeight;
 androidSheetAnimate(panel,from,androidSheetTranslate(height+24),220,()=>{
  if(sheet===target){delete target.closing;backWithoutTransitions(fromHistory);}
 },true);
};
const androidSheetOriginalRender=render;
render=function(){
 if(sheet?.kind==='import'&&(androidSheetDrag||androidSheetMotion?.target===sheet)){
  androidSheetRenderPending=true;return;
 }
 return androidSheetOriginalRender();
};
root.addEventListener('pointerdown',event=>{
 if(event.button!==0||sheet?.kind!=='import'||sheet.closing||!event.target.closest('.grabber,.sheet-heading')||event.target.closest('button,input,textarea,select'))return;
 const panel=root.querySelector('.import-sheet');if(!panel)return;
 androidSheetCancelMotion();const initialOffset=androidSheetOffset(panel);panel.classList.add('android-sheet-moving');
 androidSheetDrag={pointer:event.pointerId,panel,target:sheet,start:event.clientY,lastY:event.clientY,lastTime:event.timeStamp,velocity:0,initialOffset,offset:initialOffset,dy:0,height:panel.getBoundingClientRect?.().height||innerHeight};
 try{panel.setPointerCapture?.(event.pointerId);}catch{}
});
root.addEventListener('pointermove',event=>{
 const drag=androidSheetDrag;if(!drag||drag.pointer!==event.pointerId||sheet!==drag.target)return;
 const elapsed=event.timeStamp-drag.lastTime;
 if(elapsed>0)drag.velocity=(event.clientY-drag.lastY)/elapsed;
 drag.lastY=event.clientY;drag.lastTime=event.timeStamp;drag.dy=event.clientY-drag.start;
 const offset=drag.initialOffset+drag.dy;
 drag.offset=offset<0?-Math.min(24,-offset*.18):Math.min(drag.height,offset);
 event.preventDefault();
 if(!androidSheetFrame)androidSheetFrame=androidSheetFrameRequest(()=>{androidSheetFrame=0;if(androidSheetDrag===drag)drag.panel.style.transform=androidSheetTranslate(drag.offset);});
});
function androidSheetRelease(event){
 const drag=androidSheetDrag;if(!drag||drag.pointer!==event.pointerId)return;
 androidSheetFlush();androidSheetDrag=null;
 try{drag.panel.releasePointerCapture?.(event.pointerId);}catch{}
 if(sheet!==drag.target){drag.panel.classList.remove('android-sheet-moving');androidSheetRenderDeferred();return;}
 const recent=event.timeStamp-drag.lastTime<100;
 if(event.type!=='pointercancel'&&(drag.dy>Math.min(120,drag.height*.25)||drag.dy>24&&recent&&drag.velocity>.6)){back();return;}
 androidSheetAnimate(drag.panel,androidSheetTranslate(drag.offset),androidSheetTranslate(0),180,()=>{drag.panel.style.transform='';drag.panel.classList.remove('android-sheet-moving');});
}
root.addEventListener('pointerup',androidSheetRelease);
root.addEventListener('pointercancel',androidSheetRelease);
document.addEventListener('visibilitychange',()=>{
 if(!document.hidden)return;
 if(androidSheetDrag){const drag=androidSheetDrag;androidSheetFlush();androidSheetDrag=null;drag.panel.style.transform='';drag.panel.classList.remove('android-sheet-moving');}
 androidSheetMotion?.animation.finish?.();androidSheetRenderDeferred();
});
