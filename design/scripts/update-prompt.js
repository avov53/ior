const dismissedUpdateTags=new Set();let updatePrompt=null;
function paintUpdatePrompt(){
 if(page==='settings'&&category==='advanced'){const content=root.querySelector('.settings-content');if(content&&!content.querySelector('.check-app-update')){const button=document.createElement('button');button.className='check-app-update';button.textContent='Проверить обновления';button.onclick=()=>{dismissedUpdateTags.clear();perform('app.update.check',{},result=>{if(!result?.available)showToast('Подходящих обновлений для этой сборки нет.');});};content.append(button);}}
 const update=snapshot?.update;if(!update)return;
 if(!updatePrompt){
  if(update.status!=='available'||update.offerDismissed||!update.hasAsset||dismissedUpdateTags.has(update.tag)||dialog||routeConsentDialog)return;
  const popup=document.createElement('dialog');popup.style.cssText='max-width:min(440px,90vw);padding:24px;border:1px solid #7775;border-radius:22px;background:var(--panel,#181818);color:var(--text,#fff)';
  const heading=document.createElement('h2');heading.textContent='Доступна версия '+update.tag;popup.append(heading);
  const status=document.createElement('p');status.style.cssText='margin:18px 0';status.textContent='Обновить ior? Приложение установит новую версию в текущую папку и перезапустится.';popup.append(status);
  const accept=document.createElement('button');accept.textContent='Обновить';popup.append(accept);
  const later=document.createElement('button');later.textContent='Позже';popup.append(later);
  const close=()=>{dismissedUpdateTags.add(update.tag);popup.remove();updatePrompt=null;};
  const dismiss=()=>{close();request('app.update.dismiss',{tag:update.tag}).catch(error=>showToast(error.message));};later.onclick=dismiss;popup.addEventListener('cancel',event=>{event.preventDefault();dismiss();});
  accept.onclick=()=>{dismissedUpdateTags.add(update.tag);accept.disabled=true;status.textContent='Загрузка обновления…';perform('app.update.accept',{tag:update.tag});};
  updatePrompt={popup,status,accept,later};document.body.append(popup);popup.showModal?.();if(!popup.showModal)popup.setAttribute('open','');
  if(!state.settings.reduced)popup.animate?.([{opacity:0,transform:'translateY(12px)'},{opacity:1,transform:'translateY(0)'}],{duration:200,easing:'ease-out'});
 }
 if(update.status==='downloading'){updatePrompt.accept.disabled=true;updatePrompt.status.textContent='Загрузка и подготовка: '+(update.percent==null?'…':update.percent+'%');}
 if(update.status==='ready'){updatePrompt.accept.disabled=true;updatePrompt.status.textContent='Установка и перезапуск приложения…';}
 if(update.status==='failed'){updatePrompt.status.textContent='Обновление не установлено. '+(update.error||'Попробуйте позже.');updatePrompt.accept.disabled=false;updatePrompt.accept.textContent='Повторить';}
}
const renderWithUpdatePrompt=render;render=function(){renderWithUpdatePrompt();paintUpdatePrompt();};
