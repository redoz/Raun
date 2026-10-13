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
vm.runInContext(template.slice(start, end) + '\nthis.api = {stepNotesHtml,coverageRank, coverageCompare, coverageSegments, coverageLabels, coverageFlow, buildCoverageReport, buildCoverageSource, buildReportViews:typeof buildReportViews === "function" ? buildReportViews : undefined, buildScenarioOutcomes:typeof buildScenarioOutcomes === "function" ? buildScenarioOutcomes : undefined, buildTestsReport:typeof buildTestsReport === "function" ? buildTestsReport : undefined, buildRunHeader:typeof buildRunHeader === "function" ? buildRunHeader : undefined, coverageHierarchy:typeof coverageHierarchy === "function" ? coverageHierarchy : undefined};', context);
const {coverageRank, coverageCompare, coverageSegments, coverageLabels, coverageFlow} = context.api;
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

test('branch flow conserves counts directly from each kind to its outcomes', () => {
  const flow=context.api.coverageFlow([
    {category:4,counts:{inventory:10,complete:2,incomplete:1,reached:1,partial:2,notReached:2,unknown:2}},
    {category:0,counts:{inventory:3,exercised:1,unmeasurable:2}}
  ]);
  assert.equal(flow.nodes.some(node=>node.id==='all'),false);
  assert.equal(flow.nodes.filter(node=>node.column===0).reduce((sum,node)=>sum+node.count,0),13);
  for (const node of flow.nodes) {
    if (node.column>0) assert.equal(flow.links.filter(link=>link.to===node.id).reduce((sum,link)=>sum+link.count,0),node.count);
    if (node.column<1) assert.equal(flow.links.filter(link=>link.from===node.id).reduce((sum,link)=>sum+link.count,0),node.count);
  }
  assert.equal(flow.nodes.find(node=>node.id==='outcome:unknown').count,2);
  assert.equal(flow.nodes.find(node=>node.id==='outcome:helper').count,2);
});
test('branch flow includes unclassified branches and separate partial filter evidence without double counting', () => {
  const flow=context.api.coverageFlow([{category:null,counts:{inventory:4,evaluated:1,filterPartial:1,filterNotReached:1,notReached:1,unknown:1}}]);
  assert.equal(flow.nodes.find(node=>node.id==='kind:null').count,4);
  assert.equal(flow.nodes.filter(node=>node.column===1).reduce((sum,node)=>sum+node.count,0),4);
});
test('an empty branch inventory produces no flow bands', () => {
  assert.equal(context.api.coverageFlow([]).links.length,0);
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
  assert.equal(context.api.coverageFlow(method.categories).nodes.find(node=>node.id==='outcome:complete').count,1);
  assert.equal(tree.children[1].children[0].name,'(global namespace)');
});

