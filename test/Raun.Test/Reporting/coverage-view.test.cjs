const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const template = fs.readFileSync(path.join(__dirname, '../../../src/Raun/Reporting/Html/report-template.html'), 'utf8');
const start = template.indexOf('function stepNotesHtml');
const end = template.indexOf('function renderAll', start);
class ViewStyle {
  setProperty(name,value){(this.properties ??= {})[name]=String(value);}
  getPropertyValue(name){return this.properties?.[name] || "";}
}
class ViewNode {
  constructor(tag){this.tag=tag;this.children=[];this.attributes={};this.style=new ViewStyle();this.events={};this.dataset={};}
  get classList(){const node=this;return {add(value){node.className=((node.className||'')+' '+value).trim();},remove(value){node.className=(node.className||'').split(' ').filter(x=>x!==value).join(' ');},contains(value){return (node.className||'').split(' ').includes(value);}};}
  appendChild(node){this.children.push(node);return node;}
  replaceChildren(...nodes){this.children=nodes;}
  setAttribute(name,value){this.attributes[name]=String(value);}
  getAttribute(name){return this.attributes[name] ?? null;}
  addEventListener(name,handler){this.events[name]=handler;}
  focus(){}
  querySelectorAll(selector){return descendants(this).filter(node=>node.tag===selector);}
}
function descendants(node){return node.children.flatMap(child=>[child,...descendants(child)]);}
const document={createElement:tag=>new ViewNode(tag),createTextNode:text=>Object.assign(new ViewNode('#text'),{textContent:text})};
const el=(tag,cls)=>Object.assign(new ViewNode(tag),{className:cls || ''});
const svgEl=(tag,attrs)=>{const node=new ViewNode(tag);for (const [key,value] of Object.entries(attrs || {})) node.setAttribute(key,value);return node;};
const context = vm.createContext({document,el,svgEl,esc:value=>String(value).replaceAll("&","&amp;").replaceAll("<","&lt;").replaceAll(">","&gt;").replaceAll('"',"&quot;"),fmtDur:ms=>(ms/1000).toFixed(2)+" s",fmtGen:iso=>iso.replace("T"," ").replace("Z"," UTC")});
vm.runInContext(template.slice(template.indexOf('const shortName ='),template.indexOf('const collapsedGroups =')),context);
vm.runInContext(template.slice(start, end) + '\nthis.api = {stepNotesHtml,coverageRank, coverageCompare, coverageSegments, coverageLabels, buildCoverageReport, buildCoverageSource, buildReportViews:typeof buildReportViews === "function" ? buildReportViews : undefined, buildScenarioOutcomes:typeof buildScenarioOutcomes === "function" ? buildScenarioOutcomes : undefined, buildTestsReport:typeof buildTestsReport === "function" ? buildTestsReport : undefined, buildRunHeader:typeof buildRunHeader === "function" ? buildRunHeader : undefined, coverageHierarchy:typeof coverageHierarchy === "function" ? coverageHierarchy : undefined};', context);
const {coverageRank, coverageCompare, coverageSegments, coverageLabels} = context.api;
const item = (name, counts, category=4) => ({name, categories:[{category, counts}]});

test('logic ranking uses complete observable decisions, not expression hits or argument checks', () => {
  const row = item('Finance', {inventory:100, complete:1, decisionMeasurable:10, reached:80, unknown:10});
  row.categories.push({category:0, counts:{inventory:20, exercised:20, exerciseMeasurable:20}});
  assert.equal(coverageRank(row,4).fraction,0.1);
  assert.equal(coverageRank(row,0).fraction,1);
});
test('worst comes first and fully unknown rows are not scored as perfect or missed', () => {
  const rows=[item('Best',{inventory:10,complete:10,decisionMeasurable:10}),
    item('Unknown',{inventory:10,unknown:10}), item('Worst',{inventory:10,complete:0,decisionMeasurable:10})];
  rows.sort((a,b)=>coverageCompare(a,b,4));
  assert.deepEqual(rows.map(row=>row.name),['Worst','Best','Unknown']);
  assert.equal(coverageRank(rows[2],4).fraction,null);
});
test('coarse expression reach is a separate ranking family', () => {
  const row=item('Expression',{inventory:5,reached:1,partial:3,expressionNotReached:1});
  assert.equal(coverageRank(row,4).family,'expressions');
  assert.equal(coverageRank(row,4).fraction,0.2);
});
test('type totals use integer denominators, never a mean of method percentages', () => {
  assert.equal(coverageRank(item('Type',{inventory:10,complete:1,decisionMeasurable:10}),4).fraction,0.1);
});
test('unknown and unmeasurable evidence stay distinct in the visual profile', () => {
  const segments=coverageSegments({inventory:12,complete:1,incomplete:2,notReached:3,reached:1,partial:1,expressionNotReached:1,evaluated:1,unknown:2,unmeasurable:1});
  assert.equal(segments.reduce((sum,segment)=>sum+segment.count,0),12);
  assert.equal(segments.find(segment=>segment.key==='unknown').count,2);
  assert.equal(segments.find(segment=>segment.key==='helper').count,1);
  assert.equal(segments.find(segment=>segment.key==='missed').count,2);
});
test('equal coverage prioritizes more known incomplete decisions then sorts names deterministically', () => {
  const a=item('Small',{inventory:2,complete:1,decisionMeasurable:2});
  const b=item('Large',{inventory:20,complete:10,decisionMeasurable:20});
  assert.ok(coverageCompare(b,a,4)<0);
});

