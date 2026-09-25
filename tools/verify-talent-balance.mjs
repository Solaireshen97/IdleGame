// Verify complete, fresh fixed-seed grids and the builds actually used in combat.
// This is a regression check, not an estimate of live player win rates.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = name => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8').replace(/^\uFEFF/, ''));
const profilesFile = 'docs/talent-build-profiles.json';
const profiles = read(profilesFile);
const names = Object.keys(profiles);
const standard = ['knight', 'priest', 'elementalist', 'marksman', 'trickster'];
const world = read('Game.Server/world.json').World;
const elements = ['Fire', 'Water', 'Earth', 'Wind', 'Light', 'Dark'];
const elementOf = dungeon => {
  const value = world.Regions.find(region => region.Code === dungeon.RegionCode).FeaturedElement;
  return typeof value === 'number' ? elements[value] : value;
};
function sourcePaths(directory) {
  return fs.readdirSync(path.join(root, directory), { withFileTypes: true }).flatMap(entry => {
    const name = `${directory}/${entry.name}`;
    return entry.isDirectory() ? (['bin', 'obj'].includes(entry.name) ? [] : sourcePaths(name)) :
      (entry.name.endsWith('.cs') ? [name] : []);
  });
}
const sources = [
  ...['Game.Server/Services', 'Game.Server/Configuration', 'Game.Shared'].flatMap(sourcePaths),
  'Game.Server/appsettings.json', 'Game.Server/world.json', 'tools/Game.BalanceSimulator/Program.cs', profilesFile
].sort();
const hash = name => crypto.createHash('sha256').update(
  fs.readFileSync(path.join(root, name), 'utf8').replace(/^\uFEFF/, '').replaceAll('\r\n', '\n')).digest('hex').toUpperCase();
const graph = read('docs/talent-prerequisite-audit.json');
assert.equal(graph.SourcesUnchangedDuringRun, true);
assert.equal(graph.Compatibility.Passed, true);
assert.equal(graph.ReferenceBuilds.AllValid, true);
assert.equal(graph.ReferenceBuilds.Count, names.length);
for (const [source, fingerprint] of Object.entries(graph.SourceFiles))
  assert.equal(fingerprint, hash(source), `prerequisite audit: stale ${source}`);
const compositions = names.map(name => standard.map(role => role === profiles[name].Role ? name : role));
assert.equal(names.length, 15, 'Expected three reference builds per class');
for (const role of standard) {
  assert.deepEqual(names.filter(name => profiles[name].Role === role).sort(),
    ['offense', 'survival', 'tactics'].map(branch => `${role}-${branch}`).sort());
}

