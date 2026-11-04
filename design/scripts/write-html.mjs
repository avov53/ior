import {readFile,writeFile,rename,rm} from 'node:fs/promises';
import {randomUUID} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';

export async function writeHtml(path,html,io={readFile,writeFile,rename,rm},wait=delay){
  try{if(await io.readFile(path,'utf8')===html)return;}catch(error){if(error.code!=='ENOENT')throw error;}
  const temporary=new URL('./.index-'+randomUUID()+'.tmp',path);
  try{
    await io.writeFile(temporary,html,{encoding:'utf8',flag:'wx'});
    for(let attempt=0;;attempt++){
      try{await io.rename(temporary,path);return;}
      catch(error){
        if(!['UNKNOWN','EBUSY','EPERM','EACCES'].includes(error.code)||attempt===5){
          throw new Error('Не удалось заменить design/index.html. Предыдущая страница сохранена. Закройте её предпросмотр и проверьте права записи, затем повторите сборку.',{cause:error});
        }
        await wait(200*(attempt+1));
      }
    }
  }finally{await io.rm(temporary,{force:true}).catch(()=>{});}
}
