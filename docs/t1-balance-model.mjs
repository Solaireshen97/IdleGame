// Design-only model. Run: node docs/t1-balance-model.mjs
// No database writes. This estimates direct damage, not real combat clear rates.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const effects = {
  attack: { perLevel: 2, cap: 60 }, health: { perLevel: 3, cap: 75 },
  critical: { perLevel: 3, cap: 60 }, stamina: { perLevel: 2, cap: 30 },
  enmity: { perLevel: 4, cap: 60 }, double: { perLevel: 2, cap: 40 },
  echo: { perLevel: 1.5, cap: 30 }, skill: { perLevel: 4, cap: 80 }
};
export const composites = {
  might: { attack: .5, health: .5 }, tactics: { attack: .5, skill: .5 },
  momentum: { double: .4, echo: .4 }, struggle: { attack: .5, enmity: .5 }
};
const names = { attack:'攻击',health:'生命',critical:'暴击',stamina:'强壮',enmity:'背水',double:'二连击',echo:'追击',skill:'技能伤害',might:'神威',tactics:'战技',momentum:'连势',struggle:'奋战' };
const weapon = (name, attack, health, skills) => ({ name, attack, health, skills });
export const fieldWeapons = {
  Fire: [weapon('燃刃短斧',24,40,[['attack',2],['enmity',1]]),weapon('赤蝎钩刃',22,45,[['critical',2],['echo',1]]),weapon('烟熏铁锤',18,55,[['might',2],['skill',1]]),weapon('烛头短杖',16,60,[['health',2],['stamina',1]])],
  Water:[weapon('鱼骨短刀',24,40,[['double',2],['attack',1]]),weapon('霜鬃猎矛',22,45,[['critical',2],['skill',1]]),weapon('结霜木杖',18,55,[['might',2],['stamina',1]]),weapon('冰牙骨槌',16,60,[['health',2],['echo',1]])],
  Earth:[weapon('缺口矿镐',24,40,[['attack',2],['skill',1]]),weapon('石刃手斧',22,45,[['critical',2],['double',1]]),weapon('獠牙木棒',18,55,[['might',2],['enmity',1]]),weapon('铁箍木槌',16,60,[['health',2],['stamina',1]])],
  Wind:[weapon('兽牙猎矛',24,40,[['attack',2],['critical',1]]),weapon('兽筋短弓',22,45,[['double',2],['echo',1]]),weapon('羽饰短杖',18,55,[['might',2],['skill',1]]),weapon('兽皮缠柄棍',16,60,[['health',2],['stamina',1]])],
  Light:[weapon('哨兵旧剑',24,40,[['attack',2],['stamina',1]]),weapon('铜环祭杖',22,45,[['skill',2],['critical',1]]),weapon('蒙尘祈祷锤',18,55,[['might',2],['double',1]]),weapon('裂晶短杖',16,60,[['health',2],['echo',1]])],
  Dark:[weapon('蛛牙小刀',24,40,[['attack',2],['enmity',1]]),weapon('木柄祭匕',22,45,[['enmity',2],['skill',1]]),weapon('守墓棘杖',18,55,[['might',2],['critical',1]]),weapon('黯淡学徒杖',16,60,[['health',2],['echo',1]])]
};
export const bosses = {
  Fire:weapon('怒焰裂刃',26,45,[['tactics',3],['enmity',2]]),
  Water:weapon('冰牙寒泉枪',18,65,[['might',3],['stamina',2]]),
  Earth:weapon('金牙的精工矿镐',22,60,[['might',3],['skill',2]]),
  Wind:weapon('风怒翎羽弓',24,50,[['momentum',3],['critical',2]]),
  Light:weapon('晨曦守望之杖',20,60,[['tactics',3],['stamina',2]]),
  Dark:weapon('织亡者之牙',24,50,[['struggle',3],['echo',2]])
};
export const exchangeShapes = [
  weapon('矿洞兑换型',20,55,[['might',3],['attack',1]]),
  weapon('蛛谷兑换型',22,50,[['struggle',3],['critical',1]]),
  weapon('怒焰兑换型',22,50,[['tactics',3],['skill',1]]),
  weapon('寒泉兑换型',18,60,[['health',2],['stamina',2]]),
  weapon('风巢兑换型',22,50,[['double',2],['critical',2]]),
  weapon('晨曦兑换型',20,55,[['might',2],['stamina',2]])
];
export function weightedLevel(level) {
  level=Math.max(0,level);
  return Math.min(level,10) + Math.max(0,Math.min(level-10,10))*.5 + Math.max(0,Math.min(level-20,20))*.25 + Math.max(0,level-40)*.1;
}
export function levelsFor(item, enhancement=0, quality=0, qualitySlot=0) {
  const levels=Object.fromEntries(Object.keys(effects).map(key=>[key,0]));
  item.skills.forEach(([code,base],index)=>{
    const level=base+enhancement+(index===qualitySlot?quality:0);
    for(const [effect,weight] of Object.entries(composites[code]??{[code]:1})) levels[effect]+=level*weight;
  });
  return levels;
}
export function evaluate(loadout, hpRatio=.9, skillFrequency=.5, enhancement=0, quality=0) {
  let attack=0,health=0;
  const levels=Object.fromEntries(Object.keys(effects).map(key=>[key,0]));
  for(const {item,count} of loadout) {
    attack+=item.attack*count;health+=item.health*count;
    for(const [effect,level] of Object.entries(levelsFor(item,enhancement,quality))) levels[effect]+=level*count;
  }
  return evaluateTotals(attack,health,levels,hpRatio,skillFrequency);
}
function evaluateTotals(attack,health,levels,hpRatio,skillFrequency) {
  const bonus=Object.fromEntries(Object.entries(effects).map(([key,rule])=>[key,Math.min(rule.cap,weightedLevel(levels[key])*rule.perLevel)/100]));
  const hpMultiplier=1+bonus.stamina*Math.max(0,Math.min(1,(hpRatio-.75)/.25))+bonus.enmity*Math.max(0,Math.min(1,(.5-hpRatio)/.5));
  const direct=attack*(1+bonus.attack)*hpMultiplier*(1+.5*bonus.critical);
  const normal=direct*(1+bonus.double)*(1+bonus.echo);
  const skill=direct*(1+bonus.skill)*skillFrequency;
  return {attack,health:health*(1+bonus.health),normal,skill,total:normal+skill,levels,bonus};
}
function search(items,{enhancement=0,hpRatio=.9,minHealth=0,skillFrequency=.5}={}) {
  // Exhaustive multisets: repeated weapons allowed, exactly ten equipped weapons.
  const variants=items.map(item=>({item,levels:levelsFor(item,enhancement)}));
  const counts=Array(items.length).fill(0);
  let best=null, evaluated=0, feasible=0;
  const levels=Object.fromEntries(Object.keys(effects).map(key=>[key,0]));
  function walk(index,left,attack,health) {
    if(index===items.length-1) {
      const variant=variants[index];counts[index]=left;
      for(const key of Object.keys(effects)) levels[key]+=left*variant.levels[key];
      const score=evaluateTotals(attack+left*variant.item.attack,health+left*variant.item.health,levels,hpRatio,skillFrequency); evaluated++;
      if(score.health>=minHealth){feasible++;if(!best||score.total>best.total) best={...score,levels:{...score.levels},loadout:items.flatMap((item,i)=>counts[i]?[`${item.name}×${counts[i]}`]:[])};}
      for(const key of Object.keys(effects)) levels[key]-=left*variant.levels[key];
      return;
    }
    const variant=variants[index];
    for(let count=0;count<=left;count++){
      counts[index]=count;
      for(const key of Object.keys(effects)) levels[key]+=count*variant.levels[key];
      walk(index+1,left-count,attack+count*variant.item.attack,health+count*variant.item.health);
      for(const key of Object.keys(effects)) levels[key]-=count*variant.levels[key];
    }
  }
  walk(0,10,0,0);return {evaluated,feasible,best};
}

