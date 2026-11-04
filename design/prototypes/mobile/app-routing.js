// Android-facing UI only. Installed packages and network validation require native code.
const appCategories=[['browser','Браузеры'],['chat','Общение'],['video','Видео'],['games','Игры'],['system','Системные']];
const appExamples=[
  ['org.example.browser','Браузер','browser','Б'],['org.example.privatebrowser','Приватный браузер','browser','П'],
  ['org.example.chat','Сообщения','chat','С'],['org.example.calls','Видеозвонки','chat','В'],['org.example.mail','Почта','chat','П'],
  ['org.example.video','Видеоплеер','video','В'],['org.example.stream','Стримы и трансляции','video','Т'],
  ['org.example.game','Игра','games','И'],['org.example.longgame','Игра с очень длинным названием для проверки списка','games','И'],
  ['org.example.store','Магазин приложений','system','М'],['org.example.update','Обновление системы','system','О'],['org.example.download','Загрузки','system','З']
].map(([id,name,category,letter])=>({id,name,category,letter,system:category==='system'}));
const routingDefaults={enabled:false,mode:'vpn',ids:[]};
function normalizeRouting(value){return {enabled:value?.enabled===true,mode:value?.mode==='direct'?'direct':'vpn',ids:[...new Set(Array.isArray(value?.ids)?value.ids.filter(id=>appExamples.some(a=>a.id===id)):[])]};}
state.appRouting=normalizeRouting(state.appRouting);state.appCatalog=state.appCatalog===true;
defaults.appRouting=structuredClone(routingDefaults);defaults.appCatalog=false;
let appSearch='',appFilter='all';
const routingLabel=mode=>mode==='vpn'?'Через VPN':'Напрямую';
names['setting-apps']='Приложения и VPN';
settingPages.splice(3,0,['apps','Приложения и VPN','profiles']);
const previousGo=go;go=function(next){const before=screen;previousGo(next);if(screen!==before&&screen!=='setting-apps'){appSearch='';appFilter='all';}};
const previousBack=back;back=function(fromHistory=false){const before=screen;previousBack(fromHistory);if(before==='setting-apps'&&screen!==before){appSearch='';appFilter='all';}};
function appsView(){
  const d=state.appRouting,q=appSearch.trim().toLocaleLowerCase('ru-RU');
  const catalog=state.appCatalog?appExamples:[];
  const visible=catalog.filter(a=>(appFilter!=='selected'||d.ids.includes(a.id))&&(appFilter!=='system'||a.system)&&[a.name,a.id].join(' ').toLocaleLowerCase('ru-RU').includes(q));
  return `<section class="apps-toolbar" data-focus="apps-toolbar">${searchField('apps',appSearch,'Найти приложение')}<div class="apps-filters" role="group" aria-label="Фильтр приложений">${[['all','Все'],['selected','Выбраны'],['system','Системные']].map(([id,title])=>btn('apps-filter',title,appFilter===id?'current':'',`data-id="${id}" aria-pressed="${appFilter===id}"`)).join('')}</div></section>
    <section class="apps-settings"><div class="apps-enable"><div><strong>Маршрутизация приложений</strong><small>Локальный сценарий прототипа</small></div>${btn('apps-enabled','<span class="switch-track"><span></span></span>','toggle',`role="switch" aria-label="Маршрутизация приложений" aria-checked="${d.enabled}"`)}</div>
    <div class="apps-modes" role="group" aria-label="Режим маршрутизации">${['vpn','direct'].map(mode=>btn('apps-mode',routingLabel(mode),d.mode===mode?'current':'',`data-id="${mode}" aria-pressed="${d.mode===mode}"`)).join('')}</div>
    <p class="hint">${d.mode==='vpn'?'Только выбранные приложения используют VPN. Остальные идут напрямую.':'Выбранные приложения идут напрямую. Остальные используют VPN.'}</p>
    <p class="apps-count" role="status">${routingLabel(d.mode)} · ${d.ids.length}${d.enabled?'':' · выключено'}</p>
    ${!d.ids.length?`<p class="apps-warning" role="status">${d.mode==='vpn'?'Выберите хотя бы одно приложение для режима «Через VPN».':'Список пуст: после включения все приложения будут использовать VPN.'}</p>`:''}
    </section>
    <p class="hint muted">HTML не получает список приложений телефона. Ниже — отдельный демонстрационный каталог.</p>
    ${!state.appCatalog?`<div class="apps-empty"><h3>Список пока пуст</h3><p>Загрузите примеры, чтобы проверить поиск и выбор.</p>${btn('apps-examples','Загрузить примеры','outline')}</div>`:visible.length?appCategories.map(([category,title])=>{
      const items=visible.filter(a=>a.category===category).sort((a,b)=>a.name.localeCompare(b.name,'ru'));
      if(!items.length)return '';return `<section class="apps-category"><header><h3>${title}</h3><small>${items.length}</small></header><div class="apps-list">${items.map(a=>`<label class="app-choice" data-focus="app-row-${a.id}" data-selected="${d.ids.includes(a.id)}"><span class="app-tile" aria-hidden="true">${a.letter}</span><span class="app-copy"><strong>${esc(a.name)}</strong><small>${esc(a.id)}</small></span><input type="checkbox" data-app="${a.id}" data-focus="app-${a.id}" ${d.ids.includes(a.id)?'checked':''} aria-label="${esc(a.name)}: ${routingLabel(d.mode)}"><span class="app-check" aria-hidden="true">${icon('check')}</span></label>`).join('')}</div></section>`;
    }).join(''):`<div class="apps-empty"><h3>Нет совпадений</h3><p>${appFilter==='selected'?'Выберите приложения во вкладке «Все».':'Попробуйте другое название или фильтр.'}</p></div>`}`;
}
const previousSettingPage=settingPage;settingPage=function(){return screen==='setting-apps'?appsView():previousSettingPage();};
root.addEventListener('input',event=>{if(event.target.dataset.search==='apps'){appSearch=event.target.value;render();}},true);
root.addEventListener('change',event=>{if(!event.target.dataset.app)return;const id=event.target.dataset.app;if(!appExamples.some(a=>a.id===id))return;const ids=new Set(state.appRouting.ids);event.target.checked?ids.add(id):ids.delete(id);state.appRouting.ids=[...ids];if(state.appRouting.enabled&&state.appRouting.mode==='vpn'&&!ids.size)state.appRouting.enabled=false;persist();log((event.target.checked?'Выбрано приложение «':'Снято приложение «')+appExamples.find(a=>a.id===id).name+'» · '+routingLabel(state.appRouting.mode)+' (локальный макет).');render();},true);

