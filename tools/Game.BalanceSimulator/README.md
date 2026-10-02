# Balance simulator

Run from the repository root. The simulator uses isolated in-memory SQLite databases and the production battle, monster, reward, weapon and skill services. A single seeded `Random` instance is shared by the services within each sample. Configuration and source hashes are recorded separately from combat results.

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --stages starter --elements Fire --targets normal --runs 1 --seed-start 7213 --trace --output trace-a.json
```

Repeat the command with `--output trace-b.json`. Corresponding samples must have the same `BattleFingerprint` when configuration, seed, loadout and control policy are unchanged. `--trace` includes `RoundTrace`: ordered normalized round facts and a fingerprint for each round. Omit the flag to retain only the encounter fingerprint. Compare round fingerprints to locate the first combat divergence, then compare that round's `Data`.

`--stages deep-entry` supplies the deep-LV1 equipment benchmark: level-10 native professions, ten same-element regional hunt weapons (four from the Lv1 hunt, three from Lv3, three from Lv5), all fully broken through. Slots 1-5 have both weapon skills at Lv10; slots 6-10 have both at Lv7. The first five source slots are Lv1/Lv3/Lv5/Lv1/Lv3; the remaining five are Lv1/Lv3/Lv5/Lv1/Lv5. Primary hunt drops are resolved from the current world and reward definitions. This benchmark measures combat with supplied equipment; it does not simulate farming or make a claim about the time required to acquire the loadout. `Builds.Stats` records base attack, base HP, effective HP and passive weapon bonuses for every member.

`--stages deep-four` supplies the LV4 calibration benchmark confirmed on 2026-10-01: every level-10 character equips two copies of each of the region's two new `t1-deep-` templates, plus two copies of each Lv1/Lv3/Lv5 primary hunt drop. All ten weapons have quality rank 3; all 24 unlocked weapon skills have level 10. This balanced starting loadout contains four deep weapons and six transition weapons per character. It supplies the new templates directly even while their acquisition rewards remain pending. Use `--starting-potions 0 --soul-loadouts none` for the equipment and native-profession baseline. Production `ragefire-heart --depth 4` executes the calibrated fire core cycle: boss-local rounds 6/16/26, four full player rounds, 7% actual direct Water damage to break, heated slash splash, and 20% damage taken for the next three full rounds on success. `MonsterPhaseRounds` records committed phase progress and activation/break/expiry/splash counts. Other elemental LV4 bosses and fire LV2/LV3 still use legacy placeholders. Fire LV5+ inherits LV4 mechanics and the existing uncalibrated stat growth.

`--weapon-elements Fire,Light,Water,Wind,Dark` selects one whole-loadout element for each corresponding party member. The number of entries must equal the party size. It cannot be combined with `--weapon-element`. The default remains the selected dungeon region's element for every member.

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-heart --stages deep-entry --composition knight,priest,mage,hunter,rogue --weapon-elements Fire,Light,Water,Wind,Dark --soul-loadouts none --starting-potions 2 --runs 20 --seed-start 7213 --output deep-fire-lv1.json
```

Direct damage uses the production `CombatDamage.VariancePercent` configuration and the same seeded `Random` shared by combat and rewards. The current application configuration sets 3: each actual target and damage segment independently receives a uniform multiplier from 0.97 to 1.03 after its damage formulas and amplification, before HP deduction. The fractional result rounds upward with probability equal to its fractional part and otherwise downward, avoiding systematic downward rounding. Derived echo damage does not roll again; healing and damage-over-time are unchanged. The room freezes this policy with its other rules, and old snapshots without the field retain zero variance. `DirectDamageVariancePercent` is declared in assumptions, samples and summary grouping; `DirectDamageRounding` explains the rounding policy. A custom `--config` with variance 0 selects deterministic fixed damage and preserves that mode's random stream. Changing variance can change later random choices and traces; repeatability requires the same configuration and seed. Regression fixtures for fixed numbers explicitly use 0, while a dedicated 3% scenario checks actual damage differences and complete trace repeatability.

Trace schema 1 preserves event order, source and target, damage, healing, status changes, remaining active states, skill and consumable cooldowns, healing-potion quotas, operation-potion modifiers, consumable buffs, HP, encounter run and waves. Actor identity uses character slots and monster wave/position, allowing database IDs to differ. Timestamps, global event IDs, storage IDs, optimistic concurrency versions and presentation text are excluded. Wall-clock-derived `CycleSeconds` and report `GeneratedAtUtc` are not fingerprint inputs. `SourceFiles` identify the implementation that produced a run, but are not mixed into the combat fingerprint so a behavior-preserving refactor remains comparable.

