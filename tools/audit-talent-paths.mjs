import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

// Enumerates legal purchase sequences using SkillCatalog/SkillService rules.
// This is a prerequisite audit, not a combat-strength or optimal-build ranking.
const repositoryRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const args = process.argv.slice(2);
const option = (name, fallback) => args.includes(name) ? args[args.indexOf(name) + 1] : fallback;
const configPath = option('--config', 'Game.Server/appsettings.json');
const snapshotPath = option('--snapshot', 'Game.Server.Tests/Fixtures/talent-prerequisites-before-accessibility.json');
const profilesPath = option('--builds', 'docs/talent-build-profiles.json');
const outputPath = option('--output', 'docs/talent-prerequisite-audit.json');
const levelLimit = 10;
const pointBudget = 9;
const searchPointLimit = 12;
const absolute = file => path.resolve(repositoryRoot, file);
const readText = file => fs.readFileSync(absolute(file), 'utf8').replace(/^\uFEFF/, '');
const readJson = file => JSON.parse(readText(file));
const sourcePaths = [configPath, snapshotPath, profilesPath, 'tools/audit-talent-paths.mjs',
  'Game.Server/Services/SkillCatalog.cs', 'Game.Server/Services/SkillService.cs',
  'Game.Server/Services/ProgressionService.cs', 'Game.Server/Configuration/SkillOptions.cs',
  'Game.Server.Tests/SkillTalentTreeTests.cs'];
const sourceHashes = () => Object.fromEntries([...new Set(sourcePaths)].sort().map(file => [file,
  crypto.createHash('sha256').update(readText(file).replace(/\r\n/g, '\n')).digest('hex').toUpperCase()]));
const fingerprints = sourceHashes();
const config = readJson(configPath);
const snapshot = readJson(snapshotPath);
const profiles = readJson(profilesPath);
const groupedNodes = Object.groupBy(config.Skills.TalentNodes, node => node.ProfessionCode);
const oldGroupedNodes = Object.groupBy(snapshot.TalentNodes, node => node.ProfessionCode);
const numericalEffects = new Set(['MaxHpPercent', 'NormalAttackPercent', 'SkillDamagePercent',
  'HealingDonePercent', 'HealingReceivedPercent', 'SkillCriticalChancePercent',
  'HolyBoltPercent', 'HealSkillPercent']);

function enumerate(sourceNodes) {
  const nodes = [...sourceNodes].sort((a, b) => a.Tier - b.Tier || a.Column - b.Column || a.Code.localeCompare(b.Code));
  const indices = new Map(nodes.map((node, index) => [node.Code, index]));
  if (indices.size !== nodes.length) throw new Error('Duplicate talent node code');
  for (const node of nodes) {
    if ((node.Cost ?? 1) !== 1 || !Number.isInteger(node.MaxRank ?? 1) || (node.MaxRank ?? 1) < 1)
      throw new Error(`Unsupported rank/cost for ${node.Code}`);
    for (const prerequisite of [...(node.Prerequisites ?? []), ...(node.AnyPrerequisites ?? [])]) {
      const parent = nodes[indices.get(prerequisite)];
      if (!parent || parent.Tier >= node.Tier) throw new Error(`Invalid prerequisite ${node.Code} -> ${prerequisite}`);
    }
  }
  const keyFor = ranks => ranks.join(',');
  const layers = [new Map([[keyFor(nodes.map(() => 0)), { ranks: nodes.map(() => 0), earliestLevel: 1, purchaseOrder: [] }]])];
  for (let spent = 0; spent < searchPointLimit; spent++) {
    const next = new Map();
    for (const state of layers[spent].values()) {
      nodes.forEach((node, index) => {
        const rank = state.ranks[index];
        if (rank >= (node.MaxRank ?? 1) || spent < (node.RequiredTreePoints ?? 0)) return;
        const fullRank = code => state.ranks[indices.get(code)] >= (nodes[indices.get(code)].MaxRank ?? 1);
        if ((node.Prerequisites ?? []).some(code => !fullRank(code))) return;
        if (node.AnyPrerequisites?.length && !node.AnyPrerequisites.some(fullRank)) return;
        if (node.ExclusiveGroup && nodes.some((other, otherIndex) => otherIndex !== index &&
          other.ExclusiveGroup === node.ExclusiveGroup && state.ranks[otherIndex] > 0)) return;
        const ranks = [...state.ranks];
        ranks[index]++;
        // One talent point per level after level one; unspent points may be banked.
        const earliestLevel = Math.max(state.earliestLevel, (node.RequiredLevel ?? 1) + rank, spent + 2);
        const key = keyFor(ranks);
        if (!next.has(key) || next.get(key).earliestLevel > earliestLevel)
          next.set(key, { ranks, earliestLevel, purchaseOrder: [...state.purchaseOrder, node.Code] });
      });
    }
    layers.push(next);
  }
  const atBudget = [...layers[pointBudget].values()].filter(state => state.earliestLevel <= levelLimit);
  const allWithinBudget = layers.slice(0, pointBudget + 1).flatMap(layer => [...layer.values()])
    .filter(state => state.earliestLevel <= levelLimit);
  const ranksObject = state => Object.fromEntries(nodes.flatMap((node, index) =>
    state.ranks[index] > 0 ? [[node.Code, state.ranks[index]]] : []));
  function firstMatch(predicate) {
    for (let spent = 0; spent < layers.length; spent++) {
      const state = [...layers[spent].values()].filter(predicate).sort((a, b) => a.earliestLevel - b.earliestLevel)[0];
      if (state) return { Points: spent, EarliestLevel: state.earliestLevel, ReachableAtLevelTen: state.earliestLevel <= levelLimit,
        Nodes: ranksObject(state), PurchaseOrder: state.purchaseOrder };
    }
    return null;
  }
  return { nodes, indices, layers, atBudget, allWithinBudget, keyFor, ranksObject, firstMatch };
}