test('unevaluated filters stay visible and out of exact decision tracks', () => {
  const segments=coverageSegments({inventory:3,complete:1,decisionMeasurable:1,evaluated:1,notReached:1,filterNotReached:1});
  assert.equal(segments.reduce((sum,segment)=>sum+segment.count,0),3);
  assert.equal(segments.some(segment=>segment.key==='missed'),false);
  assert.equal(segments.find(segment=>segment.key==='filter-missed').family,'filters');
  const rank=coverageRank(item('Filter',{inventory:2,evaluated:1,notReached:1,filterNotReached:1}),4);
  assert.equal(rank.family,'filters');
  assert.equal(rank.fraction,0.5);
});
test('a filter with no evaluations ranks as zero evaluated rather than unverified', () => {
  const row=item('Filter',{inventory:1,notReached:1,filterNotReached:1});
  assert.equal(coverageRank(row,4).family,'filters');
  assert.equal(coverageRank(row,4).fraction,0);
});

test('coverage labels explain what is counted and distinguish missing evidence from missed execution', () => {
  const labels=coverageLabels({inventory:68,exerciseMeasurable:45,exercised:0,reached:1,partial:1,expressionNotReached:1,unknown:20},1);
  assert.ok(labels.some(label=>label.text==='0 of 45 error paths tested'));
  assert.ok(labels.some(label=>label.text==='Coverage undetermined for 20 branches'));
  assert.ok(labels.some(label=>label.text==='1 expression not reached'));
  assert.ok(labels.some(label=>label.text==='1 expression partly reached'));
});
test('argument check and helper labels name checks rather than generic branches', () => {
  const labels=coverageLabels({inventory:3,exerciseMeasurable:1,exercised:0,unmeasurable:2},0);
  assert.ok(labels.some(label=>label.text==='0 of 1 argument checks triggered'));
  assert.ok(labels.some(label=>label.text==='2 library checks: coverage unavailable'));
});

test('partly evaluated filters remain separate from expression partial execution', () => {
  const counts={inventory:3,evaluated:1,filterPartial:1,notReached:1,filterNotReached:1};
  const segments=coverageSegments(counts);
  assert.equal(segments.reduce((sum,segment)=>sum+segment.count,0),3);
  assert.equal(segments.find(segment=>segment.key==='filter-partial').family,'filters');
  assert.equal(segments.some(segment=>segment.family==='coarse'),false);
  assert.equal(coverageRank(item('Filter',counts),4).fraction,1/3);
  assert.ok(coverageLabels(counts,1).some(label=>label.text==='1 exception filter partly evaluated'));
});

const logic=counts=>[{category:4,counts}];
const treeView=()=>context.api.buildCoverageReport({status:'available',categories:logic({inventory:9,decisionMeasurable:6,complete:2,incomplete:1,notReached:5}),types:[
  {assembly:'Zeta',namespace:'Z',name:'Z.Billing',categories:[{category:0,counts:{inventory:3,exerciseMeasurable:3,exercised:1}},...logic({inventory:7,decisionMeasurable:4,incomplete:1,notReached:5})],methods:[
    {signature:'Z.Billing.Refund()',categories:logic({inventory:3,decisionMeasurable:1,incomplete:1,notReached:1})},
    {signature:'Z.Billing.Charge()',categories:logic({inventory:4,decisionMeasurable:3,notReached:4})}]},
  {assembly:'Alpha',namespace:'A',name:'A.Worker',categories:logic({inventory:2,decisionMeasurable:2,complete:2}),methods:[
    {signature:'A.Worker.Run(int)',categories:logic({inventory:2,decisionMeasurable:2,complete:2}),sources:[{path:'src/Worker.cs',startLine:3,text:'if (x) Run();',tokens:[],branches:[]}]}]}
]});
const treeRows=view=>descendants(view).filter(n=>n.attributes.role==='row' && n.className.split(' ').includes('coverage-row'));
const rowName=row=>descendants(row).find(n=>n.className==='coverage-row-name').textContent;
const rowNamed=(view,name)=>treeRows(view).find(row=>rowName(row)===name);
const expand=(view,...names)=>{for (const name of names) descendants(rowNamed(view,name)).find(n=>n.className==='coverage-toggle').events.click();};
const detailName=view=>descendants(view).find(n=>n.className==='coverage-preview-name').textContent;
const key=(row,name)=>row.events.keydown({key:name,preventDefault(){}});
const selectedName=view=>rowName(treeRows(view).find(row=>row.attributes['aria-selected']==='true'));

