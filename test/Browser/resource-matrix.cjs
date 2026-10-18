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
for(const [action,expectedEffect] of [['Register','Create'],['Hold','Update'],['Reserve','Update'],['Booked','Create'],['Rescheduled','Create'],['Cancel','Delete']]){
  assert.ok(domainEffects.some(effect=>effect.actionName===action && effect.effect===expectedEffect),'The real sample demonstrates '+action+' with '+expectedEffect+' semantics');
}
for(const scenario of model.scenarios)for(const step of scenario.steps){
  const booked=step.effects.find(effect=>effect.actionName==='Booked');
  const reserved=step.effects.filter(effect=>effect.actionName==='Reserve');
  for(const slot of reserved){
    assert.equal(step.effects.filter(effect=>effect.resourceId===slot.resourceId && effect.actionName==='Reserve').length,1,'The reservation records one named Update');
    assert.equal(slot.effect,'Update','Reserve changes the slot with exclusive access');
  }
  if(!booked)continue;
  assert.ok(scenario.links.some(edge=>edge.sourceId===booked.resourceId && edge.role==='Patient' && edge.targetType==='Patient'),'Booked retains the patient link');
  assert.ok(scenario.links.some(edge=>edge.sourceId===booked.resourceId && edge.role==='Slot' && edge.targetType==='Slot'),'Booked links its reserved slot');
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
assert.equal(keyReference.effect,'Read');
assert.equal(keyReference.label,'Jane');
assert.equal(keyReference.data,null,'A typed relationship through a string key does not borrow the registry snapshot');
assert.ok(bookById.displayName.includes('Jane'),'Booking by key resolves the earlier readable name');
const booking=bookById.effects.find(effect=>effect.actionName==='Booked');
const registered=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Register'));
const approved=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Approve'));
const available=actionSample.steps.find(step=>step.effects.some(effect=>effect.type==='Slot' && effect.effect==='Create'));
const slotResource=actionSample.resources.find(resource=>resource.type==='Slot');
for(const [step,mentions] of [[registered,[['Jane',patientResource.id],['pat-42',patientResource.id]]],[approved,[['Jane',patientResource.id]]],[available,[['slot 01',slotResource.id]]],[bookById,[['Jane',patientResource.id],['slot 01',slotResource.id]]]]){
  for(const [text,resourceId] of mentions){
    assert.ok(step.titleMessage.parts.some(part=>part.text===text && part.resourceId===resourceId),'Title text '+text+' is a reference to the correct resource in '+step.displayName);
  }
}

assert.ok(actionSample.links.some(edge=>edge.sourceId===booking.resourceId && edge.targetId===patientResource.id && edge.role==='Patient'));
const audit=actionSample.steps.find(step=>step.effects.some(effect=>effect.actionName==='Audited'));
assert.ok(audit.displayName.includes('Jane'),'A key-only generated title reuses the patient name');
assert.ok(audit.effects.some(effect=>effect.resourceId===patientResource.id && effect.label==='Jane' && effect.data==null),'Key-only effects have names without borrowed snapshots');
assert.ok(actionSample.steps.some(step=>step.effects.some(effect=>effect.actionName==='Approve' && effect.effect==='Update')));

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
  const created=attempt.effects.filter(effect=>effect.type==='Appointment' && effect.effect==='Create');
  assert.equal(created.length,creates?1:0,'Only successful conditional booking creates an appointment');
  assert.equal(attempt.effects.filter(effect=>effect.actionName==='Reserve').length,creates?1:0,'Waitlisted and refused bookings do not reserve a slot');
  assert.equal(scenario.links.length,creates?2:0,'Only successful conditional booking records lineage');
  if(creates){
    const appointment=created[0];
    assert.equal(appointment.actionName,'Booked','Conditional booking uses the same domain action as direct booking');
    for(const [type,kind] of [['Patient','Patient'],['Slot','Slot']]){
      const target=scenario.resources.find(resource=>resource.type===type);
      assert.ok(scenario.links.some(edge=>edge.sourceId===appointment.resourceId && edge.targetId===target.id && edge.role===kind),'Conditional booking records the correct '+kind+' endpoint');
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
    const assertTimingInsets=async()=>{
      const insets=await page.locator('.card-head,.step-head').evaluateAll(heads=>heads.filter(head=>head.getBoundingClientRect().height>0).map(head=>{
        const time=head.querySelector('.dur,.dm');
        const boundary=head.matches('.step-head') && !head.closest('.resource-matrix') ? head.closest('.step-entry') : head;
        return boundary.getBoundingClientRect().right-time.getBoundingClientRect().right;
      }));
      assert.ok(insets.length>0 && insets.every(inset=>inset>=12),'Timings have at least 12px of space before their right border: '+JSON.stringify(insets));
    };
    await assertTimingInsets();
    const registryCard=page.locator('.card').filter({hasText:'registered patient books by ID'});
    await registryCard.locator('.card-head').click();
    assert.equal(await registryCard.locator('.step-dependency-pixels').count(),0,'Timing replaces the unreadable dependency pixels');
    const timingControls=registryCard.locator('.step-entry:not(.step-lifecycle) .step-gantt');
    assert.equal(await timingControls.count(),5,'Each ordinary step has an integrated timing strip');
    await timingControls.nth(2).hover();
    assert.ok(await registryCard.locator('.step-entry.dependency-related').count()>0,'Hovering timing reveals direct prerequisites');
    await page.mouse.move(1,1);await timingControls.nth(2).focus();
    assert.ok(await registryCard.locator('.step-entry.dependency-related').count()>0,'Keyboard focus reveals the same prerequisites');
    await timingControls.nth(2).click();
    assert.ok(await registryCard.locator('.step-prerequisites:not([hidden]) .predecessor-link').count()>0,'Prerequisites are readable step names');
    assert.equal(await registryCard.locator('.step-entry:not(.step-lifecycle).open').count(),0,'Timing disclosure does not also expand logs');
    await timingControls.nth(2).press('Escape');await page.locator('.card-head').first().focus();
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
      const lastRow=Array.from(card.querySelectorAll('.matrix-row')).at(-1).getBoundingClientRect();
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
    await page.locator('.card').nth(matrixScenarioIndex).locator('.step-logs-toggle').first().click();
    assert.equal(await scenarioPanel.evaluate(panel=>panel.clientWidth),beforeScrollWidth,'Expanding step evidence cannot shift the content width');
    await page.locator('.card').nth(matrixScenarioIndex).locator('.step-logs-toggle').first().click();
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
    await assertTimingInsets();
    assert.equal(await page.locator('.step-head .sx').count(),0,'Step titles have no number badges');
    const titleInsets=await page.locator('.step-head').evaluateAll(heads=>heads.filter(head=>head.getBoundingClientRect().height>0).map(head=>{
      const title=head.querySelector('.dn').getBoundingClientRect(),status=head.querySelector('.scenario-status').getBoundingClientRect();
      const gutter=parseFloat(getComputedStyle(head).getPropertyValue('--dependency-gutter')) || 8;
      const offset=parseFloat(getComputedStyle(head).getPropertyValue('--dependency-offset')) || 0;
      return title.left-status.right-(gutter-8-offset);
    }));
    assert.ok(titleInsets.every(gap=>gap>=5 && gap<=10),'Titles follow the graph gutter without an empty number column');
    const expandedGaps=await page.locator('.card:not(.collapsed)+.card:not(.collapsed)').evaluateAll(async cards=>{
      await Promise.all(cards.flatMap(card=>[card,card.previousElementSibling]).map(card=>card.scenarioAnimation?.finished.catch(()=>{})));
      return cards.map(card=>card.getBoundingClientRect().top-card.previousElementSibling.getBoundingClientRect().bottom);
    });
    assert.ok(expandedGaps.length>0 && expandedGaps.every(gap=>Math.abs(gap-16)<1),'Adjacent expanded scenarios have space between their background boxes');
    const neighborGroup=page.locator('.group').filter({has:page.locator('.card + .card')}).first();
    const upper=neighborGroup.locator('.card').nth(0),lower=neighborGroup.locator('.card').nth(1);
    const settleNeighbors=()=>neighborGroup.locator('.card').evaluateAll(async cards=>{
      await Promise.all(cards.flatMap(card=>card.getAnimations({subtree:true})).map(animation=>animation.finished.catch(()=>{})));
    });
    const setNeighborStates=async(first,second)=>{
      await neighborGroup.locator('.card').evaluateAll((cards,states)=>{
        states.forEach((open,index)=>{if(cards[index].classList.contains('collapsed')===open)cards[index].querySelector('.card-head').click();});
      },[first,second]);
      await settleNeighbors();
    };
    const neighborGeometry=()=>neighborGroup.locator('.card').evaluateAll(cards=>cards.slice(0,2).map(card=>{
      const box=card.getBoundingClientRect(),head=card.querySelector('.card-head').getBoundingClientRect(),style=getComputedStyle(card);
      const origin=card.parentElement.getBoundingClientRect().top;return {top:box.top-origin,bottom:box.bottom-origin,height:box.height,marginTop:parseFloat(style.marginTop),marginBottom:parseFloat(style.marginBottom),padding:style.padding,headTop:head.top-box.top,headHeight:head.height};
    }));
    for(const width of [1600,560]){
      await page.setViewportSize({width,height:1100});
      if(width===560)await upper.locator('.scenario-title').evaluate(title=>title.textContent='A wrapped scenario title that remains stable when the neighboring scenario is expanded or collapsed');
      await setNeighborStates(false,false);
      const collapsed=await neighborGeometry();
      for(const [first,second,gap] of [[false,false,0],[true,false,8],[false,true,8],[true,true,16]]){
        await setNeighborStates(first,second);const geometry=await neighborGeometry();
        assert.ok(Math.abs(geometry[1].top-geometry[0].bottom-gap)<1,'Scenario margins add independently: '+JSON.stringify({width,first,second,gap,geometry}));
        for(const [index,open] of [first,second].entries()){
          assert.equal(geometry[index].marginTop,open?8:0);assert.equal(geometry[index].marginBottom,open?8:0);
          if(!open)for(const property of ['height','padding','headTop','headHeight'])assert.equal(geometry[index][property],collapsed[index][property],'A collapsed neighbor keeps its own '+property);
        }
        const before=await neighborGeometry();await (first?lower:upper).hover();
        assert.deepEqual(await neighborGeometry(),before,'Hover changes appearance without moving scenario geometry');
        await (first?lower:upper).locator('.card-head').focus();
        assert.deepEqual(await neighborGeometry(),before,'Focus does not change scenario spacing');
      }
      const edgeMargins=await neighborGroup.locator('.card').evaluateAll(cards=>[cards[0],cards.at(-1)].map(card=>{const style=getComputedStyle(card);return {open:!card.classList.contains('collapsed'),top:parseFloat(style.marginTop),bottom:parseFloat(style.marginBottom)};}));
      for(const edge of edgeMargins){assert.equal(edge.top,edge.open?8:0);assert.equal(edge.bottom,edge.open?8:0);}
    }
    await page.setViewportSize({width:1600,height:1100});
    await setNeighborStates(false,false);
    const marginMotion=await lower.evaluate(async card=>{
      const head=card.querySelector('.card-head'),measure=()=>{const s=getComputedStyle(card);return {top:parseFloat(s.marginTop),bottom:parseFloat(s.marginBottom),body:card.querySelector('.scenario-reveal').getBoundingClientRect().height};};
      head.click();const opening=card.scenarioAnimation;opening.pause();opening.currentTime=0;const start=measure();
      opening.currentTime=80;const middle=measure();
      head.click();const closing=card.scenarioAnimation;closing.pause();closing.currentTime=0;const reversed=measure();
      closing.play();await closing.finished;const end=measure();
      return {start,middle,reversed,end,stale:card.classList.contains('is-animating') || Boolean(card.scenarioAnimation)};
    });
    assert.deepEqual(marginMotion.start,{top:0,bottom:0,body:0},'Opening starts at the current collapsed geometry');
    assert.ok(marginMotion.middle.top>0 && marginMotion.middle.top<8 && marginMotion.middle.top===marginMotion.middle.bottom && marginMotion.middle.body>0,'Margins and body expand together');
    for(const key of ['top','bottom','body'])assert.ok(Math.abs(marginMotion.middle[key]-marginMotion.reversed[key])<1,'Reversal keeps current '+key);
    assert.deepEqual(marginMotion.end,{top:0,bottom:0,body:0});assert.equal(marginMotion.stale,false);
    await page.emulateMedia({reducedMotion:'reduce'});
    await setNeighborStates(true,true);assert.equal((await neighborGeometry())[0].marginBottom,8);
    assert.equal(await lower.evaluate(card=>card.getAnimations({subtree:true}).length),0,'Reduced motion applies spacing immediately');
    await page.emulateMedia({reducedMotion:'no-preference'});
    const assertNoteAlignment=async()=>{
      const notes=await page.locator('.matrix-row>.step-notes').evaluateAll(nodes=>nodes.map(node=>{
        const note=node.querySelector('.step-note').getBoundingClientRect(),title=node.parentElement.querySelector('.step-head .dn').getBoundingClientRect();
        return {left:note.left-title.left,gap:note.top-title.bottom};
      }));
      assert.ok(notes.length>0,'The sample includes step notes');
      assert.ok(notes.every(note=>Math.abs(note.left)<1 && note.gap>=1 && note.gap<=5),'Notes align with their titles with a small gap: '+JSON.stringify(notes));
      const backgrounds=await page.locator('.step-entry:has(>.step-notes)').evaluateAll(rows=>rows.filter(row=>row.getBoundingClientRect().height>0).map(row=>{
        const track=row.querySelector('.gantt-track').getBoundingClientRect(),notes=row.querySelector('.step-notes').getBoundingClientRect(),cell=Array.from(row.querySelectorAll('.matrix-cell,.matrix-summary-cell')).map(cell=>cell.getBoundingClientRect()).find(box=>box.width>0);
        return {name:row.dataset.step,top:track.top,noteTop:notes.top,bottom:track.bottom,noteBottom:notes.bottom,right:track.right,cellLeft:cell?.left,ok:track.top<=notes.top && track.bottom>=notes.bottom && (!cell || track.right<=cell.left+.1)};
      }));
      assert.ok(backgrounds.length && backgrounds.every(item=>item.ok),'Timing backgrounds include the complete note and stop before the resource matrix: '+JSON.stringify(backgrounds.filter(item=>!item.ok)));
    };
    await assertNoteAlignment();
    await page.setViewportSize({width:560,height:1100});
    await assertNoteAlignment();
    await page.setViewportSize({width:1600,height:1100});
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
    const expander = first.locator('.step-entry.has-body:not(.step-lifecycle) .step-logs-toggle').first();
    await expander.click();
    assert.equal(await expander.getAttribute('aria-expanded'), 'true');
    const expandedRow=expander.locator('xpath=ancestor::*[contains(@class,"step-entry")][1]');
    await expandedRow.evaluate(async row=>{await row.stepAnimation?.finished;});
    assert.equal(await expandedRow.locator('.step-logs summary').count(),0,'Logs do not require a second disclosure control');
    assert.ok(await expandedRow.locator('.logs').isVisible(),'Logs are directly visible when the step expands');
    assert.ok(await expandedRow.evaluate(row=>Math.abs(row.querySelector('.logs').getBoundingClientRect().left-row.querySelector('.step-head>.scenario-status').getBoundingClientRect().left)<1),'Log text aligns vertically with the step status marker');
    assert.ok(await expandedRow.evaluate(row=>{
      const body=row.querySelector('.step-body').getBoundingClientRect(),columns=Array.from(row.querySelectorAll('.matrix-continuation'));
      return columns.length===row.closest('.card').querySelectorAll('.matrix-resource').length && columns.every(column=>{
        const box=column.getBoundingClientRect();return box.top<=body.top && box.bottom>=body.bottom && getComputedStyle(column).borderLeftStyle==='solid';
      });
    }),'Resource column borders continue alongside expanded step details');
    await expandedRow.evaluate(async row=>{
      await row.stepAnimation?.finished;
      const control=row.querySelector('.step-logs-toggle');control.click();
      const closing=row.stepAnimation;closing.pause();closing.currentTime=80;
      const middle=row.querySelector('.step-body').getBoundingClientRect().height;
      control.click();await row.stepAnimation?.finished;
      if(!row.classList.contains('open') || row.querySelector('.step-body').inert || middle<=0)throw new Error('Rapid disclosure reversal must retain open, interactive evidence');
    });
    await expander.click();
    await expandedRow.evaluate(async row=>{await row.stepAnimation?.finished;});
    assert.ok(await first.locator('.matrix-scroll').evaluate(node => node.scrollHeight) <= before + 2, 'Collapsing evidence removes its space');
    await page.emulateMedia({reducedMotion:'reduce'});
    await expander.click();
    assert.ok(await expandedRow.evaluate(row=>!row.stepAnimation && !row.querySelector('.step-body').inert),'Reduced motion expands evidence immediately');
    await expander.click();
    assert.ok(await expandedRow.evaluate(row=>!row.stepAnimation && row.querySelector('.step-body').inert),'Reduced motion collapses evidence immediately');
    await page.emulateMedia({reducedMotion:'no-preference'});
    assert.equal(await page.locator('.matrix-clear').count(),0,'No Clear selection control');
    const relation=page.locator('.matrix-connection').first();
    assert.ok(await relation.isVisible(),'Resource inputs connect to Create before selecting a resource');
    assert.match(await relation.getAttribute('aria-label'),/Patient|Slot/,'Connections describe the actual input action');
    assert.ok(await relation.evaluate(svg=>{
      const row=svg.closest('.matrix-row'),a=svg.getBoundingClientRect();
      return Math.abs(a.height-row.clientHeight)<1 && /^M [^ ]+ [^ ]+ H [^ ]+$/.test(svg.querySelector('path').getAttribute('d'));
    }),'Connections follow measured badge edges with straight routing behind resource labels');
    const connectionRow=relation.locator('xpath=..');
    await connectionRow.locator('.step-logs-toggle').click();
    await page.waitForTimeout(40);
    assert.ok(await relation.evaluate(svg=>{
      const row=svg.closest('.matrix-row'),bounds=row.getBoundingClientRect();
      const source=row.querySelector('.matrix-cell').getBoundingClientRect();
      const lanes=Array.from(row.querySelectorAll('.matrix-connection'),connection=>Number(connection.querySelector('path').getAttribute('d').match(/^M ([^ ]+) ([^ ]+)/)[2]));
      const center=lanes.reduce((sum,y)=>sum+y,0)/lanes.length;
      return Math.abs(svg.getBoundingClientRect().height-row.clientHeight)<1 &&
        Math.abs(center-((source.top+source.bottom)/2-bounds.top))<1;
    }),'Expanded logs preserve badge-aligned connections instead of moving them to the row midpoint');
    await connectionRow.locator('.step-logs-toggle').click();
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
    assert.match(await lane.getAttribute('title'), /Patient|Slot/, 'Relationship details remain on the matrix heading');
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
    const editEffect=model.scenarios[matrixScenarioIndex].steps.flatMap(step=>step.effects).find(effect=>effect.effect==='Update');
    assert.ok(editEffect && editEffect.data,'Sample contains a captured edit snapshot');
    await first.getByRole('button',{name:/^Update Patient:.*Show evidence/}).click();
    assert.equal(await page.evaluate(()=>document.activeElement?.getAttribute('data-resource-data')),editEffect.data,'Matrix action focuses its event snapshot, not a title reference');
    await page.evaluate(()=>{window.resourceCopies=[];Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async text=>window.resourceCopies.push(text)}});});
    const inspector=page.locator('.resource-hover'),property=inspector.locator('.resource-json-key').first();
    const propertyName=JSON.parse(await property.textContent());
    await property.click();
    assert.equal(await page.evaluate(()=>window.resourceCopies.at(-1)),propertyName,'One click copies the raw property name');
    assert.equal(await property.getAttribute('data-copy-state'),'copied');
    await property.click();
    const value=inspector.locator('.resource-json-string').first();
    const valueText=JSON.parse(await value.textContent());
    await value.focus();await value.press('Enter');
    assert.equal(await page.evaluate(()=>window.resourceCopies.at(-1)),valueText,'Keyboard activation copies a raw string value');
    const beforeCopy=await inspector.boundingBox();
    await inspector.getByRole('button',{name:'Copy entire object'}).click();
    const whole=await page.evaluate(()=>window.resourceCopies.at(-1));
    assert.deepEqual(JSON.parse(whole),JSON.parse(editEffect.data),'Whole object copy retains complete captured evidence');
    assert.ok(whole.includes('\n'),'Whole object copy is formatted JSON');
    assert.deepEqual(await inspector.boundingBox(),beforeCopy,'Copy feedback does not move or resize the popup');
    await page.waitForTimeout(1700);
    assert.equal(await inspector.locator('[data-copy-state]').count(),0,'Copy feedback resets without changing JSON text');
    assert.equal(await property.getAttribute('title'),'Copy property name: '+propertyName,'Repeated copying restores the original tooltip');
    await inspector.screenshot({path:path.join(out,'resource-inspector.png')});
    await page.keyboard.press('Escape');
    const open = first.locator('.step-entry.open .step-logs-toggle');
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
    await page.waitForFunction(()=>document.querySelector('.card .matrix-compact'));
    await assertNoteAlignment();
    await first.evaluate(card=>card.scrollIntoView({block:'start'}));
    await first.locator('.matrix-summary-header').click();
    const mobileLanes=page.locator('.matrix-lanes-popover:popover-open');
    assert.ok(await mobileLanes.isVisible(),'Mobile users can open the full resource lanes');
    assert.ok(await mobileLanes.evaluate(node=>node.getBoundingClientRect().right<=innerWidth),'The lane popover fits the mobile viewport');
    await mobileLanes.locator('.matrix-scroll').evaluate(node=>node.scrollLeft=node.scrollWidth);
    assert.ok(await mobileLanes.locator('.matrix-resource').last().evaluate(node=>{const box=node.getBoundingClientRect();return box.right>0 && box.left<innerWidth;}),'All lanes remain reachable on mobile');
    assert.ok(await mobileLanes.locator('.matrix-resource').last().evaluate(node=>{const r=node.getBoundingClientRect();return node.contains(document.elementFromPoint(r.left+r.width/2,r.top+r.height/2));}),'Mobile lane headers are visible and hit-testable after scrolling');
    await page.keyboard.press('Escape');
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
      arrange.effects.push({ effect: 'Create', type: seed.type, key: resource.key, resourceId: resource.id, offsetMs: 0, data: null });
    }
    crowded.displayName = 'Many resources (matrix fixture)';
    crowded.steps.find(step=>step.status==='failed').exception=Array.from({length:80},(_,i)=>'Exception detail '+i).join('\n');
    await page.goto(renderFixture('resource-matrix-crowded', stress));
    assert.equal(await page.locator('.matrix-compact').count(),1,'Crowded resource lanes collapse to one activity column');
    const mainGeometry=await page.locator('.card').evaluate(card=>{const r=card.getBoundingClientRect();return {width:r.width,height:r.height};});
    await page.locator('.matrix-summary').filter({hasText:/[0-9]/}).first().click();
    const lanes=page.getByRole('dialog',{name:'Resource lanes for '+crowded.displayName});
    assert.ok(await lanes.isVisible(),'Activity opens a resource lane popover');
    const assertLaneAlignment=async()=>{
      await page.locator('.card .scenario-scroll .step-entry').evaluateAll(async rows=>{await Promise.all(rows.map(row=>row.stepAnimation?.finished));await new Promise(requestAnimationFrame);});
      const geometry=await lanes.evaluate(popup=>{
        const card=popup.closest('.card'),source=card.querySelector('.scenario-sheet'),a=card.getBoundingClientRect(),b=popup.getBoundingClientRect();
        const original=Array.from(source.querySelectorAll('.step-entry')),overlay=Array.from(popup.querySelectorAll('.step-entry'));
        return {top:b.top-a.top,height:b.height-a.height,rows:overlay.map((row,index)=>{const r=row.getBoundingClientRect(),s=original[index].getBoundingClientRect();return {top:r.top-s.top,height:r.height-s.height};}),verticalOverflow:popup.querySelector('.matrix-scroll').scrollHeight-popup.querySelector('.matrix-scroll').clientHeight};
      });
      assert.ok(Math.abs(geometry.top)<2 && Math.abs(geometry.height)<2,'The lane overlay sits on the scenario and matches its full height: '+JSON.stringify(geometry));
      assert.ok(geometry.rows.every(row=>Math.abs(row.top)<2 && Math.abs(row.height)<2),'Overlay lanes stay aligned with the scenario step rows: '+JSON.stringify(geometry));
      assert.ok(geometry.verticalOverflow<2,'The overlay has no separate vertical scroll');
    };
    await assertLaneAlignment();
    await page.locator('.report-view:has(>.tests-narrative)').evaluate(node=>node.scrollTop+=180);
    await page.waitForFunction(()=>{const popup=document.querySelector('.matrix-lanes-popover');return Math.abs(popup.getBoundingClientRect().top-popup.closest('.card').getBoundingClientRect().top)<2;});
    await assertLaneAlignment();
    assert.ok(await lanes.evaluate(popup=>{const bar=document.querySelector('.appbar'),a=bar.getBoundingClientRect(),p=popup.getBoundingClientRect();return bar.contains(document.elementFromPoint(p.right-10,a.top+a.height/2));}),'The scrolling overlay cannot cover application toolbar controls');
    await page.locator('.card .scenario-scroll .step-logs-toggle').first().focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(()=>{const popup=document.querySelector('.matrix-lanes-popover');return Math.abs(popup.getBoundingClientRect().height-popup.closest('.card').getBoundingClientRect().height)<2;});
    await assertLaneAlignment();
    await page.locator('.card .scenario-scroll .step-logs-toggle').first().focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(()=>{const popup=document.querySelector('.matrix-lanes-popover');return Math.abs(popup.getBoundingClientRect().height-popup.closest('.card').getBoundingClientRect().height)<2;});

    await page.locator('.report-view:has(>.tests-narrative)').evaluate(node=>node.scrollTop=0);
    await page.waitForFunction(()=>{const popup=document.querySelector('.matrix-lanes-popover');return Math.abs(popup.getBoundingClientRect().top-popup.closest('.card').getBoundingClientRect().top)<2;});
    assert.equal(await lanes.locator('.matrix-resource').count(),crowded.resources.length,'The popover retains every resource identity');
    const laneHeader=lanes.locator('.matrix-resource').first();
    await laneHeader.click();assert.equal(await laneHeader.getAttribute('aria-pressed'),'true','A popover lane can be selected');
    const columnWidth=await laneHeader.evaluate(node=>node.getBoundingClientRect().width);
    const laneScroller=lanes.locator('.matrix-scroll');
    await laneScroller.evaluate(node=>{node.arrowPositions=[];node.addEventListener('scroll',()=>node.arrowPositions.push(node.scrollLeft));});
    const settledScroll=()=>laneScroller.evaluate(async node=>{
      let previous=node.scrollLeft,stable=0;
      while(stable<4){await new Promise(requestAnimationFrame);const current=node.scrollLeft;stable=current===previous ? stable+1 : 0;previous=current;}
      return node.scrollLeft;
    });
    await lanes.getByRole('button',{name:'Scroll resource lanes right'}).click();
    assert.equal(await laneHeader.getAttribute('aria-pressed'),'true','Horizontal scrolling preserves the selected resource');
    assert.equal(await settledScroll(),columnWidth,'The right arrow advances exactly one whole column');
    assert.ok(await laneScroller.evaluate((node,width)=>node.arrowPositions.some(offset=>offset>0 && offset<width),columnWidth),'Arrow scrolling animates through intermediate positions');
    await lanes.getByRole('button',{name:'Scroll resource lanes left'}).click();
    assert.equal(await settledScroll(),0,'The left arrow returns exactly one whole column');
    await page.emulateMedia({reducedMotion:'reduce'});
    await lanes.getByRole('button',{name:'Scroll resource lanes right'}).click();
    assert.equal(await laneScroller.evaluate(node=>node.scrollLeft),columnWidth,'Reduced-motion users get the same column movement without animation');
    await lanes.getByRole('button',{name:'Scroll resource lanes left'}).click();
    await page.emulateMedia({reducedMotion:'no-preference'});
    await lanes.getByRole('button',{name:'Scroll resource lanes right'}).evaluate(button=>{button.click();button.click();});
    assert.equal(await settledScroll(),columnWidth*2,'Rapid arrow presses retain one whole column per press');
    await lanes.getByRole('button',{name:'Scroll resource lanes left'}).evaluate(button=>{button.click();button.click();});
    assert.equal(await settledScroll(),0,'Rapid left presses return by the same number of columns');
    await laneHeader.click();assert.equal(await laneHeader.getAttribute('aria-pressed'),'false','Clicking the same popover lane clears its selection');
    const identityColors=await lanes.locator('.matrix-resource').evaluateAll(nodes=>nodes.map(node=>getComputedStyle(node).borderTopColor));
    assert.equal(new Set(identityColors).size,crowded.resources.length,'Resource colors do not wrap beyond the palette');
    assert.ok(await lanes.locator('.matrix-scroll').evaluate(node=>node.scrollWidth>node.clientWidth),'Crowded popovers scroll horizontally');
    assert.deepEqual(await page.locator('.card').evaluate(card=>{const r=card.getBoundingClientRect();return {width:r.width,height:r.height};}),mainGeometry,'Opening lanes does not change the main report layout');
    await lanes.locator('.matrix-event').first().focus();
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.matrix-lanes-popover:popover-open').count(),0,'Escape dismisses the lanes');
    assert.ok(await page.locator('.matrix-summary').evaluateAll(nodes=>nodes.some(node=>node===document.activeElement)),'Dismissal restores keyboard focus');
    await page.locator('.matrix-summary-header').focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(()=>document.querySelector('.matrix-lanes-popover:popover-open'));
    await page.locator('.tests-class-name').click();
    await page.waitForFunction(()=>!document.querySelector('.matrix-lanes-popover'));
    assert.equal(await page.locator('.tests-class-name').evaluate(node=>node===document.activeElement),true,'Clicking outside dismisses without stealing focus');
    await page.setViewportSize({width:2400,height:1100});
    await page.locator('.tests-narrative').evaluate(node=>node.style.maxWidth='none');
    await page.waitForFunction(()=>!document.querySelector('.matrix-compact'));
    assert.equal(await page.locator('.card .matrix-resource:visible').count(),crowded.resources.length,'Lanes return inline when enough room is available');
    const sourceFixture=structuredClone(model);sourceFixture.scenarios=[sourceFixture.scenarios[matrixScenarioIndex]];
    const sourceScenario=sourceFixture.scenarios[0];
    sourceScenario.sourceUrl='https://github.com/example/repo/blob/abc123/tests/Booking.cs#L42';
    sourceScenario.typeSourceUrl='https://github.com/example/repo/blob/abc123/tests/Booking.cs#L12';
    await page.goto(renderFixture('source-links',sourceFixture));
    await page.mouse.move(0,0);
    await page.waitForFunction(()=>Array.from(document.querySelectorAll('.report-meta')).every(node=>getComputedStyle(node).opacity==='0'));
    const heading=page.locator('.tests-class-name'),scenarioHead=page.locator('.card-head');
    const classLink=heading.locator('a.group-type'),methodLink=scenarioHead.locator('a.scenario-method');
    assert.equal(await classLink.getAttribute('href'),sourceScenario.typeSourceUrl);
    assert.equal(await methodLink.getAttribute('href'),sourceScenario.sourceUrl);
    assert.ok(await page.locator('.report-meta').evaluateAll(nodes=>nodes.every(node=>getComputedStyle(node).opacity==='0' && getComputedStyle(node).fontStyle==='italic' && getComputedStyle(node).fontSize==='10.5px' && getComputedStyle(node).fontFamily===getComputedStyle(document.querySelector('.matrix-resource small')).fontFamily)),'Code names are 10.5px italic monospace and hidden by default');
    const slide=await methodLink.evaluate(node=>{const m=new DOMMatrix(getComputedStyle(node).transform);return {x:m.m41,y:m.m42};});
    assert.ok(slide.x<0 && slide.y===0,'Hidden identifiers start to the left and slide horizontally');
    const assertInlineIdentifier=async(owner,labelSelector)=>{
      const aligned=await owner.evaluate((node,labelSelector)=>{
        const title=node.querySelector(labelSelector).getBoundingClientRect(),meta=node.querySelector('.report-meta').getBoundingClientRect();
        const textBounds=element=>{const range=document.createRange();range.selectNodeContents(element);return range.getBoundingClientRect();};
        const titleText=textBounds(node.querySelector(labelSelector)),codeText=textBounds(node.querySelector('.report-meta [dir=ltr]'));
        const link=node.querySelector('.report-meta>.leading-ellipsis').getBoundingClientRect();
        return {gap:meta.left+parseFloat(getComputedStyle(node.querySelector('.report-meta')).paddingLeft)-title.right,center:(meta.top+meta.bottom-title.top-title.bottom)/2,textCenter:(codeText.top+codeText.bottom-titleText.top-titleText.bottom)/2,hitHeight:link.height};
      },labelSelector);
      assert.ok(aligned.gap>=4 && Math.abs(aligned.center)<1,'Code identifiers end after the title and vertically centered: '+JSON.stringify(aligned));
      assert.ok(Math.abs(aligned.textCenter)<1 && aligned.hitHeight>=22,'Code text is centered and has a larger link hit area: '+JSON.stringify(aligned));
    };
    const flyoutOpacity=async(owner)=>owner.locator('.report-meta').evaluate(async node=>{
      await Promise.all(node.getAnimations({subtree:true}).map(animation=>animation.finished.catch(()=>{})));
      return getComputedStyle(node).opacity;
    });
    await scenarioHead.locator('.dur').hover();
    assert.equal(await flyoutOpacity(scenarioHead),'0','Hovering the timing does not reveal the method name');
    await scenarioHead.locator('.dur').click();
    await scenarioHead.evaluate(async head=>{await head.closest('.card').scenarioAnimation?.finished.catch(()=>{});});
    assert.equal(await flyoutOpacity(scenarioHead),'0','Pointer focus on the row does not reveal the method name');
    const headingBox=await heading.boundingBox();
    await heading.hover({position:{x:headingBox.width-2,y:headingBox.height/2}});
    assert.equal(await flyoutOpacity(heading),'0','Hovering empty group heading space does not reveal the type name');
    await heading.locator('.tests-class-label').hover();
    assert.equal(await flyoutOpacity(heading),'1','Hovering the group title reveals the type name');
    const beforeReveal=await scenarioHead.evaluate(node=>{const rect=node.getBoundingClientRect();return {height:rect.height,width:rect.width};});
    await scenarioHead.locator('.scenario-title').hover();
    await page.waitForFunction(()=>getComputedStyle(document.querySelector('.card-head .report-meta')).opacity==='1');
    await assertInlineIdentifier(scenarioHead,'.scenario-title');
    const titleBox=await scenarioHead.locator('.scenario-title').boundingBox();
    await page.mouse.move(titleBox.x+titleBox.width/2,titleBox.y-3);
    assert.equal(await flyoutOpacity(scenarioHead),'1','The title has a forgiving hover margin above its text');
    await page.mouse.move(titleBox.x+titleBox.width+3,titleBox.y+titleBox.height/2);
    assert.equal(await flyoutOpacity(scenarioHead),'1','The flyout stays open while moving from the title to its source link');
    await methodLink.hover();
    assert.equal(await flyoutOpacity(scenarioHead),'1','The revealed source link remains reachable');
    const linkBox=await methodLink.boundingBox();
    await page.mouse.move(linkBox.x+linkBox.width/2,linkBox.y+2);
    assert.equal(await flyoutOpacity(scenarioHead),'1','The flyout stays open in the padding above the source name');
    assert.deepEqual(await scenarioHead.evaluate(node=>{const rect=node.getBoundingClientRect();return {height:rect.height,width:rect.width};}),beforeReveal,'Revealing method names does not change layout');
    await page.mouse.move(0,0);
    assert.equal(await flyoutOpacity(scenarioHead),'0','Leaving the title and source link hides the flyout');
    await page.keyboard.press('Tab');
    await scenarioHead.focus();
    assert.equal(await flyoutOpacity(scenarioHead),'1','Keyboard focus can still reveal method links');
    await heading.focus();
    await page.waitForFunction(()=>getComputedStyle(document.querySelector('.tests-class-name .report-meta')).opacity==='1');
    await assertInlineIdentifier(heading,'.tests-class-label');
    assert.equal(await methodLink.getAttribute('target'),'_blank','Source navigation keeps the report open');
    await methodLink.focus();
    await page.waitForFunction(()=>getComputedStyle(document.querySelector('.card-head .report-meta')).opacity==='1');
    const initialCardClass=await page.locator('.card').getAttribute('class');
    await page.route('https://github.com/example/repo/**',route=>route.fulfill({status:200,contentType:'text/plain',body:'Source link navigation test'}));
    const navigation=page.waitForEvent('popup');
    await methodLink.click();
    const sourceTab=await navigation;await sourceTab.waitForLoadState();
    assert.equal(sourceTab.url(),sourceScenario.sourceUrl,'Method links navigate to the selected source line');
    assert.equal(await page.locator('.card').getAttribute('class'),initialCardClass,'Following a source link does not toggle the scenario');
    await sourceTab.close();
    const bundleFixture=structuredClone(model),bundleScenario=bundleFixture.scenarios[matrixScenarioIndex];bundleFixture.scenarios=[bundleScenario];
    const bundleRef=bundleScenario.links.find(reference=>reference.role==='Patient');
    const bundleStep=bundleScenario.steps.find(step=>step.effects.some(effect=>effect.resourceId===bundleRef.sourceId && effect.effect==='Create'));
    const extraResource={...bundleScenario.resources[0],id:'bundle-extra',key:'Companion',label:'Companion'};
    bundleScenario.resources.push(extraResource);
    bundleStep.effects.push({...bundleStep.effects.find(effect=>effect.effect==='Read'),resourceId:extraResource.id,key:extraResource.key,label:extraResource.label});
    bundleScenario.links.push({...bundleRef,targetId:extraResource.id,targetKey:extraResource.key});
    await page.goto(renderFixture('resource-matrix-three-inputs',bundleFixture));await page.locator('.card-head').click();
    assert.ok(await page.locator('.matrix-row').filter({has:page.locator('.matrix-connection')}).first().evaluate(row=>{
      const paths=Array.from(row.querySelectorAll('.matrix-connection path:not(.matrix-arrow)'));
      const lanes=paths.map(path=>Number(path.getAttribute('d').match(/^M ([^ ]+) ([^ ]+)/)[2])).sort((a,b)=>a-b);
      const bounds=row.getBoundingClientRect(),cell=row.querySelector('.matrix-cell').getBoundingClientRect();
      return lanes.length===3 && lanes[1]>lanes[0] && Math.abs((lanes[2]-lanes[1])-(lanes[1]-lanes[0]))<.1 &&
        Math.abs(lanes[1]-((cell.top+cell.bottom)/2-bounds.top))<1 && paths.every(path=>/^M [^ ]+ [^ ]+ H [^ ]+$/.test(path.getAttribute('d')));
    }),'Three connections form an evenly spaced straight bundle centered on the action row');
    for(const effect of ['Update','Read']){
      const fixture=structuredClone(model),sc=fixture.scenarios[matrixScenarioIndex];fixture.scenarios=[sc];
      const ref=sc.links.find(reference=>reference.role==='Patient');
      const producer=sc.steps.find(step=>step.effects.some(effect=>effect.resourceId===ref.sourceId && effect.effect==='Create'));
      producer.effects.find(effect=>effect.resourceId===ref.sourceId && effect.effect==='Create').effect=effect;
      await page.goto(renderFixture('resource-matrix-'+effect.toLowerCase()+'-relationship',fixture));await page.locator('.card-head').click();
      assert.equal(await page.locator('.matrix-connection').filter({hasText:''}).evaluateAll((nodes,effect)=>nodes.some(svg=>svg.getAttribute('aria-label').includes('Patient → '+effect)),effect),true,effect+' producers retain their recorded relationships');
    }
    const snapshots=structuredClone(model),snapshotScenario=snapshots.scenarios[matrixScenarioIndex];
    snapshots.scenarios=[snapshotScenario];
    const editStep=snapshotScenario.steps.find(step=>step.effects.some(effect=>effect.effect==='Update'));
    const captured=editStep.effects.find(effect=>effect.effect==='Update');
    editStep.titleMessage={text:editStep.displayName,parts:[{text:'Given '},{text:'the patient',resourceId:captured.resourceId,data:'{"City":"before-edit-title-fixture"}'}]};
    await page.goto(renderFixture('resource-matrix-snapshot-fixture',snapshots));
    await page.locator('.card-head').click();
    await page.getByRole('button',{name:/^Update Patient:.*Show evidence/}).click();
    assert.equal(await page.evaluate(()=>document.activeElement?.getAttribute('data-resource-data')),captured.data,'Action inspection uses the event snapshot even when its title has a different snapshot');
    assert.deepEqual(errors, []);
    const repeated=structuredClone(model),repeatScenario=repeated.scenarios[matrixScenarioIndex];repeated.scenarios=[repeatScenario];
    const repeatStep=repeatScenario.steps.find(step=>step.effects.length && !step.isLifecycle),seedEffect=repeatStep.effects[0];
    repeatStep.logs=[];repeatStep.logMessages=[];repeatStep.notes=[];repeatStep.noteMessages=[];repeatStep.attachments=[];delete repeatStep.traceId;
    repeatStep.effects=[{...seedEffect,effect:'Read',data:'{"Version":1}',offsetMs:repeatStep.offsetMs+10},{...seedEffect,effect:'Read',data:'{"Version":2}',offsetMs:repeatStep.offsetMs+20}];
    await page.goto(renderFixture('resource-matrix-event-snapshots',repeated));await page.locator('.card-head').click();
    const onlyEffects=page.locator('.step-entry').filter({has:page.locator('.matrix-event')}).first();
    assert.equal(await onlyEffects.locator('.step-body,.step-logs-toggle').count(),0,'Resource events alone no longer create a redundant expansion panel');
    const repeatEvents=onlyEffects.locator('.matrix-event');assert.equal(await repeatEvents.count(),2,'Repeated actions retain both captured states');
    for(const [index,value] of [1,2].entries()){
      await repeatEvents.nth(index).focus();
      assert.equal(await page.evaluate(()=>document.activeElement.getAttribute('data-resource-data')),JSON.stringify({Version:value}));
      assert.equal(await page.locator('.resource-hover-event').textContent(),repeatStep.effects[index].actionName ?? repeatStep.effects[index].effect,'Inspector shows the action without a step-relative timestamp');
      assert.ok(!(await repeatEvents.nth(index).getAttribute('aria-label')).includes('from step start'),'Accessible action labels omit the timestamp too');
      await page.keyboard.press('Escape');
    }
    const actionFixture=structuredClone(model),actionScenario=actionFixture.scenarios[matrixScenarioIndex];
    actionFixture.scenarios=[actionScenario];
    const actionStep=actionScenario.steps.find(step=>step.effects.length),actionEffect=actionStep.effects[0];
    actionEffect.effect='Update';actionEffect.actionName='Approve <now>';actionEffect.label='Jane <Doe>';
    actionStep.effects.push({...actionEffect,actionName:'Audited',data:null});
    actionStep.effects.push({...actionEffect,actionName:'Approve <now>',data:null});
    await page.goto(renderFixture('custom-actions',actionFixture));
    await page.locator('.card-head').first().click();
    const actions=page.locator('.matrix-event').filter({hasText:'Approve <now>'});
    assert.equal(await actions.count(),2,'Equal action labels remain separate recorded effects');
    assert.equal(await actions.first().getAttribute('data-matrix-effect'),'Update','Custom names preserve effect semantics');
    assert.equal(await page.locator('.matrix-event').filter({hasText:'Audited'}).count(),1,'Default custom names are visible');
    await actions.first().focus();
    assert.match(await page.locator('.resource-hover-event').textContent(),/Approve <now>/);
    assert.deepEqual(errors,[]);
    await page.screenshot({path:path.join(out,'custom-actions.png'),fullPage:true});
    const historyFixture=structuredClone(model),historyScenario=historyFixture.scenarios.find(scenario=>scenario.scenarioId===actionSample.scenarioId);historyFixture.scenarios=[historyScenario];
    const historyReference=historyScenario.links.find(edge=>edge.role==='Patient');
    const historySource=historyScenario.steps.find(step=>step.effects.some(effect=>effect.resourceId===historyReference.sourceId && effect.effect==='Create'));
    const historyTarget=historySource.effects.find(effect=>effect.resourceId===historyReference.targetId && effect.effect==='Read');
    const historyProduced=historySource.effects.find(effect=>effect.resourceId===historyReference.sourceId && effect.effect==='Create');
    historyScenario.steps.push({...historySource,stepId:'history-later',label:'later',displayName:'Later consumption of a referenced input',titleMessage:null,
      effects:[{...historyTarget,effect:'Update',actionName:null},{...historyProduced,effect:'Update',actionName:null}]});
    historyScenario.links.push({...historyReference,role:'Payer'});
    await page.goto(renderFixture('relationship-history',historyFixture));
    await page.locator('.card-head').first().click();
    const historyLabels=await page.locator('.matrix-connection').evaluateAll(nodes=>nodes.map(node=>node.getAttribute('aria-label')));
    const historyPrefix=historyReference.targetType+' '+historyReference.targetKey+' ';
    assert.ok(historyLabels.some(label=>label.startsWith(historyPrefix+'Patient → Create')),'The original reference keeps its connection');
    assert.ok(historyLabels.some(label=>label.startsWith(historyPrefix+'Payer → Update')),'Later consumption of the same pair keeps its own connection');
    assert.deepEqual(errors,[]);
    const graphPage=await browser.newPage({viewport:{width:1400,height:1000}});
    const parallel=model.scenarios.find(sc=>sc.displayName==='customer books with parallel arrange');
    await graphPage.goto(renderFixture('step-timing',{...model,scenarios:[parallel]}));
    await graphPage.locator('.card-head').click();
    assert.equal(await graphPage.locator('.step-entry:not(.step-lifecycle) .step-gantt').count(),5);
    const edges=await graphPage.locator('.step-entry:not(.step-lifecycle) .predecessor-link').evaluateAll(links=>links.map(link=>[Number(link.dataset.from),Number(link.dataset.to)]).filter(([from])=>from!==0));
    assert.deepEqual(edges,[[1,2],[1,3],[2,4],[3,4],[4,5]],'Named prerequisites preserve the fork and join without inventing sibling connections');
    const marks=await graphPage.locator('.step-head > .scenario-status').evaluateAll(nodes=>nodes.filter(node=>node.getBoundingClientRect().height>0).map(node=>node.getBoundingClientRect().left));
    assert.ok(marks.every(x=>x===marks[0]),'Status squares stay aligned');
    const titles=graphPage.locator('.step-entry .dn');
    const timingTitlesBefore=await titles.evaluateAll(nodes=>nodes.map(node=>({left:node.getBoundingClientRect().left,width:node.getBoundingClientRect().width})));
    await graphPage.locator('.step-gantt').nth(2).hover();
    assert.deepEqual(await titles.evaluateAll(nodes=>nodes.map(node=>({left:node.getBoundingClientRect().left,width:node.getBoundingClientRect().width}))),timingTitlesBefore,'Tracing does not move titles');
    await graphPage.screenshot({path:path.join(out,'step-timing.png'),fullPage:true});
    for(const scenarioName of ['priority patients get an urgent appointment','a booking either lands, waits, or is refused']){
      const scenario=model.scenarios.find(sc=>sc.displayName===scenarioName);
      await graphPage.goto(renderFixture('step-branches-'+scenario.scenarioId.replaceAll(/[^a-z0-9]/gi,'-'),{...model,scenarios:[scenario]}));
      await graphPage.locator('.card-head').click();
      assert.ok(await graphPage.locator('.flow-branch').count()>=2,'Real if/switch source arms have branch regions');
      assert.ok(await graphPage.locator('.flow-toggle[aria-expanded="false"]').count()>0,'Untaken branches are folded');
      const transparent=new Set(scenario.steps.filter(step=>step.isValueMerge && step.status!=='failed').map(step=>step.index));
      const expected=scenario.steps.flatMap(step=>step.dependsOn.filter(from=>!transparent.has(from)).map(from=>[from,step.index]));
      assert.deepEqual(await graphPage.locator('.predecessor-link').evaluateAll(links=>links.map(link=>[Number(link.dataset.from),Number(link.dataset.to)])),expected,'Grouping retains guard, ordering and real branch prerequisites without synthetic merge links');
    }
    const nested=structuredClone(parallel);
    nested.scenarioId='nested-dependencies';nested.displayName='nested dependencies';nested.resources=[];nested.links=[];
    const names=['A','B','C','D','E','F','J','K'],dependencies=[[],[0],[0],[0],[3],[3],[1,2],[4,5,6]];
    const stepBase=parallel.steps.find(step=>!step.isLifecycle);
    nested.steps=names.map((name,index)=>({...stepBase,index,stepId:'nested-'+name,displayName:name,dependsOn:dependencies[index],dependencies:null,guards:null,arms:null,effects:[],logs:[],logMessages:[],notes:[],noteMessages:[],attachments:[],titleMessage:{text:name,parts:[{text:name}]}}));
    await graphPage.goto(renderFixture('step-dependencies-nested',{...model,scenarios:[nested]}));await graphPage.locator('.card-head').click();
    assert.deepEqual(await graphPage.locator('.predecessor-link').evaluateAll(links=>links.map(link=>[Number(link.dataset.from),Number(link.dataset.to)])),dependencies.flatMap((sources,to)=>sources.map(from=>[from,to])),'Nested joins preserve every actual connection');
    const wide=structuredClone(nested);
    wide.steps=Array.from({length:30},(_,index)=>({...nested.steps[0],index,stepId:'wide-'+index,displayName:'Step '+index,dependsOn:index?[index-1]:[]}));
    await graphPage.goto(renderFixture('step-timing-wide',{...model,scenarios:[wide]}));await graphPage.locator('.card-head').click();
    assert.equal(await graphPage.locator('.predecessor-link').count(),29,'Prerequisite controls scale with actual edges');
    assert.equal(await graphPage.locator('.step-gantt').count(),30,'Every step keeps the same bounded time axis');
    await graphPage.close();
    console.log('PASS: default resource matrix; every sample row and order; unchanged no-resource lists; keyboard focus; snapshots; disclosure; failures; compact lanes; source links; mobile access.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
