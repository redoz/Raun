const fs = require('node:fs');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');
const root = process.env.RAUN_MATRIX_ROOT || path.resolve(__dirname, '../..');
if (!process.argv[2]) throw new Error('Usage: node test/Browser/resource-matrix.cjs <sample-report.html> [output-directory]');
const out = path.resolve(process.argv[3] || path.join(root, 'artifacts/resource-matrix'));
fs.mkdirSync(out, { recursive: true });
const source = fs.readFileSync(path.join(root, 'src/Raun/Reporting/Html/report-template.html'), 'utf8');
const sample = fs.readFileSync(process.argv[2], 'utf8');
const payload = sample.match(/<script id="model" type="application\/json">([\s\S]*?)<\/script>/)[1];
const model = JSON.parse(payload);
// Use the scenario with patient edits, relationships, and logs; cases may be registered first.
const matrixScenarioIndex=model.scenarios.findIndex(scenario=>scenario.displayName==='out-of-town patients get a travel reminder');
assert.ok(matrixScenarioIndex>=0,'The canonical sample includes the travel reminder scenario');
const domainEffects=model.scenarios.flatMap(scenario=>scenario.steps.flatMap(step=>step.effects));
for(const [action,verb] of [['Register','Create'],['Hold','Edit'],['Booked','Create'],['Rescheduled','Create'],['Cancel','Delete']]){
  assert.ok(domainEffects.some(effect=>effect.actionName===action && effect.verb===verb),'The real sample demonstrates '+action+' with '+verb+' semantics');
}
for(const scenario of model.scenarios)for(const step of scenario.steps){
  const booked=step.effects.find(effect=>effect.actionName==='Booked');
  if(!booked)continue;
  assert.ok(scenario.references.some(edge=>edge.subjectId===booked.resourceId && edge.kind==='Reference' && edge.targetType==='Patient'),'Booked retains the patient reference');
  assert.ok(scenario.references.some(edge=>edge.subjectId===booked.resourceId && edge.kind==='Consume' && edge.targetType==='Slot'),'Booked consumes the chosen slot');
}

const actionSample=model.scenarios.find(scenario=>scenario.displayName==='registered patient books by ID');
assert.ok(actionSample,'The sample books a registered patient through an opaque ID');
const patientResources=actionSample.resources.filter(resource=>resource.type==='Patient');
assert.equal(patientResources.length,1,'Registry row, domain patient and string key share one canonical resource');
const patientResource=patientResources[0];
assert.equal(patientResource.key,'pat-42');
assert.ok(!actionSample.resources.some(resource=>resource.type==='RegistryPatient'),'The external row does not create a separate identity');
const patientEffects=actionSample.steps.flatMap(step=>step.effects).filter(effect=>effect.resourceId===patientResource.id);
assert.ok(patientEffects.some(effect=>effect.data && JSON.parse(effect.data).FullName==='Jane'),'The original registry representation is captured');
assert.ok(patientEffects.some(effect=>effect.data && JSON.parse(effect.data).Name==='Jane'),'The domain representation keeps its own evidence');
const bookById=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Booked'));
const keyReference=bookById.effects.find(effect=>effect.resourceId===patientResource.id);
assert.equal(keyReference.verb,'Reference');
assert.equal(keyReference.label,'Jane');
assert.equal(keyReference.data,null,'A typed relationship through a string key does not borrow the registry snapshot');
assert.ok(bookById.displayName.includes('Jane'),'Booking by key resolves the earlier readable name');
const booking=bookById.effects.find(effect=>effect.actionName==='Booked');
const registered=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Register'));
const approved=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Approve'));
const available=actionSample.steps.find(step=>step.effects.some(effect=>effect.type==='Slot' && effect.verb==='Create'));
const slotResource=actionSample.resources.find(resource=>resource.type==='Slot');
for(const [step,mentions] of [[registered,[['Jane',patientResource.id],['pat-42',patientResource.id]]],[approved,[['Jane',patientResource.id]]],[available,[['slot 01',slotResource.id]]],[bookById,[['Jane',patientResource.id],['slot 01',slotResource.id]]]]){
  for(const [text,resourceId] of mentions){
    assert.ok(step.titleMessage.parts.some(part=>part.text===text && part.resourceId===resourceId),'Title text '+text+' is a reference to the correct resource in '+step.displayName);
  }
}

assert.ok(actionSample.references.some(edge=>edge.subjectId===booking.resourceId && edge.targetId===patientResource.id && edge.kind==='Reference'));
const audit=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Audited'));
assert.ok(audit.displayName.includes('Jane'),'A key-only generated title reuses the patient name');
assert.ok(audit.effects.some(effect=>effect.resourceId===patientResource.id && effect.label==='Jane' && effect.data==null),'Key-only effects have names without borrowed snapshots');
assert.ok(actionSample.steps.some(step=>step.effects.some(effect=>effect.actionName==='Approve' && effect.verb==='Edit')));

