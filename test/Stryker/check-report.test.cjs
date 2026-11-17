const {test}=require('node:test');
const assert=require('node:assert/strict');
const path=require('node:path');
const {spawnSync}=require('node:child_process');
const {check}=require('./check-report.cjs');

const SOURCE='class A {\n  bool Ok() => true; // stryker-sentinel\n  bool Other() => true;\n}\n';
const mutant=(line,status,extra={})=>({mutatorName:'Boolean',replacement:'false',status,location:{start:{line,column:5}},...extra});
const report=({tests,mutants,file='a.cs',source=SOURCE})=>({
  files:{[file]:{source,mutants}},
  testFiles:{'t.cs':{tests:tests.map(id=>({id,name:'n'}))}},
});
const healthyMutants=()=>[mutant(2,'Survived'),mutant(3,'Killed')];
const scenario=(o={})=>report({tests:['s1','s2'],mutants:healthyMutants(),...o});
const step=(o={})=>report({tests:['s1:a','s1:b','s2:a'],mutants:healthyMutants(),...o});

test('passes for a healthy pair of runs',()=>assert.deepEqual(check(scenario(),step()),[]));
test('fails when the scenario run lists step uids',()=>{
  const f=check(scenario({tests:['s1:a','s2']}),step());
  assert.ok(f.some(m=>/scenario run: test id s1:a looks like a step uid/.test(m)),f.join('\n'));
});
test('fails when the step run lists scenario uids',()=>{
  const f=check(scenario(),step({tests:['s1:a','s2']}));
  assert.ok(f.some(m=>/step run: test id s2 looks like a scenario uid/.test(m)),f.join('\n'));
});
test('fails when scenario ids differ between runs',()=>{
  const f=check(scenario(),step({tests:['s1:a','s3:a']}));
  assert.ok(f.some(m=>m.includes('s3')),f.join('\n'));
});
test('fails when a non-sentinel mutant survives',()=>{
  const m=[mutant(2,'Survived'),mutant(3,'Survived')];
  const f=check(scenario({mutants:m}),step({mutants:m}));
  assert.ok(f.some(x=>x.includes('a.cs')&&x.includes('3')&&x.includes('Boolean')),f.join('\n'));
});
test('fails when the sentinel mutant is killed',()=>{
  const bad=[mutant(2,'Killed'),mutant(3,'Killed')];
  const named=(f,run)=>f.some(x=>x.startsWith(`${run} run: sentinel mutant a.cs:2 Boolean is Killed`));
  const both=check(scenario({mutants:bad}),step({mutants:bad}));
  assert.ok(named(both,'scenario')&&named(both,'step'),both.join('\n'));
  const first=check(scenario({mutants:bad}),step());
  assert.ok(named(first,'scenario'),first.join('\n'));
  const second=check(scenario(),step({mutants:bad}));
  assert.ok(named(second,'step'),second.join('\n'));
});
test('fails when the sentinel mutant is not covered',()=>{
  const bad=[mutant(2,'NoCoverage'),mutant(3,'Killed')];
  const f=check(scenario({mutants:bad}),step({mutants:bad}));
  assert.ok(f.some(x=>/sentinel mutant a\.cs:2 Boolean is NoCoverage/.test(x)),f.join('\n'));
});
test('fails when no sentinel line exists',()=>{
  const source='class A {}\n';
  const m=[mutant(1,'Killed')];
  const f=check(scenario({source,mutants:m}),step({source,mutants:m}));
  assert.ok(f.some(x=>/sentinel/.test(x)),f.join('\n'));
});
test('fails when a mutant status differs between runs',()=>{
  const f=check(scenario(),step({mutants:[mutant(2,'Survived'),mutant(3,'Timeout')]}));
  assert.ok(f.some(x=>x.includes('a.cs')&&x.includes('3')),f.join('\n'));
});
test('fails when a mutant exists in only one run',()=>{
  const f=check(scenario(),step({mutants:[...healthyMutants(),mutant(3,'Killed',{mutatorName:'Other'})]}));
  assert.ok(f.some(x=>x.includes('Other')),f.join('\n'));
});
test('fails on zero mutants',()=>{
  const f=check(scenario({mutants:[]}),step({mutants:[]}));
  assert.ok(f.includes('scenario run: no mutants'),f.join('\n'));
  assert.ok(f.includes('step run: no mutants'),f.join('\n'));
});
test('fails on zero tests',()=>{
  const a=check(scenario({tests:[]}),step());
  assert.ok(a.includes('scenario run: no tests'),a.join('\n'));
  const b=check(scenario(),step({tests:[]}));
  assert.ok(b.includes('step run: no tests'),b.join('\n'));
});
test('pairs mutants across path separators',()=>{
  assert.deepEqual(check(scenario({file:'a\\b.cs'}),step({file:'a/b.cs'})),[]);
});
test('cli exits 1 naming a missing report path',()=>{
  const missing=path.join(__dirname,'no-such-report.json');
  const r=spawnSync(process.execPath,[path.join(__dirname,'check-report.cjs'),missing,missing],{encoding:'utf8'});
  assert.equal(r.status,1);
  assert.ok(r.stderr.includes(missing),r.stderr);
});
