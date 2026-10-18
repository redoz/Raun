const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'../..'),out=path.join(root,'artifacts/scenario-flow');fs.mkdirSync(out,{recursive:true});
const template=fs.readFileSync(path.join(root,'src/Raun/Reporting/Html/report-template.html'),'utf8');
const model=JSON.parse(fs.readFileSync(process.argv[2],'utf8').match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const base=model.scenarios.find(s=>s.displayName==='customer books with parallel arrange');
const stepBase=base.steps.find(s=>!s.isLifecycle);
function step(index,name,start,duration,dependsOn=[],guards=[],extra={}){return {...stepBase,index,stepId:'flow-'+index,displayName:name,titleMessage:{text:name,parts:[{text:name}]},offsetMs:start,durationMs:duration,dependsOn,guards,arms:null,logs:[],logMessages:[],notes:[],noteMessages:[],effects:[],attachments:[],...extra};}
const fixture={...base,scenarioId:'flow',displayName:'Nested booking flow',durationMs:1000,resources:[],references:[],steps:[
 step(0,'Given the database is ready',0,100),
 step(1,'Given Jane is registered',100,200,[0],[],{arms:['if (priority)','else']}),
 step(2,'Given slot 01 is available',100,400,[0]),
 step(3,'When checking eligibility',300,100,[1],[{conditionIndex:1,arm:0}],{arms:['case Eligible','default']}),
 step(4,'When approving Jane',400,100,[3],[{conditionIndex:1,arm:0},{conditionIndex:3,arm:0}]),
 step(5,'When refusing Jane',0,0,[3],[{conditionIndex:1,arm:0},{conditionIndex:3,arm:1}],{status:'skipped',skipReason:'not taken: default'}),
 step(6,'When using standard booking',0,0,[1],[{conditionIndex:1,arm:1}],{status:'skipped',skipReason:'not taken: else'}),
 step(7,'When booking Jane',500,500,[4,2])
]};
function render(name,data){const file=path.join(out,name+'.html');fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',JSON.stringify({...model,scenarios:[data]}).replaceAll('<','\\u003C')));return pathToFileURL(file).href;}
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE?{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}:{})});try{
 const page=await browser.newPage({viewport:{width:1200,height:1100}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(render('nested',fixture));await page.locator('.card-head').click();
 assert.equal(await page.locator('.step-gantt').count(),8,'Every step has a timing strip on the same axis');
 const geometry=await page.locator('.step-gantt').evaluateAll(nodes=>nodes.map(n=>{const b=n.querySelector('.gantt-bar'),r=n.getBoundingClientRect(),x=b?.getBoundingClientRect();return {left:r.left,width:r.width,start:x?(x.left-r.left)/r.width:null,end:x?(x.right-r.left)/r.width:null};}));
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
 assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile has no horizontal page overflow');
 const mobileAxis=await book.locator('.step-gantt').boundingBox();assert.ok(mobileAxis.width>=56,'Mobile keeps a readable timing strip');
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
 assert.ok(await loggedSetup.locator('.step-body').isVisible(),'Navigating to lifecycle evidence opens its logs');
 const timedFixture={...fixture,steps:[step(0,'Then instant completion',1000,0),step(1,'Then blocked',0,0,[],[],{status:'skipped',skipReason:'dependency failed: earlier'})]};
 await page.goto(render('zero-duration',timedFixture));await page.locator('.card-head').click();
 const zero=await page.locator('.step-gantt').first().evaluate(n=>{const a=n.getBoundingClientRect(),b=n.querySelector('.gantt-bar').getBoundingClientRect();return {right:b.right-a.right,width:b.width};});
 assert.ok(zero.width>=2 && zero.right<=.1,'Zero-duration marks remain visible inside the shared axis');
 assert.equal(await page.locator('.gantt-bar').count(),1,'Blocked steps do not look as though they executed');
 const priority=model.scenarios.find(s=>s.displayName==='priority patients get an urgent appointment');
 await page.goto(render('real-conditional',priority));await page.locator('.card-head').click();
 await page.locator('.matrix-summary-header').click();
 const dialog=page.getByRole('dialog',{name:'Resource lanes for '+priority.displayName});await dialog.waitFor({state:'visible'});
 await page.waitForFunction(()=>{const dialog=document.querySelector('.matrix-lanes-popover');const card=dialog.closest('.card');return Array.from(dialog.querySelectorAll('.matrix-row')).every(row=>{const source=Array.from(card.querySelectorAll('.scenario-drill:not(.matrix-lanes-popover .scenario-drill) .step-entry')).find(n=>n.dataset.step===row.dataset.step);if(!source)return false;const a=source.getBoundingClientRect(),b=row.getBoundingClientRect();return a.height===0?b.height===0:Math.abs(a.top-b.top)<1 && Math.abs(a.height-b.height)<1;});});
 await dialog.getByRole('button',{name:'Close resource lanes',exact:true}).click();
 await page.setViewportSize({width:1200,height:1000});await page.emulateMedia({colorScheme:'dark'});
 await page.goto(render('real-conditional-dark',priority)+'?theme=dark');await page.locator('.card-head').click();
 await page.locator('.card').evaluate(async n=>{if(n.scenarioAnimation)await n.scenarioAnimation.finished;});
 await page.screenshot({path:path.join(out,'conditional-dark.png'),fullPage:true});
 assert.deepEqual(errors,[]);console.log('PASS scenario flow: shared timing, parallel overlap, join, nested if/switch, untaken disclosure, named predecessors, navigation, mobile and real sample.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
