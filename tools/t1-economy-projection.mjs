// Resource sufficiency projection, NOT a simulated new-account playthrough.
// Run from repository root after Game.BalanceSimulator.
import fs from 'node:fs';
const config = JSON.parse(fs.readFileSync('Game.Server/appsettings.json', 'utf8'));
const world = JSON.parse(fs.readFileSync('Game.Server/world.json', 'utf8')).World;
const fights = JSON.parse(fs.readFileSync('docs/t1-profession-balance-final.json', 'utf8')).Summary;
const trials = 2000;
const budget = { hunts: 40, dungeons: 30, elites: 12, explorationAndFirstDepthAttempt: 2 };
const professions = ['knight', 'warrior', 'priest', 'inquisitor', 'elementalist', 'arcanist',
  'marksman', 'beastmaster', 'assassin', 'trickster'];
function generator(seed) { return () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; }; }
const qualityWeights = Object.values(config.Weapons.DropQualityWeights);
function quality(rng) {
  let roll = rng() * qualityWeights.reduce((a, b) => a + b, 0);
  for (let i = 0; i < qualityWeights.length; i++) { roll -= qualityWeights[i]; if (roll < 0) return i; }
  return qualityWeights.length - 1;
}
function quantiles(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return Object.fromEntries([.1, .5, .9].map(q => [`p${q * 100}`, sorted[Math.floor(q * (sorted.length - 1))]]));
}
const cases = [];
for (const region of world.Regions) for (const profession of professions) {
  const samples = fights.filter(x => x.Key.Element === region.FeaturedElement && x.Key.Profession === profession);
  const normalDungeon = world.Dungeons.find(d => d.RegionCode === region.Code && d.DungeonKind === 'Hunt' && d.MinimumLevel === 7);
  const eliteDungeon = world.Dungeons.find(d => d.RegionCode === region.Code && d.DungeonKind === 'Elite' && d.MinimumLevel === 10);
  const hunt = samples.find(x => x.Key.Stage === 'week' && x.Key.Dungeon === normalDungeon.Code);
  const weekDungeon = samples.find(x => x.Key.Stage === 'week' && x.Key.Dungeon === region.FeaturedDungeonCode);
  const weekElite = samples.find(x => x.Key.Stage === 'week' && x.Key.Dungeon === eliteDungeon.Code);
  if (!hunt || !weekDungeon || !weekElite) throw new Error(`Missing combat sample for ${region.FeaturedElement}/${profession}`);
  const huntChance = config.Rewards.MonsterKills[normalDungeon.Code].Drops.find(d => d.Kind === 'Weapon').ChancePercent / 100;
  const bossCode = region.FeaturedWeaponCode;
  const bossChance = Object.values(config.Rewards.MonsterKills).flatMap(b => b.Drops)
    .find(d => d.Kind === 'Weapon' && d.Code === bossCode).ChancePercent / 100;
  const eliteChance = config.Rewards.MonsterKills[eliteDungeon.Code].Drops.find(d => d.Kind === 'Weapon').ChancePercent / 100;
  const offer = config.DungeonExchange.Offers.find(o => o.DungeonCode === region.FeaturedDungeonCode &&
    (!o.RewardKind || o.RewardKind === 'Weapon'));
  if (!offer) throw new Error(`Missing weapon exchange for ${region.FeaturedDungeonCode}`);
  const count = { hunts: Math.floor(budget.hunts * 3600 / hunt.MeanCycleSeconds),
    dungeons: Math.floor(budget.dungeons * 3600 / weekDungeon.MeanCycleSeconds),
    elites: Math.floor(budget.elites * 3600 / weekElite.MeanCycleSeconds) };
  const exchangeCount = Math.floor((count.dungeons + 2) / offer.Cost);
  const outcomes = [];
  for (let trial = 1; trial <= trials; trial++) {
    const rng = generator(trial);
    // Four target templates, eight retained ordinary weapons: 3/2/2/1.
    const needed = [3, 2, 2, 1];
    const qualityCopies = [[], [], [], []];
    let huntDrops = 0, bossDrops = 0, eliteDrops = 0, bossBlue = 0, exchangeBlue = 0;
    for (let run = 0; run < count.hunts; run++) {
      if (rng() >= huntChance) continue;
      const deficit = needed.map((n, i) => n - qualityCopies[i].filter(q => q >= 1).length);
      const max = Math.max(...deficit);
      const target = max > 0 ? deficit.indexOf(max) : run % 4;
      qualityCopies[target].push(quality(rng)); huntDrops++;
    }
    for (let run = 0; run < count.dungeons; run++) if (rng() < bossChance) {
      bossDrops++; if (quality(rng) >= 2) bossBlue++;
    }
    for (let run = 0; run < count.elites; run++) if (rng() < eliteChance) { eliteDrops++; quality(rng); }
    for (let item = 0; item < exchangeCount; item++) if (quality(rng) >= 2) exchangeBlue++;
    const greenOrdinary = needed.reduce((n, goal, i) => n + Math.min(goal, qualityCopies[i].filter(q => q >= 1).length), 0);
    // Retain eight ordinary, one Boss, one exchange and one elite; dismantle other drops.
    // No assumed fragments from shop items, defeated attempts, or enhancement refunds.
    const fragments = Math.max(0, huntDrops - 8) + Math.max(0, bossDrops - 1) * 2 +
      Math.max(0, exchangeCount - 1) * 2 + Math.max(0, eliteDrops - 1) * 2;
    const coreCost = 10 * (config.Weapons.EnhancementFragmentCosts[0] * 2 + config.Weapons.EnhancementFragmentCosts[1]);
    outcomes.push({ huntDrops, bossDrops, eliteDrops, greenOrdinary, fragments, bossBlue, exchangeBlue,
      resourceReady: greenOrdinary === 8 && bossDrops >= 1 && exchangeCount >= 1 && fragments >= coreCost });
  }
  cases.push({ element: region.FeaturedElement, profession, count, exchangeCount,
    cyclesMinutes: { hunt: hunt.MeanCycleSeconds / 60, dungeon: weekDungeon.MeanCycleSeconds / 60, elite: weekElite.MeanCycleSeconds / 60 },
    resourceReadyFraction: outcomes.filter(x => x.resourceReady).length / trials,
    metrics: Object.fromEntries(['huntDrops', 'bossDrops', 'eliteDrops', 'greenOrdinary', 'fragments', 'bossBlue', 'exchangeBlue'].map(key => [key, quantiles(outcomes.map(o => o[key]))])) });
}
const report = { trials, budgetHours: budget, assumptions: [
  'Resource sufficiency after 82h of fixed farming plus 2h reserved exploration; not earliest completion time.',
  'Uses measured mean Lv7 hunt/featured dungeon/Lv10 elite times for all ten promotion routes with preset week equipment; observed victories were 539/540.',
  'Assumes four ordinary target sources take the representative Lv7 fight time and share its drop rate; real lower-level sources differ.',
  'Farm one featured dungeon currency, clear reward one token plus account first-clear two; spend all tokens on that exchange direction.',
  'Quality distribution comes from production. Green-or-better raises enhancement capacity; the combat week presets use green weapons and fixed legal enhancement levels.',
  'Does not evolve equipment or level during the route, model first-clear manual input, cross-region travel, potion shortages, or failed deep attempts.',
  'Fragments are available before intermediate enhancement/swap losses; ten skills +2 and ten +1 cost 120 at current prices.',
  'This can reject resource-starved targets, but cannot establish that a new account needs exactly 84h or is guaranteed to finish by then.'
], cases };
fs.writeFileSync('docs/t1-economy-projection.json', JSON.stringify(report, null, 2) + '\n');
console.table(cases.map(c => ({ element: c.element, profession: c.profession, ready: c.resourceReadyFraction,
  fragmentsP10: c.metrics.fragments.p10, fragmentsP50: c.metrics.fragments.p50,
  bossBlueP50: c.metrics.bossBlue.p50, exchanges: c.exchangeCount })));
