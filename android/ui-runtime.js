let nativeAppsLoaded=false,nativeAppsLoading=false,nativeAppsError='',nativeMenuKey='',nativeJournalBytes=null;
let nativeMenuClosing=false;
const nativeBack=back;back=function(fromHistory=false){const menu=sheet?.kind==='menu'?root.querySelector('.context-menu'):null;if(menu?.animate&&!reduceMotion()){if(nativeMenuClosing)return;nativeMenuClosing=true;const animation=menu.animate([{opacity:1,transform:'scale(1)'},{opacity:0,transform:'scale(.96)'}],{duration:120,easing:'ease-in',fill:'forwards'});const done=()=>{nativeMenuClosing=false;nativeBack(fromHistory);};animation.onfinish=done;animation.oncancel=done;return;}return nativeBack(fromHistory);};
window.iorAndroidCloseMenu=()=>{if(sheet?.kind!=='menu')return false;back();return true;};
const nativeOpenSheet=openSheet;openSheet=function(kind,data={}){
 if(kind==='menu'&&!data.position){
  const row=[...root.querySelectorAll('[data-context]')].find(el=>el.dataset.context===data.objectKind&&el.dataset.object===data.id);
  const rect=row?.getBoundingClientRect?.();
  const held=pressOrigin&&pressOrigin.id===data.id&&pressOrigin.kind===data.objectKind;
  data={...data,position:held?{x:pressOrigin.x,y:pressOrigin.y}:rect?{x:rect.left+rect.width/2,y:rect.top+rect.height/2,top:rect.top}:undefined};
 }
 return nativeOpenSheet(kind,data);
};
function updateNativeStats(){const values=root.querySelectorAll('.stats-grid strong');if(values[0])values[0].textContent=((state.stats?.receivedBytes||0)/1048576).toFixed(1)+' МиБ';if(values[1])values[1].textContent=((state.stats?.sentBytes||0)/1048576).toFixed(1)+' МиБ';}
function nativePingContent(server){if(server.measuring)return '…';if(server.pingStale)return '—<small>Обновить</small>';const method=({icmp:'ICMP',tcp:'TCP',https:'HTTPS'})[server.pingMethod]||'';return (server.pingMs==null?'Недоступно':esc(server.pingMs)+' мс')+(method?'<small>'+method+'</small>':'');}
function nativePingTitle(server){return server.pingStale?'Сеть изменилась; измерьте повторно':server.pingMethod==='https'?'Полный HTTPS-запрос через профиль, не RTT':({PING_ICMP_UNAVAILABLE:'ICMP не отвечает',PING_DNS_FAILED:'DNS не ответил',PING_NETWORK_UNAVAILABLE:'Нет доступной сети',AWG_ACTIVE_TRANSPORT_REQUIRED:'Для HTTPS-проверки AWG требуется активное подключение',PING_TIMEOUT:'Время проверки истекло'})[server.pingReason]||'Задержка до сервера';}
function updateNativePing(server){const row=[...root.querySelectorAll('[data-server]')].find(el=>el.dataset.server===server.id);const ping=row?.querySelector('.ping');if(ping){ping.innerHTML=nativePingContent(server);ping.title=nativePingTitle(server);}}
const nativeRow=row;row=function(server){return nativeRow(server).replace(/<button[^>]*data-action="server-menu"[\s\S]*?<\/button>/,'<button class="native-accessible" data-action="android-actions" aria-label="Действия конфигурации">Действия</button>').replace(/<span class="ping"[^>]*>[\s\S]*?<\/span>/,'<span class="ping" title="'+esc(nativePingTitle(server))+'">'+nativePingContent(server)+'</span>');};
const nativeServerList=serverList;serverList=function(){return nativeServerList().replace(/<button[^>]*data-action="object-menu"[^>]*data-kind="subscription"[^>]*>[\s\S]*?<\/button>/g,button=>{const id=button.match(/data-id="([^"]+)"/)?.[1]||'';return '<button class="icon-button" data-action="ping-group" data-id="'+esc(id)+'" aria-label="Измерить пинг подписки">'+icon('activity')+'</button><button class="native-accessible" data-action="android-actions" aria-label="Действия подписки">Действия</button>';});};
settingPages.splice(settingPages.findIndex(p=>p[0]==='logs'),0,['hwid','HWID','profiles']);

names['setting-hwid']='HWID';names['setting-ping']='Пинг';names['setting-language']='Язык';
function nativeSelect(key,label,options,value){return '<label class="native-field"><span>'+esc(label)+'</span><select data-native-setting="'+key+'" data-focus="native-'+key+'">'+options.map(([id,text])=>'<option value="'+id+'" '+(id===value?'selected':'')+'>'+esc(text)+'</option>').join('')+'</select></label>';}
function saveNativeSettings(){const input={};for(const el of root.querySelectorAll('[data-native-setting]'))input[el.dataset.nativeSetting]=el.type==='checkbox'?el.checked:el.type==='range'?Number(el.value)*1000:['journalMaxMb','journalDays'].includes(el.dataset.nativeSetting)?Number(el.value):el.value;try{if(input.pingUrl){const url=new URL(input.pingUrl);if(url.protocol!=='https:'||url.username||url.password)throw Error();}command('settings',input);}catch{notify('Введите корректный HTTPS-адрес');}}
root.addEventListener('input',event=>{const el=event.target;if(el.dataset.nativeSetting==='proxyPingTimeoutMs'){const label=root.querySelector('[data-timeout-label]');if(label)label.textContent=el.value+' '+(nativeLanguage()==='en'?'s':'с');}});
root.addEventListener('change',event=>{if(event.target.matches('[data-native-setting]:not([type=url])'))saveNativeSettings();});
function nativeLanguage(){return state.language==='en'||state.language==='system'&&state.systemLanguage==='en'?'en':'ru';}
const nativeLogs=logs;logs=function(){return '<section class="settings-section native-options">'+nativeSelect('logLevel','Уровень журнала',[['INFO','INFO'],['DEBUG','DEBUG']],state.logLevel||'INFO')+btn('video-stall-marker',icon('activity')+'<span>Отметить торможение видео</span>'+icon('chevron'),'settings-row')+btn('export-diagnostics',icon('download')+'<span>Экспорт диагностики</span>'+icon('chevron'),'settings-row')+'<p class="muted">Диагностика сохраняется при INFO и DEBUG. Если видео зависло, отметьте это здесь: журнал сопоставит время с сетевыми событиями. Качество и буфер плеера приложению недоступны.</p></section>'+nativeLogs();};
const nativeAppView=appsView;appsView=function(){if(!nativeAppsLoaded)return nativeAppsError?'<p class="error">'+esc(nativeAppsError)+'</p>'+btn('apps-examples','Повторить загрузку','outline'):'<p role="status">Загрузка приложений…</p>';state.appCatalog=true;return nativeAppView()+btn('apps-examples','Обновить список','outline');};
const nativeRouteList=list;list=function(){let html=nativeRouteList();for(const route of state.routes||[]){const marker='data-object="'+esc(route.id)+'"',start=html.indexOf(marker),small=html.indexOf('<small>',start),endSmall=html.indexOf('</small>',small);const source=route.sourceFormat||(route.managedBy?'unknown':route.format||'ior');const label=({happ:'Happ',clash:'Clash',ior:'ior',unknown:'Маршрут подписки'})[source]||'Маршрут подписки';if(start>=0&&small>=0&&endSmall>=0)html=html.slice(0,small+7)+esc(label)+(state.selectedRoute===route.id?' · Выбран':'')+html.slice(endSmall);if(route.error){const end=html.indexOf('</article>',start);if(start>=0&&end>=0)html=html.slice(0,end)+'<p class="error" role="alert">'+esc(route.error)+'</p>'+html.slice(end);}}return html;};
const nativeSettingPage=settingPage;settingPage=function(){
 if(screen==='setting-data')return '<p>Данные защищены Android Keystore и хранятся только на этом устройстве.</p>'+[['android-backup','Резервная копия'],['android-restore','Восстановить'],].map(([action,label])=>btn(action,label,'settings-row native-settings')).join('');
 if(screen==='setting-hwid')return '<p class="muted">Режим отправки HWID выбирается отдельно для каждой подписки.</p>'+(state.subscriptions.length?state.subscriptions.map(sub=>'<section class="settings-section native-options"><h3>'+esc(sub.name)+'</h3><div class="native-mode-list">'+[['always','Всегда отправлять'],['required','По требованию'],['never','Не отправлять']].map(([mode,label])=>btn('hwid-mode',esc(label)+(sub.hwidMode===mode?' '+icon('check'):''),'native-mode '+(sub.hwidMode===mode?'selected':''),'data-id="'+esc(sub.id)+'" data-mode="'+mode+'" aria-pressed="'+(sub.hwidMode===mode)+'"')).join('')+'</div>'+(sub.hwidMode==='never'?'<p class="native-warning" role="status">Некоторые подписки могут не работать: они требуют обязательной отправки HWID.</p>':'')+'</section>').join(''):'<p>Нет подписок</p>');
 if(screen==='setting-ping')return '<section class="settings-section native-options">'+nativeSelect('pingMode','Метод измерения',[['auto','Авто'],['icmp','ICMP'],['tcp','TCP'],['head','Через прокси HEAD'],['get','Через прокси GET']],state.pingMode||'auto')+'<label class="native-field"><span>HTTPS-адрес проверки</span><input type="url" data-native-setting="pingUrl" data-focus="native-pingUrl" value="'+esc(state.pingUrl||'https://www.gstatic.com/generate_204')+'"></label><label class="native-field"><span>Таймаут прокси-пинга <strong data-timeout-label>'+Math.round((state.proxyPingTimeoutMs||5000)/1000)+' с</strong></span><input type="range" min="5" max="15" step="1" value="'+Math.round((state.proxyPingTimeoutMs||5000)/1000)+'" data-native-setting="proxyPingTimeoutMs" data-focus="native-timeout"><small>5 с — 15 с</small></label><p class="muted">HEAD/GET измеряют полный HTTPS-запрос через профиль, а не чистый RTT.</p>'+btn('native-settings-save','Сохранить','button outline')+'</section>';
 if(screen==='setting-language')return '<section class="settings-section native-options">'+nativeSelect('language','Язык',[['system','Системный'],['ru','Русский'],['en','English']],state.language||'system')+'</section>';
 if(screen==='setting-vpn')return btn('settings-page',icon('activity')+'Пинг'+icon('chevron'),'settings-row','data-id="ping"')+btn('settings-page',icon('globe')+'LAN'+icon('chevron'),'settings-row','data-id="lan"')+ '<section class="card"><h3>Android VPN · ior-core Rust</h3><p>'+esc(connection==='on'?'Подключено':connection==='pending'?'Подключение…':'Не подключено')+'</p><p>VPN сохраняет включённое состояние при потере сети и восстанавливает связь.</p></section>';
 if(screen==='setting-general')return btn('settings-page',icon('globe')+'Язык'+icon('chevron'),'settings-row','data-id="language"')+ '<section class="card"><h3>ior Android 1.2.4</h3><p>Ядро: ior-core Rust</p><p>Android 10+ · arm64</p></section>';
 return nativeSettingPage();
};
const nativeSyncNavigation=syncNavigation;syncNavigation=function(force=false){
 nativeSyncNavigation(force);
 const nav=root.querySelector('.bottomnav'),indicator=nav?.querySelector('.tab-indicator'),button=nav?.querySelector('[aria-current=page]');
 const box=nav?.getBoundingClientRect?.(),target=button?.getBoundingClientRect?.();if(!box?.width||!target?.width||!indicator)return;
 const ratio=window.devicePixelRatio||1,snap=value=>Math.round(value*ratio)/ratio;
 const x=snap(target.left-box.left+(target.width-48)/2-(nav.clientLeft||0)),y=snap(target.top-box.top+(target.height-48)/2-(nav.clientTop||0));
 stop('nav');indicator.style.width='48px';indicator.style.height='48px';indicator.style.transform='translate('+x+'px,'+y+'px)';
};
let observedNativeNav=null;const nativeNavResize=window.ResizeObserver?new window.ResizeObserver(()=>syncNavigation(true)):null;
window.addEventListener('pointerdown',event=>{if(event.pointerType==='touch')document.documentElement.classList.add('android-touch');});
document.fonts?.ready?.then(()=>syncNavigation(true));
const nativeRender=render;render=function(){
  const scroll=root.querySelector('.content')?.scrollTop||0,oldScreen=renderedScreen;
  nativeRender();
  const nav=root.querySelector('.bottomnav');if(nav!==observedNativeNav){nativeNavResize?.disconnect();if(nav)nativeNavResize?.observe(nav);observedNativeNav=nav;}
  if(oldScreen===screen&&root.querySelector('.content'))root.querySelector('.content').scrollTop=scroll;
  const menu=root.querySelector('.context-menu');
  if(menu){stop('menu');menu.style.visibility='hidden';placeMenu();menu.style.visibility='visible';const key=sheet.objectKind+':'+sheet.id;if(key!==nativeMenuKey&&!reduceMotion())menu.animate?.([{opacity:0,transform:'scale(.96)'},{opacity:1,transform:'scale(1)'}],{duration:180,easing:'cubic-bezier(.2,.8,.2,1)'});nativeMenuKey=key;}else nativeMenuKey='';
  if(screen==='setting-apps'&&!nativeAppsLoaded&&!nativeAppsLoading&&!nativeAppsError){nativeAppsLoading=true;command('apps');}
  applyNativeLanguage?.();
};
const journalLogs=logs;logs=function(){return '<section class="settings-section native-options"><h3>Хранение журнала</h3><p>Занято: '+(nativeJournalBytes==null?'…':(nativeJournalBytes/1048576).toFixed(2)+' МиБ')+'</p>'+nativeSelect('journalMaxMb','Максимальный размер',[[5,'5 МБ'],[20,'20 МБ'],[50,'50 МБ']],Number(state.journalMaxMb||20))+nativeSelect('journalDays','Хранить не дольше',[[1,'1 дня'],[7,'7 дней'],[30,'30 дней']],Number(state.journalDays||7))+'<small>Старые записи удаляются автоматически. При уменьшении лимитов часть записей будет удалена.</small></section>'+journalLogs();};
const trafficSettingPage=settingPage;settingPage=function(){let html=trafficSettingPage();if(screen==='setting-general')html+='<section class="settings-section native-options"><label><input type="checkbox" data-native-setting="saveMobileTraffic" '+(state.saveMobileTraffic===true?'checked':'')+'> Экономить мобильный трафик</label><p>Автообновления ждут Wi-Fi. Кнопка «Обновить» разрешает ручную загрузку на мобильной сети.</p></section>';if(screen==='setting-vpn')html+='<section class="settings-section native-options"><h3>VPN при потере интернета</h3><p>VPN остаётся включённым и ждёт сеть. Для системного kill switch откройте настройки Android → ior → «Постоянная VPN» и «Блокировать соединения без VPN».</p><p>Постоянная VPN: '+(state.systemVpn?.alwaysOn?'включена':'выключена')+'. Блокировка без VPN: '+(state.systemVpn?.lockdown?'включена':'выключена')+'.</p><p>Системная блокировка также ограничивает приложения, исключённые из VPN. Сеть во время ожидания недоступна.</p>'+btn('vpn-system-settings','Открыть системные настройки VPN','outline')+'</section>';return html;};
const journalRender=render;let journalRequested=false;render=function(){if(screen==='logs'&&!journalRequested){journalRequested=true;command('journal-size');}else if(screen!=='logs')journalRequested=false;return journalRender();};
const retainedJournalLogs=logs;logs=function(){return '<p class="muted">Смена протокола не очищает журнал. На экране — последние 2000 строк; более ранние сохранённые записи доступны в экспорте диагностики (папка console). Хранение ограничено выбранным сроком и размером.</p>'+retainedJournalLogs();};
root.addEventListener('click',event=>{if(event.target.closest('[data-action="vpn-system-settings"]')){event.stopImmediatePropagation();command('vpn-system-settings');}},true);
const nativeTranslations={
 'Подключаться к':'Connect to','Показать / скрыть пароль':'Show / hide password','Копировать параметры':'Copy connection details','LAN включается с VPN':'LAN starts with VPN','Подписка удалена':'Subscription removed','Системный':'System','HTTP / SOCKS5 · TCP. На другом устройстве укажите адрес и порт прокси.':'HTTP / SOCKS5 · TCP. Configure the proxy address and port on the other device.','Без пароля устройства локальной сети смогут использовать ваш прокси. Продолжить?':'Without a password, local devices can use your proxy. Continue?',

 'Настройки подписки':'Subscription settings','Автообновление':'Automatic refresh','Интервал, часов':'Interval, hours','Задано сервером':'Required by server','Обновлять при запуске':'Refresh at startup','Пинговать при запуске':'Measure latency at startup','Подключаться при запуске':'Connect at startup','Последний использованный':'Last used','С наименьшей задержкой':'Lowest latency','Случайный':'Random','Таймаут запроса подписки':'Subscription request timeout','Для этого режима пинг выполняется при запуске.':'This mode always measures latency at startup.','Разрешить подключения из LAN':'Allow LAN connections','Пароль':'Password','Логин':'Username','Интерфейс':'Interface','Порт':'Port','Использовать пароль':'Use a password',

 'Режим отправки HWID выбирается отдельно для каждой подписки.':'Choose HWID sharing separately for each subscription.',
 'Всегда отправлять':'Always send','По требованию':'When required','Не отправлять':'Never send','Нет подписок':'No subscriptions',
 'Некоторые подписки могут не работать: они требуют обязательной отправки HWID.':'Some subscriptions may not work because they require HWID.',
 'Метод измерения':'Measurement method','Авто':'Auto','Через прокси HEAD':'Via proxy HEAD','Через прокси GET':'Via proxy GET',
 'HTTPS-адрес проверки':'HTTPS test address','Таймаут прокси-пинга':'Proxy ping timeout','5 с — 15 с':'5 s — 15 s',
 'HEAD/GET измеряют полный HTTPS-запрос через профиль, а не чистый RTT.':'HEAD/GET measure a complete HTTPS request through the profile, rather than RTT.',
 'Уровень журнала':'Log level','Введите корректный HTTPS-адрес':'Enter a valid HTTPS address',

 'Общие':'General','Оформление':'Appearance','Ядро: ior-core Rust':'Core: ior-core Rust','Данные защищены Android Keystore и хранятся только на этом устройстве.':'Data is protected by Android Keystore and stored only on this device.',
 'VPN сохраняет включённое состояние при потере сети и восстанавливает связь.':'VPN starts only after your action and Android permission.',
 'Да':'Yes','Разрешить':'Allow','Выключить':'Disable','Включить':'Enable','Отключить':'Disconnect','Выбранный маршрут несовместим с ядром':'The selected route is incompatible with the core','Маршрут содержит неподдержанные правила':'The route contains unsupported rules','Настройки и маршруты без секретов':'Settings and routes without secrets','Все данные: защитить паролем':'All data: protect with a password',
 'Пароль: минимум 10 символов':'Password: at least 10 characters','Пароль резервной копии':'Backup password','Заменить данные приложения?':'Replace application data?',
 'Передавать HWID подпискам':'Share HWID with subscriptions','Разрешить HWID этой подписке?':'Allow HWID for this subscription?',
 'HWID этой подписки':'Subscription HWID','Разрешить этому HTTPS-origin':'Allow for this HTTPS origin','Запретить':'Deny','Использовать глобальную настройку':'Use global setting',
 'Подключение':'Connection','Маршруты':'Routes','Настройки':'Settings','Приложение':'Application','Пинг':'Ping','Язык':'Language','Данные приложения':'App data',
 'Журнал и диагностика':'Logs and diagnostics','Экспорт диагностики':'Export diagnostics','Отменить проверки пинга':'Cancel ping checks','Не подключено':'Disconnected','Подключено':'Connected','Подключение…':'Connecting…',
 'Серверы':'Servers','Найти сервер или подписку':'Find a server or subscription','Найти приложение':'Find an app','Приложения и VPN':'Apps and VPN',
 'Действия':'Actions','Действия конфигурации':'Configuration actions','Действия подписки':'Subscription actions','Измерить пинг подписки':'Measure subscription latency',
 'Добавить сервер':'Add server','Добавить':'Add','Отмена':'Cancel','Продолжить':'Continue','Закрыть':'Close','Назад':'Back','Сохранить':'Save','Сохранено':'Saved',
 'Редактировать':'Edit','Проверить конфигурацию':'Validate configuration','Ссылка и QR':'Link and QR','Создать копию':'Duplicate','Удалить':'Delete',
 'Сведения':'Details','Обновить подписку':'Refresh subscription','Экспорт маршрута':'Export route','Личные конфиги':'Personal configurations','Нет данных':'No data','Не предоставляется':'Not provided',
 'Состояние сети':'Network status','Загрузка':'Download','Отдача':'Upload','Задержка':'Latency','Недоступно':'Unavailable','Тема':'Theme','Чёрная':'Dark','Белая':'Light',
 'Уменьшить движение':'Reduce motion','Учитывается и системная настройка.':'System preference is also respected.','Анимация фона':'Background animation','Медленные мягкие переливы.':'Slow, gentle colour transitions.',
 'Все':'All','Выбраны':'Selected','Системные':'System','Браузеры':'Browsers','Общение':'Communication','Видео':'Video','Игры':'Games','Другие':'Other',
 'Маршрутизация приложений':'App routing','Через VPN':'Through VPN','Напрямую':'Direct','Загрузить приложения':'Load apps','Загрузка приложений…':'Loading apps…',
 'Изменения применяются после переподключения':'Changes apply after reconnecting','Только выбранные приложения используют VPN. Остальные идут напрямую.':'Only selected apps use VPN. Other apps connect directly.',
 'Выбранные приложения идут напрямую. Остальные используют VPN.':'Selected apps connect directly. Other apps use VPN.','Список установленных приложений Android; выбор применяется после переподключения.':'Installed Android apps; selection applies after reconnecting.',
 'Список пока пуст':'The list is empty','Загрузите список установленных приложений.':'Load installed apps.','Ничего не найдено':'No matches','Журнал':'Logs','Журнал пуст':'No log entries',
 'Копировать':'Copy','Скопировано.':'Copied.','Статистика активной VPN-службы.':'Active VPN service statistics.','События приложения и Android VPN-службы.':'Application and VPN service events.',
 'Резервная копия':'Backup','Восстановить':'Restore','Отправить':'Share','Автообновление подписок':'Subscription auto-update','Передача HWID подпискам':'Subscription HWID sharing',
 'Ссылка':'Link','Подписка':'Subscription','Название':'Name','Название (необязательно)':'Name (optional)','Адрес':'Address','Ссылка протокола':'Protocol link','Адрес подписки':'Subscription URL',
 'Конфигурация .conf':'Configuration .conf','Выбрать файл':'Choose file','Вставить':'Paste','Сканировать QR':'Scan QR','Ссылка или конфигурация':'Link or configuration',
 'Ваши маршруты':'Your routes','Настройте распределение трафика в приложении.':'Configure traffic routing in the app.','Создать':'Create','Импорт':'Import','Удалить этот объект?':'Delete this item?',
 'Подписка обновлена':'Subscription refreshed','Конфигурация добавлена':'Configuration added','Создана копия':'Copy created','Файл сохранён':'File saved','Скопировано':'Copied',
 'Не удалось выполнить действие':'Action failed','Не удалось открыть конфигурацию':'Could not open configuration','Не удалось прочитать приложения':'Could not load apps','Не удалось сохранить файл':'Could not save file',
 'Данные восстановлены':'Data restored','Восстановление не выполнено':'Restore failed','Неверный пароль или повреждённая копия':'Incorrect password or damaged backup','Не удалось открыть резервную копию':'Could not open backup',
 'Сервер выбран. Нажмите подключение, чтобы переподключиться':'Server selected. Tap connect to reconnect','Не удалось открыть переданный файл':'Could not open shared file','Структура проверена ior-core; сеть не проверялась':'Configuration validated by ior-core; network was not checked',
 'Параметры проверяются ior-core при сохранении.':'ior-core validates parameters when saving.','Правила проверяются перед применением.':'Rules are validated before applying.','Подписка загружается по HTTPS. При ошибке прежние серверы сохраняются.':'Subscriptions load over HTTPS. Existing servers are preserved on error.',
 'Импорт поддерживает AWG, Hysteria2 и VLESS Reality.':'Import supports AWG, Hysteria2 and VLESS Reality.','Нет серверов. Обновите подписку или проверьте разрешение HWID.':'No servers. Refresh the subscription or check HWID permission.',
 'Формат':'Format','По умолчанию':'Default','Через VPN — домены':'VPN domains','Напрямую — домены':'Direct domains','Блокировать — домены':'Blocked domains','Исходный текст':'Source text',
 'Данные хранятся отдельно от настольного ior, только в этом браузере.':'Data is stored securely on this Android device.','Примеры не появляются автоматически.':'Only your configurations are used.',
 'Очистить прототип':'Manage application data','QR содержит секреты конфигурации. Отправляйте его только доверенному получателю.':'QR contains configuration secrets. Share only with a trusted recipient.'
};
function applyNativeLanguage(){
 const english=state.language==='en'||((!state.language||state.language==='system')&&state.systemLanguage==='en');if(!english)return;
 for(const element of root.querySelectorAll('*')){
   if(element.closest?.('.server-copy,.context-caption,.log-entry'))continue;
   for(const node of element.childNodes)if(node.nodeType===3){const text=node.textContent.trim();if(nativeTranslations[text])node.textContent=node.textContent.replace(text,nativeTranslations[text]);}
   for(const attr of ['aria-label','placeholder'])if(element.hasAttribute(attr)){const text=element.getAttribute(attr);if(nativeTranslations[text])element.setAttribute(attr,nativeTranslations[text]);}
 }
}