function main(){
  const report={ assumptions:{defense:0,elementMultiplier:1,critMultiplier:1.5,skillFrequency:.5,description:'All weapons match the main element. Expected direct damage with fixed HP; excludes healing, cooldown sequence, kill timing, buffs and multiplayer. Quality sample bonuses are placed into the first listed skill; not an expected random roll.'},effects,curve:[1,.5,.25,.1],ordinary:[],progression:[],exchangeSearch:[],quality:[],economy:{hoursPerDay:12,firstWeekHours:84,starterGoldOncePerAccount:120,shopWeaponGold:40,enhancementFragmentCosts:[2,8,24],normalDropRates:[.04,.08,.16],normalTargetCycleMinutes:[.5,1,2],exchangeTokens:12,clearTokens:1,firstClearBonusTokens:2,bossWeaponDropRate:.18,qualityProbabilities:[.60,.25,.12,.03]}};
  const shop=weapon('商店制式武器',18,45,[['attack',1]]);
  const starter=weapon('初始武器',16,40,[['attack',1]]);
  report.progression.push({stage:'初始主手 + 两把商店武器',...evaluate([{item:starter,count:1},{item:shop,count:2}])});
  report.progression.push({stage:'十把商店武器',...evaluate([{item:shop,count:10}])});
  for(const [element,items] of Object.entries(fieldWeapons)){
    for(const enhancement of [0,1,3]) for(const hpRatio of [1,.9,.75,.25]){
      const result=search(items,{enhancement,hpRatio});
      const pure=evaluate([{item:items[0],count:10}],hpRatio,.5,enhancement);
      const sturdy=search(items,{enhancement,hpRatio,minHealth:600});
      report.ordinary.push({element,enhancement,hpRatio,best:result.best,pure,improvement:result.best.total/pure.total-1,sturdy:sturdy.best});
    }
    const sample=[{item:items[0],count:4},{item:items[1],count:3},{item:items[2],count:2},{item:items[3],count:1}];
    report.progression.push({stage:`${element} 普通 4/3/2/1`,...evaluate(sample,.9,.5)});
    report.progression.push({stage:`${element} 普通 4/3/2/1 每技能+1`,...evaluate(sample,.9,.5,1)});
    report.progression.push({stage:`${element} 两把Boss + 普通3/2/2/1 每技能+1`,...evaluate([{item:bosses[element],count:2},{item:items[0],count:3},{item:items[1],count:2},{item:items[2],count:2},{item:items[3],count:1}],.9,.5,1)});
    for(const [quality,enhancement] of [[0,1],[1,1],[2,2],[3,3]]) report.quality.push({element,quality,enhancement,...evaluate(sample,.9,.5,enhancement,quality)});
    // Expanded searches use six exchange shapes plus this element's four field weapons and boss.
    for(const hpRatio of [.9,.25]) report.exchangeSearch.push({element,hpRatio,enhancement:1,...search([...items,...exchangeShapes,bosses[element]],{enhancement:1,hpRatio,minHealth:600})});
  }
  report.fieldWeapons=Object.fromEntries(Object.entries(fieldWeapons).map(([element,items])=>[element,items.map(item=>({...item,skillText:item.skills.map(([code,level])=>`${names[code]} Lv.${level}`).join(' + ')}))]));
  const eco=report.economy;
  eco.normalExpectedMinutesPerWeapon=eco.normalTargetCycleMinutes.map((minutes,index)=>minutes/eco.normalDropRates[index]);
  eco.normalChanceOfNoDropAfter10=eco.normalDropRates.map(chance=>(1-chance)**10);
  eco.fullGridFragmentsByEnhancement=[40,200,680];
  eco.fragmentOnlyHoursAtTwelvePointFiveMinutesEach=eco.fullGridFragmentsByEnhancement.map(fragments=>fragments*12.5/60);
  eco.expectedMinutesPerBossWeapon=[18,24].map(minutes=>minutes/eco.bossWeaponDropRate);
  eco.firstExchangeClears=eco.exchangeTokens-eco.firstClearBonusTokens;
  eco.firstExchangeMinutes=[18,24].map(minutes=>minutes*eco.firstExchangeClears);
  eco.expectedBlueOrBetterBossMinutes=[18,24].map(minutes=>minutes/(eco.bossWeaponDropRate*(eco.qualityProbabilities[2]+eco.qualityProbabilities[3])));
  eco.expectedExactEpicBossMinutes=[18,24].map(minutes=>minutes/(eco.bossWeaponDropRate*eco.qualityProbabilities[3]*(3/8)));
  const out=path.join(path.dirname(fileURLToPath(import.meta.url)),'t1-balance-results.json');fs.writeFileSync(out,JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({output:out,ordinary:report.ordinary.filter(x=>x.enhancement===1&&x.hpRatio===.9).map(x=>({element:x.element,improvement:+(x.improvement*100).toFixed(1),best:x.best.loadout,sturdy:x.sturdy?.loadout})),expanded:report.exchangeSearch.map(x=>({element:x.element,hp:x.hpRatio,damage:+x.best.total.toFixed(1),health:+x.best.health.toFixed(1),build:x.best.loadout})),quality:report.quality.map(({element,quality,enhancement,total})=>({element,quality,enhancement,total:+total.toFixed(1)}))},null,2));
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) main();