function summarize(professionCode, tree) {
  const { nodes, indices, atBudget, allWithinBudget, firstMatch } = tree;
  const capstoneIndices = nodes.map((node, index) => node.Tier === Math.max(...nodes.map(n => n.Tier)) ? index : -1).filter(i => i >= 0);
  const skillIndices = nodes.map((node, index) => node.SkillCode ? index : -1).filter(i => i >= 0);
  const mechanicIndices = nodes.map((node, index) => node.SkillCode || !numericalEffects.has(node.EffectCode) ? index : -1).filter(i => i >= 0);
  const signature = (state, selectedIndices) => selectedIndices.filter(index => state.ranks[index] > 0).map(index => nodes[index].Code);
  const capstoneSets = Object.groupBy(atBudget, state => signature(state, capstoneIndices).join('+'));
  const mechanicSets = Object.groupBy(atBudget, state => signature(state, mechanicIndices).join('+'));
  const nodeReports = nodes.map((node, index) => {
    const containing = atBudget.filter(state => state.ranks[index] > 0);
    const minimum = firstMatch(state => state.ranks[index] > 0);
    const forced = containing.length === 0 ? {} : Object.fromEntries(nodes.flatMap((other, otherIndex) => {
      if (otherIndex === index) return [];
      const rank = Math.min(...containing.map(state => state.ranks[otherIndex]));
      return rank > 0 ? [[other.Code, rank]] : [];
    }));
    return { Code: node.Code, Name: node.Name, Tier: node.Tier, Branch: node.BranchCode,
      RequiredLevel: node.RequiredLevel, RequiredTreePoints: node.RequiredTreePoints ?? 0,
      AllPrerequisites: node.Prerequisites ?? [], AnyPrerequisites: node.AnyPrerequisites ?? [],
      MinimumPoints: minimum?.Points ?? null, EarliestLevel: minimum?.EarliestLevel ?? null,
      MinimumPurchaseOrder: minimum?.PurchaseOrder ?? [],
      RankPaths: Array.from({ length: node.MaxRank ?? 1 }, (_, rank) => ({ Rank: rank + 1,
        Minimum: firstMatch(state => state.ranks[index] >= rank + 1) })),
      NinePointBuildsContainingNode: containing.length,
      NinePointRankDistribution: Object.fromEntries(Array.from({ length: (node.MaxRank ?? 1) + 1 }, (_, rank) =>
        [rank, atBudget.filter(state => state.ranks[index] === rank).length])),
      MandatoryOtherNodesInNinePointBuilds: forced };
  });
  const centralSkills = nodes.filter(node => node.Tier === 2 && node.Column === 2 && node.SkillCode);
  return { Profession: professionCode, NodeCount: nodes.length, NinePointBuildCount: atBudget.length,
    MechanicalFeatureSetCount: Object.keys(mechanicSets).length,
    MechanicalFeatureDefinition: 'Distinct learned talent skills and non-numerical talent mechanics; passive stat ranks are omitted. These are choices, not measured combat strength.',
    Nodes: nodeReports,
    ByTier: [...new Set(nodes.map(node => node.Tier))].map(tier => ({ Tier: tier,
      NodeCount: nodes.filter(node => node.Tier === tier).length,
      ReachableNodesAtLevelTen: nodeReports.filter(node => node.Tier === tier && node.EarliestLevel !== null && node.EarliestLevel <= levelLimit).map(node => node.Code) })),
    ByLevel: Array.from({ length: levelLimit }, (_, offset) => {
      const level = offset + 1;
      const spent = level - 1;
      return { Level: level, AvailablePoints: spent,
        FullyAllocatedBuilds: [...tree.layers[spent].values()].filter(state => state.earliestLevel <= level).length,
        ReachableNodes: nodes.filter((node, index) => allWithinBudget.some(state =>
          state.earliestLevel <= level && state.ranks[index] > 0)).map(node => node.Code) };
    }),
    Capstones: capstoneIndices.map(index => nodes[index].Code),
    MaxCapstonesInNinePoints: Math.max(...atBudget.map(state => signature(state, capstoneIndices).length)),
    AllCapstonesMinimum: firstMatch(state => capstoneIndices.every(index => state.ranks[index] > 0)),
    CapstoneCombinations: Object.entries(capstoneSets).map(([key, states]) => ({
      Codes: key.length ? key.split('+') : [], NinePointBuilds: states.length,
      Minimum: firstMatch(state => signature(state, capstoneIndices).join('+') === key) })),
    AllTalentSkillsMinimum: firstMatch(state => skillIndices.every(index => state.ranks[index] > 0)),
    AllTalentSkillsAndOneCapstoneMinimum: firstMatch(state => skillIndices.every(index => state.ranks[index] > 0) &&
      capstoneIndices.some(index => state.ranks[index] > 0)),
    RoutesWithoutCentralSkills: centralSkills.map(hub => ({ ExcludedNode: hub.Code,
      NinePointBuilds: atBudget.filter(state => state.ranks[indices.get(hub.Code)] === 0).length,
      Targets: nodes.filter(node => node.SkillCode && node.Code !== hub.Code || capstoneIndices.includes(indices.get(node.Code)))
        .map(node => ({ Code: node.Code, Minimum: firstMatch(state => state.ranks[indices.get(hub.Code)] === 0 && state.ranks[indices.get(node.Code)] > 0) })) })) };
}