test('missing namespace metadata is explicit and never inferred from nested type names', () => {
  const tree=context.api.coverageHierarchy([{assembly:'Api',name:'Business.Outer.Inner',categories:[],methods:[]}]);
  assert.equal(tree.children[0].children[0].name,'(namespace unavailable)');
});
test('navigation keeps one diagram and changes scope through method and breadcrumbs', () => {
  const categories=[{category:4,counts:{inventory:3,complete:1,decisionMeasurable:2,notReached:1,unknown:1}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'Business',name:'Business.Finance',categories,
    methods:[{signature:'Business.Finance.Calculate()',categories}]}]});
  const nodes=()=>descendants(view);
  const children=()=>nodes().filter(node=>node.className==='coverage-child');
  const diagram=()=>nodes().filter(node=>node.tag==='svg');
  assert.equal(diagram().length,1);
  for (const expected of ['Api','Business','Finance','Calculate()']){
    const button=children().find(node=>node.children[0].textContent===expected);
    assert.ok(button,expected);
    button.events.click();
    assert.equal(diagram().length,1);
    assert.equal(nodes().find(node=>node.attributes['aria-current']==='page').textContent,expected);
  }
  assert.equal(children().length,0); // method scope has no child scopes
  assert.match(diagram()[0].attributes['aria-label'],/Coverage undetermined: 1/);
  const band=nodes().find(node=>node.tag==='path' && node.attributes['aria-label']?.startsWith('Logic → Not reached')); band.events.click();
  assert.equal(nodes().find(node=>node.attributes['aria-current']==='page').textContent,'Calculate()');
  nodes().find(node=>node.tag==='button' && node.textContent==='Whole run').events.click();
  nodes().find(n=>n.tag==='button' && n.textContent==='Clear band filter').events.click();
  assert.equal(children()[0].children[0].textContent,'Api');
  assert.equal(diagram().length,1);
});
test('band selection filters children and ranks by absolute unreached logic count', () => {
  const cats=n=>[{category:4,counts:{inventory:n+1,notReached:n,decisionMeasurable:n+1,complete:1}}];
  const view=context.api.buildCoverageReport({status:'available',categories:cats(12),types:[
    {assembly:'Small',namespace:'N',name:'N.C',categories:cats(2),methods:[{signature:'N.C.Small()',categories:cats(2)}]},
    {assembly:'Large',namespace:'N',name:'N.C',categories:cats(10),methods:[{signature:'N.C.Large()',categories:cats(10)}]},
    {assembly:'Covered',namespace:'N',name:'N.C',categories:cats(0),methods:[{signature:'N.C.Covered()',categories:cats(0)}]}
  ]});
  const nodes=()=>descendants(view), children=()=>nodes().filter(n=>n.className==='coverage-child');
  assert.equal(nodes().some(n=>n.tag==='select'),false);
  assert.deepEqual(children().map(n=>n.children[0].textContent),['Large','Small','Covered']);
  const band=nodes().find(n=>n.tag==='path' && n.attributes['aria-label']?.startsWith('Logic → Not reached'));
  assert.ok(band);band.events.click();
  assert.deepEqual(nodes().filter(n=>n.className?.split(' ').includes('coverage-finding')).map(n=>n.children[0].textContent),['N.C.Large()','N.C.Small()']);
  assert.equal(children().length,3); // scope navigation survives a band filter
  assert.equal(nodes().filter(n=>n.tag==='svg').length,1);
  nodes().find(n=>n.tag==='button' && n.textContent==='Clear band filter').events.click();
  assert.equal(children().length,3);
});
test('method source is rendered as safe text with line numbers and band matches', () => {
  const categories=[{category:4,counts:{inventory:1,notReached:1}}];
  const sources=[{path:'Finance.cs',startLine:12,notExecutedLines:[12],text:'if (flag) return "<script>";',tokens:[{start:0,length:2,kind:'keyword'},{start:17,length:8,kind:'string'}],branches:[{start:0,length:25,category:4,outcome:'missed'}]}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'N',name:'N.Finance',categories,methods:[{signature:'N.Finance.Calculate()',categories,sources}]}]});
  const nodes=()=>descendants(view);
  for (const name of ['Api','N','Finance','Calculate()']) nodes().find(n=>n.className==='coverage-child' && n.children[0].textContent===name).events.click();
  const source=nodes().find(n=>n.className==='coverage-source');assert.ok(source);
  assert.ok(nodes().some(n=>n.className?.includes('coverage-source-missed')));
  assert.ok(nodes().some(n=>n.textContent==='12'));
  assert.ok(nodes().some(n=>n.className==='coverage-syntax-keyword' && n.textContent==='if'));
  assert.equal(nodes().some(n=>n.tag==='script'),false);
  nodes().find(n=>n.tag==='path' && n.attributes['aria-label']?.startsWith('Logic → Not reached')).events.click();
  assert.ok(nodes().some(n=>n.className?.includes('coverage-source-match')));
});

test('execution ranges distinguish statements on one line and preserve uncovered context', () => {
  const source={path:'C.cs',startLine:1,text:'A(); B(); // note',tokens:[{start:10,length:7,kind:'comment'}],branches:[],execution:[{start:0,length:4,state:'executed'},{start:5,length:4,state:'missed'}]};
  const view=context.api.buildCoverageSource([source],null), nodes=descendants(view);
  assert.ok(nodes.some(n=>n.className==='coverage-execution-executed' && n.title==='Executed'));
  assert.ok(nodes.some(n=>n.className==='coverage-execution-missed' && n.title==='Not executed'));
  assert.ok(nodes.some(n=>n.className==='coverage-syntax-comment' && n.textContent==='// note'));
});