const metrics = [];
const companionBuilds = new Map();
for (const suite of ['solo', 'party']) {
  const report = read(`docs/talent-balance-${suite}.json`);
  assert.equal(report.SourcesUnchangedDuringRun, true, `${suite}: sources changed during run`);
  assert.ok(Number.isFinite(Date.parse(report.GeneratedAtUtc)), `${suite}: missing generation time`);
  assert.deepEqual(Object.keys(report.SourceFiles).sort(), sources, `${suite}: incomplete source inventory`);
  for (const source of sources) assert.equal(report.SourceFiles[source], hash(source), `${suite}: stale ${source}`);
  const partySize = suite === 'solo' ? 1 : 5;
  const labels = suite === 'solo' ? names : compositions.map(members => members.join('+'));
  const dungeons = world.Dungeons.filter(d => suite === 'solo' ?
    world.Regions.some(r => r.FeaturedDungeonCode === d.Code) || d.DungeonKind === 'Elite' && d.MinimumLevel === 10 :
    d.DungeonKind === 'Dungeon' && d.MinimumLevel === 10);
  const assumptions = report.Assumptions;
  assert.equal(assumptions.RunsPerScenario, 3);
  assert.equal(assumptions.SeedStart, 2001);
  assert.equal(assumptions.PartySize, partySize);
  assert.equal(assumptions.ControlMode, 'auto');
  assert.equal(assumptions.WeaponElement, 'SameAsDungeonRegion');
  assert.equal(assumptions.TalentBuildsFile, profilesFile);
  assert.equal(assumptions.MaxRounds, 250);
  assert.deepEqual(assumptions.SoulLoadouts, [['none']]);
  assert.deepEqual(assumptions.Compositions, suite === 'solo' ? null : compositions);
  assert.equal(dungeons.length, suite === 'solo' ? 12 : 6);
  assert.equal(report.Samples.length, labels.length * dungeons.length * 3);
  const seen = new Set();
  for (const sample of report.Samples) {
    const dungeon = dungeons.find(d => d.Code === sample.Dungeon);
    assert.ok(dungeon, `${suite}: unexpected dungeon ${sample.Dungeon}`);
    assert.ok(labels.includes(sample.Profession), `${suite}: unexpected composition`);
    assert.ok([2001, 2002, 2003].includes(sample.Seed));
    assert.equal(sample.Stage, 'week');
    assert.equal(sample.ControlMode, 'auto');
    assert.equal(sample.PartySize, partySize);
    assert.equal(sample.SoulLoadout, 'none');
    assert.equal(sample.Element, elementOf(dungeon));
    const cell = [sample.Profession, sample.Dungeon, sample.Seed].join('|');
    assert.ok(!seen.has(cell), `${suite}: duplicate ${cell}`);
    seen.add(cell);
    assert.equal(sample.Builds.length, partySize);
    const members = sample.Profession.split('+');
    const builds = [...sample.Builds].sort((a, b) => a.Slot - b.Slot);
    for (let index = 0; index < partySize; index++) {
      const build = builds[index];
      const profile = profiles[members[index]];
      assert.equal(build.Slot, index + 1);
      assert.equal(build.Profession, profile?.Role ?? members[index]);
      assert.equal(build.WeaponElement, sample.Element);
      assert.equal(Object.values(build.TalentRanks).reduce((a, b) => a + b, 0), 9);
      assert.ok(build.EquippedSkills.length >= 1 && build.EquippedSkills.length <= 5);
      assert.equal(new Set(build.EquippedSkills).size, build.EquippedSkills.length);
      if (profile) {
        assert.deepEqual(build.TalentRanks, profile.Nodes, `${cell}: wrong talent build`);
        assert.deepEqual(build.EquippedSkills, profile.Skills, `${cell}: wrong skill order`);
      } else {
        const unchanged = { Nodes: build.TalentRanks, Skills: build.EquippedSkills };
        if (!companionBuilds.has(members[index])) companionBuilds.set(members[index], unchanged);
        assert.deepEqual(unchanged, companionBuilds.get(members[index]), `${cell}: companion build changed`);
      }
    }
    assert.ok(Number.isInteger(sample.Rounds) && sample.Rounds > 0 && sample.Rounds <= 250);
    assert.ok(sample.PotionsUsed >= 0 && sample.Survivors >= 0 && sample.Survivors <= partySize);
    // All reference routes should remain viable with the supplied T1 equipment.
    assert.equal(sample.Victory, true, `${suite}: reference build failed ${cell}`);
    assert.equal(sample.HardEnrageCasts, 0, `${suite}: reference build hit hard enrage ${cell}`);
  }
  for (const name of names) {
    const samples = report.Samples.filter(sample => sample.Profession.split('+').includes(name));
    for (const kind of suite === 'solo' ? ['dungeon', 'elite'] : ['endgame']) {
      const rows = samples.filter(sample => kind !== 'elite' && kind !== 'dungeon' ||
        (world.Dungeons.find(d => d.Code === sample.Dungeon).DungeonKind === 'Elite') === (kind === 'elite'));
      const mean = field => Math.round(rows.reduce((sum, row) => sum + row[field], 0) / rows.length * 100) / 100;
      metrics.push({ Profile: name, Target: kind, Samples: rows.length, Victories: rows.filter(r => r.Victory).length,
        MeanRounds: mean('Rounds'), MeanPotions: mean('PotionsUsed'),
        Casualties: rows.filter(r => r.Survivors < partySize).length,
        SoftEnrageSamples: rows.filter(r => r.SoftEnrageCasts > 0).length });
    }
  }
  console.log(`${suite}: ${report.Samples.length} fresh samples, exact grids and equipped builds verified`);
}
console.table(metrics);
console.log('Talent balance: 810/810 fixed-seed samples passed. See graph audit for all legal builds.');
