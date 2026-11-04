function paintServiceStatuses(){
 const services=state.services||[],colors=snapshot?.serviceStatusColors||{available:'#34c759',unavailable:'#ff453a'};
 const shortcut=root.querySelector('.service-shortcut');
 if(shortcut){
  shortcut.querySelector('.service-health-icons')?.remove();const shown=services.filter(s=>s.visible);
  if(shown.length){const strip=document.createElement('div');strip.className='service-health-icons';strip.setAttribute('aria-label','Доступность отслеживаемых сервисов');strip.style.cssText='display:flex;gap:14px;align-items:center;padding:10px 16px;border:1px solid var(--border,#7774);border-radius:22px;flex-wrap:wrap;margin-right:12px';
   for(const service of shown){const item=document.createElement('span'),known=['available','warning','failed'].includes(service.status),up=service.status==='available';const label=service.name+': '+(known?up?'работает':'не отвечает':'ещё не проверен');item.title=label;item.setAttribute('aria-label',label);item.style.cssText='display:flex;align-items:center;gap:7px';
    if(service.iconData){const img=document.createElement('img');img.src=service.iconData;img.alt=service.name;img.width=22;img.height=22;item.append(img);}else{const name=document.createElement('span');name.textContent=service.name.slice(0,2);name.style.cssText='font-size:12px;min-width:22px';item.append(name);}
    const dot=document.createElement('span');dot.style.cssText='width:9px;height:9px;border-radius:50%;background:'+(known?(up?colors.available:colors.unavailable):'#8e8e93');dot.setAttribute('aria-hidden','true');item.append(dot);strip.append(item);
   }shortcut.style.cssText='display:flex;justify-content:flex-end;align-items:center;flex-wrap:wrap;gap:8px';shortcut.prepend(strip);
  }
 }
 if(dialog?.featureKind==='services'){
  const list=root.querySelector('.service-list');if(list&&!root.querySelector('.service-status-colors')){const group=document.createElement('div');group.className='service-status-colors';group.style.cssText='display:flex;gap:20px;flex-wrap:wrap;padding:14px 0';
   for(const [key,title]of [['available','Работает'],['unavailable','Не отвечает']]){const label=document.createElement('label');label.textContent=title+' ';const input=document.createElement('input');input.type='color';input.style.cssText='width:48px;height:32px;padding:2px';input.value=colors[key];input.setAttribute('aria-label','Цвет статуса «'+title+'»');input.onchange=()=>{const inputs=group.querySelectorAll('input');perform('service.colors',{available:inputs[0].value,unavailable:inputs[1].value});};label.append(input);group.append(label);}list.before(group);
  }
 }
}
const renderWithServiceStatuses=render;render=function(){renderWithServiceStatuses();paintServiceStatuses();};
