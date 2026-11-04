import {readFile,writeFile,mkdir,readdir,copyFile} from 'node:fs/promises';
const prototype=new URL('../design/prototypes/mobile/index.html',import.meta.url);
let androidRuntime=await readFile(new URL('ui-runtime.js',import.meta.url),'utf8');
androidRuntime+='\n'+await readFile(new URL('sheet-gestures.js',import.meta.url),'utf8');
androidRuntime=androidRuntime.replace('const nativeTranslations=',(await readFile(new URL('subscription-pages.js',import.meta.url),'utf8'))+'\nconst nativeTranslations=');
androidRuntime+='\n'+await readFile(new URL('../design/scripts/subscription-routing-ui.js',import.meta.url),'utf8')+`\nconst routingUiSettingsContext=()=>screen==='setting-subscription'?{id:nativeSubscriptionId,node:root.querySelector('.native-options')}:null;const routingUiReview=id=>command('route-consent',{id,review:true});const routingUiCopy=text=>command('copy',{text});const routingUiConsent=(id,accept)=>command('route-consent',{id,accept});const routingUiModalBusy=()=>!!sheet;const routingUiReduced=()=>reduceMotion();const routingUiRender=render;render=function(){routingUiRender();subscriptionRoutingPaint(state,true);};`;
const androidStyles=await readFile(new URL('native-ui.css',import.meta.url),'utf8');
let html=await readFile(prototype,'utf8');
const prototypeSwipe="if(event.button!==0||!sheet||sheet.closing||!event.target.closest('.grabber,.sheet-heading')";
if(!html.includes(prototypeSwipe))throw Error('Mobile prototype swipe changed: review Android gestures');
html=html.replace(prototypeSwipe,"if(event.button!==0||!sheet||sheet.kind==='import'||sheet.closing||!event.target.closest('.grabber,.sheet-heading')");
const anchor="const root=document.querySelector('#mobile'),key='ior-mobile-prototype-v1';";
if(!html.includes(anchor))throw Error('Mobile prototype runtime changed: review Android adapter');
html=html.replace(anchor,anchor+`
  const command=(action,data={})=>window.iorNative?.postMessage(JSON.stringify({version:1,id:crypto.randomUUID(),action,data}));
  let receivedState=false;
  window.iorAndroidReceive=value=>{
    if(value.event==='state'){
      const drafts=receivedState?state.drafts:(value.data.drafts||state.drafts);state={...state,...value.data,drafts};receivedState=true;
      connection=value.data.connection==='on'?'on':(['pending','reconnecting'].includes(value.data.connection)?'pending':'off');
      connectionError=value.data.connectionError||'';
      render();
    }else if(value.event==='stats'){state.stats=value.data;updateNativeStats();}
    else if(value.event==='lan'){state.lanStatus=value.data.status;const label=root.querySelector('[data-lan-status]');if(label)label.textContent=value.data.status;}
    else if(value.event==='latency'){const item=state.servers.find(s=>s.id===value.data.id);if(item){Object.assign(item,value.data);updateNativePing(item);}}
    else if(value.event==='log'){state.logs.push(value.data);if(state.logs.length>2000)state.logs.shift();if(screen==='logs'&&value.data.text.toLowerCase().includes(logSearch.toLowerCase())){const container=root.querySelector('.content');container?.insertAdjacentHTML('beforeend','<div class="log-entry"><time>'+esc(value.data.time)+'</time>'+esc(value.data.text)+'</div>');if(container){const rows=container.querySelectorAll('.log-entry');if(rows.length>2000)rows[0].remove();}}}
    else if(value.event==='insets'){document.documentElement.classList.toggle('keyboard-open',value.data.keyboard);syncNavigation(true);}
    else if(value.event==='apps'){appExamples=value.data;state.appCatalog=true;nativeAppsLoaded=true;nativeAppsLoading=false;nativeAppsError='';render();}
    else if(value.event==='journal-size'){nativeJournalBytes=value.data;render();}
    else if(value.event==='apps-error'){nativeAppsLoading=false;nativeAppsError=value.data;render();}
    else if(value.event==='apps-invalidated'){nativeAppsLoaded=false;nativeAppsLoading=false;nativeAppsError='';if(screen==='setting-apps')render();}
    else if(value.event==='import'){const text=String(value.data).trim(),tab=text.toLowerCase().startsWith('https://')?'subscription':text.startsWith('[Interface]')?'file':'link';state.drafts[tab].value=text;openSheet('import',{tab});render();}
    else if(value.event==='editor-saved'){if(editor){original=JSON.stringify(editor);back();}notify('Сохранено');}
    else if(value.event==='message'){notify(value.data);}
  };
  root.addEventListener('click',event=>{
    const button=event.target.closest('[data-action]');if(!button)return;
    const action=button.dataset.action,id=button.dataset.id||'';
    const handled=['connect','select','confirm-server-switch','import-save','save-editor','duplicate','delete-object','validate-config','refresh','refresh-subscription','apps-examples','copy-share','copy-logs','clear-logs','paste','unavailable','examples','clear-data','android-share','android-backup','android-restore','android-auto-update','object-import-save','export-object','android-actions','ping-group','ping-cancel','hwid-mode','native-settings-save','export-diagnostics','video-stall-marker'];
    if(!handled.includes(action))return;
    event.preventDefault();event.stopImmediatePropagation();
    if(action==='android-actions'){const row=button.closest('[data-context]');openSheet('menu',{objectKind:row.dataset.context,id:row.dataset.object,position:{x:event.clientX,y:event.clientY}});return;}
    if(action==='ping-group'){command('ping-group',{id});return;}
    if(action==='ping-cancel'){command('ping-cancel');return;}
    if(action==='export-diagnostics'||action==='video-stall-marker'){event.stopImmediatePropagation();command(action);return;}
    if(action==='hwid-mode'){command(action,{id,mode:button.dataset.mode});return;}
    if(action==='native-settings-save'){saveNativeSettings();return;}
    if(action==='connect'){command('connect');return;}
    if(action==='select'||action==='confirm-server-switch'){command('select',{id:action==='select'?id:sheet.id});if(sheet)back();return;}
    if(action==='import-save'){const draft=state.drafts[sheet.tab];command('import',{...draft,tab:sheet.tab});back();return;}
    if(action==='save-editor'){command('edit',{...editor});return;}
    if(action==='duplicate'){command('duplicate',{id:sheet.id});back();return;}
    if(action==='delete-object'){if(confirm('Удалить этот объект?')){command('delete',{id:sheet.id,kind:sheet.objectKind});back();}return;}
    if(action==='validate-config'){command('validate',{id:sheet?.id||id});return;}
    if(action==='refresh'||action==='refresh-subscription'){command('subscription-refresh',{id:sheet?.id||id});return;}
    if(action==='apps-examples'){nativeAppsError='';nativeAppsLoading=true;command('apps');render();return;}
    if(action==='copy-share'){command('copy',{text:sheet.value});return;}
    if(action==='copy-logs'){command('copy',{text:state.logs.map(l=>l.time+' '+l.text).join('\\n')});return;}
    if(action==='clear-logs'){command('clear-logs');return;}
    if(action==='paste'){command('paste');return;}
    if(action==='unavailable'){if(button.textContent.includes('QR'))command('scan');else if(button.textContent.includes('подписку'))command('subscription-refresh',{id:sheet.id});else notify('Действие пока не поддерживается');return;}
    if(action==='object-import-save'){command('import-route',{raw:sheet.value});return;}
    if(action==='export-object'){command('export-route',{id:sheet.id});return;}
    if(action==='android-share'){command('share',{text:sheet.value});return;}
    if(action==='android-backup'||action==='android-restore'){command(action.substring(8));return;}
    if(action==='android-auto-update'){command('auto-update');return;}
    if(action==='examples'){notify('В приложении используются только ваши конфигурации');return;}
    if(action==='clear-data'){notify('Для восстановления используйте резервную копию; удаление данных выполняется в настройках Android');}
  },true);
  new MutationObserver(()=>{
    const share=root.querySelector('[data-action="copy-share"]');
    if(share&&!root.querySelector('[data-action="android-share"]'))share.insertAdjacentHTML('afterend','<button class="button outline" data-action="android-share">Отправить</button>');
    const settings=root.querySelector('[data-action="clear-data"]');
    if(settings&&!root.querySelector('[data-action="android-backup"]'))settings.insertAdjacentHTML('beforebegin','<button class="button outline" data-action="android-backup">Резервная копия</button><button class="button outline" data-action="android-restore">Восстановить</button><button class="button outline" data-action="android-auto-update">Автообновление подписок</button>');
  }).observe(root,{childList:true,subtree:true});
`);
html=html.replace(/let state=structuredClone\(defaults\);try\{const saved=JSON\.parse\(localStorage\.getItem\(key\)\|\|'null'\);.*?\}catch\{\}/,'let state=structuredClone(defaults);');
html=html.replace("const persist=()=>{try{localStorage.setItem(key,JSON.stringify(state));}catch{}};","const persist=()=>command('settings',{theme:state.theme,reduced:state.reduced,backgroundMotion:state.backgroundMotion,closed:state.closed,drafts:state.drafts,appRouting:state.appRouting,selectedRoute:state.selectedRoute});");
const prototypeLog="function log(text){state.logs.push({time:new Date().toLocaleTimeString('ru-RU'),text});state.logs=state.logs.slice(-200);persist();}";
if(!html.includes(prototypeLog))throw Error('Mobile prototype log changed: review native journal adapter');
html=html.replace(prototypeLog,"function log(text){command('ui-log',{text:String(text).slice(0,512)});}");
const appsBegin=html.indexOf('const appExamples=['),appsEnd=html.indexOf("const routingDefaults=",appsBegin);
if(appsBegin<0||appsEnd<0)throw Error('Application catalog markers missing');
html=html.slice(0,appsBegin)+'let appExamples=[];\n'+html.slice(appsEnd);
html=html.replace('value.ids.filter(id=>appExamples.some(a=>a.id===id))',"value.ids.filter(id=>typeof id==='string')");
html=html.replaceAll('Демо: подключено','Подключено').replaceAll('Демо: подключение…','Подключение…').replaceAll('Демо: отключиться','Отключиться').replaceAll('Демо: подключиться','Подключиться');
html=html.replace("(!s||connection==='pending')?'disabled':''","!s?'disabled':''");
html=html.replace("!server||connection==='pending'?'disabled':''","!server?'disabled':''");
html=html.replace("connection==='on'?'Отключиться':'Подключиться'","connection==='on'?(state.activeId!==state.selected?'Переподключиться':'Отключиться'):connection==='pending'?'Отменить':'Подключиться'");
html=html.replace("function home(){const s=selected();","function home(){const s=connection!=='off'?(find('server',state.activeId)||selected()):selected();");
html=html.replace("function orbControl(){const server=selected(),","function orbControl(){const server=connection!=='off'?(find('server',state.activeId)||selected()):selected(),");
html=html.replaceAll("['happ','Happ'],",'');
html=html.replaceAll('ior-mobile-route','ior-android-route');
html=html.replaceAll('Правки сохраняются локально; ядро не проверяет содержимое.','Параметры проверяются ior-core при сохранении.');
html=html.replaceAll('Исходный формат хранится без разбора ядром.','Правила проверяются перед применением.');
html=html.replaceAll('Локальные события макета. Логи Android не подключены.','События приложения и Android VPN-службы.');
html=html.replaceAll('QR кодирует показанный текст. Камера и отправка в приложения не подключены.','QR содержит секреты конфигурации. Отправляйте его только доверенному получателю.');
html=html.replaceAll('Подписка сохраняется локально. Загрузка её серверов не подключена.','Подписка загружается по HTTPS. При ошибке прежние серверы сохраняются.');
html=html.replaceAll('Импорт предназначен для просмотра интерфейса. VPN и проверка ядром не выполняются.','Импорт поддерживает AWG, Hysteria2 и VLESS Reality.');
html=html.replaceAll('Локальный сценарий прототипа','Изменения применяются после переподключения');
html=html.replaceAll('HTML не получает список приложений телефона. Ниже — отдельный демонстрационный каталог.','Список установленных приложений Android; выбор применяется после переподключения.');
html=html.replaceAll('Загрузите примеры, чтобы проверить поиск и выбор.','Загрузите список установленных приложений.');
html=html.replace("<span class=\"app-tile\" aria-hidden=\"true\">\${a.letter}</span>","<span class=\"app-tile\" aria-hidden=\"true\">\${a.icon?'<img width=\"32\" height=\"32\" src=\"'+esc(a.icon)+'\" alt=\"\">':esc(a.letter)}</span>");
html=html.replace("['games','Игры'],['system','Системные']","['games','Игры'],['system','Системные'],['other','Другие']");
html=html.replace("if(d.kind==='subscription'){title=d.name;body='<p class=\"info\">'+esc(d.url)+'</p><p>Срок и расход трафика: —</p><p class=\"info\">Загрузка и измерения не подключены.</p>';}","if(d.kind==='subscription'){const item=state.subscriptions.find(s=>s.id===d.id)||d;const m=item.metadata||{};title=item.name;body='<p>'+esc(item.status||'Нет данных')+'</p><p>Расход: '+(m.upload!=null&&m.download!=null?esc(((m.upload+m.download)/1073741824).toFixed(2))+' ГиБ':'Не предоставляется')+'</p><p>Лимит: '+(m.total!=null?esc((m.total/1073741824).toFixed(2))+' ГиБ':'Не предоставляется')+'</p><p>Срок: '+(m.expire?esc(new Date(m.expire*1000).toLocaleDateString()):'Не предоставляется')+'</p>';}");
html=html.replaceAll('Загрузить примеры','Загрузить приложения').replaceAll('демонстрационный каталог','каталог установленных приложений');
html=html.replaceAll('WG / AWG','AWG');
html=html.replace('<title>ior · мобильный прототип</title>','<title>ior Android</title>').replaceAll('Экспорт макета','Экспорт маршрута');
html=html.replace('</head>','<style>.icon-button{min-width:48px;min-height:48px}.menu-button{min-height:48px}</style></head>');
html=html.replace('В Android подключение потребует системного разрешения VPN. Здесь показан только интерфейс.','VPN работает через ior-core Rust. При первом подключении Android запросит разрешение.');
html=html.replace('Загрузка подписки не подключена к Android.','Нет серверов. Обновите подписку или проверьте разрешение HWID.');
html=html.replace('<strong>${esc(group.name)}</strong><small>${items.length}</small>','<strong>${esc(group.name)}</strong><small>${esc(group.status||\'\')} · ${items.length}</small>');
html=html.replace('Измерения и проверки сервисов не подключены.', 'Статистика активной VPN-службы.');
html=html.replace("history.replaceState({iorMobile:true},'');render();",androidRuntime+"\nhistory.replaceState({iorMobile:true},'');render();");
html=html.replaceAll('ior · Мобильный прототип','ior · Android').replaceAll('ior · мобильный прототип','ior · Android').replaceAll('Данные прототипа','Данные приложения').replaceAll('локальном макете','приложении').replaceAll('локальный макет','приложение').replaceAll('Файл макета импортирован.','Маршрут импортирован.').replaceAll('мобильного прототипа','приложения').replaceAll('Очистить прототип','Данные приложения');
html=html.replaceAll('env(safe-area-inset-top)','0px').replaceAll('env(safe-area-inset-bottom)','0px');
html=html.replace('</head>',"<style id=\"android-viewport\">html,body,#mobile{height:100%;min-height:0;overflow:hidden}*{-webkit-tap-highlight-color:transparent}#mobile>.app{height:100%!important;max-height:100%!important;min-height:0}#mobile .app:not(.modal-open)>.content{overflow-y:auto;overflow-x:hidden;min-height:0;touch-action:pan-y}#mobile .app>.bottomnav.capsule{display:grid;pointer-events:auto}.context-menu{transition:none!important}.native-accessible{position:absolute;clip-path:inset(50%);width:1px;min-width:1px;height:1px;min-height:1px;padding:0;overflow:hidden}.native-accessible:focus{position:static;clip-path:none;width:auto;height:48px;min-width:48px}.server-right .ping{width:64px;white-space:nowrap}.native-settings{width:100%;justify-content:flex-start}</style></head>");
html=html.replace('<span class="ping" aria-label="Задержка неизвестна">—</span>',"<span class=\"ping\" aria-label=\"Задержка до сервера, не скорость видео\">\${server.measuring?'…':server.pingMs==null?'Недоступно':esc(server.pingMs)+' мс'}</span>");
html=html.replace('<strong>—</strong></div><div><small>Отдача</small><strong>—</strong>',"<strong>\${state.stats?.receivedBytes==null?'—':(state.stats.receivedBytes/1048576).toFixed(1)+' МиБ'}</strong></div><div><small>Отдача</small><strong>\${state.stats?.sentBytes==null?'—':(state.stats.sentBytes/1048576).toFixed(1)+' МиБ'}</strong>");
html=html.replace('</head>',`<style id="android-polish">${androidStyles}</style><meta http-equiv="Content-Security-Policy" content="default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'none'; frame-src 'none'; object-src 'none'">\n</head>`);
const navigationScript=`<script>window.iorAndroidBack=()=>{if(window.iorAndroidCloseRouteConsent?.())return true;if(window.iorAndroidCloseMenu?.())return true;const close=document.querySelector('[data-action="sheet-close"],[data-action="back"]');if(close){close.click();return true;}return false;};window.addEventListener('load',()=>window.iorNative?.postMessage(JSON.stringify({version:1,id:crypto.randomUUID(),action:'load',data:{}})));</script>`;
html=html.includes('</body>')?html.replace('</body>',navigationScript+'</body>'):html+navigationScript;
await mkdir(new URL('app/src/main/assets/',import.meta.url),{recursive:true});
await writeFile(new URL('app/src/main/assets/index.html',import.meta.url),html);
const dictionaryText=androidRuntime.match(/const nativeTranslations=([\s\S]*?);\nfunction applyNativeLanguage/)[1];
await mkdir(new URL('app/src/main/assets/locales/',import.meta.url),{recursive:true});
await writeFile(new URL('app/src/main/assets/locales/en.json',import.meta.url),JSON.stringify(new Function('return ('+dictionaryText+')')()));
await mkdir(new URL('app/src/main/assets/licenses/',import.meta.url),{recursive:true});
for(const name of await readdir(new URL('../ior-core/licenses/',import.meta.url)))await copyFile(new URL('../ior-core/licenses/'+name,import.meta.url),new URL('app/src/main/assets/licenses/'+name,import.meta.url));
await copyFile(new URL('THIRD-PARTY.md',import.meta.url),new URL('app/src/main/assets/licenses/ANDROID-THIRD-PARTY.md',import.meta.url));
for(const name of await readdir(new URL('licenses/',import.meta.url)))await copyFile(new URL('licenses/'+name,import.meta.url),new URL('app/src/main/assets/licenses/'+name,import.meta.url));
console.log('Android UI generated from mobile prototype');