// Structural checks never claim a server, password or VPN core was tested.
function configurationReport(server){
  const errors=[],warnings=[],raw=String(server?.raw||'').trim();let address='',name='';
  if(!raw)return {errors:['Конфигурация пуста.'],warnings,address,name};
  if(raw.includes('[Interface]')||raw.includes('[Peer]')){
    const read=key=>raw.match(new RegExp('^\\s*'+key+'\\s*=\\s*(.+)$','im'))?.[1]?.trim()||'';
    if(!raw.includes('[Interface]')||!raw.includes('[Peer]'))errors.push('Нужны секции [Interface] и [Peer].');
    for(const key of ['PrivateKey','PublicKey','Endpoint'])if(!read(key))errors.push('Не заполнено поле '+key+'.');
    address=read('Endpoint');if(address&&!/^(?:\[[0-9a-f:]+\]|[^\s:/]+):\d+$/i.test(address))errors.push('Endpoint должен содержать адрес и порт.');
    const port=Number(address.match(/:(\d+)$/)?.[1]);if(address&&(!port||port>65535))errors.push('Порт должен быть от 1 до 65535.');
    for(const key of ['PrivateKey','PublicKey'])if(read(key)&&! /^[A-Za-z0-9+/]{43}=$/.test(read(key)))warnings.push('Формат '+key+' требует проверки ядром.');
  }else{
    if(/[\r\n]/.test(raw))errors.push('Ссылка должна занимать одну строку.');
    const scheme=raw.match(/^([a-z0-9]+):\/\//i)?.[1]?.toLowerCase();
    if(!['vless','vmess','hysteria2','hy2','trojan','ss','socks','http'].includes(scheme))errors.push('Протокол ссылки не поддерживается в макете.');
    else if(scheme==='vmess'){
      try{const body=JSON.parse(atob(raw.slice(8).replace(/-/g,'+').replace(/_/g,'/')));const port=Number(body.port);if(!body.add||!/^[^\s/?#]+$/.test(body.add))errors.push('VMess: не указан корректный адрес.');if(!Number.isInteger(port)||port<1||port>65535)errors.push('VMess: порт должен быть от 1 до 65535.');if(!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(body.id||''))errors.push('VMess: нужен корректный UUID.');address=body.add+':'+body.port;name=body.ps||'';}catch{errors.push('VMess: не удалось прочитать Base64 и JSON.');}
    }else{
      try{const url=new URL(raw);if(!url.hostname||/\s/.test(url.hostname))errors.push('В ссылке отсутствует корректный адрес сервера.');address=url.host;
        if(url.port&&(Number(url.port)<1||Number(url.port)>65535))errors.push('Порт должен быть от 1 до 65535.');
        if(['vless','trojan','hysteria2','hy2'].includes(scheme)&&!url.username)errors.push('В ссылке отсутствуют учётные данные.');
        if(scheme==='vless'&&!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(decodeURIComponent(url.username)))errors.push('VLESS: нужен корректный UUID.');
        if(!url.port&&scheme!=='http')warnings.push('Порт не указан явно: его определяет формат протокола.');
        name=decodeURIComponent(url.hash.slice(1));
      }catch{errors.push('Не удалось разобрать ссылку: проверьте адрес, порт и кодирование.');}
    }
  }
  if(/(?:^|[.@/])(?:[^\s/]*\.)?example\.invalid(?:[:/#]|$)|\.invalid(?:[:/#]|$)/i.test(raw))warnings.push('Используется тестовый адрес .invalid.');
  return {errors:[...new Set(errors)],warnings:[...new Set(warnings)],address,name};
}
const previousDecode=decode;decode=function(raw,name){const report=configurationReport({raw});if(report.errors.length)throw Error(report.errors.join(' '));const result=previousDecode(raw,name);if(report.address)result.address=report.address;if(!name&&report.name)result.name=report.name;return result;};
const previousSheetView=sheetView;sheetView=function(){
  if(!['validation','switch-server'].includes(sheet?.kind))return previousSheetView();
  const d=sheet;let title,body;
  if(d.kind==='validation'){
    title='Проверка конфигурации';const report=d.report;
    body=`<p class="validation-name">${esc(d.name)}</p><p class="validation-status">${report.errors.length?'Найдены ошибки':'Структура конфигурации проверена'}</p>${report.errors.map(text=>'<p class="error" role="alert">'+esc(text)+'</p>').join('')}${report.warnings.map(text=>'<p class="apps-warning">'+esc(text)+'</p>').join('')}<p class="hint">Проверены только формат и обязательные поля. Доступность сервера, ключи и совместимость ior-core требуют Android-приложения.</p>`;
  }else{
    title='Сменить сервер?';body=`<p class="hint">Активна демонстрация с сервером <strong>${esc(selected()?.name||'')}</strong>. При смене демонстрация отключится.</p><p class="validation-name">Новый сервер: ${esc(find('server',d.id)?.name||'')}</p><div class="sheet-actions">${btn('back','Отмена','outline')}${btn('confirm-server-switch','Отключить и выбрать','primary')}</div>`;
  }
  return `<div class="overlay" data-sheet="${d.kind}"><section class="sheet" role="dialog" aria-modal="true" aria-label="${title}"><div class="grabber" aria-hidden="true"></div><header class="sheet-heading"><h2>${title}</h2>${btn('back',icon('close'),'icon-button','aria-label="Закрыть" data-focus="sheet-close"')}</header>${body}</section></div>`;
};
function handleFeatureClick(event){
  const el=event.target.closest('[data-action]');if(!el||el.disabled||suppressClick||sheet?.closing)return;const action=el.dataset.action,id=el.dataset.id;
  if(action.startsWith('apps-')){
    event.mobileFeatureHandled=true;event.stopImmediatePropagation();
    if(action==='apps-examples'){state.appCatalog=true;persist();log('Загружен демонстрационный каталог приложений.');}
    if(action==='apps-enabled'){
      if(!state.appRouting.enabled&&state.appRouting.mode==='vpn'&&!state.appRouting.ids.length){notify('Сначала выберите хотя бы одно приложение для VPN.');return;}
      state.appRouting.enabled=!state.appRouting.enabled;
    }
    if(action==='apps-mode'){state.appRouting.mode=id;if(id==='vpn'&&!state.appRouting.ids.length)state.appRouting.enabled=false;}
    if(action==='apps-filter')appFilter=id;
    if(action==='apps-enabled'||action==='apps-mode'){persist();log('Маршрутизация приложений: '+routingLabel(state.appRouting.mode)+' · '+state.appRouting.ids.length+(state.appRouting.enabled?' · включено':' · выключено')+' (локальный макет).');}
    render();return;
  }
  if(action==='validate-config'){
    event.mobileFeatureHandled=true;event.stopImmediatePropagation();const server=find('server',sheet?.id||id);if(!server)return;const report=configurationReport(server);log('Проверена структура конфигурации «'+server.name+'»: '+(report.errors.length?'ошибок '+report.errors.length:'ошибок нет')+'.');sheet={kind:'validation',report,name:server.name};focusNext='sheet-close';render();return;
  }
  if(action==='select'){
    if(connection==='pending'||id===state.selected){event.mobileFeatureHandled=true;event.stopImmediatePropagation();return;}
    if(connection==='on'){event.mobileFeatureHandled=true;event.stopImmediatePropagation();openSheet('switch-server',{id});return;}
  }
  if(action==='confirm-server-switch'){
    event.mobileFeatureHandled=true;event.stopImmediatePropagation();const server=find('server',sheet.id);if(!server)return;connection='off';connectionError='';state.selected=server.id;persist();log('Демонстрация отключена при смене сервера. Выбран «'+server.name+'».');back();return;
  }
  if(action==='connect'&&connection==='off'&&selected()){
    const report=configurationReport(selected());if(report.errors.length){event.mobileFeatureHandled=true;event.stopImmediatePropagation();log('Демонстрация не начата: ошибки конфигурации «'+selected().name+'».');openSheet('validation',{report,name:selected().name});return;}
  }
}
