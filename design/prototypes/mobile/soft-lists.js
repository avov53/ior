// Custom selectors keep their appearance and motion inside the HTML prototype.
const selectorChoices=new Map();
choice=function(name,title,value,options){
  selectorChoices.set(name,{title,options});const label=options.find(([id])=>id===value)?.[1]||options[0]?.[1]||'Выбрать';
  return `<div class="field choice-field"><span id="choice-label-${name}">${esc(title)}</span>${btn('open-choice','<span>'+esc(label)+'</span>'+icon('chevron'),'choice-trigger',`data-id="${name}" aria-labelledby="choice-label-${name}" aria-haspopup="listbox" aria-expanded="${sheet?.kind==='picker'&&sheet.field===name}"`)}<select hidden aria-hidden="true" tabindex="-1" data-field="${name}" data-focus="field-${name}">${options.map(([id,text])=>`<option value="${esc(id)}" ${id===value?'selected':''}>${esc(text)}</option>`).join('')}</select></div>`;
};
const sheetBeforeSoftLists=sheetView;sheetView=function(){
  if(sheet?.kind==='picker'){
    const d=sheet;return `<div class="overlay picker-overlay" data-sheet="picker"><section class="sheet picker-panel" role="dialog" aria-modal="true" aria-label="${esc(d.title)}"><header class="picker-heading"><h2>${esc(d.title)}</h2>${btn('back',icon('close'),'icon-button','data-focus="sheet-close" aria-label="Закрыть список"')}</header><div role="listbox" aria-label="${esc(d.title)}">${d.options.map(([id,label])=>btn('pick-choice','<span>'+esc(label)+'</span><span class="choice-check">'+(id===d.value?icon('check'):'')+'</span>','choice-option',`data-id="${esc(id)}" role="option" aria-selected="${id===d.value}" tabindex="${id===d.value?'0':'-1'}"`)).join('')}</div></section></div>`;
  }
  if(sheet?.kind==='subscription'){
    const d=sheet,meta=d.metadata||{},known=n=>typeof n==='number'&&Number.isFinite(n)&&n>=0,format=n=>known(n)?(n/1e9).toLocaleString('ru-RU',{maximumFractionDigits:2})+' ГБ':'—';
    const usageKnown=known(meta.usedBytes)&&known(meta.limitBytes)&&meta.limitBytes>0,percent=usageKnown?Math.min(100,meta.usedBytes/meta.limitBytes*100):null;
    const date=meta.expiresAt?new Date(meta.expiresAt):null,expires=date&&!Number.isNaN(date.getTime())?date.toLocaleDateString('ru-RU'):'—';
    const notices=Array.isArray(meta.announcements)?meta.announcements.filter(a=>typeof a==='string'&&a.trim()):[];
    return `<div class="overlay" data-sheet="subscription"><section class="sheet subscription-sheet" role="dialog" aria-modal="true" aria-label="Сведения о подписке"><div class="grabber" aria-hidden="true"></div><header class="sheet-heading"><div><h2>${esc(d.name)}</h2></div>${btn('back',icon('close'),'icon-button','data-focus="sheet-close" aria-label="Закрыть"')}</header>${meta.demo?'<p class="subscription-demo">Демонстрационные сведения</p>':''}<section class="subscription-usage"><small>Использовано из доступного</small><strong>${format(meta.usedBytes)} <span>/ ${format(meta.limitBytes)}</span></strong>${percent!==null?`<progress value="${percent}" max="100" aria-label="Использовано ${format(meta.usedBytes)} из ${format(meta.limitBytes)}"></progress>`:'<p class="hint">Сведения о расходе и лимите пока не получены.</p>'}<dl><div><dt>Осталось</dt><dd>${usageKnown?format(Math.max(0,meta.limitBytes-meta.usedBytes)):'—'}</dd></div><div><dt>Действует до</dt><dd>${expires}</dd></div></dl></section><h3>Объявления</h3>${notices.length?notices.map(text=>'<p class="subscription-notice">'+esc(text)+'</p>').join(''):'<p class="hint">Объявления не получены.</p>'}<label class="field"><span>Сохранённый адрес подписки</span><textarea readonly aria-label="Адрес подписки">${esc(d.url)}</textarea></label><p class="hint">Загрузка подписок в Android пока не подключена. Доступность этих сведений зависит от провайдера.</p></section></div>`;
  }
  return sheetBeforeSoftLists();
};
const examplesBeforeMetadata=examples;examples=function(){const count=state.subscriptions.length;examplesBeforeMetadata();if(state.subscriptions.length===count)return;state.subscriptions.at(-1).metadata={demo:true,usedBytes:12400000000,limitBytes:100000000000,expiresAt:'2026-12-31T00:00:00Z',announcements:['Пример объявления: доступны новые серверы.','Это демонстрационные данные для просмотра интерфейса.']};persist();render();};
function placePicker(){if(sheet?.kind!=='picker')return;const panel=root.querySelector('.picker-panel');if(!panel)return;const v=window.visualViewport,width=v?.width||innerWidth,height=v?.height||innerHeight,x=v?.offsetLeft||0,y=v?.offsetTop||0,anchor=sheet.anchor;const w=Math.min(Math.max(260,anchor?.width||320),width-24);panel.style.width=w+'px';panel.style.maxHeight=Math.max(160,height-40)+'px';const h=panel.offsetHeight||Math.min(height-40,64+sheet.options.length*52);panel.style.left=Math.max(x+12,Math.min(anchor?.left||x+12,x+width-w-12))+'px';const below=(anchor?.bottom||y+80)+8;panel.style.top=Math.max(y+12,Math.min(below+h<=y+height-12?below:(anchor?.top||below)-h-8,y+height-h-12))+'px';}
const renderBeforePickers=render;render=function(){renderBeforePickers();placePicker();};
const oldFeatureClick=handleFeatureClick;handleFeatureClick=function(event){
  const el=event.target.closest('[data-action]');if(!el||el.disabled||sheet?.closing)return oldFeatureClick(event);
  const action=el.dataset.action;
  if(action==='open-choice'){
    event.mobileFeatureHandled=true;event.stopImmediatePropagation();const field=el.dataset.id,options=selectorChoices.get(field);if(!options||!editor)return;openSheet('picker',{field,...options,value:editor[field]||options.options[0]?.[0],anchor:el.getBoundingClientRect?.()});
    const selectedOption=root.querySelector('.choice-option[aria-selected=true]')||root.querySelector('.choice-option');selectedOption?.focus();return;
  }
  if(action==='pick-choice'&&sheet?.kind==='picker'){
    event.mobileFeatureHandled=true;event.stopImmediatePropagation();if(!sheet.options.some(([id])=>id===el.dataset.id))return;editor[sheet.field]=el.dataset.id;delete editor.error;back();return;
  }
  return oldFeatureClick(event);
};
document.addEventListener('keydown',event=>{
  if(sheet?.kind!=='picker'||sheet.closing)return;
  const options=[...root.querySelectorAll('.choice-option')];
  if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();event.stopImmediatePropagation();const at=options.indexOf(document.activeElement),next=event.key==='Home'?0:event.key==='End'?options.length-1:(Math.max(0,at)+(event.key==='ArrowDown'?1:-1)+options.length)%options.length;for(const option of options)option.tabIndex=-1;options[next].tabIndex=0;options[next].focus();}
  if(['Enter',' '].includes(event.key)&&options.includes(event.target)){event.preventDefault();event.stopImmediatePropagation();event.target.click();}
},true);
window.addEventListener('resize',()=>{size();syncNavigation(true);placePicker();});window.visualViewport?.addEventListener('resize',placePicker);
