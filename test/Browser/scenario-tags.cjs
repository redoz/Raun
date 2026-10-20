const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'../..');
if(!process.argv[2])throw new Error('Usage: node test/Browser/scenario-tags.cjs <sample-report.html> [output-directory]');
const out=path.resolve(process.argv[3] || path.join(root,'artifacts/scenario-tags'));fs.mkdirSync(out,{recursive:true});
const template=fs.readFileSync(path.join(root,'src/Raun/Reporting/Html/report-template.html'),'utf8');
const model=JSON.parse(fs.readFileSync(process.argv[2],'utf8').match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const file=path.join(out,'scenario-tags.html');
fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',()=>JSON.stringify(model).replaceAll('<','\\u003C')));

// The sample tags every scenario suite=appointments, the booking class area=booking, the two bulk
// imports area=users + owner=identity (through [UserImport]), and the lifecycle class area=lifecycle.
const tagsOf=sc=>(sc.tags || []).map(t=>t.key+':'+t.value);
const all=model.scenarios.map(sc=>sc.displayName);
const having=tag=>model.scenarios.filter(sc=>tagsOf(sc).includes(tag)).map(sc=>sc.displayName);
assert.deepEqual(having('area:users'),['bulk user import','bulk user import via LINQ'],'the sample tags the bulk imports');
assert.ok(having('area:lifecycle').length>0,'the sample tags the lifecycle class');
assert.equal(having('suite:appointments').length,all.length,'the assembly tag reaches every scenario');

const visible=page=>page.locator('.tests-narrative .card:visible .scenario-title').allTextContents();
const pressed=page=>page.locator('.tag-filter-option[aria-pressed="true"]').evaluateAll(nodes=>nodes.map(n=>n.dataset.tag));
const query=page=>page.evaluate(()=>new URL(location.href).searchParams.getAll('tag'));

(async()=>{const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE?{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}:{})});try{
 const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(pathToFileURL(file).href+'?theme=dark');
 await page.waitForSelector('.tag-filter');

 // Every distinct tag is offered once, nothing is selected, and every scenario shows.
 const offered=await page.locator('.tag-filter-option').evaluateAll(nodes=>nodes.map(n=>n.dataset.tag));
 assert.deepEqual([...offered].sort(),[...new Set(model.scenarios.flatMap(tagsOf))].sort());
 assert.deepEqual(await pressed(page),[]);
 assert.deepEqual(await visible(page),all);
 assert.equal(await page.locator('.tag-filter-clear').isVisible(),false);

 // Cards show their own tags, but not one every scenario shares.
 const importCard=page.locator('.card',{has:page.locator('.scenario-title',{hasText:/^bulk user import$/})});
 assert.deepEqual((await importCard.locator('.scenario-tag').allTextContents()).sort(),['area: booking','area: users','owner: identity']);
 assert.equal(await page.locator('.scenario-tag[data-tag="suite:appointments"]').count(),0);

 // Picking a tag in the bar keeps only the scenarios carrying it, and writes it into the URL.
 await page.click('.tag-filter-option[data-tag="area:users"]');
 assert.deepEqual(await visible(page),having('area:users'));
 assert.deepEqual(await query(page),['area:users']);
 assert.equal(new URL(page.url()).searchParams.get('theme'),'dark','other parameters survive');
 assert.match(await page.locator('.tag-filter-count').textContent(),new RegExp('^Showing 2 of '+all.length+' scenarios$'));
 // A class with nothing left to show hides its heading too.
 assert.equal(await page.locator('.tests-narrative .group:visible',{hasText:'Appointment lifecycle'}).count(),0);

 // A second tag widens the selection: a scenario shows when it has either tag.
 await page.click('.tag-filter-option[data-tag="area:lifecycle"]');
 const either=all.filter(name=>having('area:users').includes(name) || having('area:lifecycle').includes(name));
 assert.deepEqual(await visible(page),either);
 assert.deepEqual((await query(page)).sort(),['area:lifecycle','area:users']);
 await page.screenshot({path:path.join(out,'filtered.png')});

 // Opening the same URL restores the filter.
 await page.reload();await page.waitForSelector('.tag-filter');
 assert.deepEqual((await pressed(page)).sort(),['area:lifecycle','area:users']);
 assert.deepEqual(await visible(page),either);

 // A chip on a card toggles the same filter and leaves the card collapsed as it was.
 await page.click('.tag-filter-clear');
 assert.deepEqual(await visible(page),all);
 assert.deepEqual(await query(page),[]);
 const collapsedBefore=await importCard.evaluate(card=>card.classList.contains('collapsed'));
 await importCard.locator('.scenario-tag[data-tag="owner:identity"]').click();
 assert.deepEqual(await visible(page),having('owner:identity'));
 assert.equal(await importCard.evaluate(card=>card.classList.contains('collapsed')),collapsedBefore,'a chip click does not toggle the card');
 assert.equal(await importCard.locator('.scenario-tag[data-tag="owner:identity"]').getAttribute('aria-pressed'),'true');
 await importCard.locator('.scenario-tag[data-tag="owner:identity"]').click();
 assert.deepEqual(await visible(page),all);

 // A tag from another run's URL stays selectable, so it can be cleared, and matches nothing.
 await page.goto(pathToFileURL(file).href+'?tag=area:gone');await page.waitForSelector('.tag-filter');
 assert.deepEqual(await pressed(page),['area:gone']);
 assert.deepEqual(await visible(page),[]);
 assert.match(await page.locator('.tag-filter-count').textContent(),/^Showing 0 of /);
 await page.click('.tag-filter-option[data-tag="area:gone"]');
 assert.deepEqual(await visible(page),all);
 assert.equal(await page.locator('.tag-filter-option[data-tag="area:gone"]').count(),0,'an unknown tag goes once deselected');

 // Phone width: the bar wraps instead of scrolling the page sideways.
 await page.setViewportSize({width:375,height:812});
 assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'no horizontal page scroll');
 await page.screenshot({path:path.join(out,'phone.png')});

 assert.deepEqual(errors,[]);
 console.log('scenario-tags: ok');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