test('coverage opens on the whole run as a tree with assemblies in name order and no diagram', () => {
  const view=treeView(), rows=treeRows(view);
  assert.deepEqual(rows.map(rowName),['Whole run','Alpha','Zeta']);
  assert.equal(rows[0].attributes['aria-level'],'1');assert.equal(rows[0].attributes['aria-expanded'],'true');
  assert.equal(rows[1].attributes['aria-level'],'2');assert.equal(rows[1].attributes['aria-expanded'],'false');
  assert.equal(rows[0].attributes['aria-selected'],'true');assert.equal(rows[0].attributes.tabindex,'0');assert.equal(rows[1].attributes.tabindex,'-1');
  assert.ok(descendants(view).some(n=>n.attributes.role==='treegrid'));
  assert.equal(descendants(view).some(n=>n.tag==='svg' || n.className==='coverage-child' || n.className==='coverage-breadcrumbs'),false);
  assert.equal(detailName(view),'Whole run');
});

test('tree rows show fully tested logic decisions, unreached branches and partly tested decisions', () => {
  const view=treeView(), zeta=rowNamed(view,'Zeta');
  const text=descendants(zeta).map(n=>n.textContent || '');
  assert.ok(text.includes('0/4'));assert.ok(text.includes('5'));assert.ok(text.includes('1'));
  const bar=descendants(zeta).find(n=>n.className==='coverage-bar');
  assert.equal(bar.attributes.role,'img');assert.match(bar.attributes['aria-label'],/1 partly tested/);assert.match(bar.attributes['aria-label'],/3 untested/);
  assert.match(zeta.title,/Argument checks: 1 of 3 argument checks triggered/);
  assert.ok(descendants(rowNamed(view,'Alpha')).some(n=>n.textContent==='2/2'));
  const headers=descendants(view).filter(n=>n.attributes.role==='columnheader').map(n=>descendants(n).find(c=>c.tag==='button').textContent);
  assert.deepEqual(headers,['Scope','Logic decisions','Not reached','Partly tested']);
});

test('expanding rows reveals the hierarchy and collapsing hides it again', () => {
  const view=treeView();
  expand(view,'Zeta','Z','Billing');
  assert.deepEqual(treeRows(view).map(rowName),['Whole run','Alpha','Zeta','Z','Billing','Charge()','Refund()']);
  assert.deepEqual(treeRows(view).map(row=>row.attributes['aria-level']),['1','2','2','3','4','5','5']);
  assert.equal(rowNamed(view,'Charge()').attributes['aria-expanded'],undefined);
  assert.equal(rowNamed(view,'Charge()').title.split('\n')[0],'Z.Billing.Charge()');
  expand(view,'Zeta');
  assert.deepEqual(treeRows(view).map(rowName),['Whole run','Alpha','Zeta']);
  expand(view,'Zeta');
  assert.deepEqual(treeRows(view).map(rowName),['Whole run','Alpha','Zeta','Z','Billing','Charge()','Refund()']);
  assert.equal(selectedName(view),'Whole run');
});

test('a selected scope shows its per-kind summary and a selected method shows its source', () => {
  const view=treeView();
  rowNamed(view,'Zeta').events.click();
  assert.equal(detailName(view),'Zeta');
  const nodes=()=>descendants(view);
  assert.ok(nodes().some(n=>n.className==='coverage-scope-score' && n.textContent==='0 of 4 logic decisions fully tested'));
  assert.deepEqual(nodes().filter(n=>n.className==='coverage-kind-name').map(n=>n.textContent),['Argument checks · 3 branches','Logic · 7 branches']);
  assert.equal(nodes().some(n=>n.className==='coverage-source'),false);
  expand(view,'Alpha','A','Worker');rowNamed(view,'Run(…)').events.click();
  assert.equal(detailName(view),'A.Worker.Run(…)');
  assert.ok(nodes().some(n=>n.className==='coverage-source'));
  assert.ok(nodes().some(n=>n.className==='coverage-syntax-plain' && n.textContent.includes('Run();')));
  assert.equal(nodes().some(n=>n.className==='coverage-kind-name'),false);
  assert.equal(rowNamed(view,'Run(…)').attributes['aria-selected'],'true');
});

