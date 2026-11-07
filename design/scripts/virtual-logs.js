function groupLogWarning(groups,entry){
 if(entry.row.level!=='warning')return false;
 const parts=entry.row.time.split(':').map(Number);const seconds=parts.length===3?parts[0]*3600+parts[1]*60+parts[2]:NaN;
 if(!Number.isFinite(seconds))return false;
 const key=entry.row.groupKey||entry.row.text;const old=groups.get(key);const delta=old?(seconds-old.seconds+86400)%86400:Infinity;
 if(old&&delta<=60){old.entry.count++;old.entry.lastTime=entry.row.time;return true;}
 entry.count=1;entry.firstTime=entry.row.time;entry.lastTime=entry.row.time;groups.set(key,{seconds,entry});return false;
}
// The complete model is independent from the bounded, measured DOM viewport.
const virtualLog = {rows:[],prefix:[0],processed:0,key:'',frame:0,width:0,tree:new Float64Array(1024),measured:new Map(),groups:new Map()};
function logOffset(index){let correction=0;for(let i=index;i>0;i-=i&-i)correction+=virtualLog.tree[i];return virtualLog.prefix[index]+correction;}
function correctLogHeight(index,height){const old=virtualLog.measured.get(index)||virtualLog.prefix[index+1]-virtualLog.prefix[index],delta=height-old;virtualLog.measured.set(index,height);for(let i=index+1;i<virtualLog.tree.length;i+=i&-i)virtualLog.tree[i]+=delta;}
function growLogTree(){if(virtualLog.rows.length+1<virtualLog.tree.length)return;let size=virtualLog.tree.length;while(size<=virtualLog.rows.length+1)size*=2;virtualLog.tree=new Float64Array(size);const entries=[...virtualLog.measured];virtualLog.measured.clear();for(const [index,height]of entries)correctLogHeight(index,height);}
function logHeight(row,width){return  Math.max(32,18*Math.ceil(row.text.length/Math.max(20,Math.floor((width-170)/7)))+14);}
function indexLogRows(reset=false){
 const key=logLevel+'\n'+query.toLowerCase(),width=root.querySelector('.log-scroll')?.clientWidth||800;
 if(reset||key!==virtualLog.key||width!==virtualLog.width||virtualLog.processed>state.logs.length){virtualLog.rows=[];virtualLog.prefix=[0];virtualLog.processed=0;virtualLog.key=key;virtualLog.width=width;virtualLog.tree.fill(0);virtualLog.measured.clear();virtualLog.groups.clear();}
 const needle=query.toLowerCase();
 for(let i=virtualLog.processed;i<state.logs.length;i++){const row=state.logs[i];if((logLevel==='all'||row.level===logLevel)&&row.text.toLowerCase().includes(needle)){const entry={row,id:i};if(!groupLogWarning(virtualLog.groups,entry)){virtualLog.rows.push(entry);virtualLog.prefix.push(virtualLog.prefix.at(-1)+logHeight(row,width));}}}
 virtualLog.processed=state.logs.length;
 growLogTree();
}
function logIndexAt(y){let lo=0,hi=virtualLog.rows.length;while(lo<hi){const mid=(lo+hi)>>>1;if(logOffset(mid+1)<=y)lo=mid+1;else hi=mid;}return lo;}
function paintLogViewport(){
 const scroll=root.querySelector('.log-scroll'),list=scroll?.querySelector('.log-list');if(!list)return;
 const selection=window.getSelection?.();if(selection&&!selection.isCollapsed&&scroll.contains(selection.anchorNode))return;
 indexLogRows();const {rows,prefix}=virtualLog;
 const first=Math.max(0,logIndexAt(scroll.scrollTop)-10),last=Math.min(rows.length,logIndexAt(scroll.scrollTop+(scroll.clientHeight||600))+11);
 const anchor=logIndexAt(scroll.scrollTop),offset=scroll.scrollTop-logOffset(anchor);
 list.innerHTML=`<div aria-hidden="true" style="height:${logOffset(first)}px"></div>`+rows.slice(first,last).map(({row,id,count,firstTime,lastTime})=>`<div class="log-entry" data-log-id="${id}"><time>${escape(row.time)}</time><span class="log-level level-${row.level}">${row.level.toUpperCase()}</span><span>${escape(row.text)}${count>1?escape(` · Повторов: ${count}; первое: ${firstTime}; последнее: ${lastTime}`):''}</span></div>`).join('')+`<div aria-hidden="true" style="height:${logOffset(rows.length)-logOffset(last)}px"></div>`;
 // Refine estimates with actual wrapped heights. Preserve the viewed row.
 let changed=false,index=first;for(const el of list.querySelectorAll('[data-log-id]')){const height=el.getBoundingClientRect().height;if(height>0&&Math.abs(height-(logOffset(index+1)-logOffset(index)))>.5){correctLogHeight(index,height);changed=true;}index++;}
 if(changed){list.firstElementChild.style.height=logOffset(first)+'px';list.lastElementChild.style.height=(logOffset(rows.length)-logOffset(last))+'px';if(!(logFollow&&logAtEnd))scroll.scrollTop=logOffset(anchor)+offset;}
 const count=root.querySelector('.log-count');if(count)count.textContent=rows.length+' из '+state.logs.length+' записей';
 if(logFollow&&logAtEnd&&scroll.scrollTop<scroll.scrollHeight-scroll.clientHeight-2){scroll.scrollTop=scroll.scrollHeight;queueLogPaint();}
}
function queueLogPaint(){if(!virtualLog.frame)virtualLog.frame=requestAnimationFrame(()=>{virtualLog.frame=0;paintLogViewport();});}
logs=function(){indexLogRows();return `<div class="page premium-log-page"><div class="page-heading"><div><h1>Журнал</h1><p>Полный журнал приложения</p></div>${button('copy-logs',icon('copy')+'Копировать всё','outline')}</div><div class="logs-tools">${search('server-search','Поиск в журнале',query)}<select data-log-level="true" data-focus="log-level" aria-label="Уровень журнала">${[['all','Все уровни'],['info','Информация'],['warning','Предупреждения'],['error','Ошибки']].map(([id,name])=>`<option value="${id}" ${logLevel===id?'selected':''}>${name}</option>`).join('')}</select>${button('copy-filtered-logs','Копировать отфильтрованные','outline')}${button('log-follow','Следить','outline')}${button('clear-logs',icon('trash'),'icon-button')}</div><div class="log-scroll" data-focus="log-scroll" tabindex="0" aria-label="Записи журнала"><div class="log-list"><div style="height:${virtualLog.prefix.at(-1)}px"></div></div></div><small class="log-count"></small></div>`;};
const renderWithVirtualLogs=render;
render=function(){renderWithVirtualLogs();if(page==='logs')queueLogPaint();};
root.addEventListener('scroll',event=>{if(event.target.classList?.contains('log-scroll'))queueLogPaint();},true);
document.addEventListener('selectionchange',()=>{if(window.getSelection?.()?.isCollapsed&&page==='logs')queueLogPaint();});
let logSearchTimer;
root.addEventListener('input',event=>{if(page==='logs'&&event.target.dataset.input==='server-search'){event.stopImmediatePropagation();query=event.target.value;clearTimeout(logSearchTimer);logSearchTimer=setTimeout(()=>{indexLogRows(true);queueLogPaint();},150);}},true);
root.addEventListener('change',event=>{if(page==='logs'&&event.target.dataset.logLevel){event.stopImmediatePropagation();logLevel=event.target.value;indexLogRows(true);queueLogPaint();}},true);
const actionBeforeLogCopy=handleNativeAction;
handleNativeAction=async function(action,id,el){if(action==='copy-logs'||action==='copy-filtered-logs'){try{await request('logs.copy',{level:action==='copy-logs'?'all':logLevel,query:action==='copy-logs'?'':query});showToast('Журнал скопирован');}catch(error){showToast(error.message);}return true;}return actionBeforeLogCopy(action,id,el);};