Default roles come from configured base professions. Each character equips the native skills actually unlocked at its simulated level: level 1 for `starter`, level 5 for `shop`, level 8 for `entry`, level 10 otherwise. The `Builds` output contains the exact equipped skills. Legacy role aliases remain accepted, but no promotion or inactive combat talent tree is applied. `--talent-builds` can select explicit native skills using existing profile `Role` and `Skills` fields; `Nodes` must be empty. Unknown, unavailable or empty equipped skill sets fail instead of silently producing a skill-free result. Historical reports using removed promotion/talent mechanics should not be treated as equivalent inputs.

`--stages entry` supplies the six entry-dungeon design reference: profession level 8, the region's first hunt weapon at quality rank 3 with both weapon skills at level 10, its second hunt weapon at normal quality with its primary skill at level 4, and eight same-element shop weapons. Native skill Auto healing remains at 75%; the entry preset uses healing potions below 50% HP. This is a supplied equipment benchmark, not an acquisition-path simulation or a promise of an implemented tutorial reward. Repeat farming and first-clear rewards are separate: account admission and Auto are pre-unlocked by the simulator, so its entry farming report excludes the first-clear bonus.

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-chasm --stages entry --composition knight,priest,mage,hunter,rogue --starting-potions 2 --runs 20 --seed-start 7213 --output entry-fire.json
```

For service-recreation replay, retain the same RNG instance (and its consumed state) while reconstructing services or DbContexts. Recreating `new Random(seed)` before each round changes the random stream and is not equivalent to restarting services within the same encounter.

`--players` controls account ownership independently from the character count. It defaults to 1, accepts 1 through 5, and cannot exceed `--party` or the number of characters in `--composition`. Characters are assigned round-robin: five characters with `--players 2` belong to accounts `1,2,1,2,1`. Each account has its own current character and pre-unlocked dungeon admission; every character retains its own Auto eligibility, inventory and rewards. Compare otherwise identical commands with `--players 1`, `2` or `5` to measure the production cooperative drop policy:

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-chasm --stages entry --composition knight,priest,mage,hunter,rogue --players 5 --starting-potions 2 --runs 20 --seed-start 7213 --output entry-fire-five-accounts.json
```

`Players` is included in assumptions, samples and summary grouping. Existing `Gold` and `MeanGold` remain whole-party totals. `Rewards.Characters`, `Rewards.Accounts` and `Rewards.Team` report separate settled gold, experience and ordinary item quantities; account totals aggregate all characters owned by that account. `Summary.MeanGoldPerCharacter` and `MeanOrdinaryItemQuantityPerCharacter` divide the party totals by the character count, while `Summary.Accounts` reports each account's own mean earnings. Ordinary item counts include Base-source consumables, materials, weapons and soul imprints from regular kill/clear rewards, with kind/code breakdowns. Mastery extras, challenge fragments, rare seeds, tutorial and first-clear grants are excluded. Counts measure quantities rather than market value, and pending ledger entries are not reported as received items.

`CoopRewardEvents` records the production event's actual distinct-account count and relative drop bonus. The configured policy adds 10% relative chance per other actual participant account, capped at 40%; multiple characters on one account contribute one account. Participation evidence determines the event count, so the requested `Players` is not itself proof of eligibility. Only base regular kill/clear random item rolls receive this bonus; mastery extra rolls, challenge fragments, rare seeds and first-clear grants do not. Room snapshots preserve their policy. Sampling, seeds and trace field meanings are unchanged; changed drops can consume a different production random stream, so cross-account scenarios need not have identical battle fingerprints.

`Builds.Weapons` records every equipped slot's code, template revision, element, item level, quality, stats and weapon-skill levels/enhancements. Field weapons use descending attack with ordinal code tie-breaking; elite presets choose the strongest same-element drop with that same tie-break. The mining exchange preset uses the current `kobold-mine-depths` exchange. These current presets differ from historical reports that assumed a single elite drop and a `kobold-mine` exchange. Production profession-mechanic validation runs before simulation, including custom `--config` files.