test('arrow keys move the selection and expand or collapse rows', () => {
  const view=treeView();
  key(treeRows(view)[0],'ArrowDown');assert.equal(selectedName(view),'Alpha');
  key(rowNamed(view,'Alpha'),'ArrowRight');assert.equal(rowNamed(view,'Alpha').attributes['aria-expanded'],'true');assert.equal(selectedName(view),'Alpha');
  key(rowNamed(view,'Alpha'),'ArrowRight');assert.equal(selectedName(view),'A');assert.equal(detailName(view),'A');
  key(rowNamed(view,'A'),'ArrowLeft');assert.equal(selectedName(view),'Alpha');
  key(rowNamed(view,'Alpha'),'ArrowLeft');assert.equal(rowNamed(view,'Alpha').attributes['aria-expanded'],'false');
  key(rowNamed(view,'Alpha'),'End');assert.equal(selectedName(view),'Zeta');
  key(rowNamed(view,'Zeta'),'Enter');assert.equal(rowNamed(view,'Zeta').attributes['aria-expanded'],'true');
  key(rowNamed(view,'Zeta'),'Home');assert.equal(selectedName(view),'Whole run');
  assert.equal(treeRows(view).filter(row=>row.attributes.tabindex==='0').length,1);
});

test('column headers sort each level and a second click reverses the order', () => {
  const view=treeView(), header=label=>descendants(view).find(n=>n.attributes.role==='columnheader' && descendants(n).some(c=>c.tag==='button' && c.textContent===label));
  const sortButton=label=>descendants(header(label)).find(n=>n.tag==='button');
  assert.equal(header('Scope').attributes['aria-sort'],'ascending');
  expand(view,'Zeta','Z','Billing');
  sortButton('Not reached').events.click();
  assert.deepEqual(treeRows(view).map(rowName),['Whole run','Zeta','Z','Billing','Charge()','Refund()','Alpha']);
  assert.equal(header('Not reached').attributes['aria-sort'],'descending');assert.equal(header('Scope').attributes['aria-sort'],'none');
  sortButton('Not reached').events.click();
  assert.deepEqual(treeRows(view).map(rowName),['Whole run','Alpha','Zeta','Z','Billing','Refund()','Charge()']);
  assert.equal(header('Not reached').attributes['aria-sort'],'ascending');
  sortButton('Logic decisions').events.click();
  assert.deepEqual(treeRows(view).map(rowName).slice(1,3),['Zeta','Z']);
  sortButton('Scope').events.click();
  assert.deepEqual(treeRows(view).map(rowName).slice(0,3),['Whole run','Alpha','Zeta']);
});

test('the tree width splitter writes the grid property and survives tree changes', () => {
  const view=treeView(), nodes=()=>descendants(view);
  assert.equal(nodes().filter(n=>n.attributes.role==='separator').length,1);
  const splitter=nodes().find(n=>n.attributes['aria-label']==='Tree width');
  const start=Number(splitter.attributes['aria-valuenow']);
  splitter.events.keydown({key:'ArrowRight',preventDefault(){}});
  assert.equal(splitter.attributes['aria-valuenow'],String(start+2));
  assert.equal(nodes().find(n=>n.className==='coverage-findings').style.getPropertyValue('--coverage-list-size'),(start+2)+'%');
  expand(view,'Zeta');rowNamed(view,'Zeta').events.click();
  assert.equal(nodes().find(n=>n.attributes['aria-label']==='Tree width'),splitter);
  assert.equal(splitter.attributes['aria-valuenow'],String(start+2));
});