const currentTrees = Object.fromEntries(Object.entries(groupedNodes).map(([profession, nodes]) => [profession, enumerate(nodes)]));
const compatibility = Object.entries(oldGroupedNodes).map(([profession, oldNodes]) => {
  const previous = enumerate(oldNodes);
  const current = currentTrees[profession];
  if (!current) throw new Error(`Profession removed since snapshot: ${profession}`);
  let inaccessible = 0;
  let delayed = 0;
  let statesChecked = 0;
  let oldNinePointBuildsLost = 0;
  const examples = [];
  for (let spent = 0; spent <= pointBudget; spent++) {
    for (const state of previous.layers[spent].values()) {
      if (state.earliestLevel > levelLimit) continue;
      statesChecked++;
      const oldRanks = previous.ranksObject(state);
      const removedNode = Object.keys(oldRanks).some(code => !current.indices.has(code));
      const mappedKey = current.keyFor(current.nodes.map(node => oldRanks[node.Code] ?? 0));
      const after = removedNode ? undefined : current.layers[spent].get(mappedKey);
      if (!after || after.earliestLevel > levelLimit) {
        inaccessible++;
        if (spent === pointBudget) oldNinePointBuildsLost++;
      } else if (after.earliestLevel > state.earliestLevel) delayed++;
      if ((!after || after.earliestLevel > state.earliestLevel) && examples.length < 5)
        examples.push({ Nodes: oldRanks, PreviousEarliestLevel: state.earliestLevel, NewEarliestLevel: after?.earliestLevel ?? null });
    }
  }
  return { Profession: profession, OldNinePointBuilds: previous.atBudget.length,
    CurrentNinePointBuilds: current.atBudget.length, OldStatesCheckedThroughNinePoints: statesChecked,
    InaccessibleOldStates: inaccessible, DelayedOldStates: delayed, OldNinePointBuildsLost: oldNinePointBuildsLost,
    Passed: inaccessible === 0 && delayed === 0, Counterexamples: examples };
});