const allSteps=model.scenarios.flatMap(scenario=>scenario.steps);
assert.ok(allSteps.some(step=>step.displayName==='Given patient Jane exists'),'An omitted optional city selects the shorter title');
assert.ok(allSteps.some(step=>step.displayName==='Given patient Jane exists in Oslo'),'A supplied optional city selects the more specific title');
assert.ok(allSteps.some(step=>step.displayName==='Then Jane has an appointment in slot 01'),'Resource-valued member paths keep the readable patient and slot labels');
assert.ok(allSteps.some(step=>step.displayName==='Then the import should contain 2 users (actual 02)'),'Scalar member formatting remains available for ordinary values');
for(const [scenarioName,creates,expectedAssertion] of [['a booking either lands, waits, or is refused',true,'Then Ada has an appointment in slot 01'],['a patient whose booking waits is put on the waitlist',false,'Then Wren is 3 on the waitlist'],['a patient can decline the waitlist',false,'Then Wren is told the patient declined the waitlist']]){
  const scenario=model.scenarios.find(item=>item.displayName===scenarioName);
  assert.ok(scenario,'The sample demonstrates '+scenarioName);
  const assertions=scenario.steps.filter(step=>step.phase==='Then' && !step.isLifecycle);
  assert.deepEqual(assertions.filter(step=>step.status==='passed').map(step=>step.displayName),[expectedAssertion],'The patient choice selects its expected business outcome');
  assert.equal(assertions.filter(step=>step.status==='not-taken').length,2,'The other two business outcomes are not taken');
  const attempt=scenario.steps.find(step=>step.displayName.startsWith('When trying to book'));
  const created=attempt.effects.filter(effect=>effect.type==='Appointment' && effect.verb==='Create');
  assert.equal(created.length,creates?1:0,'Only successful conditional booking creates an appointment');
  assert.equal(scenario.references.length,creates?2:0,'Only successful conditional booking records lineage');
  if(creates){
    const appointment=created[0];
    assert.equal(appointment.actionName,'Booked','Conditional booking uses the same domain action as direct booking');
    for(const [type,kind] of [['Patient','Reference'],['Slot','Consume']]){
      const target=scenario.resources.find(resource=>resource.type===type);
      assert.ok(scenario.references.some(edge=>edge.subjectId===appointment.resourceId && edge.targetId===target.id && edge.kind===kind),'Conditional booking records the correct '+kind+' endpoint');
    }
  }
}

