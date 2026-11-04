import {readFile,writeFile} from 'node:fs/promises';const base=new URL('./',import.meta.url);let js=await readFile(new URL('prototype.js',base),'utf8');
// Adding a configuration must not silently retarget an active or pending connection.
js=js.replace("state.servers.push(item);state.selected=item.id;", "state.servers.push(item);if(connection==='off')state.selected=item.id;else if(connection==='on'&&confirm('Конфигурация добавлена. Завершить тестовое подключение и выбрать новый сервер?')){connection='off';state.selected=item.id;}");
// Inline validation beside the relevant input as well as the editor summary.
js=js.replace("${hint?'<small>'+hint+'</small>':''}</label>", "${editorError&&((key==='port'&&editorError.includes('Порт'))||(key==='userId'&&editorError.includes('UUID'))||(key==='name'&&!editor.name.trim())||(key==='server'&&!p.server.trim()))?'<small class=\"inline-field-error\" role=\"alert\">'+escape(editorError)+'</small>':''}${hint?'<small>'+hint+'</small>':''}</label>");
js=js.replace("const items=[...container.querySelectorAll('button')],index=", "const items=[...container.querySelectorAll('button:not(:disabled)')],index=");
// Keep the user's scroll position during notification rendering; first entry follows the log.
js=js.replace("renderedPage=page;renderedCategory=category;", "if(!samePage&&page==='logs'&&logFollow){const logs=root.querySelector('.log-scroll');if(logs)logs.scrollTop=logs.scrollHeight;}renderedPage=page;renderedCategory=category;");
// Informative secret labels are stable across repeated reveal/hide actions.
js=js.replace("el.setAttribute('aria-label',(input.type==='text'?'Скрыть ':'Показать ')+input.getAttribute('aria-label'));", "el.setAttribute('aria-label',(input.type==='text'?'Скрыть ':'Показать ')+input.getAttribute('aria-label'));el.setAttribute('aria-pressed',String(input.type==='text'));");
new Function(js);await writeFile(new URL('prototype.js',base),js);console.log('Final interaction refinements saved.');
