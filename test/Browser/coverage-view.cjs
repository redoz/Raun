const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'../..');
if(!process.argv[2])throw new Error('Usage: node test/Browser/coverage-view.cjs <sample-report.html> [output-directory]');
const out=path.resolve(process.argv[3] || path.join(root,'artifacts/coverage-view'));fs.mkdirSync(out,{recursive:true});
const template=fs.readFileSync(path.join(root,'src/Raun/Reporting/Html/report-template.html'),'utf8');
const model=JSON.parse(fs.readFileSync(process.argv[2],'utf8').match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1]);
assert.equal(model.coverage?.status,'available','The sample report collects coverage');
const assemblies=[...new Set(model.coverage.types.map(type=>type.assembly))].sort();
assert.deepEqual(assemblies,['AppointmentTests'],'testconfig.json limits coverage to the sample');
const file=path.join(out,'coverage-view.html');
fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',()=>JSON.stringify(model).replaceAll('<','\\u003C')));
const logic=model.coverage.categories.find(category=>category.category===4).counts;

const rows=page=>page.locator('.coverage-row');
const names=page=>page.locator('.coverage-row .coverage-row-name').allTextContents();
const selected=page=>page.locator('.coverage-row[aria-selected="true"] .coverage-row-name').textContent();

(async()=>{const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE?{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}:{})});try{
 const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(pathToFileURL(file).href);
 await page.click('#report-tab-coverage');
 await page.waitForSelector('.coverage-row');

 // The tree opens on the whole run, which shows the same logic score as the header.
 assert.deepEqual(await names(page),['Whole run','AppointmentTests']);
 assert.equal(await selected(page),'Whole run');
 assert.equal(await page.locator('.coverage-row[aria-level="1"] .coverage-row-score').textContent(),logic.complete+'/'+logic.decisionMeasurable);
 assert.equal(await page.locator('.coverage-preview .coverage-scope-score').textContent(),logic.complete+' of '+logic.decisionMeasurable+' logic decisions fully tested');
 assert.equal((await page.locator('.coverage-collection>summary').textContent()).trim(),'Reported source: 1 assembly');

 // The flow fills the pane at 1:1, so its labels keep the body text size, and follows the splitter.
 const flow=()=>page.locator('.coverage-flow').evaluate(figure=>({figure:figure.clientWidth,svg:figure.querySelector('svg').getBoundingClientRect().width,
  label:getComputedStyle(figure.querySelector('svg text:not(.coverage-flow-heading)')).fontSize,body:getComputedStyle(document.querySelector('.coverage-tree')).fontSize}));
 // The first frame draws at a default width; the resize observer then redraws at the pane's width.
 await page.waitForFunction(()=>{const figure=document.querySelector('.coverage-flow');return Math.abs(figure.querySelector('svg').getBoundingClientRect().width-figure.clientWidth)<=1;});
 let size=await flow();
 assert.ok(Math.abs(size.svg-size.figure)<=1,'flow spans the pane: '+JSON.stringify(size));
 assert.equal(size.label,size.body);
 await page.screenshot({path:path.join(out,'scope.png')});
 await page.locator('.coverage-splitter').focus();
 for(let i=0;i<6;i++)await page.keyboard.press('ArrowLeft');
 await page.waitForFunction(width=>{const figure=document.querySelector('.coverage-flow'),svg=figure.querySelector('svg').getBoundingClientRect().width;
  return Math.abs(svg-width)>40 && Math.abs(svg-figure.clientWidth)<=1;},size.svg);
 size=await flow();assert.ok(Math.abs(size.svg-size.figure)<=1,'flow follows the splitter: '+JSON.stringify(size));

 // Arrow keys walk down to a method, which shows its source instead of the flow.
 await rows(page).first().focus();
 await page.keyboard.press('ArrowDown');assert.equal(await selected(page),'AppointmentTests');
 for(let level=2;level<5;level++){
  await page.keyboard.press('ArrowRight');await page.keyboard.press('ArrowRight');
 }
 assert.equal(await page.locator('.coverage-row[aria-selected="true"]').getAttribute('aria-level'),'5');
 assert.equal(await page.locator('.coverage-flow').count(),0);
 assert.ok(await page.locator('.coverage-source-line').count()>0,'method source is rendered');
 assert.ok(await page.locator('[class^="coverage-execution-"]').count()>0,'execution evidence is marked');
 await page.keyboard.press('Home');assert.equal(await selected(page),'Whole run');

 // Sorting applies per level and reports its direction.
 assert.equal(await page.locator('.coverage-row[aria-level="2"]').getAttribute('aria-expanded'),'true','the drill-down left the assembly open');
 const notReached=page.locator('.coverage-tree-heading.coverage-cell-missed');
 await notReached.locator('button').click();
 assert.equal(await notReached.getAttribute('aria-sort'),'descending');
 const counts=(await page.locator('.coverage-row[aria-level="3"] .coverage-cell-missed').allTextContents()).map(text=>text==='—' ? 0 : Number(text));
 assert.deepEqual(counts,[...counts].sort((a,b)=>b-a));
 await page.locator('.coverage-tree-heading.coverage-cell-name button').click();

 // A band filters the tree to scopes with branches in it, until it is cleared.
 const all=await rows(page).count();
 const band=page.locator('.coverage-flow-link[aria-label^="Logic → Not reached"]');
 const label=await band.getAttribute('aria-label');
 await band.click();
 assert.equal(await page.locator('.coverage-tree-filter-label').textContent(),label.replace(/: [\d,]+ branches$/,''));
 assert.equal(await page.locator('.coverage-flow-link[aria-pressed="true"]').count(),1);
 const filtered=await rows(page).count();
 assert.ok(filtered<all,'the filter hides scopes without unreached logic: '+filtered+' of '+all);
 for(const missed of await page.locator('.coverage-row[aria-level="3"] .coverage-cell-missed').allTextContents())assert.notEqual(missed,'—');
 await page.screenshot({path:path.join(out,'band.png')});
 await page.getByRole('button',{name:'Clear filter'}).click();
 assert.equal(await rows(page).count(),all);

 // Collector notes open from the caption.
 await page.locator('.coverage-collection>summary').click();
 assert.ok(await page.locator('.coverage-collection>p',{hasText:'Source scope: runtime dependencies of AppointmentTests'}).isVisible());

 // Phones stack the tree above the pane without page-wide horizontal scrolling.
 await page.setViewportSize({width:390,height:844});await page.waitForTimeout(100);
 assert.ok(await page.evaluate(()=>document.scrollingElement.scrollWidth<=innerWidth),'no horizontal page scroll');
 const tree=await page.locator('.coverage-tree-pane').boundingBox(),pane=await page.locator('.coverage-preview').boundingBox();
 assert.ok(tree.y+tree.height<=pane.y+1,'tree sits above the pane on phones');
 await page.screenshot({path:path.join(out,'mobile.png')});

 await page.emulateMedia({colorScheme:'dark'});await page.setViewportSize({width:1440,height:900});
 await page.screenshot({path:path.join(out,'dark.png')});
 assert.deepEqual(errors,[]);
 console.log('PASS coverage view: sample scope, whole-run score, flow width and type size, keyboard drill-down to source, sorting, band filter, collector notes, mobile and dark.');
}finally{await browser.close();}})().catch(error=>{console.error(error);process.exitCode=1;});
