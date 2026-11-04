// Compact object menus and a stable import frame, inside the mobile closure.
function menuButtons(kind){
  if(kind==='subscription')return btn('subscription-details',icon('info')+'Сведения','menu-button')+btn('unavailable',icon('activity')+'Обновить подписку','menu-button')+btn('delete-object',icon('trash')+'Удалить','menu-button danger');
  return btn('edit-object',icon('edit')+'Редактировать','menu-button')+(kind==='server'?btn('validate-config',icon('check')+'Проверить конфигурацию','menu-button')+btn('share',icon('copy')+'Ссылка и QR','menu-button')+btn('duplicate',icon('copy')+'Создать копию','menu-button'):btn('select-object',icon('check')+'Выбрать','menu-button')+btn('export-object',icon('download')+'Экспорт макета','menu-button'))+btn('delete-object',icon('trash')+'Удалить','menu-button danger');
}
function contextMenuView(){const item=find(sheet.objectKind,sheet.id);if(!item)return '';return '<div class="context-menu" data-sheet="menu" role="menu" aria-label="Действия: '+esc(item.name)+'" style="left:8px;top:8px"><div class="context-caption">'+esc(item.name)+'</div>'+menuButtons(sheet.objectKind).replace(/<button /g,'<button role="menuitem" tabindex="-1" ')+'</div>';}
function placeMenu(){if(sheet?.kind!=='menu')return;const menu=root.querySelector('.context-menu');if(!menu)return;const viewport=window.visualViewport;const width=viewport?.width||innerWidth,height=viewport?.height||innerHeight,offsetX=viewport?.offsetLeft||0,offsetY=viewport?.offsetTop||0;const menuWidth=Math.min(240,width-16);menu.style.width=menuWidth+'px';menu.style.maxHeight=Math.max(48,height-16)+'px';const bounds=menu.getBoundingClientRect?.(),menuHeight=menu.offsetHeight||bounds?.height||((sheet.objectKind==='server'?6:sheet.objectKind==='subscription'?3:4)*48+44);const point=sheet.position||{x:offsetX+8,y:offsetY+8};let x=point.x,y=point.y;if(x+menuWidth>offsetX+width-8)x=point.x-menuWidth;if(y+menuHeight>offsetY+height-8)y=(point.top??point.y)-menuHeight;menu.style.left=Math.max(offsetX+8,Math.min(x,offsetX+width-menuWidth-8))+'px';menu.style.top=Math.max(offsetY+8,Math.min(y,offsetY+height-menuHeight-8))+'px';}
const openSheetBeforeMenus=openSheet;openSheet=function(kind,data={}){
  if(kind!=='menu')return openSheetBeforeMenus(kind,data);
  const trigger=[...root.querySelectorAll('[data-action="server-menu"],[data-action="object-menu"]')].find(el=>el.dataset.id===data.id&&(!el.dataset.kind||el.dataset.kind===data.objectKind));
  const rect=trigger?.getBoundingClientRect?.();const position=data.position||(rect?{x:rect.left,y:rect.bottom,top:rect.top}:{x:8,y:8});
  const active=document.activeElement,activeRow=active?.closest?.('[data-context]');
  const returnTo=(activeRow?.dataset.object===data.id?active.dataset.focus:null)||trigger?.dataset.focus||active?.dataset?.focus||opener;
  opener=returnTo;
  if(sheet?.kind==='menu'){sheet={kind,objectKind:data.objectKind,id:data.id,position};render();}
  else {openSheetBeforeMenus(kind,{...data,position});opener=returnTo;}
  const items=[...root.querySelectorAll('.context-menu [role=menuitem]')];if(items[0]){items[0].tabIndex=0;items[0].focus();}placeMenu();
};
const sheetViewBeforeControls=sheetView;sheetView=function(){
  if(sheet?.kind==='menu')return contextMenuView();
  const html=sheetViewBeforeControls();if(sheet?.kind!=='import')return html;
  const headerEnd=html.indexOf('</header>')+9,footerStart=html.lastIndexOf('<div class="sheet-actions">'),footerEnd=html.indexOf('</div>',footerStart)+6,sectionEnd=html.indexOf('</section>');
  if(headerEnd<9||footerStart<0)return html;
  let content=html.slice(headerEnd,footerStart);content=content.replace(/(?:<input[^>]*data-file="config"[^>]*>)?<button[^>]*data-action="(?:paste|choose-file)"[^>]*>[\s\S]*?<\/button>/,match=>'<div class="import-tools">'+match+'</div>');
  return html.slice(0,headerEnd).replace('class="sheet"','class="sheet import-sheet"')+'<div class="import-scroll">'+content+html.slice(footerEnd,sectionEnd)+'</div><footer class="import-footer">'+html.slice(footerStart,footerEnd)+'</footer></section></div>';
};
const renderBeforeControls=render;render=function(){const scroll=root.querySelector('.import-scroll')?.scrollTop||0;renderBeforeControls();const content=root.querySelector('.import-scroll');if(content)content.scrollTop=scroll;placeMenu();};
root.addEventListener('contextmenu',event=>{const row=event.target.closest('[data-context]');if(!row)return;event.preventDefault();event.stopImmediatePropagation();if(suppressClick)return;openSheet('menu',{objectKind:row.dataset.context,id:row.dataset.object,position:{x:event.clientX,y:event.clientY}});},true);
document.addEventListener('click',event=>{if(event.target.tagName==='A'&&event.target.download)return;if(sheet?.kind!=='menu'||event.target.closest('.context-menu,[data-action="server-menu"],[data-action="object-menu"]'))return;event.preventDefault();event.stopImmediatePropagation();back();},true);
document.addEventListener('scroll',event=>{if(sheet?.kind==='menu'&&!event.target.closest?.('.context-menu'))back();},true);
document.addEventListener('keydown',event=>{
  if(event.key==='ContextMenu'||event.shiftKey&&event.key==='F10'){const row=event.target.closest?.('[data-context]');if(row){event.preventDefault();event.stopImmediatePropagation();openSheet('menu',{objectKind:row.dataset.context,id:row.dataset.object});}return;}
  if(sheet?.kind!=='menu')return;
  const items=[...root.querySelectorAll('.context-menu [role=menuitem]')];
  if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();event.stopImmediatePropagation();const at=items.indexOf(document.activeElement),next=event.key==='Home'?0:event.key==='End'?items.length-1:(at+(event.key==='ArrowDown'?1:-1)+items.length)%items.length;for(const item of items)item.tabIndex=-1;items[next].tabIndex=0;items[next].focus();}
  if(event.key==='Tab'){event.preventDefault();event.stopImmediatePropagation();back();}
  if(['Enter',' '].includes(event.key)&&items.includes(event.target)){event.preventDefault();event.target.click();}
},true);
