// HTML adaptation of MIT shadcn/ui Button, Card, Item and Badge patterns.
// Sources: https://www.shadcn.io/ui/{button,card,item}
// React renders component templates at build time; browser actions remain local.
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url),React=require('../../node_modules/react'),{renderToStaticMarkup}=require('../../node_modules/react-dom/server'),lucide=require('../../node_modules/lucide-react');
const h=React.createElement;
export function Button(){return h('button',{'data-slot':'button','data-action':'__ACTION__','data-variant':'__VARIANT__',className:'ui-button __CLASS__','data-extra':''},'__CONTENT__')}
export function Card(){return h('section',{'data-slot':'card',className:'ui-card __CLASS__','data-extra':''},'__CONTENT__')}
export function Item(){return h('article',{'data-slot':'item','data-variant':'outline',role:'listitem',className:'ui-item __CLASS__','data-extra':''},'__CONTENT__')}
export function Badge(){return h('span',{'data-slot':'badge',className:'ui-badge __CLASS__'},'__CONTENT__')}
export function Switch(){return h('button',{'data-slot':'switch','data-action':'switch',role:'switch','aria-checked':'__CHECKED__','data-state':'__STATE__',className:'ui-switch switch __CLASS__','data-extra':''},h('span',{'data-slot':'switch-thumb','data-state':'__STATE__'}))}
export function Input(){return h('input',{'data-slot':'input',className:'ui-input __CLASS__','data-extra':''})}
const components={button:Button,card:Card,item:Item,badge:Badge,switch:Switch,input:Input};
const names={home:'House',servers:'Server',profiles:'Layers',route:'Route',logs:'ScrollText',settings:'Settings2',search:'Search',panel:'PanelLeft',plus:'Plus',more:'Ellipsis',globe:'Globe2',check:'Check',power:'Power',copy:'Copy',close:'X',sun:'Sun',info:'Info',download:'Download',bell:'Bell',folder:'Folder',eye:'Eye',edit:'SquarePen',chevron:'ChevronDown',star:'Star',activity:'Activity',trash:'Trash2',loader:'LoaderCircle'};
export const iconMarkup=Object.fromEntries(Object.entries(names).map(([name,component])=>[name,renderToStaticMarkup(h(lucide[component],{size:18,strokeWidth:1.7,'aria-hidden':true}))]));
export function browserComponents(){const templates=Object.fromEntries(Object.entries(components).map(([key,component])=>[key,renderToStaticMarkup(h(component))]));return `window.iorIcons=${JSON.stringify(iconMarkup)};window.iorComponents=(()=>{const templates=${JSON.stringify(templates)};const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));const render=(kind,p)=>templates[kind].replace('data-extra=""',()=>p.extra||'').replaceAll('__CLASS__',()=>esc(p.className||'')).replaceAll('__ACTION__',()=>esc(p.action||'')).replaceAll('__VARIANT__',()=>esc(p.variant||'ghost')).replaceAll('__STATE__',()=>p.checked?'checked':'unchecked').replaceAll('__CHECKED__',()=>String(!!p.checked)).replace('__CONTENT__',()=>p.html||'');return Object.fromEntries(Object.keys(templates).map(k=>[k,p=>render(k,p)]))})();`}