const validatedProfiles = Object.entries(profiles).map(([name, profile]) => {
  const promotion = config.Skills.Professions.find(profession => profession.Code === profile.Role && profession.IsPromotion);
  if (!promotion) throw new Error(`${name}: unknown promotion ${profile.Role}`);
  const base = config.Skills.Professions.find(profession => profession.Code === promotion.BaseProfessionCode);
  const tree = currentTrees[base.Code];
  for (const [code, rank] of Object.entries(profile.Nodes)) {
    const node = tree.nodes[tree.indices.get(code)];
    if (!node || !Number.isInteger(rank) || rank < 1 || rank > (node.MaxRank ?? 1)) throw new Error(`${name}: invalid talent ${code}:${rank}`);
  }
  const spent = Object.values(profile.Nodes).reduce((sum, rank) => sum + rank, 0);
  if (spent !== pointBudget) throw new Error(`${name}: expected nine points, found ${spent}`);
  const key = tree.keyFor(tree.nodes.map(node => profile.Nodes[node.Code] ?? 0));
  const state = tree.layers[pointBudget].get(key);
  if (!state || state.earliestLevel > levelLimit) throw new Error(`${name}: no legal level-ten purchase sequence`);
  const learned = new Set([...base.StartingSkills, ...promotion.StartingSkills,
    ...tree.nodes.filter(node => profile.Nodes[node.Code] > 0 && node.SkillCode).map(node => node.SkillCode)]);
  if (!profile.Skills?.length || profile.Skills.length > 5 || new Set(profile.Skills).size !== profile.Skills.length ||
      profile.Skills.some(code => !learned.has(code))) throw new Error(`${name}: invalid or unlearned skill loadout`);
  return { Profile: name, Role: profile.Role, BaseProfession: base.Code, SpentPoints: spent,
    EarliestTalentLevel: state.earliestLevel, PromotionLevel: promotion.RequiredLevel,
    PurchaseOrder: state.purchaseOrder, Capstones: tree.nodes.filter(node => node.Tier === 5 && profile.Nodes[node.Code] > 0).map(node => node.Code),
    EquippedSkills: profile.Skills, LearnedSkillsNotEquipped: [...learned].filter(code => !profile.Skills.includes(code)) };
});

const report = { GeneratedAtUtc: new Date().toISOString(), SourceFiles: fingerprints,
  SourcesUnchangedDuringRun: JSON.stringify(fingerprints) === JSON.stringify(sourceHashes()),
  Assumptions: { LevelLimit: levelLimit, PointBudget: pointBudget, SearchPointLimit: searchPointLimit,
    Rules: 'Cost=1; level must be RequiredLevel+current rank; prerequisites require full parent rank; all required and one optional parent; mutually exclusive groups; one point per level after level one. Banking points is allowed.',
    Limits: 'Searches up to twelve points only to expose capstone opportunity costs. Builds needing level 11+ are not level-ten builds. Counts and feature sets do not prove equal combat power.' },
  Compatibility: { Snapshot: snapshotPath, SourceCommit: snapshot.SourceCommit ?? null,
    Passed: compatibility.every(item => item.Passed), Professions: compatibility },
  Professions: Object.entries(currentTrees).map(([profession, tree]) => summarize(profession, tree)),
  ReferenceBuilds: { Source: profilesPath, Count: validatedProfiles.length, AllValid: true, Profiles: validatedProfiles } };
if (!report.SourcesUnchangedDuringRun) throw new Error('Sources changed while auditing; rerun the audit');
fs.writeFileSync(absolute(outputPath), `${JSON.stringify(report, null, 2)}\n`, 'utf8');
for (const profession of report.Professions)
  console.log(`${profession.Profession}: ${profession.NinePointBuildCount} nine-point builds, ${profession.MechanicalFeatureSetCount} mechanic sets, max ${profession.MaxCapstonesInNinePoints} capstones.`);
console.log(`Reference builds: ${validatedProfiles.length} valid. Snapshot compatibility: ${report.Compatibility.Passed ? 'passed' : 'FAILED'}. Saved ${outputPath}`);
if (!report.Compatibility.Passed) process.exitCode = 1;