fs.writeFileSync(path.join(out, 'resource-matrix-preview.html'), source.replace('/*__RAUN_REPORT_JSON__*/', payload));
const hasResources = scenario => scenario.resources.length || scenario.steps.some(step => step.effects.length);
const renderFixture = (name, data) => {
  const file = path.join(out, name + '.html');
  fs.writeFileSync(file, source.replace('/*__RAUN_REPORT_JSON__*/', JSON.stringify(data).replaceAll('<', '\\u003C')));
  return pathToFileURL(file).href;
};
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(out, 'resource-matrix-preview.html')).href);
    await page.evaluate(() => document.fonts.ready);
    assert.deepEqual(errors, []);
    const registryCard=page.locator('.card').filter({hasText:'registered patient books by ID'});
    await registryCard.locator('.card-head').click();
    const patientHeader=registryCard.getByRole('button',{name:/^Jane, Patient\./});
    assert.equal(await patientHeader.count(),1,'The patient column uses LabelFor rather than its opaque key');
    assert.ok((await patientHeader.getAttribute('title')).includes('Patient: pat-42'),'The column tooltip retains the stable identity');
    const registryLinks=registryCard.locator('.dn .message-resource');
    assert.equal(await registryLinks.count(),8,'All eight resource mentions in the real registry journey render as interactive references');
    const linkedNames=await registryLinks.allTextContents();
    assert.ok(linkedNames.includes('pat-42') && linkedNames.includes('slot 01'),'ID and slot text render as resource buttons too');
    for(const colorScheme of ['light','dark']){
      await page.emulateMedia({colorScheme});
      const mentionColors=await registryLinks.evaluateAll(links=>links.map(link=>{
        const column=Array.from(link.closest('.card').querySelectorAll('.matrix-resource')).find(header=>header.dataset.resourceColumn===link.dataset.messageResource);
        const style=getComputedStyle(link);
        const normal=getComputedStyle(link.closest('.dn')).color;
        const muted=getComputedStyle(link.closest('.card').querySelector('.card-head .dur')).color;
        const phase=getComputedStyle(link.closest('.dn').querySelector('.phase-given,.phase-when,.phase-then'));
        const columnColor=getComputedStyle(column).borderTopColor;
        const rgb=color=>color.match(/[0-9.]+/g).slice(0,3).map(Number).map(value=>color.startsWith('color(srgb')?value*255:value);
        const distance=(a,b)=>rgb(a).reduce((sum,value,index)=>sum+Math.abs(value-rgb(b)[index]),0);
        return {text:style.color,normal,underline:style.textDecorationColor,tintDistance:distance(style.textDecorationColor,muted),resourceDistance:distance(columnColor,muted),line:style.textDecorationLine,pattern:style.textDecorationStyle,offset:style.textUnderlineOffset,phaseText:phase.color,phaseLine:phase.textDecorationLine,thickness:style.textDecorationThickness,border:style.borderBottomWidth};
      }));
      assert.ok(mentionColors.every(color=>color.text===color.normal && color.tintDistance>0 && color.tintDistance<color.resourceDistance*.8),'Title references use normal text and subdued resource-colored underlines in '+colorScheme+' mode: '+JSON.stringify(mentionColors));
      assert.ok(mentionColors.every(color=>color.line==='underline' && color.pattern==='solid' && color.offset==='2px' && color.thickness==='1px' && color.border==='0px'),'Solid resource underlines sit at the intended text position without a button border');
      assert.ok(mentionColors.every(color=>color.phaseText!==color.normal && color.phaseLine==='none'),'Matching phase words use their phase color without decorations');
      assert.equal(new Set(mentionColors.map(color=>color.underline)).size,3,'Resources retain distinct muted underline colors');
      const logReferences=await registryCard.locator('.logs .message-resource').evaluateAll(links=>links.map(link=>{
        const title=Array.from(link.closest('.card').querySelectorAll('.dn .message-resource')).find(mention=>mention.dataset.messageResource===link.dataset.messageResource);
        const style=getComputedStyle(link),titleStyle=getComputedStyle(title);
        return {color:link.style.getPropertyValue('--resource-color'),titleColor:title.style.getPropertyValue('--resource-color'),underline:style.textDecorationColor,titleUnderline:titleStyle.textDecorationColor,line:style.textDecorationLine,pattern:style.textDecorationStyle};
      }));
      assert.ok(logReferences.length>0,'The registry journey has resource references in its logs');
      assert.ok(logReferences.every(link=>link.color && link.color===link.titleColor && link.underline===link.titleUnderline && link.line==='underline' && link.pattern==='solid'),'Log references use the same resource colors and solid underlines as titles in '+colorScheme+' mode');
    }
    await page.emulateMedia({colorScheme:'light'});

    const registrationId=registryLinks.filter({hasText:'pat-42'});
    await registrationId.click();
    assert.equal(await registryCard.locator('.dn .message-resource.hl').count(),5,'Clicking the registration ID highlights all five patient mentions across the journey');
    await registrationId.click();
    const columnGeometry=()=>registryCard.locator('.scenario-heading-row,.card-head,.matrix-resource,.matrix-resource small,.matrix-row').evaluateAll(nodes=>nodes.map(node=>{
      const rect=node.getBoundingClientRect();return {x:rect.x,y:rect.y,width:rect.width,height:rect.height};
    }));
    const unselectedGeometry=await columnGeometry();
    await patientHeader.click();
    assert.ok(await registryCard.locator('.matrix-reference').count()>0,'Column selection marks related resources');
    assert.deepEqual(await columnGeometry(),unselectedGeometry,'Selecting a resource column must not move headers, text or grid rows');
    await patientHeader.click();
    assert.deepEqual(await columnGeometry(),unselectedGeometry,'Clearing the selected column must keep the same geometry');

    await registryCard.locator('.card-head').click();

    assert.equal(await page.locator('.resource-events').count(),0,'The matrix replaces duplicate Resource events tables');
    assert.equal(await page.locator('.matrix-event').count(),model.scenarios.reduce((total,scenario)=>total+scenario.steps.reduce((count,step)=>count+step.effects.length,0),0),'Every recorded resource event remains inspectable in the matrix');
    assert.equal(await page.locator('.resource-matrix').count(), model.scenarios.filter(hasResources).length, 'Resource matrix is part of the default view');
    assert.equal(await page.locator('.flow-mode,.flow-dependencies,.actdiag').count(), 0, 'Default rows have resource columns without comparison controls or the dependency prototype');
    const collapsedScenario=page.locator('.card').nth(matrixScenarioIndex);
    assert.equal(await collapsedScenario.locator('.matrix-resource').first().isVisible(),false,'Resource headings hide with the collapsed scenario');
    await collapsedScenario.locator('.card-head').click();
    const alignedHeadings=await collapsedScenario.evaluate(card=>{
      const heading=card.querySelector('.card-head').getBoundingClientRect();
      return Array.from(card.querySelectorAll('.matrix-resource')).every(resource=>{
        const box=resource.getBoundingClientRect();return box.top<heading.bottom && box.bottom>heading.top;
      });
    });
    assert.ok(alignedHeadings,'Resource headings share the scenario heading row');
    const expandedBounds=await collapsedScenario.evaluate(async card=>{
      if(card.scenarioAnimation)await card.scenarioAnimation.finished;
      const box=card.getBoundingClientRect();
      const lastRow=card.querySelector('.matrix-row:last-child').getBoundingClientRect();
      const chevron=card.querySelector('.card-head .tw').getBoundingClientRect();
      const duration=card.querySelector('.card-head .dur').getBoundingClientRect();
      const firstColumn=card.querySelector('.matrix-resource').getBoundingClientRect();
      return {bottomGap:box.bottom-lastRow.bottom,leftInset:chevron.left-box.left,durationGap:firstColumn.left-duration.right};
    });
    assert.ok(Math.abs(expandedBounds.bottomGap)<=1,'Expanded border ends at the last grid row, without an empty footer: '+JSON.stringify(expandedBounds));
    assert.ok(expandedBounds.leftInset>=8,'Scenario chevron has space inside the left border: '+JSON.stringify(expandedBounds));
    assert.ok(expandedBounds.durationGap>=12,'Scenario duration has space before the resource matrix: '+JSON.stringify(expandedBounds));

    assert.equal(await collapsedScenario.locator('.matrix-steps').count(),0,'No separate Steps header row');
    await collapsedScenario.locator('.card-head').click();
    assert.equal(await collapsedScenario.locator('.matrix-resource').first().isVisible(),false,'Resource headings hide again on collapse');
    await collapsedScenario.evaluate(async card=>{if(card.scenarioAnimation)await card.scenarioAnimation.finished;});
    const motion=await collapsedScenario.evaluate(async card=>{
      card.querySelector('.card-head').click();
      const opening=card.scenarioAnimation,animatedOpen=Boolean(opening);
      if(opening)await opening.finished;
      const expandedHeight=card.querySelector('.scenario-reveal').getBoundingClientRect().height;
      card.querySelector('.card-head').click();
      const closing=card.scenarioAnimation,animatedClose=Boolean(closing);
      if(closing)await closing.finished;
      return {animatedOpen,animatedClose,expandedHeight,closedHeight:card.querySelector('.scenario-reveal').getBoundingClientRect().height};
    });
    assert.ok(motion.animatedOpen && motion.animatedClose && motion.expandedHeight>0 && motion.closedHeight===0,'Scenario disclosure animates both directions and settles to natural height: '+JSON.stringify(motion));
    await page.emulateMedia({reducedMotion:'reduce'});
    const reduced=await collapsedScenario.evaluate(card=>{
      card.querySelector('.card-head').click();const animation=Boolean(card.scenarioAnimation);
      const expandedHeight=card.querySelector('.scenario-reveal').getBoundingClientRect().height;
      card.querySelector('.card-head').click();
      return {animation,expandedHeight,closedHeight:card.querySelector('.scenario-reveal').getBoundingClientRect().height};
    });
    assert.ok(!reduced.animation && reduced.expandedHeight>0 && reduced.closedHeight===0,'Reduced-motion preference skips disclosure animation');
    await page.emulateMedia({reducedMotion:'no-preference'});
    const interrupted=await collapsedScenario.evaluate(async card=>{
      const head=card.querySelector('.card-head');head.click();
      await new Promise(resolve=>setTimeout(resolve,30));head.click();
      await new Promise(resolve=>setTimeout(resolve,20));head.click();
      if(card.scenarioAnimation)await card.scenarioAnimation.finished;
      const settled=!card.classList.contains('collapsed') && !card.classList.contains('is-animating') && card.querySelector('.scenario-reveal').getBoundingClientRect().height>0;
      head.click();if(card.scenarioAnimation)await card.scenarioAnimation.finished;return settled;
    });
    assert.ok(interrupted,'Rapid disclosure toggles settle without a stale animation or clipped content');
    const scenarioPanel=page.locator('#report-view-tests');
    assert.equal(await scenarioPanel.evaluate(panel=>getComputedStyle(panel).overflowY),'scroll','Scenario scrollbar is always present');
    await page.setViewportSize({width:1600,height:420});
    const beforeScrollWidth=await scenarioPanel.evaluate(panel=>panel.clientWidth);
    await page.locator('.card').nth(matrixScenarioIndex).locator('.card-head').click();
    assert.equal(await scenarioPanel.evaluate(panel=>panel.clientWidth),beforeScrollWidth,'Expanding a scenario cannot shift the content width');
    await page.locator('.card').nth(matrixScenarioIndex).locator('.step-expander').first().click();
    assert.equal(await scenarioPanel.evaluate(panel=>panel.clientWidth),beforeScrollWidth,'Expanding step evidence cannot shift the content width');
    await page.locator('.card').nth(matrixScenarioIndex).locator('.step-expander').first().click();
    await page.locator('.card').nth(matrixScenarioIndex).locator('.card-head').click();
    await page.setViewportSize({width:1600,height:1100});
    for (const [index, scenario] of model.scenarios.entries()) {
      const card = page.locator('.card').nth(index);
      if ((await card.getAttribute('class')).includes('collapsed')) await card.locator('.card-head').click();
      assert.equal(await card.locator('.step-entry').count(), scenario.steps.length, 'Every existing step, guard, merge and lifecycle row remains');
      assert.equal(await card.locator('.scenario-drill').count(), 1, 'One default step list per scenario');
      const stepIds = await card.locator('.step-entry').evaluateAll(rows => rows.map(row => row.dataset.step));
      assert.deepEqual(stepIds, scenario.steps.map(step => step.stepId), 'Registration order is preserved');
      if (!hasResources(scenario)) assert.equal(await card.locator('.resource-matrix,.matrix-tools').count(), 0, 'Scenarios without resources retain their normal list');
    }
    const expandedGaps=await page.locator('.card:not(.collapsed)+.card:not(.collapsed)').evaluateAll(async cards=>{
      await Promise.all(cards.flatMap(card=>[card,card.previousElementSibling]).map(card=>card.scenarioAnimation?.finished.catch(()=>{})));
      return cards.map(card=>card.getBoundingClientRect().top-card.previousElementSibling.getBoundingClientRect().bottom);
    });
    assert.ok(expandedGaps.length>0 && expandedGaps.every(gap=>gap>=8),'Adjacent expanded scenarios have space between their background boxes');
    for(const theme of ['light','dark']){
      await page.evaluate(value=>document.documentElement.dataset.theme=value,theme);
      for(const [index,scenario] of model.scenarios.entries()){
        if(!hasResources(scenario) || scenario.resources.length>6)continue;
        const colors=await page.locator('.card').nth(index).locator('.matrix-resource').evaluateAll(nodes=>nodes.map(node=>getComputedStyle(node).borderTopColor));
        assert.equal(new Set(colors).size,colors.length,'Every resource retains a distinct color throughout the scenario, including same-type resources ('+theme+')');
      }
    }
    await page.evaluate(()=>document.documentElement.dataset.theme='light');
    const trackIssues=await page.evaluate(()=>{
      const issues=[];
      for(const matrix of document.querySelectorAll('.resource-matrix'))for(const header of matrix.closest('.card').querySelectorAll('.matrix-resource')){
        const cells=Array.from(matrix.querySelectorAll('.matrix-cell')).filter(cell=>cell.dataset.resourceColumn===header.dataset.resourceColumn);
        const events=cells.filter(cell=>cell.querySelector('.matrix-event'));
        if(!events.length)continue;
        const tracks=cells.filter(cell=>cell.classList.contains('matrix-track'));
        if(events.length===1){if(tracks.length)issues.push('Single event extends a line');continue;}
        if(!tracks.length){issues.push('Multiple events have no connecting line');continue;}
        const first=events[0],last=events.at(-1),firstEvent=first.querySelector('.matrix-event').getBoundingClientRect(),lastEvent=last.querySelector('.matrix-event:last-child').getBoundingClientRect();
        const top=first.getBoundingClientRect().top+parseFloat(getComputedStyle(first,'::before').top);
        const bottom=last.getBoundingClientRect().bottom-parseFloat(getComputedStyle(last,'::before').bottom);
        if(top<firstEvent.top-1)issues.push('Line extends above first event');
        if(bottom>lastEvent.bottom+1)issues.push('Line extends below last event');
      }return issues;
    });
    assert.deepEqual(trackIssues,[],'Resource lines stop at their first and last events');
    const first = page.locator('.card').nth(matrixScenarioIndex);
    const before = await first.locator('.matrix-scroll').evaluate(node => node.scrollHeight);
    const expander = first.locator('.step-entry.has-body:not(.step-lifecycle) .step-expander').first();
    await expander.click();
    assert.equal(await expander.getAttribute('aria-expanded'), 'true');
    const expandedRow=expander.locator('xpath=ancestor::*[contains(@class,"step-entry")][1]');
    assert.equal(await expandedRow.locator('.step-logs summary').count(),0,'Logs do not require a second disclosure control');
    assert.ok(await expandedRow.locator('.logs').isVisible(),'Logs are directly visible when the step expands');
    assert.ok(await expandedRow.evaluate(row=>Math.abs(row.querySelector('.logs').getBoundingClientRect().left-row.querySelector('.step-head>.scenario-status').getBoundingClientRect().left)<1),'Log text aligns vertically with the step status marker');
    assert.ok(await expandedRow.evaluate(row=>{
      const body=row.querySelector('.step-body').getBoundingClientRect(),columns=Array.from(row.querySelectorAll('.matrix-continuation'));
      return columns.length===row.closest('.card').querySelectorAll('.matrix-resource').length && columns.every(column=>{
        const box=column.getBoundingClientRect();return box.top<=body.top && box.bottom>=body.bottom && getComputedStyle(column).borderLeftStyle==='solid';
      });
    }),'Resource column borders continue alongside expanded step details');
    await expander.click();
    assert.ok(await first.locator('.matrix-scroll').evaluate(node => node.scrollHeight) <= before + 2, 'Collapsing evidence removes its space');
    assert.equal(await page.locator('.matrix-clear').count(),0,'No Clear selection control');
    const relation=page.locator('.matrix-connection').first();
    assert.ok(await relation.isVisible(),'Resource inputs connect to Create before selecting a resource');
    assert.match(await relation.getAttribute('aria-label'),/Reference|Consume/,'Connections describe the actual input action');
    assert.ok(await relation.evaluate(svg=>{
      const row=svg.closest('.matrix-row'),a=svg.getBoundingClientRect();
      return Math.abs(a.height-row.clientHeight)<1 && /^M [^ ]+ [^ ]+ H [^ ]+$/.test(svg.querySelector('path').getAttribute('d'));
    }),'Connections follow measured badge edges with straight routing behind resource labels');
    const connectionRow=relation.locator('xpath=..');
    await connectionRow.locator('.step-expander').click();
    await page.waitForTimeout(40);
    assert.ok(await relation.evaluate(svg=>{
      const row=svg.closest('.matrix-row'),bounds=row.getBoundingClientRect();
      const source=row.querySelector('.matrix-cell').getBoundingClientRect();
      const lanes=Array.from(row.querySelectorAll('.matrix-connection'),connection=>Number(connection.querySelector('path').getAttribute('d').match(/^M ([^ ]+) ([^ ]+)/)[2]));
      const center=lanes.reduce((sum,y)=>sum+y,0)/lanes.length;
      return Math.abs(svg.getBoundingClientRect().height-row.clientHeight)<1 &&
        Math.abs(center-((source.top+source.bottom)/2-bounds.top))<1;
    }),'Expanded logs preserve badge-aligned connections instead of moving them to the row midpoint');
    await connectionRow.locator('.step-expander').click();
    assert.ok(await page.locator('.matrix-continuation.matrix-track').count()>0,'Resource lifelines continue alongside logs');
    assert.ok(await page.locator('.matrix-continuation.matrix-track').first().evaluate(cell=>{
      const line=getComputedStyle(cell,'::before');return Math.abs(parseFloat(line.left)-cell.clientWidth/2)<1;
    }),'Lifelines stay centered on their resource column through logs');
    const lane = first.locator('.matrix-resource').first();
    await lane.focus(); await lane.press('Enter');
    assert.equal(await lane.getAttribute('aria-pressed'), 'true');
    assert.ok(await first.locator('.matrix-related').count() > 0, 'Keyboard resource selection highlights touching rows');
    assert.equal(await first.locator('.matrix-lineage').count(), 0, 'Resource relationships never add a separate row');
    assert.ok(await first.locator('.matrix-resource.matrix-reference').count() > 0, 'Resource selection marks related columns in the matrix');
    assert.match(await lane.getAttribute('title'), /references|consumes/, 'Relationship details remain on the matrix heading');
    await page.locator('body').dispatchEvent('click');
    assert.equal(await lane.getAttribute('aria-pressed'), 'false','Outside clicks clear resource selection');
    await lane.click();await lane.press('Escape');
    assert.equal(await lane.getAttribute('aria-pressed'), 'false');
    const event = first.locator('.matrix-event').first();
    await event.click();
    assert.equal(await event.locator('xpath=ancestor::*[contains(@class,"step-entry")][1]').getAttribute('class').then(value => value.includes('open')), false, 'Resource selection does not expand its step');
    assert.ok(await first.locator('.resource-hover').isVisible(), 'Event selection retains captured snapshot inspection');
    assert.ok(await first.locator('.resource-hover').evaluate(popup=>popup.matches(':popover-open')),'Resource inspector is promoted to the browser top layer');
    assert.ok(await first.locator('.resource-hover').evaluate(popup=>{
      const cover=document.createElement('div');cover.style.cssText='position:fixed;inset:0;z-index:2147483647;background:transparent';document.body.appendChild(cover);
      try{const box=popup.getBoundingClientRect();return popup.contains(document.elementFromPoint(box.left+box.width/2,box.top+box.height/2));}
      finally{cover.remove();}
    }),'Tooltip stays above scenario cells and other stacking contexts');
    await first.locator('.resource-hover').hover();await page.waitForTimeout(100);
    assert.ok(await first.locator('.resource-hover').isVisible(),'Moving into the inspector preserves access to its copy actions');
    await page.keyboard.press('Escape');
    const editEffect=model.scenarios[matrixScenarioIndex].steps.flatMap(step=>step.effects).find(effect=>effect.verb==='Edit');
    assert.ok(editEffect && editEffect.data,'Sample contains a captured edit snapshot');
    await first.getByRole('button',{name:/^Edit Patient:.*Show evidence/}).click();
    assert.equal(await page.evaluate(()=>document.activeElement?.getAttribute('data-resource-data')),editEffect.data,'Matrix action focuses its event snapshot, not a title reference');
    await page.keyboard.press('Escape');
    const open = first.locator('.step-entry.open .step-expander');
    while(await open.count())await open.first().click();
    await page.locator('body').dispatchEvent('click');
    await first.screenshot({ path: path.join(out, 'resource-matrix-desktop.png') });
    const usersIndex=model.scenarios.findIndex(scenario=>scenario.displayName==='bulk user import');
    await page.locator('.card').nth(usersIndex).screenshot({path:path.join(out,'resource-matrix-colors.png')});
    const busyIndex = model.scenarios.findIndex(scenario => scenario.displayName === 'patient reschedules to a later slot');
    await page.locator('.card').nth(busyIndex).screenshot({ path: path.join(out, 'resource-matrix-resources.png') });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile document stays within the viewport');
    await first.scrollIntoViewIfNeeded();
    await first.locator('.matrix-scroll').evaluate(node => { node.scrollLeft = node.scrollWidth; });
    assert.ok(await first.locator('.matrix-resource').last().evaluate(node => { const box = node.getBoundingClientRect(); return box.right > 0 && box.left < innerWidth; }), 'Resource columns can be reached on mobile');
    assert.ok(await first.evaluate(card=>{
      const heading=Array.from(card.querySelectorAll('.matrix-resource')).at(-1);
      const cell=Array.from(card.querySelectorAll('.matrix-cell')).find(node=>node.dataset.resourceColumn===heading.dataset.resourceColumn);
      const a=heading.getBoundingClientRect(),b=cell.getBoundingClientRect();return Math.abs(a.left-b.left)<1 && Math.abs(a.width-b.width)<1;
    }),'Resource headings stay aligned with cells when scrolling horizontally');
    await page.screenshot({ path: path.join(out, 'resource-matrix-mobile.png') });
    const fixture = structuredClone(model), failed = fixture.scenarios[busyIndex];
    fixture.scenarios = [failed]; failed.scenarioId = 'matrix-failure-fixture'; failed.displayName += ' (failure fixture)'; failed.status = 'failed';
    const failedStep = failed.steps.find(step => !step.isLifecycle && step.phase === 'Then');
    failedStep.status = 'failed'; failedStep.exception = 'Matrix failure fixture: expected the appointment to use the later slot.';
    fixture.summary = { ...fixture.summary, passed: 0, failed: 1, skipped: 0 };
    await page.setViewportSize({ width: 1600, height: 1100 });
    await page.goto(renderFixture('resource-matrix-failure', fixture));
    assert.ok(await page.locator('.step-entry.status-failed.open .exc').isVisible(), 'Failed evidence still opens automatically');
    await page.locator('.card').screenshot({ path: path.join(out, 'resource-matrix-failure.png') });
    const stress = structuredClone(fixture), crowded = stress.scenarios[0], seed = crowded.resources[0], arrange = crowded.steps.find(step => !step.isLifecycle);
    for (let index = 0; index < 8; index++) {
      const resource = { ...seed, id: 'matrix-fixture-' + index, key: 'fixture-' + index, events: [] };
      crowded.resources.push(resource);
      arrange.effects.push({ verb: 'Create', type: seed.type, key: resource.key, resourceId: resource.id, offsetMs: 0, data: null });
    }
    crowded.displayName = 'Many resources (matrix fixture)';
    await page.goto(renderFixture('resource-matrix-crowded', stress));
    const groupedCount = await page.locator('.matrix-resource').count();
    await page.getByRole('button', { name: /Show individual resources/ }).click();
    assert.equal(await page.locator('.matrix-resource').count(), crowded.resources.length, 'All resource identities remain available');
    const identityColors=await page.locator('.matrix-resource').evaluateAll(nodes=>nodes.map(node=>getComputedStyle(node).borderTopColor));
    assert.equal(new Set(identityColors).size,crowded.resources.length,'Resource colors do not wrap when the palette exceeds eight entries');
    const beforeGrouping=await page.locator('.matrix-resource').evaluateAll(nodes=>Object.fromEntries(nodes.map(node=>[node.dataset.resourceColumn,getComputedStyle(node).borderTopColor])));
    await page.getByRole('button', { name: 'Group resources by type' }).click();
    assert.equal(await page.locator('.matrix-resource').count(), groupedCount);
    await page.getByRole('button',{name:/Show individual resources/}).click();
    const afterGrouping=await page.locator('.matrix-resource').evaluateAll(nodes=>Object.fromEntries(nodes.map(node=>[node.dataset.resourceColumn,getComputedStyle(node).borderTopColor])));
    assert.deepEqual(afterGrouping,beforeGrouping,'Grouping never reallocates resource colors');
    const bundleFixture=structuredClone(model),bundleScenario=bundleFixture.scenarios[matrixScenarioIndex];bundleFixture.scenarios=[bundleScenario];
    const bundleRef=bundleScenario.references.find(reference=>reference.kind==='Reference');
    const bundleStep=bundleScenario.steps.find(step=>step.effects.some(effect=>effect.resourceId===bundleRef.subjectId && effect.verb==='Create'));
    const extraResource={...bundleScenario.resources[0],id:'bundle-extra',key:'Companion',label:'Companion'};
    bundleScenario.resources.push(extraResource);
    bundleStep.effects.push({...bundleStep.effects.find(effect=>effect.verb==='Reference'),resourceId:extraResource.id,key:extraResource.key,label:extraResource.label});
    bundleScenario.references.push({...bundleRef,targetId:extraResource.id,targetKey:extraResource.key});
    await page.goto(renderFixture('resource-matrix-three-inputs',bundleFixture));await page.locator('.card-head').click();
    assert.ok(await page.locator('.matrix-row').filter({has:page.locator('.matrix-connection')}).first().evaluate(row=>{
      const paths=Array.from(row.querySelectorAll('.matrix-connection path:not(.matrix-arrow)'));
      const lanes=paths.map(path=>Number(path.getAttribute('d').match(/^M ([^ ]+) ([^ ]+)/)[2])).sort((a,b)=>a-b);
      const bounds=row.getBoundingClientRect(),cell=row.querySelector('.matrix-cell').getBoundingClientRect();
      return lanes.length===3 && lanes[1]>lanes[0] && Math.abs((lanes[2]-lanes[1])-(lanes[1]-lanes[0]))<.1 &&
        Math.abs(lanes[1]-((cell.top+cell.bottom)/2-bounds.top))<1 && paths.every(path=>/^M [^ ]+ [^ ]+ H [^ ]+$/.test(path.getAttribute('d')));
    }),'Three connections form an evenly spaced straight bundle centered on the action row');
    for(const verb of ['Edit','Load']){
      const fixture=structuredClone(model),sc=fixture.scenarios[matrixScenarioIndex];fixture.scenarios=[sc];
      const ref=sc.references.find(reference=>reference.kind==='Reference');
      const producer=sc.steps.find(step=>step.effects.some(effect=>effect.resourceId===ref.subjectId && effect.verb==='Create'));
      producer.effects.find(effect=>effect.resourceId===ref.subjectId && effect.verb==='Create').verb=verb;
      await page.goto(renderFixture('resource-matrix-'+verb.toLowerCase()+'-relationship',fixture));await page.locator('.card-head').click();
      assert.equal(await page.locator('.matrix-connection').filter({hasText:''}).evaluateAll((nodes,verb)=>nodes.some(svg=>svg.getAttribute('aria-label').includes('Reference → '+verb)),verb),true,verb+' producers retain their recorded relationships');
    }
    const snapshots=structuredClone(model),snapshotScenario=snapshots.scenarios[matrixScenarioIndex];
    snapshots.scenarios=[snapshotScenario];
    const editStep=snapshotScenario.steps.find(step=>step.effects.some(effect=>effect.verb==='Edit'));
    const captured=editStep.effects.find(effect=>effect.verb==='Edit');
    editStep.titleMessage={text:editStep.displayName,parts:[{text:'Given '},{text:'the patient',resourceId:captured.resourceId,data:'{"City":"before-edit-title-fixture"}'}]};
    await page.goto(renderFixture('resource-matrix-snapshot-fixture',snapshots));
    await page.locator('.card-head').click();
    await page.getByRole('button',{name:/^Edit Patient:.*Show evidence/}).click();
    assert.equal(await page.evaluate(()=>document.activeElement?.getAttribute('data-resource-data')),captured.data,'Action inspection uses the event snapshot even when its title has a different snapshot');
    assert.deepEqual(errors, []);
    const repeated=structuredClone(model),repeatScenario=repeated.scenarios[matrixScenarioIndex];repeated.scenarios=[repeatScenario];
    const repeatStep=repeatScenario.steps.find(step=>step.effects.length && !step.isLifecycle),seedEffect=repeatStep.effects[0];
    repeatStep.logs=[];repeatStep.logMessages=[];repeatStep.notes=[];repeatStep.noteMessages=[];repeatStep.attachments=[];delete repeatStep.traceId;
    repeatStep.effects=[{...seedEffect,verb:'Read',data:'{"Version":1}',offsetMs:repeatStep.offsetMs+10},{...seedEffect,verb:'Read',data:'{"Version":2}',offsetMs:repeatStep.offsetMs+20}];
    await page.goto(renderFixture('resource-matrix-event-snapshots',repeated));await page.locator('.card-head').click();
    const onlyEffects=page.locator('.step-entry').filter({has:page.locator('.matrix-event')}).first();
    assert.equal(await onlyEffects.locator('.step-body,.step-expander').count(),0,'Resource events alone no longer create a redundant expansion panel');
    const repeatEvents=onlyEffects.locator('.matrix-event');assert.equal(await repeatEvents.count(),2,'Repeated actions retain both captured states');
    for(const [index,value] of [1,2].entries()){
      await repeatEvents.nth(index).focus();
      assert.equal(await page.evaluate(()=>document.activeElement.getAttribute('data-resource-data')),JSON.stringify({Version:value}));
      assert.ok((await page.locator('.resource-hover-event').textContent()).includes('+'+((index+1)*10).toFixed(1)+' ms'),'Inspector retains each event offset from step start');
      await page.keyboard.press('Escape');
    }
    const actionFixture=structuredClone(model),actionScenario=actionFixture.scenarios[matrixScenarioIndex];
    actionFixture.scenarios=[actionScenario];
    const actionStep=actionScenario.steps.find(step=>step.effects.length),actionEffect=actionStep.effects[0];
    actionEffect.verb='Edit';actionEffect.actionName='Approve <now>';actionEffect.label='Jane <Doe>';
    actionStep.effects.push({...actionEffect,actionName:'Audited',data:null});
    actionStep.effects.push({...actionEffect,actionName:'Approve <now>',data:null});
    await page.goto(renderFixture('custom-actions',actionFixture));
    await page.locator('.card-head').first().click();
    const actions=page.locator('.matrix-event').filter({hasText:'Approve <now>'});
    assert.equal(await actions.count(),2,'Equal action labels remain separate recorded effects');
    assert.equal(await actions.first().getAttribute('data-matrix-verb'),'Edit','Custom names preserve effect semantics');
    assert.equal(await page.locator('.matrix-event').filter({hasText:'Audited'}).count(),1,'Default custom names are visible');
    await actions.first().focus();
    assert.match(await page.locator('.resource-hover-event').textContent(),/Approve <now>/);
    assert.deepEqual(errors,[]);
    await page.screenshot({path:path.join(out,'custom-actions.png'),fullPage:true});
    const historyFixture=structuredClone(model),historyScenario=historyFixture.scenarios.find(scenario=>scenario.scenarioId===actionSample.scenarioId);historyFixture.scenarios=[historyScenario];
    const historyReference=historyScenario.references.find(edge=>edge.kind==='Reference');
    const historySource=historyScenario.steps.find(step=>step.effects.some(effect=>effect.resourceId===historyReference.subjectId && effect.verb==='Create'));
    const historyTarget=historySource.effects.find(effect=>effect.resourceId===historyReference.targetId && effect.verb==='Reference');
    const historyProduced=historySource.effects.find(effect=>effect.resourceId===historyReference.subjectId && effect.verb==='Create');
    historyScenario.steps.push({...historySource,stepId:'history-later',label:'later',displayName:'Later consumption of a referenced input',titleMessage:null,
      effects:[{...historyTarget,verb:'Consume',actionName:null},{...historyProduced,verb:'Edit',actionName:null}]});
    historyScenario.references.push({...historyReference,kind:'Consume'});
    await page.goto(renderFixture('relationship-history',historyFixture));
    await page.locator('.card-head').first().click();
    const historyLabels=await page.locator('.matrix-connection').evaluateAll(nodes=>nodes.map(node=>node.getAttribute('aria-label')));
    const historyPrefix=historyReference.targetType+' '+historyReference.targetKey+' ';
    assert.ok(historyLabels.some(label=>label.startsWith(historyPrefix+'Reference → Create')),'The original reference keeps its connection');
    assert.ok(historyLabels.some(label=>label.startsWith(historyPrefix+'Consume → Edit')),'Later consumption of the same pair keeps its own connection');
    assert.deepEqual(errors,[]);
    console.log('PASS: default resource matrix; every sample row and order; unchanged no-resource lists; keyboard focus; snapshots; disclosure; failures; grouping; mobile access.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
