const fs=require('node:fs');

const PASSING=new Set(['Killed','Timeout','CompileError','Ignored']);
const norm=p=>p.replaceAll('\\','/');

function testIds(report){
  return Object.values(report.testFiles??{}).flatMap(f=>(f.tests??[]).map(t=>t.id));
}

function sentinelLine(source){
  const i=(source??'').split(/\r?\n/).findIndex(l=>l.includes('stryker-sentinel'));
  return i<0?null:i+1;
}

// Flattens a report to key -> { status, sentinel, label }, plus a list of file-level problems.
function mutants(report,run,failures){
  const map=new Map();
  let anySentinel=false;
  for(const [file,f] of Object.entries(report.files??{})){
    const line=sentinelLine(f.source);
    if(line!==null)anySentinel=true;
    let onSentinel=0;
    for(const m of f.mutants??[]){
      const {line:l,column}=m.location.start;
      const label=`${norm(file)}:${l} ${m.mutatorName}`;
      map.set(`${norm(file)}|${l}|${column}|${m.mutatorName}|${m.replacement}`,{status:m.status,label});
      if(l===line){
        onSentinel++;
        if(m.status!=='Survived')failures.push(`${run} run: sentinel mutant ${label} is ${m.status}, expected Survived`);
      }else if(!PASSING.has(m.status)){
        failures.push(`${run} run: mutant ${label} is ${m.status}`);
      }
    }
    if(line!==null&&onSentinel===0)failures.push(`${run} run: no mutant on the sentinel line ${norm(file)}:${line}`);
  }
  if(!anySentinel)failures.push(`${run} run: no sentinel line found in any file`);
  if(map.size===0)failures.push(`${run} run: no mutants`);
  return map;
}

function check(scenarioReport,stepReport){
  const failures=[];
  const scenarioIds=testIds(scenarioReport);
  const stepIds=testIds(stepReport);
  if(scenarioIds.length===0)failures.push('scenario run: no tests');
  if(stepIds.length===0)failures.push('step run: no tests');
  for(const id of scenarioIds)if(id.includes(':'))failures.push(`scenario run: test id ${id} looks like a step uid`);
  for(const id of stepIds)if(!id.includes(':'))failures.push(`step run: test id ${id} looks like a scenario uid`);

  const fromScenario=new Set(scenarioIds);
  const fromStep=new Set(stepIds.filter(id=>id.includes(':')).map(id=>id.slice(0,id.lastIndexOf(':'))));
  for(const id of fromScenario)if(!fromStep.has(id)&&scenarioIds.length&&stepIds.length)failures.push(`scenario id ${id} has no steps in the step run`);
  for(const id of fromStep)if(!fromScenario.has(id)&&scenarioIds.length&&stepIds.length)failures.push(`scenario id ${id} appears only in the step run`);

  const a=mutants(scenarioReport,'scenario',failures);
  const b=mutants(stepReport,'step',failures);
  for(const [key,m] of a){
    const other=b.get(key);
    if(!other)failures.push(`mutant ${m.label} is missing from the step run`);
    else if(other.status!==m.status)failures.push(`mutant ${m.label} is ${m.status} in the scenario run but ${other.status} in the step run`);
  }
  for(const [key,m] of b)if(!a.has(key))failures.push(`mutant ${m.label} is missing from the scenario run`);
  return failures;
}

function main(argv){
  const [scenarioPath,stepPath]=argv;
  if(!scenarioPath||!stepPath){
    console.error('usage: check-report.cjs <scenario-report> <step-report>');
    return 1;
  }
  const load=p=>{
    try{return JSON.parse(fs.readFileSync(p,'utf8'));}
    catch(e){console.error(`cannot read report ${p}: ${e.message}`);return null;}
  };
  const s=load(scenarioPath),t=load(stepPath);
  if(!s||!t)return 1;
  const failures=check(s,t);
  if(failures.length){
    for(const f of failures)console.error(`FAIL: ${f}`);
    return 1;
  }
  console.log(`ok: ${testIds(s).length} scenario tests, ${testIds(t).length} step tests, mutant statuses match across runs`);
  return 0;
}

module.exports={check};
if(require.main===module)process.exitCode=main(process.argv.slice(2));
