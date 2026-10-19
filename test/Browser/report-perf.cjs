// Load-time profile of the HTML report at scale.
//
//   node test/Browser/report-perf.cjs /absolute/path/to/raun-report.html [--scenarios 300] [--runs 3] [--out dir] [--template file]
//
// The sample's scenarios are cloned (with fresh ids) until the run holds --scenarios of them, then
// rendered with the current template (or --template). Each run loads the page in headless Chromium and waits until
// it has been quiet for a second. It reports time to settled (the end of the last long frame or
// task), Chromium's script/layout/style totals, and a CPU profile summed by function.
const fs=require('node:fs'),path=require('node:path');
const {pathToFileURL}=require('node:url'),{chromium}=require('playwright');

const args=process.argv.slice(2),option=(name,fallback)=>{const at=args.indexOf('--'+name);return at>=0 ? args[at+1] : fallback;};
const source=args[0];
if(!source || source.startsWith('--')){console.error('usage: report-perf.cjs <raun-report.html> [--scenarios 300] [--runs 3] [--out dir] [--template file]');process.exit(2);}
const target=Number(option('scenarios',300)),runs=Number(option('runs',3));
const root=path.resolve(__dirname,'../..'),out=path.resolve(option('out',path.join(root,'artifacts/report-perf')));
fs.mkdirSync(out,{recursive:true});

const template=fs.readFileSync(path.resolve(option('template',path.join(root,'src/Raun/Reporting/Html/report-template.html'))),'utf8');
const model=JSON.parse(fs.readFileSync(source,'utf8').match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const scenarios=[];
for(let copy=0;scenarios.length<target;copy++)for(const sc of model.scenarios){
  if(scenarios.length>=target)break;
  scenarios.push(copy===0 ? sc : {...sc,scenarioId:sc.scenarioId+'~'+copy,displayName:sc.displayName+' #'+copy});
}
const file=path.join(out,'report-'+target+'.html');
fs.writeFileSync(file,template.replace('/*__RAUN_REPORT_JSON__*/',JSON.stringify({...model,scenarios}).replaceAll('<','\\u003C')));

// Installed before the report script: records the end of every long frame and long task.
const observe=()=>{
  window.__perf={lastBusy:0,frames:0};
  const record=entry=>{window.__perf.lastBusy=Math.max(window.__perf.lastBusy,entry.startTime+entry.duration);window.__perf.frames++;};
  for(const type of ['long-animation-frame','longtask'])try{new PerformanceObserver(list=>list.getEntries().forEach(record)).observe({type,buffered:true});}catch{}
};

function summarize(profile){
  const nodes=new Map(profile.nodes.map(node=>[node.id,node])),self=new Map(),parent=new Map();
  for(const node of profile.nodes)for(const child of node.children || [])parent.set(child,node.id);
  const deltas=profile.timeDeltas,samples=profile.samples,total=new Map();
  const key=node=>{const f=node.callFrame;return (f.functionName || '(anonymous)')+(f.url ? ':'+(f.lineNumber+1) : '');};
  for(let i=0;i<samples.length;i++){
    const ms=(deltas[i+1] ?? 0)/1000;let node=nodes.get(samples[i]);
    self.set(key(node),(self.get(key(node)) || 0)+ms);
    // Inclusive time: count each distinct function once per sample, however deep it recurses.
    const seen=new Set();
    for(let id=samples[i];id!==undefined;id=parent.get(id)){const name=key(nodes.get(id));if(!seen.has(name)){seen.add(name);total.set(name,(total.get(name) || 0)+ms);}}
  }
  return {self,total};
}

const median=values=>{const sorted=[...values].sort((a,b)=>a-b);return sorted[Math.floor(sorted.length/2)];};
const fmt=ms=>ms.toFixed(0).padStart(6)+' ms';

(async()=>{
  const browser=await chromium.launch({headless:true,...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ? {executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE} : {})});
  const results=[];
  try{
    for(let run=0;run<runs;run++){
      const page=await browser.newPage({viewport:{width:1440,height:900}});
      await page.addInitScript(observe);
      const cdp=await page.context().newCDPSession(page);
      await cdp.send('Performance.enable');await cdp.send('Profiler.enable');
      await cdp.send('Profiler.setSamplingInterval',{interval:100});await cdp.send('Profiler.start');
      await page.goto(pathToFileURL(file).href,{waitUntil:'load'});
      // Settled once a full second passes with no new long frame or task.
      let last=-1;
      for(;;){
        await page.waitForTimeout(1000);
        const now=await page.evaluate(()=>window.__perf.lastBusy);
        if(now===last)break;last=now;
      }
      const {profile}=await cdp.send('Profiler.stop');
      const metrics=Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map(m=>[m.name,m.value]));
      const timing=await page.evaluate(()=>({settled:window.__perf.lastBusy,domContentLoaded:performance.getEntriesByType('navigation')[0].domContentLoadedEventEnd,cards:document.querySelectorAll('.card').length,nodes:document.getElementsByTagName('*').length}));
      results.push({timing,metrics,profile:summarize(profile)});
      fs.writeFileSync(path.join(out,'profile-'+run+'.cpuprofile'),JSON.stringify(profile));
      await page.close();
    }
  }finally{await browser.close();}

  const pick=f=>median(results.map(f));
  const {cards,nodes}=results[0].timing;
  console.log(`report: ${cards} scenario cards, ${nodes} elements, ${runs} runs (medians)`);
  console.log(`  time to settled      ${fmt(pick(r=>Math.max(r.timing.settled,r.timing.domContentLoaded)))}`);
  console.log(`  DOMContentLoaded     ${fmt(pick(r=>r.timing.domContentLoaded))}`);
  console.log(`  script               ${fmt(pick(r=>r.metrics.ScriptDuration*1000))}`);
  console.log(`  layout               ${fmt(pick(r=>r.metrics.LayoutDuration*1000))}  (${pick(r=>r.metrics.LayoutCount)} layouts)`);
  console.log(`  style recalc         ${fmt(pick(r=>r.metrics.RecalcStyleDuration*1000))}  (${pick(r=>r.metrics.RecalcStyleCount)} recalcs)`);
  const names=new Set(results.flatMap(r=>[...r.profile.total.keys()]));
  const rows=[...names].map(name=>({name,total:pick(r=>r.profile.total.get(name) || 0),self:pick(r=>r.profile.self.get(name) || 0)}));
  const report=name=>!/^\((root|idle|program)\)/.test(name);
  console.log('  inclusive time by function (top 15):');
  for(const row of rows.filter(r=>report(r.name)).sort((a,b)=>b.total-a.total).slice(0,15))console.log(`    ${fmt(row.total)}  ${row.name}`);
  console.log('  self time by function (top 10):');
  for(const row of rows.sort((a,b)=>b.self-a.self).slice(0,10))console.log(`    ${fmt(row.self)}  ${row.name}`);
})().catch(error=>{console.error(error);process.exit(1);});
