const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{chromium}=require('playwright');
const script=path.resolve(__dirname,'../../Game.Client/wwwroot/js/battle-combat.js');
const css=fs.readFileSync(path.resolve(__dirname,'../../Game.Client/wwwroot/css/battle-combat.css'),'utf8');
async function scene(run,reducedMotion='no-preference'){
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||(process.platform==='win32'?'msedge':undefined)});
 try{const page=await browser.newPage({viewport:{width:390,height:844},reducedMotion});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.setContent(`<style>${css}body{margin:0;background:#152b3d}.battle-field{position:relative;width:390px;height:220px;display:flex;align-items:flex-end;gap:4px}.unit{width:54px;height:155px;position:relative;color:white}.combat-health{height:12px}.combat-health progress{width:100%;height:8px}.fighter__portrait,.battle-field__enemy-art{height:115px;width:54px;background:linear-gradient(transparent,#67839933);position:relative}</style><main class="battle-page"><section id="root"><span data-combat-action></span><span data-combat-hits></span><span data-combat-total></span><div class="battle-field">${['1','2','3','4','5','enemy'].map(key=>`<div class="unit" data-combat-unit="${key}" data-combat-name="同名" data-hp="95" data-max-hp="100"><div class="combat-health"><progress max="100" value="95"></progress></div><div class="${key==='enemy'?'battle-field__enemy-art':'fighter__portrait'}"><span data-combat-status-live></span></div></div>`).join('')}<div class="combat-fx-layer"></div></div></section></main>`);
 await page.addScriptTag({path:script});await page.evaluate(()=>{window.effects=[];window.observer=new MutationObserver(records=>{for(const r of records)for(const n of r.addedNodes){if(!(n instanceof HTMLElement))continue; if(n.matches('.combat-ability,.combat-number,.combat-effect--skill'))window.effects.push({className:n.className,skill:n.dataset.skillCode,source:n.dataset.combatSource,target:n.dataset.combatTarget,cast:n.dataset.castKey,paths:n.querySelector('path')?.getAttribute('d'),text:n.textContent});}});observer.observe(document.querySelector('.combat-fx-layer'),{childList:true});});
 await run(page);assert.deepEqual(errors,[]);
 }finally{await browser.close();}
}
const event=(skillCode,source='1',target='enemy',kind='damage',amount=10)=>({source,target,skillCode,kind,amount,isSkill:true,castKey:`round1:${source}:${skillCode}`,actionKind:0,style:'pulse',tone:'light',label:'同名技能',hpAfter:85,targetMaxHp:100});
const plan=events=>({round:1,hitCount:events.filter(e=>e.kind==='damage').length,totalDamage:events.filter(e=>e.kind==='damage').reduce((a,e)=>a+e.amount,0),events,initialStatuses:{},finalVitals:{},defeated:false,victory:false});
async function play(page,events){await page.evaluate(p=>battleCombat.play(document.querySelector('#root'),p),plan(events));return page.evaluate(()=>window.effects);}

test('five professions use distinct SVG art anchored by source and target IDs',()=>scene(async page=>{
 const entries=[event('sword-slash','1'),event('acolyte-holy-bolt','2'),event('mage-frost-bolt','3'),event('hunter-tracking-shot','4'),event('rogue-shadow-strike','5')];
 const effects=await play(page,entries),casts=effects.filter(e=>e.className.includes('--cast')),impacts=effects.filter(e=>e.className.includes('--impact'));
 assert.equal(casts.length,5);assert.deepEqual(casts.map(e=>e.source),['1','2','3','4','5']);assert(impacts.every(e=>e.target==='enemy'));assert.equal(new Set(impacts.map(e=>e.paths)).size,5);
}));

