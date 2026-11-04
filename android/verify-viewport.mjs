// Geometry regression check for the packaged WebView document in Chromium.
// Uses only synthetic profiles and an isolated browser profile, never a VPN.
import {readFile,writeFile} from 'node:fs/promises';
const before=process.argv.includes('--before');
let html=await readFile(new URL('app/src/main/assets/index.html',import.meta.url),'utf8');
if(before)html=html.replace(/<style id="android-viewport">[\s\S]*?<\/style>/,'<style>html,body{height:100%;overflow:hidden}.app{height:100%!important;max-height:100%!important}</style>');
html=html.replace('<head>','<head><script>window.iorNative={postMessage(){}};</script>');
const script=`<script>addEventListener('unhandledrejection',event=>document.body.setAttribute('data-layout-error',String(event.reason)));addEventListener('load',()=>setTimeout(async()=>{
 const results=[];
 for(const theme of ['dark','light'])for(const height of [740,480]){
  document.documentElement.style.height=height+'px';
  window.iorAndroidReceive({event:'state',data:{theme,reduced:true,servers:Array.from({length:18},(_,i)=>({id:'layout-'+i,name:'Layout '+i,protocol:'AmneziaWG',address:'example.invalid',subscriptionId:''})),subscriptions:[],routes:[],profiles:[],selected:'layout-0',selectedRoute:'',closed:[],logs:[],connection:'off',drafts:{link:{},subscription:{},file:{}}}});
  window.iorAndroidReceive({event:'insets',data:{keyboard:height===480}});
  const app=document.querySelector('#mobile>.app'),content=app.querySelector('.content'),nav=app.querySelector('.bottomnav');
  const area=app.getBoundingClientRect(),bar=nav.getBoundingClientRect();content.scrollTop=80;
  results.push({theme,height,appHeight:area.height,navBottom:bar.bottom,navVisible:getComputedStyle(nav).display!=='none'&&bar.height>0&&bar.bottom<=height+1,scrollable:content.scrollHeight>content.clientHeight&&content.scrollTop>0});
 }
 document.body.setAttribute('data-viewport-results',JSON.stringify(results));
 document.documentElement.style.height='740px';
 window.iorAndroidReceive({event:'state',data:{subscriptions:[{id:'touch-sub',name:'Touch subscription'}],servers:[{id:'touch-server',name:'Touch server',protocol:'AmneziaWG',subscriptionId:'touch-sub'}],selected:'touch-server',closed:[]}});
 const menus=[];
 for(const kind of ['server','subscription']){
  const row=document.querySelector('[data-context="'+kind+'"]');const box=row.getBoundingClientRect();const x=box.left+box.width/2,y=box.top+box.height/2;
  document.documentElement.classList.add('android-touch');
  row.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:x,clientY:y}));
  const menu=document.querySelector('.context-menu').getBoundingClientRect();
  menus.push({kind,y,top:menu.top,bottom:menu.bottom,nearTouch:Math.min(Math.abs(menu.top-y),Math.abs(menu.bottom-y))<2});
  document.dispatchEvent(new PointerEvent('pointerup'));window.iorAndroidBack();await new Promise(resolve=>setTimeout(resolve,120));
 }
 document.body.setAttribute('data-menu-results',JSON.stringify(menus));
 const polish=[];
 for(const theme of ['dark','light']){
  window.iorAndroidReceive({event:'state',data:{theme}});
  for(const tab of ['home','routes','settings']){
   document.querySelector('.bottomnav [data-id="'+tab+'"]').click();
   const indicator=document.querySelector('.tab-indicator').getBoundingClientRect(),button=document.querySelector('.bottomnav [aria-current="page"]').getBoundingClientRect();
   polish.push({theme,tab,centered:Math.abs(indicator.left+indicator.width/2-button.left-button.width/2)<1&&Math.abs(indicator.top+indicator.height/2-button.top-button.height/2)<1});
  }
  const groups=document.querySelectorAll('.settings-section'),gap=groups[1].getBoundingClientRect().top-groups[0].getBoundingClientRect().bottom;
  polish.push({theme,settingsGap:gap,gapCorrect:Math.abs(gap-16)<1,icons:document.querySelectorAll('.native-settings .settings-icon').length===5});
 }
 document.body.setAttribute('data-polish-results',JSON.stringify(polish));
},100));</script>`;
await writeFile(new URL('../android-output/current/viewport-check.html',import.meta.url),html.replace('</body>',script+'</body>'));