test('method source is rendered as safe text with line numbers and branch marks', () => {
  const categories=[{category:4,counts:{inventory:1,notReached:1}}];
  const sources=[{path:'Finance.cs',startLine:12,notExecutedLines:[12],text:'if (flag) return "<script>";',tokens:[{start:0,length:2,kind:'keyword'},{start:17,length:8,kind:'string'}],branches:[{start:0,length:25,category:4,outcome:'missed'}]}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'N',name:'N.Finance',categories,methods:[{signature:'N.Finance.Calculate()',categories,sources}]}]});
  expand(view,'Api','N','Finance');rowNamed(view,'Calculate()').events.click();
  const nodes=descendants(view);
  assert.ok(nodes.some(n=>n.className==='coverage-source'));
  assert.ok(nodes.some(n=>n.className?.includes('coverage-source-missed')));
  assert.ok(nodes.some(n=>n.textContent==='12'));
  assert.ok(nodes.some(n=>n.className==='coverage-syntax-keyword' && n.textContent==='if'));
  assert.ok(nodes.some(n=>n.className?.includes('coverage-source-match')));
  assert.equal(nodes.some(n=>n.tag==='script'),false);
});

test('snippet choices switch one source pane and the choice survives reselecting the method', () => {
  const cats=n=>[{category:4,counts:{inventory:n,notReached:n}}];
  const source=(text,line)=>({path:'C.cs',startLine:line,text,tokens:[],branches:[{start:0,length:text.length,category:4,outcome:'missed'}]});
  const view=context.api.buildCoverageReport({status:'available',categories:cats(3),types:[
    {assembly:'Api',namespace:'N',name:'N.C',categories:cats(3),methods:[
      {signature:'N.C.Small()',categories:cats(1),sources:[source('if (small) Skip();',10)]},
      {signature:'N.C.Large()',categories:cats(2),sources:[source('if (large) Skip();',20),source('if (other) Skip();',30)]}
    ]}
  ]});
  const nodes=()=>descendants(view);
  const text=()=>nodes().filter(n=>n.className?.startsWith('coverage-syntax-')).map(n=>n.textContent).join('');
  expand(view,'Api','N','C');rowNamed(view,'Large()').events.click();
  assert.equal(nodes().filter(n=>n.className==='coverage-source-code').length,1);
  assert.match(text(),/large/);assert.doesNotMatch(text(),/other|small/);
  nodes().find(n=>n.className==='coverage-snippet-choice' && n.textContent.includes(':30')).events.click();
  assert.match(text(),/other/);assert.doesNotMatch(text(),/large/);
  rowNamed(view,'Small()').events.click();
  assert.match(text(),/small/);assert.doesNotMatch(text(),/large|other/);
  assert.equal(nodes().filter(n=>n.className==='coverage-source-code').length,1);
});

test('drill-down groups semantic namespaces and preserves nested type identity', () => {
  const tree=context.api.coverageHierarchy([
    {assembly:'Api',namespace:'Business',name:'Business.Outer.Inner',categories:[{category:4,counts:{inventory:2,complete:1,decisionMeasurable:2}}],methods:[{signature:'Business.Outer.Inner.Calculate()',categories:[{category:4,counts:{inventory:2,complete:1,decisionMeasurable:2}}]}]},
    {assembly:'Api',namespace:'Business',name:'Business.Finance',categories:[{category:4,counts:{inventory:8,decisionMeasurable:8,notReached:8}}],methods:[]},
    {assembly:'Other',namespace:'',name:'Program',categories:[{category:0,counts:{inventory:1,unmeasurable:1}}],methods:[]}
  ]);
  assert.equal(tree.children.length,2);
  const assembly=tree.children.find(node=>node.name==='Api');
  assert.equal(assembly.children.length,1);
  assert.equal(assembly.children[0].name,'Business');
  assert.equal(assembly.children[0].children[0].name,'Outer.Inner');
  assert.equal(coverageRank(assembly,4).fraction,0.1);
  assert.equal(assembly.categories[0].counts.inventory,10);
  const method=assembly.children[0].children[0].children[0];
  assert.equal(method.level,'method');
  assert.equal(method.categories[0].counts.complete,1);
  assert.equal(tree.children[1].children[0].name,'(global namespace)');
});

test('missing namespace metadata is explicit and never inferred from nested type names', () => {
  const tree=context.api.coverageHierarchy([{assembly:'Api',name:'Business.Outer.Inner',categories:[],methods:[]}]);
  assert.equal(tree.children[0].children[0].name,'(namespace unavailable)');
});
test('execution ranges distinguish statements on one line and preserve uncovered context', () => {
  const source={path:'C.cs',startLine:1,text:'A(); B(); // note',tokens:[{start:10,length:7,kind:'comment'}],branches:[],execution:[{start:0,length:4,state:'executed'},{start:5,length:4,state:'missed'}]};
  const view=context.api.buildCoverageSource([source],null), nodes=descendants(view);
  assert.ok(nodes.some(n=>n.className==='coverage-execution-executed' && n.title==='Executed'));
  assert.ok(nodes.some(n=>n.className==='coverage-execution-missed' && n.title==='Not executed'));
  assert.ok(nodes.some(n=>n.className==='coverage-syntax-comment' && n.textContent==='// note'));
});