test('group shield casts once, draws once for each target, and preserves per-target status',()=>scene(async page=>{
 const events=['1','2','3','4','5'].map(target=>({...event('knight-faith-barrier','1',target,'buff',0),statusEffectType:'Guard',status:{code:'guard',name:'护盾',description:'护盾',isPositive:true,stacks:1,glyph:'◇'},statusChange:'Added',countAfter:1}));
 const effects=await play(page,[...events,events[4]]);assert.equal(effects.filter(e=>e.className.includes('--cast')).length,1);const hits=effects.filter(e=>e.className.includes('--impact'));assert.equal(hits.length,5);assert.deepEqual(hits.map(e=>e.target),['1','2','3','4','5']);assert.equal(effects.filter(e=>e.className==='combat-effect combat-effect--skill').length,0);
}));

test('flurry has three separate impact animations and numbers with one cast',()=>scene(async page=>{
 const effects=await play(page,[10,11,12].map(amount=>event('rogue-blade-flurry','5','enemy','damage',amount)));
 assert.equal(effects.filter(e=>e.className.includes('--cast')).length,1);assert.equal(effects.filter(e=>e.className.includes('--impact')).length,3);assert.deepEqual(effects.filter(e=>e.className.includes('combat-number--damage')).map(e=>e.text),['10','11','12']);
}));

test('periodic damage and healing use short tick effects without full casts',()=>scene(async page=>{
 const effects=await play(page,[{...event('mage-scorch','3'),isSkill:false,actionKind:5},{...event('acolyte-group-heal','2','1','heal',4),isSkill:false,actionKind:5}]);assert.equal(effects.filter(e=>e.className.includes('--cast')).length,0);assert.equal(effects.filter(e=>e.className.includes('--tick')).length,2);assert.equal(effects.filter(e=>e.className.includes('combat-ability--impact')).length,0);
}));

test('25-effect chain is bounded and opening a panel cancels all temporary effects',()=>scene(async page=>{
 const events=Array.from({length:25},(_,i)=>({...event('mage-arcane-bolt','3','enemy'),castKey:'cast'+i}));const start=Date.now();await play(page,events);assert(Date.now()-start<7000,'25 effects took too long');
 await page.evaluate(p=>{window.playing=battleCombat.play(document.querySelector('#root'),p)},plan(events));await page.locator('.combat-ability').first().waitFor();await page.evaluate(()=>document.querySelector('.battle-page').classList.add('battle-page--panel-open'));await page.evaluate(()=>window.playing);assert.equal(await page.locator('.combat-fx-layer > *').count(),0);assert.equal(await page.locator('#root').getAttribute('data-combat-phase'),null);
}));

test('reduced motion performs no skill movement or effects',()=>scene(async page=>{
 const effects=await play(page,[event('mage-frost-bolt','3')]);assert.deepEqual(effects,[]);assert.equal(await page.locator('.combat-fx-layer > *').count(),0);
},'reduce'));


test('a newer snapshot replaces playback and skill shapes stay below target health bars',()=>scene(async page=>{
 const oldPlan=plan(Array.from({length:12},()=>event('mage-scorch','3')));
 await page.evaluate(p=>{window.oldPlay=battleCombat.play(document.querySelector('#root'),p)},oldPlan);await page.locator('.combat-ability--cast').first().waitFor();
 await page.evaluate(p=>{window.newPlay=battleCombat.play(document.querySelector('#root'),p)},plan([event('mage-frost-bolt','3')]));
 await page.locator('.combat-ability--frost.combat-ability--impact').waitFor();
 const safe=await page.locator('.combat-ability--frost.combat-ability--impact').evaluate(n=>{const root=n.closest('#root'),target=root.querySelector('[data-combat-unit="'+n.dataset.combatTarget+'"]'),health=target.querySelector('.combat-health').getBoundingClientRect(),box=n.getBoundingClientRect();return box.top>=health.bottom;});assert(safe);
 assert.equal(await page.locator('.combat-ability[data-skill-code="mage-scorch"]').count(),0);await page.evaluate(()=>Promise.all([window.oldPlay,window.newPlay]));assert.equal(await page.locator('.combat-fx-layer > *').count(),0);assert.equal(await page.locator('.combat-playing').count(),0);
}));