test('band findings replace one source pane without changing diagram scope', () => {
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
  nodes().find(n=>n.tag==='path' && n.attributes['aria-label']?.startsWith('Logic → Not reached')).events.click();
  assert.equal(nodes().filter(n=>n.className==='coverage-source-code').length,1);
  assert.match(text(),/large/);assert.doesNotMatch(text(),/other|small/);
  nodes().find(n=>n.className==='coverage-snippet-choice' && n.textContent.includes(':30')).events.click();
  assert.match(text(),/other/);assert.doesNotMatch(text(),/large/);
  nodes().find(n=>n.className?.split(' ').includes('coverage-finding') && n.children[0].textContent==='N.C.Small()').events.click();
  assert.match(text(),/small/);assert.doesNotMatch(text(),/large|other/);
  assert.equal(nodes().filter(n=>n.className==='coverage-source-code').length,1);
  assert.equal(nodes().find(n=>n.attributes['aria-current']==='page').textContent,'Whole run');
  assert.equal(nodes().filter(n=>n.tag==='svg').length,1);
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

test('band focus suppresses rectangular outline and follows SVG path', () => {
  assert.match(template,/\.coverage-flow-link:focus\s*\{outline:none/);
  assert.match(template,/\.coverage-flow-link\[aria-pressed="true"\]\s*\{[^}]*stroke:var\(--ink\)/);
  assert.match(template,/\.coverage-flow-link:focus-visible\s*\{[^}]*stroke:var\(--ink\)/);
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


test('coverage app starts with the first finding and scope navigation', () => {
  const categories=[{category:4,counts:{inventory:2,notReached:2}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'N',name:'N.C',categories,methods:[{signature:'N.C.Work()',categories}]}]});
  assert.equal(view.tag,'section');
  const nodes=descendants(view);
  assert.ok(nodes.some(n=>n.className==='coverage-preview-name'&&n.textContent==='N.C.Work()'));
  assert.equal(nodes.some(n=>n.textContent?.includes('Choose a band')),false);
  assert.equal(nodes.filter(n=>n.attributes.role==='separator').length,2);
  const splitter=nodes.find(n=>n.attributes['aria-label']==='Diagram height');
  const event={key:'ArrowDown',preventDefault(){}};
  splitter.events.keydown(event);
  assert.equal(splitter.attributes['aria-valuenow'],'28');
});

test('coverage band changes only the findings workspace, keeping the scope controls', () => {
  const categories=[{category:4,counts:{inventory:2,notReached:2}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'N',name:'N.C',categories,methods:[{signature:'N.C.Work()',categories}]}]});
  const before=descendants(view).find(n=>n.attributes['aria-label']==='Diagram height');
  descendants(view).find(n=>n.tag==='path' && n.attributes['aria-label']?.startsWith('Logic → Not reached')).events.click();
  const nodes=descendants(view);
  assert.equal(nodes.find(n=>n.attributes['aria-label']==='Diagram height'),before);
  assert.ok(nodes.some(n=>n.className==='coverage-preview-name' && n.textContent==='N.C.Work()'));
  assert.equal(nodes.filter(n=>n.attributes.role==='separator').length,2);
});


test('findings width persists across band changes and clearing the filter', () => {
  const categories=[{category:4,counts:{inventory:2,notReached:2}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[{assembly:'Api',namespace:'N',name:'N.C',categories,methods:[{signature:'N.C.Work()',categories}]}]});
  const nodes=()=>descendants(view);
  nodes().find(n=>n.attributes['aria-label']==='Findings width').events.keydown({key:'ArrowRight',preventDefault(){}});
  nodes().find(n=>n.tag==='path' && n.attributes['aria-label']?.startsWith('Logic → Not reached')).events.click();
  assert.equal(nodes().find(n=>n.attributes['aria-label']==='Findings width').attributes['aria-valuenow'],'30');
  nodes().find(n=>n.textContent==='Clear band filter').events.click();
  assert.equal(nodes().find(n=>n.attributes['aria-label']==='Findings width').attributes['aria-valuenow'],'30');
});


test('resizing writes real CSS properties for the grid, not only divider values', () => {
  const categories=[{category:4,counts:{inventory:2,notReached:2}}];
  const view=context.api.buildCoverageReport({status:'available',categories,types:[]});
  const nodes=descendants(view), explorer=nodes.find(n=>n.className==='coverage-explorer'), findings=nodes.find(n=>n.className==='coverage-findings');
  nodes.find(n=>n.attributes['aria-label']==='Diagram height').events.keydown({key:'ArrowDown',preventDefault(){}});
  nodes.find(n=>n.attributes['aria-label']==='Findings width').events.keydown({key:'ArrowRight',preventDefault(){}});
  assert.equal(explorer.style.getPropertyValue('--coverage-chart-size'),'28%');
  assert.equal(findings.style.getPropertyValue('--coverage-list-size'),'30%');
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
  assert.match(descendants(tests).map(n=>n.textContent || '').join(' '),/Tests.*Passed.*4.*Failed.*1.*Skipped.*2/);
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