test('path counts show frequencies without guessing semantic arm labels', () => {
  const view=context.api.buildCoverageSource([{path:'C.cs',startLine:1,text:'if (flag) A();',tokens:[],branches:[{start:0,length:14,category:4,outcome:'partial',pathHits:[2,3917]}]}],null);
  const labels=descendants(view).filter(n=>n.className==='coverage-path-hits').map(n=>n.textContent).join('');
  assert.match(labels,/2\/3,919/);assert.match(labels,/0\.05%/);assert.match(labels,/3,917\/3,919/);
  assert.doesNotMatch(labels,/true|false|then|else/);
});

test('specific arm execution overrides enclosing mixed expression evidence', () => {
  const source={path:'C.cs',startLine:1,text:'b ? Yes() : No()',tokens:[],branches:[],execution:[
    {start:0,length:16,state:'partial'}, {start:0,length:1,state:'executed'},
    {start:4,length:5,state:'missed'}, {start:12,length:4,state:'executed'}]};
  const nodes=descendants(context.api.buildCoverageSource([source],null));
  const marks=nodes.filter(n=>n.className?.startsWith('coverage-execution-'));
  assert.ok(marks.some(n=>n.className==='coverage-execution-executed' && n.children[0].textContent==='b'));
  assert.ok(marks.some(n=>n.className==='coverage-execution-missed' && n.children[0].textContent==='Yes()'));
  assert.ok(marks.some(n=>n.className==='coverage-execution-executed' && n.children[0].textContent==='No()'));
  assert.ok(marks.some(n=>n.className==='coverage-execution-partial'));
});

test('compact run header keeps summary and measured logic score inline', () => {
  const view=context.api.buildRunHeader({summary:{passed:4,failed:1,skipped:2,totalMs:3210},generatedAtUtc:'2026-10-11T11:36:52Z',coverage:{status:'available',categories:[
    {category:0,counts:{complete:99,decisionMeasurable:99}},
    {category:4,counts:{complete:1,decisionMeasurable:3,unknown:2,reached:40}}]}});
  const nodes=descendants(view);
  const text=nodes.map(n=>n.textContent || '').join(' ');
  assert.match(text,/Passed.*4.*Failed.*1.*Skipped.*2.*Logical.*33%.*Wall clock.*3.21 s.*2026-10-11 11:36:52/);
  assert.ok(nodes.some(n=>n.className==='summary-score' && n.title.includes('2 unresolved')));
  assert.equal(nodes.filter(n=>n.className==='summary-score').length,1);
  assert.ok(nodes.find(n=>n.className==='summary-score').title.includes('1 of 3'));
});

test('header does not invent a logic score without measurable decisions', () => {
  for (const coverage of [undefined,{status:'pending',branchPaths:{covered:5,total:5},categories:[{category:4,counts:{complete:5,decisionMeasurable:5}}]},{status:'available',categories:[{category:4,counts:{reached:5}}]}]) {
    const nodes=descendants(context.api.buildRunHeader({summary:{passed:0,failed:0,skipped:0,totalMs:0},generatedAtUtc:'2026-10-11T11:36:52Z',coverage}));
    assert.equal(nodes.some(n=>/NaN|Infinity|100%|0%/.test(n.textContent || '')),false);
  }
});


test('run header exposes pass or fail state for the logo', () => {
  for (const failed of [0,1]) {
    const view=context.api.buildRunHeader({summary:{passed:4,failed,skipped:0,totalMs:10},generatedAtUtc:'2026-10-11T11:36:52Z'});
    assert.equal(view.attributes['data-run-status'],failed ? 'fail' : 'pass');
  }
});

test('coverage diagnostics stay in a collapsed disclosure without a global counts table', () => {
  const categories=[{category:4,counts:{inventory:1,complete:1,decisionMeasurable:1}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[],diagnostics:['sample diagnostic']});
  const nodes=descendants(view);
  assert.equal(nodes.some(n=>n.className==='coverage-counts' || n.className==='coverage-diagnostics' || n.tag==='table'),false);
  const details=nodes.find(n=>n.tag==='details'&&n.className==='coverage-collection');
  assert.ok(details);assert.equal(details.attributes.open,undefined);
  assert.ok(descendants(details).some(n=>n.textContent==='sample diagnostic'));
  assert.ok(nodes.some(n=>n.className==='coverage-explorer'));
});


