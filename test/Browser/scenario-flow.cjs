const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'../..'),out=path.join(root,'artifacts/scenario-flow');fs.mkdirSync(out,{recursive:true});
const template=fs.readFileSync(path.join(root,'src/Raun/Reporting/Html/report-template.html'),'utf8');
const model=JSON.parse(fs.readFileSync(process.argv[2],'utf8').match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const base=model.scenarios.find(s=>s.displayName==='customer books with parallel arrange');
const stepBase=base.steps.find(s=>!s.isLifecycle);
function step(index,name,start,duration,dependsOn=[],guards=[],extra={}){return {...stepBase,index,stepId:'flow-'+index,displayName:name,titleMessage:{text:name,parts:[{text:name}]},offsetMs:start,durationMs:duration,dependsOn,guards,arms:null,logs:[],logMessages:[],notes:[],noteMessages:[],effects:[],attachments:[],...extra};}
const fixture={...base,scenarioId:'flow',displayName:'Nested booking flow',durationMs:1000,resources:[],links:[],steps:[
 step(0,'Given the database is ready',0,100),
 step(1,'Given Jane is registered',100,200,[0],[],{arms:['if (priority)','else']}),
 step(2,'Given slot 01 is available',100,400,[0]),
 step(3,'When checking eligibility',300,100,[1],[{conditionIndex:1,arm:0}],{arms:['case Eligible','default']}),
 step(4,'When approving Jane',400,100,[3],[{conditionIndex:1,arm:0},{conditionIndex:3,arm:0}]),
 step(5,'When refusing Jane',0,0,[3],[{conditionIndex:1,arm:0},{conditionIndex:3,arm:1}],{status:'skipped',skipReason:'not taken: default'}),
 step(6,'When using standard booking',0,0,[1],[{conditionIndex:1,arm:1}],{status:'skipped',skipReason:'not taken: else'}),
 step(7,'When booking Jane',500,500,[4,2])
]};
async function branchText(locator){return locator.evaluate(n=>Array.from(n.childNodes).filter(x=>x.nodeType===3).map(x=>x.textContent).join('').trim());}
async function assertBranchIndentation(page){
 const positions=await page.locator('.step-head').evaluateAll(heads=>heads.flatMap(head=>{
  const mark=head.querySelector('.scenario-status'),title=head.querySelector('.dn'),chevron=head.querySelector('.step-expander,.lifecycle-toggle');
  if(!head.getBoundingClientRect().height || getComputedStyle(mark).visibility==='hidden')return [];
  return [{depth:Number(head.parentElement.style.getPropertyValue('--step-flow-depth') || 0),left:mark.getBoundingClientRect().left,controlLeft:head.firstElementChild.getBoundingClientRect().left,chevronLeft:chevron?.getBoundingClientRect().left ?? null,
   gap:title.getBoundingClientRect().left+parseFloat(getComputedStyle(title).paddingLeft)-mark.getBoundingClientRect().right}];
 }));
 const base=positions.find(position=>position.depth===0);assert.ok(base);
 const baseChevron=positions.find(position=>position.depth===0 && position.chevronLeft!==null);
 const headings=await page.locator('.flow-branch').evaluateAll(branches=>branches.flatMap(branch=>{
  const toggle=branch.querySelector(':scope > .flow-heading .flow-toggle');
  return toggle.getBoundingClientRect().height ? [{depth:Number(branch.style.getPropertyValue('--flow-depth')),left:toggle.getBoundingClientRect().left}] : [];
 }));
 for(const heading of headings)assert.ok(Math.abs(heading.left-base.controlLeft-heading.depth*12)<1,'Branch heading stays at its parent indentation level');
 for(const position of positions){
  assert.ok(Math.abs(position.left-base.left-position.depth*12)<1,'Status squircle follows each branch indentation level');
  assert.ok(Math.abs(position.gap-base.gap)<1,'Status and title keep their normal spacing at every depth');
  if(position.chevronLeft!==null){assert.ok(baseChevron);assert.ok(Math.abs(position.chevronLeft-baseChevron.chevronLeft-position.depth*12)<1,'Step chevron follows the same branch indentation as its status and title');}
 }
}
function render(name,data){const file=path.join(out,name+'.html');fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',JSON.stringify({...model,scenarios:[data]}).replaceAll('<','\\u003C')));return pathToFileURL(file).href;}
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE?{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}:{})});try{
 const page=await browser.newPage({viewport:{width:1200,height:1100}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(render('nested',fixture));await page.locator('.card-head').click();
 await assertBranchIndentation(page);
 assert.equal(await page.locator('.step-gantt').count(),8,'Every step has a timing strip on the same axis');
 const backgroundBounds=await page.locator('.step-head').evaluateAll(heads=>heads.filter(h=>h.getBoundingClientRect().height>0).map(h=>{const row=h.getBoundingClientRect(),entry=h.parentElement,outer=entry.getBoundingClientRect(),track=h.querySelector('.gantt-track')?.getBoundingClientRect();const padded=!entry.classList.contains('matrix-row'),details=entry.matches('.open,:has(>.step-notes),:has(>.step-prerequisites:not([hidden]))');return Boolean(track && Math.abs(track.left-row.left)<1 && Math.abs(track.right-row.right)<1 && Math.abs(track.top-(padded?outer.top:row.top))<1 && Math.abs(track.bottom-(padded&&!details?outer.bottom-parseFloat(getComputedStyle(entry).borderBottomWidth):row.bottom))<1);}));
 assert.ok(backgroundBounds.every(Boolean),'Timing fills each title-row background');
 const geometry=await page.locator('.gantt-track').evaluateAll(nodes=>nodes.map(n=>{const b=n.querySelector('.gantt-bar'),r=n.getBoundingClientRect(),x=b?.getBoundingClientRect();return {left:r.left,width:r.width,start:x?(x.left-r.left)/r.width:null,end:x?(x.right-r.left)/r.width:null};}));
 assert.ok(geometry.filter(g=>g.width>0).every(g=>Math.abs(g.left-geometry[0].left)<1),'Timing axes align across nested branches');
 assert.ok(Math.abs(geometry[1].start-.1)<.01 && Math.abs(geometry[2].start-.1)<.01,'Parallel setup bars share their actual start');
 assert.ok(Math.abs(geometry[7].start-.5)<.01 && Math.abs(geometry[7].end-1)<.01,'Join timing uses scenario time, not row-relative time');
 assert.equal(await page.locator('[data-step="flow-5"] .gantt-bar').count(),0,'Untaken steps have no execution bars');
 const outer=page.locator('.flow-branch[data-condition="1"][data-arm="0"]');
 assert.equal(await outer.locator('.flow-branch').count(),2,'Nested switch stays inside the selected if arm');
 assert.equal(await page.locator('[data-step="flow-6"]').isVisible(),false,'Untaken else is folded');
 const elseButton=page.locator('.flow-branch[data-condition="1"][data-arm="1"] > .flow-heading button');
 await elseButton.click();assert.ok(await page.locator('[data-step="flow-6"]').isVisible());await elseButton.click();
 const book=page.locator('[data-step="flow-7"]');await book.locator('.step-gantt').click();
 assert.ok(await book.getByRole('button',{name:'Given slot 01 is available',exact:true}).isVisible(),'Dependencies are named on demand');
 assert.equal(await book.locator('.step-body:visible').count(),0,'Timing disclosure does not open logs');
 await outer.locator(':scope > .flow-heading button').click();await book.getByRole('button',{name:'When approving Jane',exact:true}).click();
 assert.ok(await page.locator('[data-step="flow-4"]').isVisible(),'Dependency navigation reveals collapsed ancestors');
 assert.equal(await page.locator('.step-dependency-pixels').count(),0);
 await page.screenshot({path:path.join(out,'nested.png'),fullPage:true});
 await page.setViewportSize({width:390,height:1000});
 await assertBranchIndentation(page);
 assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile has no horizontal page overflow');
 const mobileAxis=await book.locator('.gantt-track').boundingBox();assert.ok(mobileAxis.width>=56,'Mobile keeps a readable timing strip');
 await page.goto(render('real-parallel',base));await page.locator('.card-head').click();assert.ok(await page.locator('.gantt-bar').count()>0);
 const first=page.locator('.step-entry:not(.step-lifecycle)').first();await first.locator('.step-gantt').click();
 const setupLink=first.getByRole('button',{name:'Setup',exact:true});await setupLink.focus();await setupLink.press('Escape');
 assert.ok(await first.locator('.step-gantt').evaluate(n=>n===document.activeElement),'Escape returns focus to the timing disclosure');
 await first.locator('.step-gantt').click();
 const setup=base.steps.find(s=>s.isLifecycle && s.displayName==='Setup');assert.ok(setup);
 await first.getByRole('button',{name:'Setup',exact:true}).click();
 const setupRow=page.locator('[data-step="'+setup.stepId+'"]');
 assert.ok(await setupRow.locator('.step-gantt').isVisible(),'Navigating to setup reveals lifecycle metadata');
 assert.ok(await setupRow.locator('.step-gantt').evaluate(n=>n===document.activeElement),'Lifecycle prerequisite receives keyboard focus');
 const lifecycle=structuredClone(base),setupWithLogs=lifecycle.steps.find(s=>s.isLifecycle && s.displayName==='Setup');
 setupWithLogs.logs=['World ready'];setupWithLogs.logMessages=[];
 await page.goto(render('lifecycle-logs',lifecycle));await page.locator('.card-head').click();
 const lifecycleFirst=page.locator('.step-entry:not(.step-lifecycle)').first();await lifecycleFirst.locator('.step-gantt').click();
 await lifecycleFirst.getByRole('button',{name:'Setup',exact:true}).click();
 const loggedSetup=page.locator('[data-step="'+setupWithLogs.stepId+'"]');
 assert.equal(await loggedSetup.locator('.step-expander').getAttribute('aria-expanded'),'true','Lifecycle log disclosure matches its visible state');
 await loggedSetup.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.ok(await loggedSetup.locator('.step-body').isVisible(),'Navigating to lifecycle evidence opens its logs');
 const timedFixture={...fixture,steps:[step(0,'Then instant completion',1000,0),step(1,'Then blocked',0,0,[],[],{status:'skipped',skipReason:'dependency failed: earlier'})]};
 await page.goto(render('zero-duration',timedFixture));await page.locator('.card-head').click();
 const zero=await page.locator('.gantt-track').first().evaluate(n=>{const a=n.getBoundingClientRect(),b=n.querySelector('.gantt-bar').getBoundingClientRect();return {right:b.right-a.right,width:b.width};});
 assert.ok(zero.width>=2 && zero.right<=.1,'Zero-duration marks remain visible inside the shared axis');
 assert.equal(await page.locator('.gantt-bar').count(),1,'Blocked steps do not look as though they executed');
 const merged={...fixture,scenarioId:'merged-value',steps:[
 step(0,'When booking Alice',0,100),
 step(1,'«merge appointment»',100,0,[0],[],{isValueMerge:true}),
 step(2,'Then Alice has an appointment',100,50,[1])]};
 await page.goto(render('merged-value',merged));await page.locator('.card-head .tw').click();
 assert.equal(await page.locator('[data-step="flow-1"]').isVisible(),false,'Successful value merge plumbing is hidden');
 await page.locator('[data-step="flow-2"] .step-gantt').click();
 assert.equal(await page.locator('[data-step="flow-2"] .predecessor-link').innerText(),'When booking Alice','Prerequisites bypass successful merge plumbing');
 await page.locator('[data-step="flow-2"] .predecessor-link').click();
 assert.ok(await page.locator('[data-step="flow-0"] .step-gantt').evaluate(n=>n===document.activeElement));
 assert.equal(await page.locator('[data-step="flow-1"]').isVisible(),false);
 const failedMerge=structuredClone(merged);failedMerge.scenarioId='failed-merge';failedMerge.steps[1].status='failed';failedMerge.steps[1].exception='No selected branch value';
 await page.goto(render('failed-merge',failedMerge));await page.locator('.card-head .tw').click();
 assert.ok(await page.locator('[data-step="flow-1"]').isVisible(),'Failed merge remains visible for diagnosis');
 const readable={...fixture,scenarioId:'readable-branches',steps:[
 step(0,'Given Alice is a priority case',0,100,[],[],{arms:['if','else']}),
 step(1,'When booking urgently',100,100,[0],[{conditionIndex:0,arm:0}]),
 step(2,'When booking normally',0,0,[0],[{conditionIndex:0,arm:1}],{status:'skipped',skipReason:'not taken: else'}),
 step(3,'When trying to book',200,100,[],[],{arms:['case Waitlisted waitlisted','case not null','default']}),
 step(4,'«case Waitlisted waitlisted»',300,0,[3],[{conditionIndex:3,arm:0}],{isBranchBinding:true}),
 step(5,'Then Jane is on the waitlist',300,100,[4],[{conditionIndex:3,arm:0}]),
 step(6,'Then a value was returned',0,0,[3],[{conditionIndex:3,arm:1}],{status:'skipped',skipReason:'not taken: case not null'}),
 step(7,'Then nothing matched',0,0,[3],[{conditionIndex:3,arm:2}],{status:'skipped',skipReason:'not taken: default'})]};
 await page.goto(render('readable-branches',readable));if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 assert.equal(await branchText(page.locator('.flow-toggle').first()),'If Alice is a priority case','Boolean branch uses the actual readable condition');
 assert.equal(await branchText(page.locator('.flow-branch[data-condition="0"][data-arm="1"] > .flow-heading button')),'Otherwise');
 const matched=page.locator('.flow-branch[data-condition="3"][data-arm="0"] > .flow-heading button');
 assert.equal(await branchText(matched),'Waitlisted','Simple outcome labels omit C# case and the binding variable');
 assert.ok((await matched.getAttribute('title')).includes('case Waitlisted waitlisted'),'Exact source label remains available');
 assert.equal(await page.locator('[data-step="flow-4"]').isVisible(),false,'Successful pattern-binding plumbing does not repeat the branch heading');
 await page.locator('[data-step="flow-5"] .step-gantt').click();
 const bindingLink=page.locator('[data-step="flow-5"] .predecessor-link');assert.equal(await bindingLink.innerText(),'Waitlisted (matched value)');await bindingLink.click();
 assert.ok(await page.locator('[data-step="flow-4"]').isVisible(),'Explicit prerequisite navigation can reveal binding evidence');
 assert.equal(await branchText(page.locator('.flow-branch[data-condition="3"][data-arm="1"] > .flow-heading button')),'not null','Negated patterns must not be mistaken for a type and variable');
 const failedBinding=structuredClone(readable);failedBinding.scenarioId='failed-binding';failedBinding.steps[4].status='failed';failedBinding.steps[4].exception='Pattern extraction failed';
 await page.goto(render('failed-binding',failedBinding));if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();
 assert.ok(await page.locator('[data-step="flow-4"]').isVisible(),'Failed binding plumbing remains visible for diagnosis');
 const priority=model.scenarios.find(s=>s.displayName==='priority patients get an urgent appointment');
 await page.goto(render('real-conditional',priority));await page.locator('.card-head').click();
 await assertBranchIndentation(page);
 const branchWithLogs=priority.steps.find(step=>step.guards?.length && step.status==='passed' && step.logs.length);
 assert.ok(branchWithLogs,'Real branch has log evidence');
 const branchRow=page.locator('[data-step="'+branchWithLogs.stepId+'"]');await branchRow.locator('.step-expander').click();
 assert.ok(await branchRow.evaluate(row=>Math.abs(row.querySelector('.step-body').getBoundingClientRect().left-row.querySelector('.scenario-status').getBoundingClientRect().left)<1),'Expanded branch evidence stays aligned with its indented status');
 await branchRow.locator('.step-expander').click();
 assert.ok(priority.steps.some(step=>step.isValueMerge),'Real compiler merges carry report metadata');
 for(const merge of priority.steps.filter(step=>step.isValueMerge && step.status!=='failed'))assert.equal(await page.locator('[data-step="'+merge.stepId+'"]').isVisible(),false,'Real conditional value merges do not add visible rows');
 const matrixBackgrounds=await page.locator('.step-head').evaluateAll(heads=>heads.filter(h=>h.getBoundingClientRect().height>0).map(h=>{const track=h.querySelector('.gantt-track').getBoundingClientRect(),cell=h.parentElement.querySelector('.matrix-cell,.matrix-summary-cell').getBoundingClientRect();return track.right<=cell.left+.1;}));
 assert.ok(matrixBackgrounds.every(Boolean),'Timing backgrounds stop before the resource matrix');
 await page.locator('.matrix-summary-header').click();
 const dialog=page.getByRole('dialog',{name:'Resource lanes for '+priority.displayName});await dialog.waitFor({state:'visible'});
 await page.waitForFunction(()=>{const dialog=document.querySelector('.matrix-lanes-popover');const card=dialog.closest('.card');return Array.from(dialog.querySelectorAll('.matrix-row')).every(row=>{const source=Array.from(card.querySelectorAll('.scenario-drill:not(.matrix-lanes-popover .scenario-drill) .step-entry')).find(n=>n.dataset.step===row.dataset.step);if(!source)return false;const a=source.getBoundingClientRect(),b=row.getBoundingClientRect();return a.height===0?b.height===0:Math.abs(a.top-b.top)<1 && Math.abs(a.height-b.height)<1;});});
 await dialog.getByRole('button',{name:'Close resource lanes',exact:true}).click();
 await page.setViewportSize({width:1200,height:1000});await page.emulateMedia({colorScheme:'dark'});
 await page.goto(render('real-conditional-dark',priority)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 await page.screenshot({path:path.join(out,'conditional-dark.png'),fullPage:true});
 const waitlisted=model.scenarios.find(s=>s.displayName==='a patient whose booking waits is put on the waitlist');
 await page.goto(render('real-waitlisted-dark',waitlisted)+'?theme=dark');
 if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();
 await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 const selected=page.locator('.flow-toggle[aria-expanded="true"]');assert.equal(await branchText(selected),'Waitlisted','The real waitlist scenario has one readable outcome heading');
 for(const binding of waitlisted.steps.filter(s=>s.isBranchBinding && s.status!=='failed'))assert.equal(await page.locator('[data-step="'+binding.stepId+'"]').isVisible(),false,'Real compiler case bindings do not duplicate business headings');
 await page.screenshot({path:path.join(out,'waitlisted-dark.png'),fullPage:true});
 assert.deepEqual(errors,[]);console.log('PASS scenario flow: shared timing, parallel overlap, join, nested if/switch, untaken disclosure, named predecessors, navigation, mobile and real sample.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
