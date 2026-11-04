// Shared presentation; the host adapters supply persisted data and commands.
let routeConsentDialog=null;let routingKnownRoutes=null;
function subscriptionRoutingPaint(data,mobile){
 const subscriptions=data?.subscriptions||[],routes=data?.routes||[];
 const blockedCount=data?.unsupportedStoredCount||0,main=root.querySelector(mobile?'.content':'.main');
 if(blockedCount&&main&&!main.querySelector('.unsupported-stored-warning')){const banner=document.createElement('p');banner.className='unsupported-stored-warning';banner.setAttribute('role','status');banner.textContent='VMess, Shadowsocks и Trojan пока не поддерживаются. Скрыто конфигураций: '+blockedCount+'. Выберите поддерживаемый сервер.';main.prepend(banner);}
 for(const option of root.querySelectorAll('select option'))if(['vmess','shadowsocks','ss','trojan'].includes(String(option.value).toLowerCase()))option.remove();
 for(const row of root.querySelectorAll('[data-context="route"]')){
  const id=row.getAttribute('data-object')||row.getAttribute('data-object-id');
  const route=routes.find(r=>r.id===id),sub=subscriptions.find(s=>s.id===(route?.managedBy||route?.managedBySubscriptionId));
  if(id==='none')for(const button of row.querySelectorAll('[data-action]'))if(/edit|delete/.test(button.dataset.action))button.remove();
  if(sub&&!row.querySelector('.route-subscription-source')){
   const details=document.createElement('details');details.className='route-subscription-source';details.style.cssText='grid-column:1/-1;flex-basis:100%;width:100%;min-width:0;margin-bottom:10px';row.style.flexWrap='wrap';
   const title=document.createElement('summary');title.textContent=sub.name+' · '+sub.url;title.style.cssText='overflow:hidden;text-overflow:ellipsis;white-space:nowrap';details.append(title);
   const url=document.createElement('p');url.textContent=sub.url;url.style.cssText='overflow-wrap:anywhere;user-select:text';details.append(url);
   const copy=document.createElement('button');copy.textContent='Копировать ссылку';copy.onclick=event=>{event.stopPropagation();routingUiCopy(sub.url);};details.append(copy);details.onclick=event=>event.stopPropagation();row.prepend(details);
  }
 }
 for(const row of root.querySelectorAll('[data-context="subscription"]')){
  const id=row.getAttribute('data-object')||row.getAttribute('data-object-id'),sub=subscriptions.find(s=>s.id===id);
  if(sub?.importWarning&&!row.querySelector('.subscription-import-warning')){const note=document.createElement('p');note.className='subscription-import-warning';note.style.cssText='grid-column:1/-1;flex-basis:100%;width:100%';note.setAttribute('role','status');note.textContent=sub.importWarning;row.prepend(note);}
 }
 if(!document.getElementById('routing-toggle-style')){const style=document.createElement('style');style.id='routing-toggle-style';style.textContent='.routing-master input{appearance:none!important;-webkit-appearance:none!important;position:relative;width:44px!important;height:26px!important;border:1px solid #7776!important;border-radius:20px!important;background:#555!important;cursor:pointer;flex:none;transition:background .18s}.routing-master input:checked{background:#4c9b72!important}.routing-master input:before{content:"";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .18s}.routing-master input:checked:before{transform:translateX(18px)}.routing-master input:focus-visible{outline:2px solid currentColor;outline-offset:3px}@media(prefers-reduced-motion:reduce){.routing-master input,.routing-master input:before{transition:none}}';document.head.append(style);}
 const content=root.querySelector(mobile?'.content':'.main');
 if((mobile?screen==='routes':page==='library'&&libraryKind==='routing')&&content&&!content.querySelector('.routing-master')){const label=document.createElement('label');label.className='routing-master';label.style.cssText='display:flex;align-items:center;gap:14px;padding:16px 0';label.textContent='Использовать маршрутизацию';const input=document.createElement('input');input.type='checkbox';input.setAttribute('role','switch');input.checked=data?.routingEnabled!==false;input.onchange=()=>mobile?command('route-enabled',{enabled:input.checked}):perform('route.enabled',{enabled:input.checked});label.append(input);content.prepend(label);}
 if(!data||(mobile&&data.schema!==1))return;
 const known=new Set(routes.filter(r=>r.managedBy||r.managedBySubscriptionId).map(r=>r.id));
 if(routingKnownRoutes)for(const id of known)if(!routingKnownRoutes.has(id)){const r=routes.find(r=>r.id===id),sub=subscriptions.find(s=>s.id===(r.managedBy||r.managedBySubscriptionId));const text='Маршрутизация подписки «'+(sub?.name||'')+'» добавлена. Управление — во вкладке «Маршрутизация».';setTimeout(()=>mobile?notify(text):showToast(text),0);}
 routingKnownRoutes=known;
}
window.iorAndroidCloseRouteConsent=()=>false;