test('report tabs build each view once and preserve its existing DOM when switching', () => {
  const tabs=el('nav'), changes=[], builds={tests:0,coverage:0};
  const tests=el('div'), coverage=el('div');tests.scrollTop=123;
  const app=context.api.buildReportViews([
    {key:'tests',label:'Tests',build:()=>{builds.tests++;return tests;}},
    {key:'coverage',label:'Coverage',build:()=>{builds.coverage++;return coverage;}}
  ],tabs,key=>changes.push(key),'coverage');
  const buttons=descendants(tabs).filter(n=>n.tag==='button');
  assert.equal(builds.tests,0);assert.equal(builds.coverage,1);
  buttons[0].events.click();buttons[1].events.click();buttons[0].events.click();
  assert.deepEqual(builds,{tests:1,coverage:1});assert.equal(tests.scrollTop,123);
  assert.equal(descendants(app).find(n=>n.id==='report-view-tests').hidden,false);
  assert.equal(descendants(app).find(n=>n.id==='report-view-coverage').hidden,true);
  assert.equal(buttons[0].attributes['aria-selected'],'true');
  assert.equal(changes.at(-1),'tests');
});

test('tests render scenarios as a narrative in registration order without a sidebar',()=>{
 const rendered=[];
 const scenarios=[{scenarioId:'pass',methodName:'N.C.Pass',displayName:'books',status:'passed',durationMs:1,steps:[]},{scenarioId:'fail',methodName:'N.C.Fail',displayName:'rejects',status:'failed',durationMs:1,steps:[]}];
 const app=context.api.buildTestsReport({scenarios},sc=>{rendered.push(sc.scenarioId);return el('section','card');});
 assert.deepEqual(rendered,['pass','fail']);
 assert.equal(descendants(app).some(n=>n.className?.includes('tests-sidebar')),false);
});

test('tests-only reports have an operable Tests tab and empty-run state', () => {
  const tabs=el('nav');
  const app=context.api.buildReportViews([{key:'tests',label:'Tests',build:()=>context.api.buildTestsReport({scenarios:[]},()=>{throw new Error('empty run');})}],tabs,()=>{},'tests');
  assert.equal(descendants(tabs).filter(n=>n.tag==='button').length,1);
  assert.ok(descendants(app).some(n=>n.textContent==='No scenarios in this run.'));
});


test('outcome chart exposes counts and filters by clicking a status', () => {
  const selected=[];
  const chart=context.api.buildScenarioOutcomes([{status:'passed'},{status:'passed'},{status:'failed'},{status:'skipped'}],'all',key=>selected.push(key));
  const nodes=descendants(chart);
  assert.ok(nodes.some(n=>n.tag==='svg'));
  const failed=nodes.find(n=>n.tag==='button' && n.attributes['aria-label']==='Failed: 1 scenario');
  failed.events.click();assert.deepEqual(selected,['failed']);
  assert.ok(nodes.some(n=>n.attributes['aria-label']==='Passed: 2 scenarios'));
  const all=nodes.find(n=>n.tag==='button' && n.attributes['aria-label']==='All: 4 scenarios');
  all.events.click();assert.deepEqual(selected,['failed','all']);
});


test('header summary buttons contain separate logic and collector totals and control the views', () => {
  const report={summary:{passed:4,failed:1,skipped:2,totalMs:3000},generatedAtUtc:'2026-10-11T11:36:52Z',coverage:{status:'available',branchPaths:{covered:9,total:16},categories:[{category:4,counts:{complete:1,decisionMeasurable:3}}]}};
  const header=context.api.buildRunHeader(report), nodes=descendants(header);
  const tests=nodes.find(n=>n.tag==='button' && n.dataset.view==='tests'), coverage=nodes.find(n=>n.tag==='button' && n.dataset.view==='coverage');
  assert.ok(tests);assert.ok(coverage);
  assert.match(descendants(tests).map(n=>n.textContent || '').join(' '),/Scenarios.*Passed.*4.*Failed.*1.*Skipped.*2/);
  assert.match(descendants(coverage).map(n=>n.textContent || '').join(' '),/Coverage.*Logical.*33%.*Total.*56%/);
  const changes=[];
  const tabHost=nodes.find(n=>n.id==='report-tabs');
  context.api.buildReportViews([{key:'tests',label:'Tests',build:()=>el('div')},{key:'coverage',label:'Coverage',build:()=>el('div')}],tabHost,key=>changes.push(key),'tests');
  coverage.events.click();assert.equal(changes.at(-1),'coverage');
  tests.events.click();assert.equal(changes.at(-1),'tests');
  assert.equal(descendants(tabHost).filter(n=>n.tag==='button').length,2);
  assert.equal(descendants(header).filter(n=>n.attributes['data-generated']==='true').length,1);
});