Choose a specific configured dungeon and depth instead of a preset target matrix:

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code kobold-mine-depths --depth 4 --mastery 2 --starting-potions 5 --stages raid --roles knight --runs 5 --seed-start 7213 --trace --output depth-four.json
```

`--dungeon-code` accepts any dungeon in the selected world and cannot be combined with `--targets`. Its region selects the scenario element; use `--weapon-element` to test different weapons. `--depth` defaults to 1 and must fit that dungeon's configured maximum; ordinary dungeons support only LV1. Monster stats and depth profiles come from the production encounter and depth catalogs. The current production LV4 mechanisms continue through LV10; testing LV10 does not imply additional mechanics from LV5 onward. `--config` selects the application configuration and `--world` selects the world file; defaults remain `Game.Server/appsettings.json` and `Game.Server/world.json`. Their actual paths and hashes are recorded. Both files must describe compatible encounters, rewards and preset equipment.

`--mastery` is the initial character mastery from 0 to 4, default 0. The simulator pre-unlocks account admission and character Auto eligibility separately from the requested mastery, then freezes that mastery in the production run-participant record. This is a controlled farming input, not a claim that a real character has reached the dungeon with that history. Positive mastery seeds valid personal progress; zero creates no personal progress row. Clear rewards use the production mastery bonuses and subsequent progress updates. `--starting-potions` supplies that many minor healing potions per actor, from 0 to 1,000,000, default 1000; production per-run quotas and Auto thresholds still apply. It does not simulate buying the stock. These defaults retain the previous plentiful-potion LV1 scenario policy.

Every room captures a production rule snapshot before combat. `RuleRevision` identifies the exact frozen encounter, depth, combat, reward and party rules; repeated runs in that room use its snapshot. `InitialMonsters` reports the configured depth stats before party scaling. The report declares depth, initial mastery and potion stock in both assumptions and samples.

Metrics schema 2 keys `BossSkillUses` by stable `SkillCode`. It counts each boss/round/code once from committed battle effect events, across all boss actors. Canceled skills or skills emitting no effect events are absent, so this is an observed-use count, not an attempted-cast counter. `CharacterDeathEvents` counts positive-HP to zero-HP damage facts; `Casualties` counts characters dead at termination. `PotionsUsed` comes from production healing-potion quota state and `RemainingPotions` from final inventory. Failure categories describe observed terminal state (`PartyDefeated`, `RoundLimit`, `BattleOverWithoutVictory`, `StepLimit`); they do not infer that potion shortage or a particular skill caused the loss. Human-readable failure logs remain available for investigation. Trace schema and seed semantics remain unchanged.

The dedicated simulator tests use isolated in-memory SQLite and temporary custom configuration/world files. They cover an arbitrary Boss at LV1/LV4/LV10, production mastery rewards, defeat facts, invalid inputs and repeatable complete traces:

```powershell
dotnet test tools/Game.BalanceSimulator/Tests/Game.BalanceSimulator.Tests.csproj
```

The checked regression fixture is `Game.Server.Tests/Fixtures/BattleRoundTrace.seed7213.json`: six rounds, seed 7213, two characters attacking at 10, a 50% critical chance on the first actor, monster attacks at 4, configured all-party damage/weakness and a two-round skill cooldown, followed by a second wave. Tests compare complete normalized rounds, not just reruns of the current code. After an intentional semantic change, inspect the first differing round's events/state and then explicitly regenerate the fixture:

```powershell
$env:IDLEGAME_UPDATE_BATTLE_TRACE = '1'
dotnet test Game.Server.Tests --filter FullyQualifiedName~SeededMultiRoundEncounterTrace
Remove-Item Env:/IDLEGAME_UPDATE_BATTLE_TRACE
```

Review the fixture diff before accepting the new baseline, then rerun with the variable absent. Normal tests never update the fixture.

## 火深层 LV2 装备基准

`--stages deep-two` 提供每名角色一把满突破、三条技能 Lv10 的深层武器，以及九把满突破的区域 Lv1／3／5 主掉落武器，各三把。坦克和治疗选择深层生存武器，输出选择深层输出武器；装备槽 1 为深层武器，槽 2～6 的五把过渡武器两条技能 Lv10，槽 7～10 的四把过渡武器两条技能 Lv7。每人共 21 条已解锁装备技能，角色等级为 10；原生职业技能、自动行为和装备词条全部沿用生产计算。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-heart --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Fire,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output fire-lv2.json
```

这是供数值校准使用的装备快照，不是新增的入场检查或掉落途径。`MonsterPhaseRounds` 保存已提交回合的机制进度；结合结构化回合事件可核验首次血量触发、实际水伤、四回合处理窗口和随后三回合奖励。现行结果见 [LV2 参数](../../docs/t1-fire-deep-lv2-balance.json) 与 [模拟记录](../../docs/t1-fire-deep-lv2-simulation.json)。

## 火深层 LV3 装备基准

`--stages deep-three` 提供每名角色各一把深层输出／生存武器，加两把区域 Lv1、三把 Lv3、三把 Lv5 主掉落武器。十把装备均满突破，所有已解锁技能 Lv10，每人共22条装备技能；角色等级10，使用生产职业技能及Auto。装备快照用于数值校准，不增加装备数量入场限制或掉落途径。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-heart --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Water,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output fire-lv3.json
```

LV3首次降至65%血量后，从下一完整玩家回合开始四回合蓄热，攻击+25%、火焰斩额外攻击两名不同存活角色，各为80%攻击。累计5%最大生命的实际水伤可破核，之后三完整回合受到伤害+20%。一次机制、成功奖励、扩散与打断均沿用生产执行器。参数及完整样本索引见 [LV3参数](../../docs/t1-fire-deep-lv3-balance.json) 和 [模拟记录](../../docs/t1-fire-deep-lv3-simulation.json)。
