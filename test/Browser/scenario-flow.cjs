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
  const mark=head.querySelector('.scenario-status'),title=head.querySelector('.dn'),chevron=head.querySelector('.flow-condition-toggle');
  if(!head.getBoundingClientRect().height || getComputedStyle(mark).visibility==='hidden')return [];
  return [{depth:Number(head.parentElement.style.getPropertyValue('--step-flow-depth') || 0),left:mark.getBoundingClientRect().left,controlLeft:head.firstElementChild.getBoundingClientRect().left,chevronLeft:chevron?.getBoundingClientRect().left ?? null,
   gap:title.getBoundingClientRect().left+parseFloat(getComputedStyle(title).paddingLeft)-mark.getBoundingClientRect().right}];
 }));
 const base=positions.find(position=>position.depth===0);assert.ok(base);
 const baseChevron=positions.find(position=>position.depth===0 && position.chevronLeft!==null);
 const headings=await page.locator('.flow-branch').evaluateAll(branches=>branches.flatMap(branch=>{
  const toggle=branch.querySelector(':scope > .flow-heading .flow-toggle');
  return toggle?.getBoundingClientRect().height ? [{depth:Number(branch.style.getPropertyValue('--flow-depth')),left:toggle.getBoundingClientRect().left}] : [];
 }));
 for(const heading of headings)assert.ok(Math.abs(heading.left-base.controlLeft-heading.depth*12)<1,'Branch heading stays at its parent indentation level');
 for(const position of positions){
  assert.ok(Math.abs(position.left-base.left-position.depth*12)<1,'Status squircle follows each branch indentation level');
  assert.ok(Math.abs(position.gap-base.gap)<1,'Status and title keep their normal spacing at every depth');
  if(position.chevronLeft!==null){assert.ok(baseChevron);assert.ok(Math.abs(position.chevronLeft-baseChevron.chevronLeft-position.depth*12)<1,'Step chevron follows the same branch indentation as its status and title');}
 }
}
async function assertResourceCrossesHeading(root,heading,resourceId,expected){
 const bounds=await heading.boundingBox();assert.ok(bounds);
 const crosses=await root.evaluate((node,{bounds,resourceId})=>{
  const path=Array.from(node.querySelectorAll('.matrix-track-bridges path')).find(path=>path.dataset.resourceColumn===resourceId);
  const cell=Array.from(node.querySelectorAll('.matrix-cell')).filter(cell=>cell.dataset.resourceColumn===resourceId).map(cell=>cell.getBoundingClientRect()).find(box=>box.width>0);
  if(!path || !cell)return false;
  const inverse=path.getScreenCTM().inverse(),x=(cell.left+cell.right)/2;
  return [.1,.5,.9].every(fraction=>path.isPointInStroke(new DOMPoint(x,bounds.y+bounds.height*fraction).matrixTransform(inverse)));
 },{bounds,resourceId});
 assert.equal(crosses,expected,'Resource lifelines cross structural headings only while their lifetime continues');
}
function render(name,data){const file=path.join(out,name+'.html');fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',JSON.stringify({...model,scenarios:[data]}).replaceAll('<','\\u003C')));return pathToFileURL(file).href;}
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE?{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}:{})});try{
 const page=await browser.newPage({viewport:{width:1200,height:1100}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(render('nested',fixture));await page.locator('.card-head').click();
 await assertBranchIndentation(page);
 assert.equal(await page.locator('.step-gantt').count(),8,'Every step has a timing strip on the same axis');
 const backgroundBounds=await page.locator('.step-head').evaluateAll(heads=>heads.filter(h=>h.getBoundingClientRect().height>0).map(h=>{const row=h.getBoundingClientRect(),entry=h.parentElement,outer=entry.getBoundingClientRect(),track=h.parentElement.querySelector('.gantt-track')?.getBoundingClientRect();const padded=!entry.classList.contains('matrix-row'),details=entry.matches('.open,:has(>.step-notes),:has(>.step-prerequisites:not([hidden]))');return Boolean(track && Math.abs(track.left-row.left)<1 && Math.abs(track.right-row.right)<1 && Math.abs(track.top-(padded?outer.top:row.top))<1 && Math.abs(track.bottom-(padded&&!details?outer.bottom-parseFloat(getComputedStyle(entry).borderBottomWidth):row.bottom))<1);}));
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
 await page.locator('[data-step="flow-1"] .flow-condition-toggle').click();await book.getByRole('button',{name:'When approving Jane',exact:true}).click();
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
 assert.ok(await page.locator('[data-step="'+setupWithLogs.stepId+'"] .step-logs-toggle').isVisible(),'Lifecycle evidence glyphs remain visible while the step is collapsed');
 const lifecycleFirst=page.locator('.step-entry:not(.step-lifecycle)').first();await lifecycleFirst.locator('.step-gantt').click();
 await lifecycleFirst.getByRole('button',{name:'Setup',exact:true}).click();
 const loggedSetup=page.locator('[data-step="'+setupWithLogs.stepId+'"]');
 assert.equal(await loggedSetup.locator('.step-logs-toggle').getAttribute('aria-expanded'),'true','Lifecycle log disclosure matches its visible state');
 await loggedSetup.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.ok(await loggedSetup.locator('.step-body').isVisible(),'Navigating to lifecycle evidence opens its logs');
 const noted={...fixture,steps:[step(0,'Given a patient exists',0,100,[],[],{notes:['A long note belongs to the step summary and wraps onto more than one line when the available width is narrow.\nA second line remains part of the same summary.'],logs:['Detailed evidence stays outside the timing background.'],attachments:[{name:'response.txt',value:'A captured response'}]}),step(1,'Then the patient is ready',100,50,[0])]};
 await page.goto(render('notes-background',noted));await page.locator('.card-head').click();
 const notedRow=page.locator('[data-step="flow-0"]');
 for(const width of [1200,560]){
  await page.setViewportSize({width,height:1100});
  assert.ok(await notedRow.evaluate(row=>{const track=row.querySelector('.gantt-track').getBoundingClientRect(),note=row.querySelector('.step-notes').getBoundingClientRect();return track.top<=note.top && track.bottom>=note.bottom;}),'Non-matrix timing backgrounds include multiline notes');
 }
 await notedRow.locator('.step-logs-toggle').click();await notedRow.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.ok(await notedRow.evaluate(row=>row.querySelector('.gantt-track').getBoundingClientRect().bottom<=row.querySelector('.step-body').getBoundingClientRect().top),'Expanded logs remain outside the summary timing background');
 assert.equal(await notedRow.locator('.step-head > .stw svg').count(),0,'Ordinary steps have no disclosure chevron');
 assert.equal(await notedRow.locator('.step-evidence-toggle').allTextContents().then(texts=>texts.join('')),'','Glyph controls contain no counters or labels');
 await notedRow.locator('.step-attachments-toggle').focus();await page.keyboard.press('Space');await notedRow.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.ok(await notedRow.locator('[data-evidence-panel="attachments"]').isVisible(),'Paperclip reveals attachments by keyboard');
 assert.equal(await notedRow.locator('[data-evidence-panel="logs"]').isVisible(),false,'Selecting attachments hides the log panel');
 assert.equal(await notedRow.locator('.step-logs-toggle').getAttribute('aria-expanded'),'false');
 assert.equal(await notedRow.locator('.step-attachments-toggle').getAttribute('aria-expanded'),'true');
 await notedRow.locator('.att summary').click();assert.ok(await notedRow.locator('.att-body').isVisible(),'Captured files remain readable');
 await notedRow.locator('.step-attachments-toggle').click();await notedRow.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.equal(await notedRow.locator('.step-body').isVisible(),false,'Clicking the active glyph closes its panel');
 await page.setViewportSize({width:390,height:1000});
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
 step(0,'Given Alice is a priority case',0,100,[],[],{arms:['if','else'],logs:['Evaluated the priority condition']}),
 step(1,'When booking urgently',100,100,[0],[{conditionIndex:0,arm:0}]),
 step(2,'When booking normally',0,0,[0],[{conditionIndex:0,arm:1}],{status:'skipped',skipReason:'not taken: else'}),
 step(3,'When trying to book',200,100,[],[],{arms:['case Waitlisted waitlisted','case not null','default']}),
 step(4,'«case Waitlisted waitlisted»',300,0,[3],[{conditionIndex:3,arm:0}],{isBranchBinding:true}),
 step(5,'Then Jane is on the waitlist',300,100,[4],[{conditionIndex:3,arm:0}]),
 step(6,'Then a value was returned',0,0,[3],[{conditionIndex:3,arm:1}],{status:'skipped',skipReason:'not taken: case not null'}),
 step(7,'Then nothing matched',0,0,[3],[{conditionIndex:3,arm:2}],{status:'skipped',skipReason:'not taken: default'})]};
 await page.goto(render('readable-branches',readable));if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 const conditionRow=page.locator('[data-step="flow-0"]'),conditionTitle=conditionRow.locator('.dn');
 assert.equal(await conditionTitle.innerText(),'If Alice is a priority case','The condition step itself supplies the If title');
 assert.equal(await page.locator('.flow-heading').filter({hasText:'If Alice is a priority case'}).count(),0,'No duplicate If heading');
 assert.equal(await conditionRow.locator('.step-gantt').count(),1,'The If row retains timing');
 await conditionTitle.click();assert.equal(await conditionRow.locator('.step-body').isVisible(),false,'Condition titles do not disclose evidence');
 await conditionRow.locator('.step-logs-toggle').focus();await page.keyboard.press('Enter');await conditionRow.evaluate(async row=>{await row.stepAnimation?.finished;});
 assert.ok(await conditionRow.locator('.step-body').isVisible(),'The log glyph opens condition evidence by keyboard');
 await conditionRow.locator('.flow-condition-toggle').click();
 assert.equal(await page.locator('[data-step="flow-1"]').isVisible(),false,'The condition chevron folds its branch');
 assert.ok(await conditionRow.locator('.step-body').isVisible(),'Branch folding leaves condition evidence open');
 await conditionRow.locator('.flow-condition-toggle').click();
 assert.ok(await page.locator('[data-step="flow-1"]').isVisible(),'The condition chevron reveals its children');
 assert.deepEqual(await page.locator('.step-entry').evaluateAll(rows=>rows.map(row=>row.dataset.step)),readable.steps.map(step=>step.stepId),'Folding and evidence preserve registration order');
 assert.equal(await page.locator('#model').evaluate(node=>JSON.parse(node.textContent).scenarios[0].steps[0].displayName),'Given Alice is a priority case','Original test names remain unchanged');
 assert.equal(await branchText(page.locator('.flow-branch[data-condition="0"][data-arm="1"] > .flow-heading button')),'Otherwise');
 const matched=page.locator('.flow-branch[data-condition="3"][data-arm="0"] > .flow-heading button');
 assert.equal(await branchText(matched),'Waitlisted','Simple outcome labels omit C# case and the binding variable');
 assert.ok((await matched.getAttribute('title')).includes('case Waitlisted waitlisted'),'Exact source label remains available');
 assert.equal(await page.locator('[data-step="flow-4"]').isVisible(),false,'Successful pattern-binding plumbing does not repeat the branch heading');
 await page.locator('[data-step="flow-5"] .step-gantt').click();
 const bindingLink=page.locator('[data-step="flow-5"] .predecessor-link');assert.equal(await bindingLink.innerText(),'Waitlisted (matched value)');await bindingLink.click();
 assert.ok(await page.locator('[data-step="flow-4"]').isVisible(),'Explicit prerequisite navigation can reveal binding evidence');
 assert.equal(await branchText(page.locator('.flow-branch[data-condition="3"][data-arm="1"] > .flow-heading button')),'not null','Negated patterns must not be mistaken for a type and variable');
 const untaken=structuredClone(readable);untaken.scenarioId='false-if';untaken.steps[1].status='skipped';untaken.steps[1].skipReason='not taken: if';untaken.steps[2].status='passed';untaken.steps[2].skipReason=null;
 await page.goto(render('false-if',untaken));await page.locator('.card-head').click();
 const falseCondition=page.locator('[data-step="flow-0"]');
 assert.ok(await falseCondition.isVisible(),'A false condition remains visible with its own result');
 assert.equal(await falseCondition.locator('.flow-condition-toggle').getAttribute('aria-expanded'),'false');
 assert.equal(await page.locator('[data-step="flow-1"]').isVisible(),false);
 assert.ok(await page.locator('[data-step="flow-2"]').isVisible(),'Otherwise remains independently visible');
 await falseCondition.locator('.flow-condition-toggle').click();assert.ok(await page.locator('[data-step="flow-1"]').isVisible(),'Untaken true-arm evidence remains inspectable');
 const nestedIf={...fixture,scenarioId:'nested-if',steps:[
 step(0,'Given the patient is eligible',0,10,[],[],{arms:['if','else']}),
 step(1,'Eventually the slot is ready',10,10,[0],[{conditionIndex:0,arm:0}],{phase:'Eventually',arms:['if','else']}),
 step(2,'When booking',20,10,[1],[{conditionIndex:0,arm:0},{conditionIndex:1,arm:0}]),
 step(3,'Then the slot is unavailable',0,0,[1],[{conditionIndex:0,arm:0},{conditionIndex:1,arm:1}],{status:'skipped',skipReason:'not taken: else'}),
 step(4,'Then the patient is ineligible',0,0,[0],[{conditionIndex:0,arm:1}],{status:'skipped',skipReason:'not taken: else'})]};
 await page.goto(render('nested-if',nestedIf));await page.locator('.card-head').click();
 assert.equal(await page.locator('[data-step="flow-1"] .dn').innerText(),'If the slot is ready','Custom phase prefixes also become If');
 await page.locator('[data-step="flow-1"] .flow-condition-toggle').click();assert.equal(await page.locator('[data-step="flow-2"]').isVisible(),false);
 await page.locator('[data-step="flow-0"] .flow-condition-toggle').click();assert.equal(await page.locator('[data-step="flow-1"]').isVisible(),false);
 await page.locator('[data-step="flow-0"] .flow-condition-toggle').click();assert.ok(await page.locator('[data-step="flow-1"]').isVisible());
 assert.equal(await page.locator('[data-step="flow-2"]').isVisible(),false,'Outer disclosure preserves the nested branch state');
 const failedBinding=structuredClone(readable);failedBinding.scenarioId='failed-binding';failedBinding.steps[4].status='failed';failedBinding.steps[4].exception='Pattern extraction failed';
 await page.goto(render('failed-binding',failedBinding));if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();
 assert.ok(await page.locator('[data-step="flow-4"]').isVisible(),'Failed binding plumbing remains visible for diagnosis');
 const priority=model.scenarios.find(s=>s.displayName==='priority patients get an urgent appointment');
 await page.goto(render('real-conditional',priority));await page.locator('.card-head').click();
 await assertBranchIndentation(page);
 const realCondition=priority.steps.find(step=>step.arms?.includes('if')),realIf=page.locator('[data-step="'+realCondition.stepId+'"]');
 assert.ok((await realIf.locator('.dn').innerText()).startsWith('If Alice'),'The real condition has one readable If title');
 assert.equal(await realIf.locator('.matrix-event[data-matrix-effect="Read"]').count(),1,'The condition Read stays on the If row');
 assert.ok(await realIf.locator('.dn .message-resource').count()>0,'The condition preserves its linked resource label');
 assert.equal(await realIf.locator('.dn').getAttribute('role'),null,'Condition titles do not wrap resource links in an interactive ancestor');
 const branchWithLogs=priority.steps.find(step=>step.guards?.length && step.status==='passed' && step.logs.length);
 assert.ok(branchWithLogs,'Real branch has log evidence');
 const branchRow=page.locator('[data-step="'+branchWithLogs.stepId+'"]');await branchRow.locator('.step-logs-toggle').click();
 assert.ok(await branchRow.evaluate(row=>Math.abs(row.querySelector('.step-body').getBoundingClientRect().left-row.querySelector('.scenario-status').getBoundingClientRect().left)<1),'Expanded branch evidence stays aligned with its indented status');
 await branchRow.locator('.step-logs-toggle').click();
 assert.ok(priority.steps.some(step=>step.isValueMerge),'Real compiler merges carry report metadata');
 for(const merge of priority.steps.filter(step=>step.isValueMerge && step.status!=='failed'))assert.equal(await page.locator('[data-step="'+merge.stepId+'"]').isVisible(),false,'Real conditional value merges do not add visible rows');
 const matrixBackgrounds=await page.locator('.step-head').evaluateAll(heads=>heads.filter(h=>h.getBoundingClientRect().height>0).map(h=>{const track=h.parentElement.querySelector('.gantt-track').getBoundingClientRect(),cell=Array.from(h.parentElement.querySelectorAll('.matrix-cell,.matrix-summary-cell')).map(cell=>cell.getBoundingClientRect()).find(box=>box.width>0);return track.right<=cell.left+.1;}));
 assert.ok(matrixBackgrounds.every(Boolean),'Timing backgrounds stop before the resource matrix');
 await page.locator('.matrix-summary-header').click();
 const dialog=page.getByRole('dialog',{name:'Resource lanes for '+priority.displayName});await dialog.waitFor({state:'visible'});
 await page.waitForFunction(()=>{const dialog=document.querySelector('.matrix-lanes-popover');const card=dialog.closest('.card');return Array.from(dialog.querySelectorAll('.matrix-row')).every(row=>{const source=Array.from(card.querySelectorAll('.scenario-drill:not(.matrix-lanes-popover .scenario-drill) .step-entry')).find(n=>n.dataset.step===row.dataset.step);if(!source)return false;const a=source.getBoundingClientRect(),b=row.getBoundingClientRect();return a.height===0?b.height===0:Math.abs(a.top-b.top)<1 && Math.abs(a.height-b.height)<1;});});
 await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
 await assertResourceCrossesHeading(dialog,page.locator('.flow-branch[data-arm="1"] > .flow-heading'),priority.resources.find(resource=>resource.type==='Appointment').id,true);
 await dialog.getByRole('button',{name:'Close resource lanes',exact:true}).click();
 await page.setViewportSize({width:1200,height:1000});await page.emulateMedia({colorScheme:'dark'});
 await page.goto(render('real-conditional-dark',priority)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
 const otherwiseHeading=page.locator('.flow-branch[data-arm="1"] > .flow-heading');
 const appointmentId=priority.resources.find(resource=>resource.type==='Appointment').id,patientId=priority.resources.find(resource=>resource.type==='Patient').id;
 await assertResourceCrossesHeading(page.locator('.scenario-drill'),otherwiseHeading,appointmentId,true);
 await assertResourceCrossesHeading(page.locator('.scenario-drill'),otherwiseHeading,patientId,false);
 await otherwiseHeading.locator('button').click();
 await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
 await assertResourceCrossesHeading(page.locator('.scenario-drill'),otherwiseHeading,appointmentId,true);
 await otherwiseHeading.locator('button').click();

 await page.screenshot({path:path.join(out,'conditional-dark.png'),fullPage:true});
 const bulk=model.scenarios.find(s=>s.displayName==='bulk user import via LINQ');assert.ok(bulk);
 await page.goto(render('bulk-join',bulk)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{await n.scenarioAnimation?.finished;});
 const importer=bulk.steps.find(step=>step.displayName==='When importing the users'),join=page.locator('.gantt-join[data-join-step="'+importer.stepId+'"]');
 assert.equal(await join.count(),1,'A collection fan-in has one merge guide');
 assert.equal(await page.locator('.gantt-join').count(),2,'Both multiple and single dependencies have guides; Setup dependencies do not');
 const assertion=bulk.steps.find(step=>step.displayName.startsWith('Then the import'));
 const singleJoin=page.locator('.gantt-join[data-join-step="'+assertion.stepId+'"]');
 assert.deepEqual(JSON.parse(await singleJoin.getAttribute('data-join-sources')),[importer.stepId],'The assertion connects to its sole data prerequisite');
 const singleMarkers=await singleJoin.locator('.gantt-join-marker').evaluateAll(nodes=>nodes.map(node=>({step:node.dataset.sourceStep,kind:node.dataset.kind})));
 assert.deepEqual(singleMarkers,[{step:importer.stepId,kind:'last'},{step:assertion.stepId,kind:'join'}],'Single dependencies have an incoming and outgoing arrow');
 assert.equal(await singleJoin.locator('.gantt-join-segment:not(.is-solid)').count(),0,'Both single-dependency segments are solid');
 const userSteps=bulk.steps.filter(step=>step.displayName.startsWith('Given user '));
 assert.deepEqual(JSON.parse(await join.getAttribute('data-join-sources')),userSteps.map(step=>step.stepId),'Merge guides use all data predecessors and exclude Setup');
 const assertJoinBounds=async()=>{
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  const geometry=await join.evaluate((node,ids)=>{
    const line=node.querySelector('.gantt-join-line'),bounds=line.getBoundingClientRect(),row=document.querySelector('.step-entry[data-step="'+node.dataset.joinStep+'"]'),track=row.querySelector('.gantt-track').getBoundingClientRect(),bar=row.querySelector('.gantt-bar').getBoundingClientRect();
    const participants=ids.map(id=>document.querySelector('.step-entry[data-step="'+id+'"] .gantt-track').getBoundingClientRect());
    return {x:bounds.left,start:bar.left,top:bounds.top,bottom:bounds.bottom,first:Math.min(...participants.map(box=>box.top)),last:track.bottom,right:track.right,dashed:getComputedStyle(line).strokeDasharray,ticks:node.querySelectorAll('.gantt-join-marker').length};
  },userSteps.map(step=>step.stepId));
  assert.ok(Math.abs(geometry.x-geometry.start)<1 && geometry.top<=geometry.first+.5 && geometry.bottom>=geometry.last-.5,'The join guide spans all source rows through the importer at its start time: '+JSON.stringify(geometry));
  assert.ok(geometry.x<geometry.right && geometry.dashed!=='none' && geometry.ticks===4,'The guide stays in the timing area and marks the three sources plus join');
 };
 const assertJoinMarkers=async expected=>{
  const markers=await join.locator('.gantt-join-marker').evaluateAll(nodes=>nodes.map(node=>({step:node.dataset.sourceStep,kind:node.dataset.kind})));
  assert.deepEqual(markers,expected,'Join markers distinguish completed dependencies, the last finishers, and the joining step');
  const segments=await join.locator('.gantt-join-segment').evaluateAll(nodes=>nodes.map(node=>({step:node.dataset.sourceStep,solid:getComputedStyle(node).strokeDasharray==='none'})));
  assert.deepEqual(segments,expected.map(marker=>({step:marker.step,solid:marker.kind!=='finished'})),'Only the last-finisher and joining rows use solid vertical lines');
  const positions=await join.evaluate(node=>{
   const x=node.querySelector('.gantt-join-line').getBoundingClientRect().left;
   return Array.from(node.querySelectorAll('.gantt-join-marker'),marker=>{const box=marker.getBoundingClientRect();return {kind:marker.dataset.kind,left:box.left-x,right:box.right-x};});
  });
  for(const marker of positions)assert.ok(marker.kind==='finished' ? marker.left<0 && marker.right>0 : marker.kind==='last' ? marker.right<0 : marker.left>0,'Crosses straddle the line; last-finisher arrows precede it and joining arrows follow it');
 };
 const expectedMarkers=userSteps.map((step,index)=>({step:step.stepId,kind:index===2 ? 'last' : 'finished'})).concat({step:importer.stepId,kind:'join'});
 await assertJoinBounds();await assertJoinMarkers(expectedMarkers);
 const hoverPoint=await join.locator('.gantt-join-segment').first().boundingBox();
 await page.mouse.move(hoverPoint.x+3,hoverPoint.y+10);
 assert.equal(await join.evaluate(node=>node.classList.contains('is-hovered')),true,'Hovering near a segment highlights the complete dependency guide');
 await page.mouse.move(20,100);
 assert.equal(await join.evaluate(node=>node.classList.contains('is-hovered')),false,'Leaving the guide clears its highlight');
 await page.screenshot({path:path.join(out,'bulk-join-dark.png'),fullPage:true});
 await page.setViewportSize({width:390,height:1000});await assertJoinBounds();
 await page.setViewportSize({width:1200,height:1000});
 const tied={...bulk,steps:bulk.steps.map(step=>step.stepId===userSteps[1].stepId ? {...step,durationMs:userSteps[2].offsetMs+userSteps[2].durationMs-step.offsetMs} : step)};
 await page.goto(render('bulk-join-tied',tied)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{await n.scenarioAnimation?.finished;});
 await assertJoinBounds();await assertJoinMarkers(expectedMarkers.map(marker=>marker.step===userSteps[1].stepId ? {...marker,kind:'last'} : marker));
 const unrelated=step(100,'Given an unrelated service is ready',0,50,[bulk.steps[0].index]);
 const withUnrelated={...bulk,steps:[...bulk.steps.slice(0,2),unrelated,...bulk.steps.slice(2)]};
 await page.goto(render('bulk-join-unrelated',withUnrelated)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{await n.scenarioAnimation?.finished;});
 await assertJoinBounds();
 const crossesUnrelated=await join.evaluate((node,id)=>{
  const line=node.querySelector('.gantt-join-line'),box=document.querySelector('.step-entry[data-step="'+id+'"] .gantt-track').getBoundingClientRect(),x=line.getBoundingClientRect().left;
  const transform=line.getScreenCTM().inverse();
  for(let y=box.top+1;y<box.bottom-1;y+=.5)if(Array.from(line.querySelectorAll('path')).some(segment=>segment.isPointInStroke(new DOMPoint(x,y).matrixTransform(transform))))return true;
  return false;
 },unrelated.stepId);
 assert.equal(crossesUnrelated,false,'Merge guides leave unrelated rows blank');
 // A step that queued after its prerequisites: the sync line sits at the release, hatching fills the wait.
 const queued={...fixture,scenarioId:'queued',displayName:'Queued join',durationMs:1000,steps:[
  step(0,'Given the database is ready',0,200),
  step(1,'Given Jane is registered',0,300),
  step(2,'When booking Jane',600,200,[0,1])
 ]};
 await page.goto(render('queued-join',queued)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{await n.scenarioAnimation?.finished;});
 await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
 const sync=await page.evaluate(()=>{
  const track=id=>document.querySelector('.step-entry[data-step="'+id+'"] .gantt-track').getBoundingClientRect();
  const box=track('flow-2'),wait=document.querySelector('[data-step="flow-2"] .gantt-wait'),bar=document.querySelector('[data-step="flow-2"] .gantt-bar').getBoundingClientRect();
  const segments=Array.from(document.querySelectorAll('.gantt-join[data-join-step="flow-2"] .gantt-join-segment'),node=>({step:node.dataset.sourceStep,x:node.getBoundingClientRect().left,solid:getComputedStyle(node).strokeDasharray==='none'}));
  const marker=document.querySelector('.gantt-join[data-join-step="flow-2"] .gantt-join-marker[data-kind="join"]').getBoundingClientRect();
  const hatch=wait && !wait.hidden ? wait.getBoundingClientRect() : null;
  return {release:box.left+box.width*.3,start:bar.left,segments,marker:marker.left,hatch:hatch && {left:hatch.left,right:hatch.right,top:hatch.top,bottom:hatch.bottom},row:{top:box.top,bottom:box.bottom},background:wait && getComputedStyle(wait).backgroundImage};
 });
 for(const segment of sync.segments.filter(segment=>segment.step!=='flow-2'))assert.ok(Math.abs(segment.x-sync.release)<1,'Prerequisite rows carry the sync line at the release moment: '+JSON.stringify(sync));
 const own=sync.segments.filter(segment=>segment.step==='flow-2');
 assert.equal(own.length,2,'A queued step has a line at the release and another where its bar starts');
 assert.ok(own.some(segment=>Math.abs(segment.x-sync.release)<1) && own.some(segment=>Math.abs(segment.x-sync.start)<1) && own.every(segment=>segment.solid),'Both of the queued step\'s lines are solid');
 assert.ok(sync.marker>=sync.start-.5,'The joining arrow points into the bar start, not the release');
 assert.ok(sync.hatch && Math.abs(sync.hatch.left-sync.release)<1 && Math.abs(sync.hatch.right-sync.start)<1,'Hatching spans the wait from release to start: '+JSON.stringify(sync));
 assert.ok(sync.hatch.top<=sync.row.top+.5 && sync.hatch.bottom>=sync.row.bottom-.5 && sync.background.includes('repeating-linear-gradient'),'The wait hatch fills the queued row');
 await page.screenshot({path:path.join(out,'queued-join-dark.png'),fullPage:true});

 // Resting on the timing area fades step text after a beat; the title text or leaving brings it back.
 const titleOpacity=()=>page.evaluate(()=>Number(getComputedStyle(document.querySelector('[data-step="flow-0"] .dn')).opacity));
 const timingPoint=await page.evaluate(()=>{
  const track=document.querySelector('[data-step="flow-2"] .gantt-track').getBoundingClientRect(),text=document.createRange();
  text.selectNodeContents(document.querySelector('[data-step="flow-2"] .dn'));
  return {x:Math.max(text.getBoundingClientRect().right+40,track.left+track.width*.55),y:(track.top+track.bottom)/2};
 });
 await page.mouse.move(timingPoint.x,timingPoint.y);
 assert.ok(await titleOpacity()>.9,'Sweeping across the timing area does not flash a fade');
 await page.waitForTimeout(1000);
 assert.ok(await titleOpacity()<.3,'Resting on the timing area fades step text');
 assert.ok(await page.evaluate(()=>Number(getComputedStyle(document.querySelector('[data-step="flow-0"] .gantt-bar')).opacity))>.05,'Timing bars stay visible while text fades');
 const titleText=await page.evaluate(()=>{const text=document.createRange();text.selectNodeContents(document.querySelector('[data-step="flow-2"] .dn'));const box=text.getBoundingClientRect();return {x:box.left+4,y:(box.top+box.bottom)/2};});
 await page.mouse.move(titleText.x,titleText.y);await page.waitForTimeout(400);
 assert.ok(await titleOpacity()>.9,'Pointing at a step title brings the text back');
 await page.mouse.move(timingPoint.x,timingPoint.y);await page.waitForTimeout(1000);
 assert.ok(await titleOpacity()<.3);
 await page.mouse.move(5,5);await page.waitForTimeout(400);
 assert.ok(await titleOpacity()>.9,'Leaving the steps brings the text back at once');

 // Timing can be hidden for the whole report; the choice survives a reload and leaves the card open.
 const timingToggle=page.locator('.card-tools .timing-toggle');
 assert.equal(await timingToggle.getAttribute('aria-pressed'),'true','Timing shows by default');
 await timingToggle.click();
 assert.equal(await page.locator('.card').evaluate(n=>n.classList.contains('collapsed')),false,'The timing toggle does not collapse its scenario');
 assert.equal(await timingToggle.getAttribute('aria-pressed'),'false');
 const hiddenTiming=await page.evaluate(()=>['.gantt-bar','.gantt-wait','.gantt-joins'].map(selector=>getComputedStyle(document.querySelector(selector)).visibility));
 assert.deepEqual(hiddenTiming,['hidden','hidden','hidden'],'Hiding timing hides bars, waits and sync lines');
 await page.reload();await page.locator('.card-head').click();
 assert.equal(await page.locator('.card-tools .timing-toggle').getAttribute('aria-pressed'),'false','The hidden-timing choice persists');
 assert.equal(await page.evaluate(()=>getComputedStyle(document.querySelector('.gantt-bar')).visibility),'hidden');
 await page.locator('.card-tools .timing-toggle').press('Enter');
 assert.equal(await page.locator('.card').evaluate(n=>n.classList.contains('collapsed')),false,'Keyboard activation does not collapse its scenario either');
 assert.equal(await page.evaluate(()=>getComputedStyle(document.querySelector('.gantt-bar')).visibility),'visible','Timing comes back');
 await page.evaluate(()=>localStorage.clear());

 const waitlisted=model.scenarios.find(s=>s.displayName==='a patient whose booking waits is put on the waitlist');
 await page.goto(render('real-waitlisted-dark',waitlisted)+'?theme=dark');
 if((await page.locator('.card').getAttribute('class')).includes('collapsed'))await page.locator('.card-head .tw').click();
 await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 const selected=page.locator('.flow-toggle[aria-expanded="true"]');assert.equal(await branchText(selected),'Waitlisted','The real waitlist scenario has one readable outcome heading');
 for(const binding of waitlisted.steps.filter(s=>s.isBranchBinding && s.status!=='failed'))assert.equal(await page.locator('[data-step="'+binding.stepId+'"]').isVisible(),false,'Real compiler case bindings do not duplicate business headings');
 await page.screenshot({path:path.join(out,'waitlisted-dark.png'),fullPage:true});
 assert.deepEqual(errors,[]);console.log('PASS scenario flow: shared timing, parallel overlap, join, nested if/switch, untaken disclosure, named predecessors, navigation, mobile and real sample.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