test('step notes are visible text independent of collapsed details, with markup escaped', () => {
  assert.equal(context.api.stepNotesHtml(undefined), '');
  assert.equal(context.api.stepNotesHtml([]), '');
  const html=context.api.stepNotesHtml(['Why <script>alert("x")</script>', 'Seed 042\nUse a known value']);
  assert.ok(html.includes('Why &lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;'));
  assert.ok(html.includes('Seed 042\nUse a known value'));
  assert.equal(html.includes('step-body'),false);
  assert.equal(html.includes('<script>'),false);
  assert.equal((html.match(/class="step-note"/g) || []).length,2);
});


test('Generated is one accessible UTC/local toggle that retains one timestamp',()=>{
 const view=context.api.buildRunHeader({summary:{passed:1,failed:0,skipped:0,totalMs:8420},generatedAtUtc:'2026-10-13T12:32:08Z'});
 const button=descendants(view).find(n=>n.tag==='button'&&n.attributes['data-generated']==='true');
 assert.ok(button,'Generated is an operable header box');
 assert.equal(button.attributes['aria-pressed'],'false');
 const time=descendants(button).find(n=>n.tag==='time');assert.ok(time);const utc=time.textContent;
 button.events.click();assert.equal(button.attributes['aria-pressed'],'true');
 assert.equal(descendants(button).filter(n=>n.tag==='time').length,1);
 button.events.click();assert.equal(button.attributes['aria-pressed'],'false');
 assert.equal(descendants(button).find(n=>n.tag==='time').textContent,utc);
});


test('interleaved scenario classes preserve canonical registration order',()=>{
 const rendered=[];
 const scenarios=[{scenarioId:'first',methodName:'N.A.First',displayName:'First',status:'passed',durationMs:1,steps:[]},{scenarioId:'middle',methodName:'N.B.Middle',displayName:'Middle',status:'passed',durationMs:1,steps:[]},{scenarioId:'last',methodName:'N.A.Last',displayName:'Last',status:'passed',durationMs:1,steps:[]}];
 context.api.buildTestsReport({scenarios},sc=>{rendered.push(sc.scenarioId);return el('section','card');});
 assert.deepEqual(rendered,['first','middle','last']);
});

test('the referenced statement is highlighted precisely within a source line',()=>{
 const source={path:'C.cs',startLine:234,text:'A(); B(); C();',tokens:[],execution:[],
  branches:[{start:5,length:4,category:4,outcome:'missed'}]};
 const view=context.api.buildCoverageSource([source],{from:'kind:4',key:'missed'});
 const nodes=descendants(view), marked=nodes.filter(n=>n.className==='coverage-branch-match');
 assert.equal(marked.map(n=>descendants(n).filter(child=>child.className?.startsWith('coverage-syntax-')).map(child=>child.textContent).join('')).join(''),'B();');
 const target=nodes.find(n=>n.attributes['data-coverage-focus']==='true');
 assert.equal(target.attributes['data-source-line'],'234');
});

test('a multiline branch highlights its whole span and labels the branch line rather than the context line',()=>{
 const text='Before();\nif (flag) {\n  B();\n}\nAfter();';
 const source={path:'C.cs',startLine:232,text,tokens:[],execution:[],
  branches:[{start:text.indexOf('if'),length:text.indexOf('After')-text.indexOf('if')-1,category:4,outcome:'missed'}]};
 const view=context.api.buildCoverageSource([source],{from:'kind:4',key:'missed'}), nodes=descendants(view);
 const lines=nodes.filter(n=>n.className?.split(' ').includes('coverage-source-match'));
 assert.deepEqual(lines.map(n=>n.attributes['data-source-line']),['233','234','235']);
 assert.equal(nodes.find(n=>n.className==='coverage-source-path').textContent,'C.cs:233');
 const focus=nodes.find(n=>n.attributes['data-coverage-focus']==='true');
 assert.equal(focus.attributes['data-source-line'],'233');
});

test('band selection only marks the matching branch span on a shared source line',()=>{
 const source={path:'C.cs',startLine:234,text:'A(); B();',tokens:[],execution:[],
  branches:[{start:0,length:4,category:0,outcome:'missed'},{start:5,length:4,category:4,outcome:'missed'}]};
 const nodes=descendants(context.api.buildCoverageSource([source],{from:'kind:4',key:'missed'}));
 const marked=nodes.filter(n=>n.className==='coverage-branch-match');
 assert.equal(marked.map(n=>descendants(n).map(child=>child.textContent || '').join('')).join(''),'B();');
});
