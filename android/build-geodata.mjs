import {readFile,writeFile,mkdir} from 'node:fs/promises';
const destination=new URL('app/src/main/assets/geodata/',import.meta.url);await mkdir(destination,{recursive:true});
function fields(data){const result=[];let offset=0;const number=()=>{let value=0,scale=1;for(let i=0;i<10;i++){if(offset>=data.length)throw Error('Truncated geodata');const b=data[offset++];value+=(b&127)*scale;if(!(b&128))return value;scale*=128;}throw Error('Invalid geodata varint');};while(offset<data.length){const tag=number(),n=Math.floor(tag/8),wire=tag&7;if(wire===0)result.push({n,value:number()});else if(wire===2){const size=number();if(size>data.length-offset)throw Error('Invalid geodata length');result.push({n,bytes:data.subarray(offset,offset+size)});offset+=size;}else if(wire===1)offset+=8;else if(wire===5)offset+=4;else throw Error('Unsupported geodata field');}return result;}
let count=0;
for(const type of ['geoip','geosite']){
 const data=await readFile(new URL('../core/xray/'+type+'.dat',import.meta.url));
 for(const entry of fields(data).filter(f=>f.n===1)){
  const values=fields(entry.bytes),name=values.find(f=>f.n===1)?.bytes?.toString('utf8').toLowerCase();if(!name||!/^[a-z0-9_-]+$/.test(name))continue;
  const rules=[];
  if(type==='geoip'&&values.some(f=>f.n===3&&f.value))continue;
  for(const item of values.filter(f=>f.n===2)){
   const p=fields(item.bytes);
   if(type==='geoip'){const ip=p.find(f=>f.n===1)?.bytes,prefix=p.find(f=>f.n===2)?.value||0;if(!ip||![4,16].includes(ip.length))throw Error('Invalid IP geodata');const text=ip.length===4?[...ip].join('.'):[...Array(8)].map((_,i)=>ip.readUInt16BE(i*2).toString(16)).join(':');rules.push({kind:'IP-CIDR',value:text+'/'+prefix});}
   else{const kind=['DOMAIN-KEYWORD','REGEXP','DOMAIN-SUFFIX','DOMAIN'][p.find(f=>f.n===1)?.value||0],value=p.find(f=>f.n===2)?.bytes?.toString('utf8');if(!kind||!value)throw Error('Invalid domain geodata');rules.push({kind,value});}
  }
  const compact=type==='geoip'?[{kind:'IP-SET',value:rules.map(r=>r.value).join('\n')}]:rules;
  await writeFile(new URL(type+'-'+name+'.json',destination),JSON.stringify(compact));count++;
 }
}
console.log('Bundled routing geodata sections:',count);
