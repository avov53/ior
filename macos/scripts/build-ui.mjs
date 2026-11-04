import {readFile,writeFile,mkdir} from 'node:fs/promises';
let html=await readFile(new URL('../../design/index.html',import.meta.url),'utf8');
// Production UI stays in its existing read-only preview until the macOS host is implemented.
html=html.replace('</body>','<div role="status" style="position:fixed;bottom:12px;left:12px;right:12px;padding:12px;background:#332b19;color:#fff;z-index:99999">ior macOS · предварительная оболочка. VPN и сохранение данных пока недоступны.</div></body>');
await mkdir(new URL('../web/',import.meta.url),{recursive:true});
await writeFile(new URL('../web/index.html',import.meta.url),html);
