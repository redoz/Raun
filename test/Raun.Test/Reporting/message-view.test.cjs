const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const template=fs.readFileSync(path.join(__dirname,'../../../src/Raun/Reporting/Html/report-template.html'),'utf8');
const esc=value=>String(value ?? '').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
const decode=text=>text.replaceAll('&quot;','"').replaceAll('&#39;',"'").replaceAll('&lt;','<').replaceAll('&gt;','>').replaceAll('&amp;','&');
// Small deterministic DOM harness: runs real template builders and bubbling listeners, never a browser.
class Node {
 constructor(tag){this.tag=tag;this.children=[];this.attributes={};this.dataset={};this.listeners={};this.hidden=false;this.style={removeProperty(){},setProperty(){},getPropertyValue(){return '';}};}
 set className(v){this.attributes.class=v;} get className(){return this.attributes.class||'';}
 get classList(){const node=this;return {contains:x=>node.className.split(' ').includes(x),add:x=>{if(!node.classList.contains(x))node.className+=' '+x;},remove:x=>node.className=node.className.split(' ').filter(c=>c!==x).join(' '),toggle(x,force){const on=force ?? !node.classList.contains(x);if(on)this.add(x);else this.remove(x);return on;}};}
 set id(value){this.setAttribute('id',value);} get id(){return this.getAttribute('id');}
 replaceWith(node){const parent=this.parentElement;if(parent){const index=parent.children.indexOf(this);parent.children[index]=node;node.parentElement=parent;}}
 setAttribute(k,v){this.attributes[k]=String(v);if(k==='hidden')this.hidden=true;if(k.startsWith('data-'))this.dataset[k.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=String(v);}
 removeAttribute(k){delete this.attributes[k];}
 getAttribute(k){if(k.startsWith('data-'))return this.dataset[k.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]??null;return this.attributes[k]??null;}
 appendChild(n){if(n.parentElement)n.parentElement.children=n.parentElement.children.filter(child=>child!==n);n.parentElement=this;this.children.push(n);return n;} append(...nodes){nodes.forEach(n=>this.appendChild(n));}
 prepend(...nodes){for(const n of [...nodes].reverse()){if(n.parentElement)n.parentElement.children=n.parentElement.children.filter(child=>child!==n);n.parentElement=this;this.children.unshift(n);}}
 replaceChildren(...nodes){this.children=[];this.append(...nodes);} remove(){if(this.parentElement)this.parentElement.children=this.parentElement.children.filter(n=>n!==this);}
 getBoundingClientRect(){return {x:0,y:0,left:0,top:0,right:0,bottom:0,width:0,height:0};}
 showPopover(){this.popoverOpen=true;}
 addEventListener(k,h){(this.listeners[k]??=[]).push(h);} focus(){this.focused=true;} scrollIntoView(){this.scrolled=true;}
 getComputedTextLength(){return (this.textContent || '').length*5;}
 matches(selector){return selector.split(',').some(s=>{s=s.trim();if(s.includes(' > ')){const [parent,child]=s.split(' > ');return this.matches(child)&&this.parentElement?.matches(parent);}
  const tag=s.match(/^[a-z]+/);if(tag&&tag[0]!==this.tag)return false;
  for(const cls of s.matchAll(/\.([\w-]+)/g))if(!this.classList.contains(cls[1]))return false;
  for(const attr of s.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g))if(this.getAttribute(attr[1])===null||(attr[2]!==undefined&&this.getAttribute(attr[1])!==attr[2]))return false;
  return true;});}
 closest(s){for(let n=this;n;n=n.parentElement)if(n.matches(s))return n;return null;}
 querySelectorAll(s){return this.children.flatMap(n=>[...(n.matches(s)?[n]:[]),...n.querySelectorAll(s)]);} querySelector(s){return this.querySelectorAll(s)[0]||null;}
 set innerHTML(html){this.html=html;this.children=[];const stack=[this];for(const token of html.matchAll(/<[^>]+>|[^<]+/g)){
  const t=token[0];if(t.startsWith('</')){stack.pop();continue;}
  if(t.startsWith('<')){const tag=t.match(/^<([\w-]+)/)?.[1];if(!tag)continue;const n=new Node(tag);for(const attr of t.slice(tag.length+1,-1).matchAll(/([\w-]+)(?:="([^"]*)")?/g))n.setAttribute(attr[1],decode(attr[2]??''));stack.at(-1).appendChild(n);if(!t.endsWith('/>')&&!['input','br','hr'].includes(tag))stack.push(n);}
  else{const n=new Node('#text');n.textContent=decode(t);stack.at(-1).appendChild(n);}
 }} get innerHTML(){return this.html||'';}
 dispatch(type,props={}){const event={type,target:this,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;},...props};for(let n=this;n;n=n.parentElement){for(const h of n.listeners[type]||[])h(event);if(event.stopped)break;}return event;}
}
const el=(tag,cls)=>{const n=new Node(tag);n.className=cls||'';return n;};
const svgEl=(tag,attrs)=>{const n=el(tag);for(const [k,v] of Object.entries(attrs||{}))n.setAttribute(k,v);return n;};
const documentEvents={};
const browserLayout={ResizeObserver:class {observe(){} disconnect(){}},requestAnimationFrame(){}};
const document={fonts:{ready:{then(){}}},documentElement:el('html'),body:el('body'),createElement:el,createTextNode:text=>Object.assign(el('#text'),{textContent:text}),addEventListener:(name,handler)=>(documentEvents[name]??=[]).push(handler)};
const dispatchDocument=(name,target,key)=>{const ev={target,key,preventDefault(){this.prevented=true;}};for(const handler of documentEvents[name]||[])handler(ev);return ev;};
const scope=vm.createContext({...browserLayout,document,el,svgEl,window:{matchMedia:()=>({matches:true})},setTimeout,clearTimeout,getComputedStyle:()=>({getPropertyValue:()=>''}),typeColor:()=>'',keyColor:()=>'',fmtMs:n=>Number(n).toFixed(1)+' ms',fmtDur:n=>n+' ms',SHOW_DIAGRAM:false,PHCOLOR:{},VCOLOR:{},Blob,typeSlot:()=>0,hashOf:()=>1,KEY_SLOTS:7,catVar:()=>'',expansionFor:()=>new Map(),labelColor:()=>'',contrastInk:()=>'',placeVerbLabels(){},RESOURCE_LINE:/^(\+[\d.]+s )?\[resource\] (\w+) ([^:\s]+):(.*)$/});
vm.runInContext(template.slice(template.indexOf('    const esc ='),template.indexOf('    const el ='))+'\nthis.actualEsc=esc;',scope);
vm.runInContext(template.slice(template.indexOf('    const expandedFor ='),template.indexOf('    const shortName =')),scope);
vm.runInContext(template.slice(template.indexOf('    const shortName ='),template.indexOf('    const collapsedGroups =')),scope);
vm.runInContext(template.match(/    let messageInspectorSequence = 0;/)[0]+'\n'+template.match(/    let stepDetailSequence = 0;/)[0],scope);
const start=template.indexOf('function resourceToken');
if(start>=0) vm.runInContext(template.slice(start,template.indexOf('function attSize',start)),scope);
vm.runInContext(template.slice(template.indexOf('function stepNotesHtml'),template.indexOf('function renderAll')),scope);
vm.runInContext(template.slice(template.indexOf('function keyColor'),template.indexOf('    // one delegated handler:')),scope);
vm.runInContext(template.slice(template.indexOf('function transitionStep'),template.indexOf('function installScenarioMatrix')),scope);
vm.runInContext(template.slice(template.indexOf('function attSize'),template.indexOf('  </script>',template.indexOf('function buildScenarioDrill'))),scope);
const resource={id:'r0-0',type:'Seed',qualifiedType:'Ns.Seed',key:'key:/"<&',events:[]};
const scenario={resources:[resource],steps:[]};
const message=text=>({text,parts:[{text,resourceId:resource.id,data:'<script>\nold',dataTruncated:true}]});
test('resource parts keep escaped text and accessible canonical identity without a popup',()=>{
 for(const text of ['Seed:key','key','<preview>']) {
  const html=scope.messageHtml(message(text),scenario);
  assert.match(html,/data-message-resource="r0-0"/);
  assert.ok(html.includes(esc(resource.qualifiedType)));assert.ok(html.includes(esc(resource.key)));
  assert.ok(html.includes(esc(text)));assert.equal(html.includes('<script>'),false);
  assert.equal(html.includes('resource-inspector'),false);
 }
});
test('identity-only references are clickable but unknown identities and resource-looking prose remain plain',()=>{
 const html=scope.messageHtml({parts:[{text:'key',resourceId:resource.id}]},scenario);
 assert.match(html,/data-message-resource="r0-0"/);
 assert.equal(scope.messageHtml({parts:[{text:'Seed:key'}]},scenario),'Seed:key');
 assert.equal(scope.messageHtml({parts:[{text:'Seed:key',resourceId:'missing'}]},scenario),'Seed:key');
});
test('all three message surfaces render structured parts in the real drill builder',()=>{
 const m=message('<preview>');
 const p=scope.buildScenarioDrill({...scenario,steps:[{stepId:'s',index:0,phase:'Then',status:'passed',displayName:'old',titleMessage:m,notes:['old'],noteMessages:[m],logs:['old'],logMessages:[m],offsetMs:0,durationMs:1}]});
 assert.equal((p.innerHTML.match(/data-message-resource="r0-0"/g)||[]).length,3);assert.equal(p.innerHTML.includes('>old<'),false);
});
const step=(id,m)=>({stepId:id,index:0,label:'1',phase:'Then',status:'passed',displayName:'Then Seed:key',titleMessage:m,noteMessages:[m],notes:[m.text],logMessages:[m],logs:[m.text],offsetMs:0,durationMs:1,effects:[]});
const nodeText=node=>(node.textContent||'')+(node.children||[]).map(nodeText).join('');

test('resource selection preserves the same step body, expansion and scroll',()=>{
 const p=scope.buildScenarioDrill({...scenario,steps:[step('s',message('Seed:key'))]});p.scrollTop=123;
 const entry=p.querySelector('.step-entry'),body=entry.querySelector('.step-body'),chip=entry.querySelector('[data-message-resource]');
 chip.dispatch('click');assert.equal(entry.classList.contains('res-hit'),true);
 assert.equal(entry.classList.contains('open'),false);assert.equal(p.scrollTop,123);
 assert.equal(body,entry.querySelector('.step-body'));
 chip.dispatch('click');assert.equal(entry.classList.contains('res-hit'),false);
 assert.equal(body,entry.querySelector('.step-body'));
});

test('equal labels with different opaque IDs never share a highlight',()=>{
 const other={...resource,id:'r0-1'};
 const first=step('first',message('key'));
 const second=step('second',{parts:[{text:'key',resourceId:other.id}]});
 const p=scope.buildScenarioDrill({resources:[resource,other],steps:[first,second]});
 p.querySelector('[data-message-resource="r0-0"]').dispatch('click');
 const rows=p.querySelectorAll('.step-entry');
 assert.equal(rows[0].classList.contains('res-hit'),true);assert.equal(rows[1].classList.contains('res-hit'),false);
 p.querySelector('[data-message-resource="r0-1"]').dispatch('click');
 assert.equal(rows[0].classList.contains('res-hit'),false);assert.equal(rows[1].classList.contains('res-hit'),true);
});

test('structured text retains literal order and does not resolve IDs from another scenario',()=>{
 const other={...resource,id:'r1-0',qualifiedType:'Other.Seed'};
 const m={parts:[{text:'before <'},{text:'id',resourceId:resource.id,data:'old'},{text:' between '},{text:'data',resourceId:resource.id,data:'new'},{text:' after'}]};
 const html=scope.messageHtml(m,scenario);
 assert.match(html,/before &lt;.*>id<.* between .*>data<.* after/s);
 assert.equal(scope.messageHtml(m,{resources:[other]}),'before &lt;id between data after');
});

test('resource references in title note and logs select the same usage without scrolling',()=>{
 const current=step('s',message('key'));current.effects=[{effect:'Read',resourceId:resource.id,type:resource.type,key:resource.key,offsetMs:3,data:'old'}];
 const p=scope.buildScenarioDrill({...scenario,steps:[current]}),entry=p.querySelector('.step-entry');
 const chips=p.querySelectorAll('[data-message-resource]');assert.equal(chips.length,3);assert.equal(p.querySelector('.resource-events'),null);
 for(const chip of chips){
  chip.dispatch('click');assert.equal(entry.classList.contains('res-hit'),true);
  assert.equal(entry.scrolled,undefined);assert.equal(entry.classList.contains('open'),false);
  dispatchDocument('keydown',chip,'Escape');assert.equal(entry.classList.contains('res-hit'),false);
 }
});

test('legacy strings and known lifecycle regex remain supported without inferring typed prose',()=>{
 const p=scope.buildScenarioDrill({steps:[{...step('s',{}),titleMessage:undefined,noteMessages:undefined,logMessages:undefined,logs:['[resource] Read Seed: old'],notes:['Why <script>'],effects:[]}],resources:[]});
 assert.match(p.innerHTML,/class="res-chip"/);assert.match(p.innerHTML,/Why &lt;script&gt;/);
 assert.equal(scope.messageHtml({parts:[{text:'[resource] Read Seed: old'}]},scenario),'[resource] Read Seed: old');
 assert.equal(scope.actualEsc('"\' <&>'),'&quot;&#39; &lt;&amp;&gt;');
});

test('retired activity and object graph renderers are removed',()=>{
 for(const symbol of ['SHOW_DIAGRAM','buildActivityDiagram','buildObjectFlow','layoutScenario','adForkView','applySun','.actdiag{'])assert.equal(template.includes(symbol),false,symbol);
});

test('plain canonical log entries retain the existing known lifecycle regex path and timer',()=>{
 const log={text:'+1.000s [resource] Read Seed:old',parts:[{text:'+1.000s '},{text:'[resource] Read Seed:old'}]};
 const p=scope.buildScenarioDrill({...scenario,steps:[{...step('s',message('x')),logMessages:[log]}]});
 assert.match(p.innerHTML,/\+1.000s <span class="lg-tag">/);assert.match(p.innerHTML,/data-res="Seed:old"/);
});
test('complete report script renders typed messages on first paint in declaration order',()=>{
 const body=el('body'),root=el('html');root.appendChild(body);
 const report=el('div');report.id='report';body.appendChild(report);
 const summary=el('div');summary.id='run-summary';body.appendChild(summary);
 const model=el('script');model.id='model';body.appendChild(model);
 model.textContent=JSON.stringify({summary:{passed:1,failed:0,skipped:0,totalMs:1},generatedAtUtc:'2026-10-13T00:00:00Z',scenarios:[{...scenario,scenarioId:'first',methodName:'N.First',displayName:'First',durationMs:1,status:'passed',steps:[step('s',message('key'))]}]});
 const document={fonts:{ready:{then(){}}},body,documentElement:root,createElement:el,createElementNS:(_,tag)=>el(tag),createTextNode:text=>Object.assign(el('#text'),{textContent:text}),getElementById:id=>root.querySelectorAll('[id]').find(n=>n.id===id),querySelectorAll:s=>root.querySelectorAll(s),addEventListener(){}};
 const context=vm.createContext({...browserLayout,document,window:{matchMedia:()=>({matches:true})},location:{search:'?theme=light'},URLSearchParams,getComputedStyle:()=>({getPropertyValue:()=>''}),Blob,CSS:{escape:x=>x}});
 const code=template.slice(template.indexOf('  <script>')+'  <script>'.length,template.lastIndexOf('  </script>'));
 assert.doesNotThrow(()=>vm.runInContext(code,context));
 assert.equal(report.querySelectorAll('[data-message-resource]').length,3);
 const card=report.querySelector('.card'),head=card.querySelector('.card-head');
 assert.equal(card.classList.contains('collapsed'),true);
 head.dispatch('click');assert.equal(card.classList.contains('collapsed'),false);
 head.dispatch('click');assert.equal(card.classList.contains('collapsed'),true);
 assert.equal(vm.runInContext('fmtMs(1.234567)',context),'1.2 ms');
 const expander=report.querySelector('.step-logs-toggle');assert.equal(expander.getAttribute('aria-expanded'),'false');
 vm.runInContext("openers[0].open('s');",context);assert.equal(expander.getAttribute('aria-expanded'),'true');
 assert.equal(report.querySelector('.step-head').getAttribute('aria-expanded'),null);
});

test('title resource controls have no interactive ancestor',()=>{
 const p=scope.buildScenarioDrill({...scenario,steps:[step('s',message('key'))]});
 const head=p.querySelector('.step-head'),title=head.querySelector('.dn');
 for(const node of title.querySelectorAll('button'))
  assert.equal(node.parentElement.closest('button,[role="button"],a[href],summary,input,select,textarea'),null);
 assert.equal(head.getAttribute('role'),null);assert.equal(head.getAttribute('tabindex'),null);
 const expander=head.querySelector('.step-logs-toggle');assert.equal(expander.tag,'button');
 assert.equal(expander.closest('.step-head'),head);assert.equal(expander.getAttribute('aria-expanded'),'false');
 assert.equal(expander.getAttribute('aria-controls'),p.querySelector('[data-evidence-panel="logs"]').id);
});
test('native Enter and Space activate only their own resource or evidence glyph once',()=>{
 for(const key of ['Enter',' ']){
  const p=scope.buildScenarioDrill({...scenario,steps:[step('s',message('key'))]}),entry=p.querySelector('.step-entry');
  const chip=entry.querySelector('[data-message-resource]');
  const keydown=chip.dispatch('keydown',{key});assert.equal(keydown.prevented,undefined);
  assert.equal(entry.classList.contains('res-hit'),false);assert.equal(entry.classList.contains('open'),false);
  // Native button activation synthesizes one click; production must not duplicate it in keydown.
  chip.dispatch('click');assert.equal(entry.classList.contains('res-hit'),true);assert.equal(entry.classList.contains('open'),false);
  const expander=entry.querySelector('.step-logs-toggle');assert.ok(expander);
  const expandKey=expander.dispatch('keydown',{key});assert.equal(expandKey.prevented,undefined);
  assert.equal(entry.classList.contains('open'),false);
  expander.dispatch('click');assert.equal(entry.classList.contains('open'),true);assert.equal(expander.getAttribute('aria-expanded'),'true');
  expander.dispatch('click');assert.equal(entry.classList.contains('open'),false);assert.equal(expander.getAttribute('aria-expanded'),'false');
  dispatchDocument('keydown',chip,'Escape');assert.equal(entry.classList.contains('res-hit'),false);
 }
});

test('resource activation highlights usage and Escape or outside click clears without changing step expansion',()=>{
 const current=step('s',message('Seed:key'));
 current.effects=[{effect:'Read',resourceId:resource.id,type:resource.type,key:resource.key,offsetMs:3}];
 const p=scope.buildScenarioDrill({...scenario,steps:[current]});
 const entry=p.querySelector('.step-entry'),chip=p.querySelector('[data-message-resource]');
 chip.dispatch('click');
 assert.equal(entry.classList.contains('res-hit'),true);
 assert.equal(chip.getAttribute('aria-pressed'),'true');
 assert.equal(entry.classList.contains('open'),false);
 assert.equal(p.querySelector('.resource-inspector'),null);
 dispatchDocument('keydown',chip,'Escape');
 assert.equal(entry.classList.contains('res-hit'),false);
 chip.dispatch('click');
 dispatchDocument('click',el('div'));
 assert.equal(entry.classList.contains('res-hit'),false);
 assert.equal(entry.classList.contains('open'),false);
});

test('expanded steps show logs directly and preserve notes without duplicate resource events',()=>{
 const current=step('s',message('Seed:key'));current.offsetMs=100;
 current.effects=[{effect:'Read',resourceId:resource.id,type:resource.type,key:resource.key,offsetMs:103,data:'<old>'}];
 const p=scope.buildScenarioDrill({...scenario,steps:[current]});
 const entry=p.querySelector('.step-entry');entry.querySelector('.step-logs-toggle').dispatch('click');
 assert.equal(entry.classList.contains('open'),true);
 const body=entry.querySelector('.step-body');
 assert.ok(body.querySelector('div.step-logs'),'logs appear without a second disclosure');
 assert.equal(body.querySelector('details.step-logs'),null);
 assert.equal(body.querySelector('table'),null,'resource evidence is rendered by the matrix');
 assert.ok(p.querySelector('.step-note'));
});

test('phase coloring uses matching authored prefix once with an empty flow gutter before status',()=>{
 const current={...step('s',{text:'Given Seed:key',parts:[{text:'Given '},{text:'Seed:key',resourceId:resource.id}]}),phase:'Given',index:12,label:'13'};
 const p=scope.buildScenarioDrill({...scenario,steps:[current]});
 const head=p.querySelector('.step-head');
 assert.equal(head.querySelector('.ph-chip'),null);
 assert.equal(head.querySelector('.pill'),null);
 assert.doesNotMatch(nodeText(head),/#13/);
 assert.ok(head.children[0].classList.contains('stw'));
 assert.ok(head.children[1].classList.contains('scenario-status'));
 assert.ok(head.children[1].classList.contains('passed'));
 assert.equal(head.children[1].getAttribute('aria-label'),'passed');
 assert.equal(head.querySelector('.sx'),null);
 assert.ok(head.querySelector('.phase-given'));
 const mismatched=scope.buildScenarioDrill({...scenario,steps:[{...current,phase:'When'}]});
 assert.equal(mismatched.querySelector('.phase-given'),null);
 assert.equal(mismatched.querySelector('.phase-when'),null);
 assert.equal((nodeText(head).match(/Given/g)||[]).length,1);
});


test('phase-like resource text remains one fully clickable part',()=>{
 for(const value of ['Given','Given item']) {
  const current={...step('s',{text:value,parts:[{text:value,resourceId:resource.id}]}),phase:'Given'};
  const p=scope.buildScenarioDrill({...scenario,steps:[current]});
  const chip=p.querySelector('.dn').querySelector('[data-message-resource]');
  assert.equal(nodeText(chip),value);
  chip.dispatch('click');assert.equal(p.querySelector('.step-entry').classList.contains('res-hit'),true);
 }
});

test('attachments use native collapsed disclosure and escaped captured content',()=>{
 const current=step('s',message('key'));current.attachments=[{name:'response <body>',value:'first\n<script>unsafe</script>'}];
 const p=scope.buildScenarioDrill({...scenario,steps:[current]});
 const attachment=p.querySelector('details.att');assert.ok(attachment);
 assert.equal(attachment.getAttribute('open'),null);
 assert.ok(attachment.querySelector('.att-body'));assert.equal(attachment.querySelector('.exc'),null);
 assert.match(p.innerHTML,/&lt;script&gt;unsafe&lt;\/script&gt;/);
 assert.equal(p.innerHTML.includes('<script>unsafe'),false);
});


test('resource hover shows captured reference data, preserves highlighting and copies actual key and data',async()=>{
 const copied=[];scope.navigator={clipboard:{writeText:async value=>copied.push(value)}};
 const captured='<preview>\n{"patient":"Alice"}';
 const current=step('hover',{text:'Appointment',parts:[{text:'Appointment',resourceId:resource.id,data:captured,dataTruncated:true}]});
 const p=scope.buildScenarioDrill({...scenario,steps:[current]});
 const chip=p.querySelector('[data-message-resource]');
 chip.dispatch('mouseover');
 const card=p.querySelector('.resource-hover');assert.ok(card);
 assert.equal(card.getAttribute('role'),'dialog');
 assert.equal(nodeText(card.querySelector('pre')),captured);
 assert.match(nodeText(card),/truncated/i);
 assert.equal(chip.getAttribute('aria-pressed'),'false');
 const id=card.querySelector('[data-resource-copy="id"]'),data=card.querySelector('[data-resource-copy="data"]');
 chip.dispatch('mouseout',{relatedTarget:id});assert.ok(p.querySelector('.resource-hover'));
 chip.dispatch('mouseout',{relatedTarget:el('div')});card.dispatch('mouseover');
 await new Promise(resolve=>setTimeout(resolve,220));assert.ok(p.querySelector('.resource-hover'));
 chip.dispatch('click');assert.equal(chip.getAttribute('aria-pressed'),'true');
 id.dispatch('click');data.dispatch('click');await new Promise(setImmediate);
 assert.deepEqual(copied,[resource.key,captured]);
 assert.equal(chip.getAttribute('aria-pressed'),'true');
 dispatchDocument('keydown',chip,'Escape');assert.equal(p.querySelector('.resource-hover'),null);
});

test('identity-only resource focus never borrows data from another reference',()=>{
 const other={...resource,id:'other'};
 const current=step('focus',{text:'id',parts:[{text:'id',resourceId:resource.id}]});
 current.effects=[{effect:'Read',resourceId:other.id,data:'unrelated'}];
 const p=scope.buildScenarioDrill({...scenario,resources:[resource,other],steps:[current]});
 const chip=p.querySelector('[data-message-resource]');chip.dispatch('focusin');
 const card=p.querySelector('.resource-hover');assert.ok(card);
 assert.match(nodeText(card),/No captured data/);
 assert.ok(card.querySelector('[data-resource-copy="data"]').disabled);
 dispatchDocument('keydown',chip,'Escape');
 current.effects.push({effect:'Read',resourceId:resource.id,data:'matching snapshot'});
 chip.dispatch('focusin');assert.match(nodeText(p.querySelector('.resource-hover')),/No captured data/);
 dispatchDocument('keydown',chip,'Escape');
 current.effects=[];current.noteMessages=[{parts:[{resourceId:resource.id,data:'captured note snapshot'}]}];
 chip.dispatch('focusin');assert.match(nodeText(p.querySelector('.resource-hover')),/No captured data/);
 dispatchDocument('keydown',chip,'Escape');
});


test('each resource reference displays its own raw snapshot and copies it without pretty-print changes',async()=>{
 const copied=[];scope.navigator={clipboard:{writeText:async value=>copied.push(value)}};
 const named={...resource,type:'Appointment',qualifiedType:'Ns.AppointmentDto'};
 const before='{"$id":"1","state":"before"}',after='{"$id":"1","state":"after"}';
 const make=(id,raw)=>step(id,{text:'Appointment',parts:[{text:'Appointment',resourceId:resource.id,data:raw}]});
 const p=scope.buildScenarioDrill({...scenario,resources:[named],steps:[make('before',before),make('after',after)]});
 const references=p.querySelectorAll('.step-entry').map(entry=>entry.querySelector('[data-message-resource]'));
 references[0].dispatch('focusin');
 let card=p.querySelector('.resource-hover');
 assert.equal(nodeText(card.querySelector('.resource-hover-title')),'Appointment · '+resource.key);
 assert.equal(card.querySelector('.resource-hover-id').parentElement,card.querySelector('.resource-hover-title'));
 assert.equal(card.querySelector('.resource-hover-empty'),null);
 assert.equal(JSON.parse(nodeText(card.querySelector('pre'))).$id,undefined);
 assert.equal(JSON.parse(nodeText(card.querySelector('pre'))).state,'before');
 card.querySelector('[data-resource-copy="data"]').dispatch('click');await new Promise(setImmediate);
 assert.deepEqual(copied,[before]);
 references[1].dispatch('focusin');card=p.querySelector('.resource-hover');
 assert.equal(JSON.parse(nodeText(card.querySelector('pre'))).state,'after');
 dispatchDocument('keydown',references[1],'Escape');
});


test('resource JSON presentation preserves large integers and decimal precision verbatim',()=>{
 const raw=String.raw`{"id":9007199254740993,"amount":0.10000000000000000001,"text":"braces: {} [] \"quoted\"","empty":[]}`;
 assert.doesNotThrow(()=>JSON.parse(raw));
 const p=scope.buildScenarioDrill({...scenario,steps:[step('precise',{text:'value',parts:[{text:'value',resourceId:resource.id,data:raw}]})]});
 const chip=p.querySelector('[data-message-resource]');chip.dispatch('mouseover');
 const display=nodeText(p.querySelector('.resource-hover').querySelector('pre'));
 assert.match(display,/9007199254740993/);assert.match(display,/0\.10000000000000000001/);
 assert.match(display,/braces: \{\} \[\]/);assert.ok(display.includes('\n'));
 dispatchDocument('keydown',chip,'Escape');
});


test('scenario and group headings keep compact identifiers alongside the display names',()=>{
 const current={...scenario,scenarioId:'metadata',methodName:'Very.Long.Namespace.Scenarios.ReadAppointment',classDisplayName:'Appointments API',displayName:'a patient reads',status:'passed',durationMs:1,waitedMs:59,waitedFor:'Schedule',steps:[{...step('timed',message('key')),offsetMs:59,durationMs:3}]};
 const body=el('body'),root=el('html');root.appendChild(body);
 const app=el('div');app.id='report';body.appendChild(app);
 const summary=el('div');summary.id='run-summary';body.appendChild(summary);
 const model=el('script');model.id='model';body.appendChild(model);
 model.textContent=JSON.stringify({summary:{passed:1,failed:0,skipped:0,totalMs:1},generatedAtUtc:'2026-10-13T00:00:00Z',scenarios:[current]});
 const document={fonts:{ready:{then(){}}},body,documentElement:root,createElement:el,createElementNS:(_,tag)=>el(tag),createTextNode:text=>Object.assign(el('#text'),{textContent:text}),getElementById:id=>root.querySelectorAll('[id]').find(n=>n.id===id),querySelectorAll:s=>root.querySelectorAll(s),addEventListener(){}};
 const context=vm.createContext({...browserLayout,document,window:{matchMedia:()=>({matches:true})},location:{search:'?theme=light'},URLSearchParams,getComputedStyle:()=>({getPropertyValue:()=>''}),Blob,CSS:{escape:x=>x}});
 const code=template.slice(template.indexOf('  <script>')+'  <script>'.length,template.lastIndexOf('  </script>'));
 vm.runInContext(code,context);
 const group=app.querySelector('.tests-class-name');
 assert.match(nodeText(group),/Appointments API/);
 assert.equal(nodeText(group.querySelector('.group-type')),'Very.Long.Namespace.Scenarios');
 const head=app.querySelector('.card-head'),method=head.querySelector('.scenario-method');
 assert.equal(nodeText(method),'ReadAppointment');
 assert.equal(method.getAttribute('title'),current.methodName);
 assert.match(nodeText(head),/a patient reads/);
 assert.equal(app.querySelector('.card-body').querySelector('.mth'),null);
 const wait=head.querySelector('.where');assert.ok(wait);assert.equal(wait.parentElement,head);
 assert.equal(nodeText(wait),'Queued for Schedule · 59.0 ms');
 const timing=app.querySelector('.step-head').querySelector('.dm');
 assert.equal(timing.querySelector('.step-offset'),null);
 assert.match(timing.querySelector('.step-gantt').title,/^\+59\.0 ms to \+62\.0 ms/);
 assert.equal(nodeText(timing.querySelector('.step-duration')),'3.0 ms');
});

test('snapshot presentation hides unused serializer IDs without removing real IDs or JSON reference information',()=>{
 for(const raw of ['{"$id":"1","Id":7,"nested":{"$id":"2","Name":"Alice"}}','{"Id":7,"$id":"1","nested":{"Name":"Alice","$id":"2"}}','{"Id":7,"nested":{"$id":"2","Name":"Alice"},"$id":"1"}']){
  const shown=scope.formatResourceSnapshot(raw);assert.deepEqual(JSON.parse(shown),{Id:7,nested:{Name:'Alice'}});
 }
 assert.equal(scope.formatResourceSnapshot('{"$id":"1"}'),'{}');
 assert.equal(JSON.parse(scope.formatResourceSnapshot('{"text":"\\\"$id\\\":\\\"1\\\"","$id":"1"}')).text,'"$id":"1"');
 const cycle='{"$id":"1","self":{"$ref":"1"}}';
 assert.deepEqual(JSON.parse(scope.formatResourceSnapshot(cycle)),JSON.parse(cycle));
});

test('lifecycle rows stay visible without number badges and retain their lifecycle status',()=>{
 const rows=[{...step('setup',{}),label:'0',isLifecycle:true,displayName:'Setup',titleMessage:undefined},{...step('given',{}),label:'1',displayName:'Teardown',titleMessage:undefined},{...step('teardown',{}),label:'2',isLifecycle:true,status:'failed',displayName:'Cleanup',titleMessage:undefined}];
 const p=scope.buildScenarioDrill({...scenario,steps:rows});
 const entries=p.querySelectorAll('.step-entry');assert.equal(entries.length,3);
 assert.equal(entries[0].querySelector('.sx'),null);
 assert.equal(entries[1].querySelector('.sx'),null);
 assert.ok(entries[0].classList.contains('step-lifecycle'));
 assert.equal(entries[1].classList.contains('step-lifecycle'),false);
 assert.ok(entries[2].classList.contains('step-lifecycle'));assert.ok(entries[2].querySelector('.scenario-status.failed'));
});

test('skipped and not-taken step names are struck through without removing resource links',()=>{
 const rows=['skipped','not-taken','passed','failed'].map(status=>({...step(status,message('Named resource')),status}));
 const view=scope.buildScenarioDrill({...scenario,steps:rows});
 const entries=view.querySelectorAll('.step-entry');
 for(let i=0;i<entries.length;i++){
  const name=entries[i].querySelector('.dn');
  assert.equal(entries[i].querySelector('.sx'),null);
  assert.equal(Boolean(name.querySelector('s')),i<2);
  assert.equal(nodeText(name),'Named resource');
  assert.ok(name.querySelector('[data-message-resource]'));
 }
});

test('successful lifecycle rows without evidence have no disclosure control',()=>{
 const current={...step('setup',{}),label:'0',isLifecycle:true,displayName:'Setup',titleMessage:undefined,
  notes:[],noteMessages:[],logs:[],logMessages:[],effects:[]};
 const view=scope.buildScenarioDrill({...scenario,steps:[current]}),entry=view.querySelector('.step-entry');
 assert.equal(entry.querySelector('.step-evidence-toggle,.lifecycle-toggle'),null);
 assert.equal(entry.querySelector('.step-body'),null);
 entry.querySelector('.dn').dispatch('click');
 assert.equal(entry.classList.contains('open'),false);
 assert.equal(entry.querySelector('.sx'),null);
 assert.ok(entry.querySelector('.scenario-status.passed'));
});

test('lifecycle disclosure retains its body and failed lifecycle rows stay fully revealed',()=>{
 const rows=[{...step('teardown',{}),label:'2',isLifecycle:true,displayName:'Teardown',titleMessage:undefined,logs:['Cleanup finished'],logMessages:[]},
  {...step('failed',{}),label:'0',isLifecycle:true,status:'failed',displayName:'Setup',titleMessage:undefined,exception:'Startup failed'}];
 const view=scope.buildScenarioDrill({...scenario,steps:rows}),entries=view.querySelectorAll('.step-entry');
 entries[0].querySelector('.step-logs-toggle').dispatch('click');
 assert.equal(entries[0].classList.contains('open'),true);
 assert.match(nodeText(entries[0].querySelector('.step-body')),/Cleanup finished/);
 assert.equal(entries[1].classList.contains('open'),true);
 assert.ok(entries[1].querySelector('.scenario-status.failed'));
 assert.equal(entries[1].querySelector('.lifecycle-toggle'),null);
 assert.match(nodeText(entries[1].querySelector('.step-body')),/Startup failed/);
});

test('ordinary steps named Setup do not acquire compact lifecycle controls',()=>{
 const current={...step('ordinary',{}),displayName:'Setup',titleMessage:undefined,notes:[],noteMessages:[],logs:[],logMessages:[],effects:[]};
 const entry=scope.buildScenarioDrill({...scenario,steps:[current]}).querySelector('.step-entry');
 assert.equal(entry.classList.contains('step-lifecycle'),false);
 assert.equal(entry.querySelector('.lifecycle-toggle'),null);
});


test('evidence glyphs independently show logs and attachments without title disclosure',()=>{
 const current={...step('evidence',message('patient')),attachments:[{name:'response.txt',value:'Captured response'}]};
 const view=scope.buildScenarioDrill({...scenario,steps:[current]}),entry=view.querySelector('.step-entry'),head=entry.querySelector('.step-head');
 const logs=head.querySelector('.step-logs-toggle'),attachments=head.querySelector('.step-attachments-toggle');
 assert.ok(logs);assert.ok(attachments);assert.equal(nodeText(logs),'');assert.equal(nodeText(attachments),'');
 assert.equal(head.querySelector('.step-expander,.lifecycle-toggle'),null);assert.ok(head.children[0].classList.contains('stw'));
 head.querySelector('.dn').dispatch('click');assert.equal(entry.classList.contains('open'),false,'Titles do not disclose evidence');
 logs.dispatch('click');assert.equal(entry.classList.contains('open'),true);assert.equal(logs.getAttribute('aria-expanded'),'true');assert.equal(attachments.getAttribute('aria-expanded'),'false');
 assert.equal(entry.querySelector('[data-evidence-panel="logs"]').hidden,false);assert.equal(entry.querySelector('[data-evidence-panel="attachments"]').hidden,true);
 attachments.dispatch('click');assert.equal(entry.classList.contains('open'),true);assert.equal(logs.getAttribute('aria-expanded'),'false');assert.equal(attachments.getAttribute('aria-expanded'),'true');
 assert.equal(entry.querySelector('[data-evidence-panel="logs"]').hidden,true);assert.equal(entry.querySelector('[data-evidence-panel="attachments"]').hidden,false);
 attachments.dispatch('click');assert.equal(entry.classList.contains('open'),false);assert.equal(attachments.getAttribute('aria-expanded'),'false');
});

test('evidence glyphs only exist for available categories and errors open as log details',()=>{
 const empty={...step('empty',{}),notes:[],noteMessages:[],logs:[],logMessages:[],effects:[]};
 const onlyAttachment={...empty,stepId:'attachment',attachments:[{name:'result.txt',value:'Result'}]};
 const failed={...empty,stepId:'failed',status:'failed',exception:'Failure details'};
 const view=scope.buildScenarioDrill({...scenario,steps:[empty,onlyAttachment,failed]}),entries=view.querySelectorAll('.step-entry');
 assert.equal(entries[0].querySelector('.step-evidence-toggle'),null);
 assert.equal(entries[1].querySelector('.step-logs-toggle'),null);assert.ok(entries[1].querySelector('.step-attachments-toggle'));
 assert.equal(entries[2].classList.contains('open'),true);assert.equal(entries[2].querySelector('.step-logs-toggle').getAttribute('aria-expanded'),'true');
 assert.match(nodeText(entries[2].querySelector('.step-body')),/Failure details/);
});
