// Inserted inside the mobile closure; Motion runs only during interactions.
const animations=new Map();let lastNav=null,lastTab='',lastWidth=0,lastX=0,lastSheet=null,swipe=null;
const reduceMotion=()=>!!state.reduced||!!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
const canAnimate=el=>!reduceMotion()&&!document.hidden&&!!window.mobileMotion&&typeof el?.animate==='function';
function stop(key){animations.get(key)?.stop();animations.delete(key);}
function stopAll(){for(const control of animations.values())control.stop();animations.clear();}
function animateOnce(key,el,frames,options){stop(key);if(!canAnimate(el))return null;try{const control=window.mobileMotion.animate(el,frames,options);animations.set(key,control);control.finished.then(()=>{if(animations.get(key)===control)animations.delete(key);},()=>{if(animations.get(key)===control)animations.delete(key);});return control;}catch{return null;}}
function syncNavigation(force=false){
  const nav=root.querySelector('.bottomnav'),indicator=nav?.querySelector('.tab-indicator'),button=nav?.querySelector('[aria-current=page]');
  if(!indicator||!button||!nav.getBoundingClientRect)return;
  const box=nav.getBoundingClientRect(),target=button.getBoundingClientRect();if(!box.width||!target.width)return;
  const width=nav.classList.contains('capsule')?48:target.width,x=target.left-box.left+(target.width-width)/2-(nav.clientLeft||0),tab=button.dataset.id,first=lastNav!==indicator;
  if(!first&&lastTab===tab&&lastWidth===width&&lastX===x){if(force&&!canAnimate(indicator)){stop('nav');indicator.style.transform='translateX('+x+'px)';indicator.style.width=width+'px';}return;}
  const changed=!first&&lastTab!==tab;
  if(first||!canAnimate(indicator)){stop('nav');indicator.style.transform='translateX('+x+'px)';indicator.style.width=width+'px';}
  else {const control=animateOnce('nav',indicator,{x,width},{duration:.16,ease:'easeOut'});if(!control){indicator.style.transform='translateX('+x+'px)';indicator.style.width=width+'px';}}
  if(changed)animateOnce('nav-icon',button.querySelector('svg'),{scale:[.92,1.06,1]},{duration:.22,ease:'easeOut'});
  lastNav=indicator;lastTab=tab;lastWidth=width;lastX=x;
}
const renderWithoutTransitions=render;render=function(){
  const previous=renderedScreen;renderWithoutTransitions();
  if(reduceMotion())stopAll();syncNavigation();
  const panel=root.querySelector('.sheet');
  if(panel!==lastSheet){stop('sheet');stop('scrim');if(panel){animateOnce('sheet',panel,sheet?.kind==='picker'?{y:[-12,0],scale:[.96,1],opacity:[0,1]}:{y:[36,0],opacity:[.3,1]},{duration:.26,ease:[.22,1,.36,1]});animateOnce('scrim',root.querySelector('.overlay'),{opacity:[0,1]},{duration:.16});}lastSheet=panel;}
  if(previous!==screen)animateOnce('page',root.querySelector('.content'),{opacity:[.6,1],y:[5,0]},{duration:.18,ease:'easeOut'});
};
const backWithoutTransitions=back;back=function(fromHistory=false){
  if(sheet?.closing){if(fromHistory){stop('sheet');stop('scrim');delete sheet.closing;backWithoutTransitions(true);}return;}
  const panel=root.querySelector('.sheet');
  if(sheet&&!fromHistory&&canAnimate(panel)){
    const target=sheet;target.closing=true;
    const control=animateOnce('sheet',panel,sheet.kind==='picker'?{y:-8,scale:.97,opacity:0}:{y:96,opacity:0},{duration:.22,ease:[.4,0,.7,.2]});
    animateOnce('scrim',root.querySelector('.overlay'),{opacity:0},{duration:.22});
    if(control){control.finished.then(()=>{if(sheet===target){delete target.closing;backWithoutTransitions(false);}},()=>{if(sheet===target){delete target.closing;backWithoutTransitions(false);}});return;}
    delete target.closing;
  }
  backWithoutTransitions(fromHistory);
};
root.addEventListener('pointerdown',event=>{
  if(event.button!==0||!sheet||sheet.closing||!event.target.closest('.grabber,.sheet-heading')||event.target.closest('button,input,textarea,select'))return;
  swipe={pointer:event.pointerId,y:event.clientY,dy:0,panel:root.querySelector('.sheet'),target:sheet};
  try{swipe.panel?.setPointerCapture?.(event.pointerId);}catch{}
});
root.addEventListener('pointermove',event=>{if(!swipe||event.pointerId!==swipe.pointer||sheet!==swipe.target)return;swipe.dy=Math.max(0,event.clientY-swipe.y);if(swipe.dy>8){stop('sheet');event.preventDefault();swipe.panel.style.transform='translateY('+Math.min(180,swipe.dy*.75)+'px)';}});
function releaseSwipe(event){if(!swipe||event.pointerId!==swipe.pointer)return;const current=swipe;swipe=null;try{current.panel?.releasePointerCapture?.(event.pointerId);}catch{}if(sheet!==current.target)return;if(event.type!=='pointercancel'&&current.dy>80){back();return;}const control=animateOnce('sheet',current.panel,{y:0},{type:'spring',stiffness:420,damping:36});if(!control)current.panel.style.transform='translateY(0)';}
root.addEventListener('pointerup',releaseSwipe);root.addEventListener('pointercancel',releaseSwipe);
root.addEventListener('click',event=>{if(event.target.classList?.contains('overlay'))back();});
window.addEventListener('resize',()=>{size();syncNavigation(true);});
document.addEventListener('visibilitychange',()=>{if(document.hidden)stopAll();else{syncNavigation(true);if(sheet?.closing){delete sheet.closing;backWithoutTransitions(false);}}});
window.matchMedia?.('(prefers-reduced-motion: reduce)').addEventListener?.('change',()=>{if(reduceMotion()){stopAll();if(sheet?.closing){delete sheet.closing;backWithoutTransitions(false);}}syncNavigation(true);});
