import {readFile,writeFile} from 'node:fs/promises';const base=new URL('./',import.meta.url);let js=await readFile(new URL('prototype.js',base),'utf8');
js=js.replace("function render(){const oldInspector", "function render(){const previousShell=root.querySelector('.app');const previousColumns=previousShell?getComputedStyle(previousShell).gridTemplateColumns:'';const oldInspector");
js=js.replace("const pop=root.querySelector('.menu');", "const shell=root.querySelector('.app');if(shell&&previousColumns&&!state.settings.reduced&&!window.matchMedia('(prefers-reduced-motion: reduce)').matches){const columns=getComputedStyle(shell).gridTemplateColumns;if(columns!==previousColumns)shell.animate?.([{gridTemplateColumns:previousColumns},{gridTemplateColumns:columns}],{duration:180,easing:'cubic-bezier(.2,.7,.2,1)'});}const pop=root.querySelector('.menu');");
js=js.replace("const compact=event.target.scrollTop>48;", "const compact=connectionCompact?event.target.scrollTop>12:event.target.scrollTop>96;");
js=js.replace("page=target;query='';", "page=target;query='';connectionCompact=false;");
new Function(js);await writeFile(new URL('prototype.js',base),js);
