# Balance simulator

2026-10-02：六本LV5～LV10毕业挑战规则已接入，保留LV4机制，每层生命+15%、攻击+10%。本轮没有进行LV5～LV10战斗模拟；下文LV1～LV4样本和源哈希是当时标定记录，不能当作挑战层通关或效率保证。挑战固定碎片为10／20／30／40／50／60；逐层每角色首通石为1／1／1／2／2／3，高层补领低层未领。见[框架第6节](../../docs/dungeon-depth-mastery-framework.md#6-lv5-与通用突破材料)及[规则记录](../../docs/t1-deep-challenge-rules.json)。

Run from the repository root. The simulator uses isolated in-memory SQLite databases and the production battle, monster, reward, weapon and skill services. A single seeded `Random` instance is shared by the services within each sample. Configuration and source hashes are recorded separately from combat results.

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --stages starter --elements Fire --targets normal --runs 1 --seed-start 7213 --trace --output trace-a.json
```

Repeat the command with `--output trace-b.json`. Corresponding samples must have the same `BattleFingerprint` when configuration, seed, loadout and control policy are unchanged. `--trace` includes `RoundTrace`: ordered normalized round facts and a fingerprint for each round. Omit the flag to retain only the encounter fingerprint. Compare round fingerprints to locate the first combat divergence, then compare that round's `Data`.

`--stages deep-entry` supplies the deep-LV1 equipment benchmark: level-10 native professions, ten same-element regional hunt weapons (four from the Lv1 hunt, three from Lv3, three from Lv5), all fully broken through. Slots 1-5 have both weapon skills at Lv10; slots 6-10 have both at Lv7. The first five source slots are Lv1/Lv3/Lv5/Lv1/Lv3; the remaining five are Lv1/Lv3/Lv5/Lv1/Lv5. Primary hunt drops are resolved from the current world and reward definitions. This benchmark measures combat with supplied equipment; it does not simulate farming or make a claim about the time required to acquire the loadout. `Builds.Stats` records base attack, base HP, effective HP and passive weapon bonuses for every member.

`--stages deep-four` supplies the LV4 calibration benchmark confirmed on 2026-10-01: every level-10 character equips two copies of each of the region's two new `t1-deep-` templates, plus two copies of each Lv1/Lv3/Lv5 primary hunt drop. All ten weapons have quality rank 3; all 24 unlocked weapon skills have level 10. This balanced starting loadout contains four deep weapons and six transition weapons per character. It supplies the templates directly; their acquisition rewards are now integrated. Use `--starting-potions 0 --soul-loadouts none` for the equipment and native-profession baseline. Production `ragefire-heart --depth 4` executes the calibrated fire core cycle: boss-local rounds 6/16/26, four full player rounds, 7% actual direct Water damage to break, heated slash splash, and 20% damage taken for the next three full rounds on success. `MonsterPhaseRounds` records committed phase progress and activation/break/expiry/splash counts. Water LV4 now uses Earth-driven deep-cold cycles at boss-local rounds 6/16/26, four full player rounds, and three full reward rounds after personal removal. Earth LV4 uses boss-local rounds 6/14/22/30, five full player rounds, 6% actual direct Wind damage, persistent resonance capped at three, and three full reward rounds after a break. Wind LV4 uses boss-local rounds 6/16/26, four full player rounds, initial four static stacks and +3 at the first two phase-round ends (cap five); three Fire actors remove ten stacks over four rounds and gain three full reward rounds starting next round, subject to boss death. Light LV4 uses boss-local rounds 6/16/26 with six reflection stacks, amplification at the first three phase-round ends, independent dispel or Dark-hit removal, and three full reward rounds. Dark LV4 now uses boss-local rounds 6/16/26 with four complete player rounds per cycle, 6% actual direct Light damage or one effective cleanse to remove all poison and erosion, and three full reward rounds. Poison grows from one to four stacks; three/four stacks lower maximum life by 10%/20%, and unresolved growth to five kills the locked front actor. Each cycle resets progress, target binding and snapshots. All six dungeons now have calibrated LV1–LV4 panels and real mechanics; LV5+ growth remains uncalibrated. Fire LV2/LV3 have calibrated single HP-triggered phases. LV5–LV10 inherits LV4 mechanics with adopted +15% HP / +10% ATK per-depth challenge growth; no challenge combat calibration or Auto guarantee.

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

## 水深层 LV1 校准

`frostspring-throne --depth 1 --stages deep-entry` 使用相同十槽过渡装备基准。水区域均衡盘主样本30/30全员通关，平均约30分钟；土T、光奶配水/风/暗输出及火/风/暗输出的两组混合样本各10/10全员通关，分别约29.2和31分钟。均允许每人两次治疗药水，不使用魂印、合剂或操作药水。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code frostspring-throne --depth 1 --stages deep-entry --composition knight,priest,mage,hunter,rogue --starting-potions 2 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output water-lv1.json
```

水本基础技能采用`UseEncounterLocalSkillClock`，初始冷却按每只怪首次预告时的局部回合计算；该选项默认关闭，旧配置与冻结快照保持原语义。后续冷却仍按已提交的全副本回合记录。短期寒冷持续之后两个角色行动回合，取较强值、不叠层，可净化；当前攻击降低不直接影响固定值/生命比例治疗。水LV1～LV4已标定；LV5+继承LV4真实机制并沿用未校准的占位数值成长。

参数见 [水LV1报告](../../docs/t1-water-deep-lv1-balance.json)，样本与逐回合指纹见 [模拟记录](../../docs/t1-water-deep-lv1-simulation.json)，完整事实保存在同目录`t1-water-deep-lv1-round-traces.json.gz`压缩JSON归档。

## 水深层 LV2 校准

`frostspring-throne --depth 2 --stages deep-two` 沿用每人一把满突破、三技能Lv10的区域深层武器，加九把Lv1/3/5过渡武器（各三把、均满突破，五把双技能Lv10、四把双技能Lv7）。T和奶使用各区生存深层模板，输出使用输出模板。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code frostspring-throne --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Fire,Wind,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output water-lv2.json
```

一次70%血量触发、四完整回合霜缚、三层每层攻击-5个百分点；一名土角色三回合可全队清零，单人净化立即清零。个人主动清零后，从下一完整回合起获得三回合直接伤害+15%及基础寒冷免疫；自然消散无奖励。周期伤害、反射与衍生追击不重复破冰。内部破冰计次与待奖励记录不出现在玩家状态列表。

水机制的`MonsterPhaseRounds.Breaks`表示已成功处理的角色数，`LinkedHits`表示实际移除过霜缚的破冰行动数，`Expiries`表示固定窗口已结束，与火核心统计语义不同。完整阶段窗口、个人奖励及冷却审计见 [水LV2参数](../../docs/t1-water-deep-lv2-balance.json) 和 [模拟记录](../../docs/t1-water-deep-lv2-simulation.json)。水LV2～LV4现按土克水的既有规则处理霜缚；土打水+25%，水打土-25%。火不再是水Boss的专属处理属性，跨属性队伍总时长仍不能单独证明机制收益。

## 火深层 LV2 装备基准

`--stages deep-two` 提供每名角色一把满突破、三条技能 Lv10 的深层武器，以及九把满突破的区域 Lv1／3／5 主掉落武器，各三把。坦克和治疗选择深层生存武器，输出选择深层输出武器；装备槽 1 为深层武器，槽 2～6 的五把过渡武器两条技能 Lv10，槽 7～10 的四把过渡武器两条技能 Lv7。每人共 21 条已解锁装备技能，角色等级为 10；原生职业技能、自动行为和装备词条全部沿用生产计算。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-heart --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Fire,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output fire-lv2.json
```

这是供数值校准使用的装备快照，不是新增的入场检查或掉落途径。`MonsterPhaseRounds` 保存已提交回合的机制进度；结合结构化回合事件可核验首次血量触发、实际水伤、四回合处理窗口和随后三回合奖励。现行结果见 [LV2 参数](../../docs/t1-fire-deep-lv2-balance.json) 与 [模拟记录](../../docs/t1-fire-deep-lv2-simulation.json)。

## 水深层 LV3 校准

水本同样使用`deep-three`：每人各一把区域深层输出／生存武器，加八把满技能过渡武器，十把均满突破且全部技能Lv10；不使用魂印、合剂或起始治疗药水。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code frostspring-throne --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Earth,Fire,Dark --starting-potions 0 --soul-loadouts none --runs 10 --seed-start 7213 --trace --output water-lv3.json
```

一次70%生命触发、四回合窗口，初始四层霜缚，前三回合末未清零者各加一层，五层触发下一回合冰封一次。两土三回合可全队清零，一土可压住增长。个人主动清零后，从下一完整回合起获得三回合直接伤害+20%与基础寒冷免疫；自然结束直接清理剩余霜缚与冰封，无奖励。

冰封暂停主动技能（含魂印）与攻击，冷却、周期效果及药水继续结算；土伤降低到五层以下或净化解除冰封，已跳过的行动当回合不补发。阶段增长、待冰封与角色冰封计次使用绑定Boss的持久化状态，无新增数据库结构；内部计次不显示为玩家状态。当前两土重标定见 [水属性修正与LV4报告](../../docs/t1-water-deep-lv4-balance.json)；原 [水LV3参数](../../docs/t1-water-deep-lv3-balance.json) 与 [模拟记录](../../docs/t1-water-deep-lv3-simulation.json) 保留火属性条件下的历史样本。跨属性总时长同时受现有属性克制和区域配装影响。

## 火深层 LV3 装备基准

`--stages deep-three` 提供每名角色各一把深层输出／生存武器，加两把区域 Lv1、三把 Lv3、三把 Lv5 主掉落武器。十把装备均满突破，所有已解锁技能 Lv10，每人共22条装备技能；角色等级10，使用生产职业技能及Auto。装备快照用于数值校准，不增加装备数量入场限制或掉落途径。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code ragefire-heart --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Water,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output fire-lv3.json
```

LV3首次降至65%血量后，从下一完整玩家回合开始四回合蓄热，攻击+25%、火焰斩额外攻击两名不同存活角色，各为80%攻击。累计5%最大生命的实际水伤可破核，之后三完整回合受到伤害+20%。一次机制、成功奖励、扩散与打断均沿用生产执行器。参数及完整样本索引见 [LV3参数](../../docs/t1-fire-deep-lv3-balance.json) 和 [模拟记录](../../docs/t1-fire-deep-lv3-simulation.json)。

## 土LV1基础校准

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code kobold-mine-depths --depth 1 --stages deep-entry --composition knight,priest,mage,hunter,rogue --starting-potions 2 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output earth-lv1.json
```

默认主队五名角色使用土区域Lv1/3/5主掉落十槽均衡盘，满突破，五把两条技能Lv10、五把两条技能Lv7。生产配置五波单怪，Boss局部第2回合起每4回合尝试碎岩重镐，第4回合起每4回合尝试矿顶崩落，第3回合起每8回合施放岩甲覆身。裂甲10%易伤、两回合、不叠层；岩甲15%减伤、两回合自然结束、可驱散，均无属性处理门槛。主队30/30全员通关，平均30.1分钟；土/光/火/土/暗无风混合队及移除法师`mage-spellbreak`的无驱散队各10/10全员通关。土LV5以上数值成长仍未校准，当前参数见 [土LV1报告](../../docs/t1-earth-deep-lv1-balance.json)。

`BossSkillUses`按已提交技能事件计数，排除原技能状态的消耗、移除与自然到期；岩甲结束不会计为再次施放。该统计修正不改变战斗结算。完整轨迹、窗口与药水上限审计见 [土LV1模拟记录](../../docs/t1-earth-deep-lv1-simulation.json)。

## 土LV2矿脉护甲校准

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code kobold-mine-depths --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Fire,Wind,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output earth-lv2.json
```

每人一把区域深层武器（T/奶选生存，输出选输出）、九把区域Lv1/3/5主掉落各三把，均满突破；深层三技能Lv10，五把过渡双技能Lv10、四把双技能Lv7。Boss一次70%血量触发、五完整回合30%减伤，累计2%最大生命的实际风属性直接伤害可破甲，随后三完整回合受到伤害+20%。原Boss周期小岩甲被替代，LV2不含攻击增长与重复护甲。主队30/30全员通关且成功处理；风法师与无风对照各10/10全员通关，后者自然结束无奖励。生命、职业冷却及装备成长沿五波连续推进。

土阶段的`MonsterPhaseRounds.ElementDamage`与`DamageElement=Wind`记录实际风伤进度；`WaterDamage`保持0。火、水既有观察字段保持原输出。底层复用既有持久化进度列，无新增数据库结构；奖励和阶段记录随回合原子保存。结果、预览与接入配置的一致性及旧本回归见 [土LV2报告](../../docs/t1-earth-deep-lv2-balance.json) 与 [模拟记录](../../docs/t1-earth-deep-lv2-simulation.json)。

## 土LV3地脉共鸣校准

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code kobold-mine-depths --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Wind,Wind,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output earth-lv3.json
```

土LV3按每人两把不同深层武器（输出、生存各一把）与八把过渡武器、全部满突破满技能校准。五波独立生命50万／60万／68万／77万／130万，攻击180／230／265／300／330。首领一次70%生命触发、五回合矿脉护甲；累计4%最大生命的实际风属性直接伤害（52000点）破甲。护甲存在的回合末积累地脉共鸣，每层攻击+10%、最多三层；成功破甲立即解除护甲与共鸣，下一完整回合起三回合受到伤害+20%。护甲自然结束保留已有共鸣至Boss倒下，停止增长。土T、光奶、风法师、风猎人、暗盗贼基准30/30全员通关，平均30.4分钟，全部第4～5回合破甲，无药水、魂印或合剂；最低坦克血线约7.6%。一风队10/10通关但均有减员；同配装两风强制超时对照10/10通关且均有减员，说明解法提供实际生存收益。

成长在敌方行动后结算，达到三层后不再刷新；普通驱散不能清除共鸣。共鸣为可见、绑定Boss的Encounter状态，死亡与离场清理；重复回合末与断线恢复不多叠层，无新增数据库结构。统计区分通关和全员存活。同配装强制超时对照只提高破甲门槛，失去的收益同时包含共鸣清除、提前解除护甲与破甲增伤；不能只归因于其中一项。一风对照还改变属性与区域装备。参数、64局完整轨迹和旧本逐回合一致性见 [土LV3报告](../../docs/t1-earth-deep-lv3-balance.json) 与 [模拟记录](../../docs/t1-earth-deep-lv3-simulation.json)。

## 土LV4深岩循环校准

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code kobold-mine-depths --depth 4 --stages deep-four --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Wind,Wind,Wind --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output earth-lv4.json
```

土LV4深岩循环已按每人四把深层武器（输出、生存各两把）与六把过渡武器、全部满突破满技能校准。五波生命53万／63万／73万／82万／135万，攻击200／250／290／325／350。首领自身第6回合起每8回合生成一次30%减伤护甲，窗口五回合，累计6%最大生命的实际风属性直接伤害（81000点）破甲。每轮风伤重计，共鸣最多三层，新轮保留已有共鸣；成功破甲立即清除护甲与全部共鸣，下一完整回合起三回合受到伤害+20%。第五回合破甲也可完整获得奖励后再进入下一轮，Boss倒下直接结束剩余窗口。土T、光奶、风法师、风猎人、风盗贼基准30/30全员通关，平均30.4分钟，91次破甲、无超时，最低坦克血线10.65%；无药水、魂印或合剂。两风对照10/10通关、4/10全员存活；同配装三风强制超时对照10/10通关、3/10全员存活，保留硬抗与强化配装空间。

逐回合审计验证固定局部时钟、每轮风伤归零、实际伤害进度、共鸣跨轮保留与清除、成功奖励和Boss死亡取消阶段。完整处理窗口与击杀结束窗口分别统计，不将最后一轮直接击杀记成超时。两风对照改变属性与区域配装；同配装强制超时仅修改破甲门槛，差异同时包含共鸣、护甲时长与奖励。结果、64局完整轨迹、接入一致性与土LV3／火LV4／水LV4回归见 [土LV4报告](../../docs/t1-earth-deep-lv4-balance.json) 与 [模拟记录](../../docs/t1-earth-deep-lv4-simulation.json)。旧冻结LV2/LV3房间的周期字段保持默认零，继续一次血量触发。

## 水LV4与处理属性修正

水LV2～LV4的属性处理统一为土属性直接伤害，每名角色每回合为全队消除一层。LV4主队使用土T、光奶、土法师、土猎人、暗盗贼；坦克也计入三名土角色。净化保留，成功清零下一完整回合起获得三回合最终直接伤害+20%。第6/16/26回合周期激活，初始四层、阶段前三回合末增长两层，五层冰封一次，个人处理与冰封记录每阶段重置。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code frostspring-throne --depth 4 --stages deep-four --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Earth,Earth,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output water-lv4.json
```

当前LV4及修正后的LV2/LV3参数见 [水LV4最新报告](../../docs/t1-water-deep-lv4-balance.json)。旧水LV2/LV3报告与原始轨迹保留为火属性处理条件下的历史记录；当前验证以LV4报告为准。旧冻结房间缺少`RemovalElement`字段时仍按原火属性处理，不改写进行中的房间。

## 风深层 LV1 校准

五波单怪使用风区域十槽过渡装均衡基准，全部满突破、五把双技能Lv10加五把双技能Lv7，计入全部装备词条与原生技能。30次主样本全部全员通关，平均30.3分钟；无火混合队及显式移除法术反制的无驱散队各10/10全员通关。每人起始两瓶治疗药水、每局最多使用两次，无魂印、合剂和操作药水。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code windfury-spire --depth 1 --stages deep-entry --composition knight,priest,mage,hunter,rogue --starting-potions 2 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output wind-lv1.json
```

无驱散复现时，`--talent-builds`文件提供名为`mage-no-dispel`、Role为`mage`、Nodes为空、Skills移除`mage-spellbreak`的预设，并以`--composition knight,priest,mage-no-dispel,hunter,rogue`显式选中；仅加载文件并继续使用`mage`不会替换技能。预设内容保存在[模拟记录](../../docs/t1-wind-deep-lv1-simulation.json)的ReproductionInputs中。

电翎穿刺预告时均匀随机选择并保存一名存活角色，重复预览和服务重建不重新选取；目标在施放前死亡时取消该次攻击。Boss俯冲与风暴局部第2/4回合起各间隔4回合，战歌第3回合起间隔8回合，攻击+15%、持续之后两个完整回合，可打断或驱散。LV1没有静电、雷暴或旧终局狂暴。风LV2～LV4静电、雷暴与循环现均已校准；LV5+继承LV4循环，数值成长待统一设计。

参数见[风LV1报告](../../docs/t1-wind-deep-lv1-balance.json)。主30次及无火10次样本使用隔离候选配置与生产服务，正式配置5次主样本及3次混合样本与对应候选逐回合一致；无驱散10次直接使用正式配置。完整67条轨迹保存在同目录`t1-wind-deep-lv1-round-traces.json.gz`，包含复核和火／水／土各3次LV4回归，不将复用种子视为独立样本。

## 风深层 LV2 校准

风LV2使用同一`deep-two`基准：每名角色一把三技能Lv10深层武器和九把Lv1/3/5主掉落（各三把、均满突破），五把双技能Lv10，四把双技能Lv7；土T、光奶、水法师、火猎人、暗盗贼，不供应起始药水、不使用魂印、合剂或操作药水。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code windfury-spire --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Fire,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output wind-lv2.json
```

一次70%生命触发，下一完整玩家回合开启四回合静电领域，初始两层，前两个回合末各增长一层。每名角色每回合首次有效火属性直接伤害消一层，职业机制直接伤害也可参与，多段与二连击不重复，衍生追击和持续伤害不计入。每层仅提高旋羽风暴伤害5%；LV2移除Boss独立高巢战歌，不产生雷暴。主动清零下一完整回合起三回合受到直接伤害+20%；自然结束清理静电，无奖励、无追加惩罚。每回合消层与每阶段增长均有持久化去重记录，冻结快照保留原声明。

主队30/30全员通关，平均31.0分钟，全部第4机制回合清零、完整获得三回合奖励；火法师对照10/10全员通关、30.2分钟，无火对照10/10全员通关、32.5分钟。同队强制不处理对照只把候选`RemovalElement`改为Wind，10/10全员通关、31.2分钟，装备和触发前轨迹精确一致；不处理自然消散，没有额外惩罚。该控制同时改变静电期间承伤和奖励，跨属性比较还改变装备与克制，不将总时长差全部解释为机制奖励。主队坦克最低8.67%，火法师对照最低4.89%，承伤有压力，样本不覆盖所有构筑。

`MonsterPhaseRounds.LinkedHits`为实际消层行动数（成功样本均4），`WaterDamage`在风机制中不使用。`LastActivationRound`与`LastBreakRound`为零基，轨迹`RoundNumber`为一基。正式接入11局与候选逐回合精确一致；风LV1及火、水、土LV4各3局精确回归。相关服务测试289项与模拟器测试59项通过。

参数见[风LV2报告](../../docs/t1-wind-deep-lv2-balance.json)，复现输入、逐回合指纹及机制审计见[模拟记录](../../docs/t1-wind-deep-lv2-simulation.json)。完整83条轨迹保存在同目录`t1-wind-deep-lv2-round-traces.json.gz`，包含复核与回归，复用种子不作为独立样本。风LV3雷暴联动与LV4循环均已接入并标定；LV5+继承LV4真实机制与未校准的占位成长，六属性完成后统一设计LV5+。

## 风深层 LV3 校准

使用`deep-three`：每人各一把当前区域深层输出／生存模板，加八把Lv1/3/5过渡主掉落（2/3/3），十把全满突破、22条装备技能全Lv10。以中性风T、光奶、水法师、火猎人、火盗贼为主，不使用起始药水、魂印、合剂或操作药水。装备使用本轮开始时已更新的当前配置。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code windfury-spire --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Wind,Light,Water,Fire,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output wind-lv3.json
```

一次70%生命触发，下一完整回合开启四回合领域，初始三层、前两个回合末各+2，最多五层；每名角色每回合首次有效火属性直接伤害消一层。双火第四回合累计七次有效消层清零，下一完整回合起获得三回合20%直接增伤。回合末五层预告下一回合雷暴；玩家先行动，出手前降到五层以下则取消整次行动，不补普攻。仍有五层时以150%全体雷暴替代当回合行动，随后清空静电并结束领域，无清零奖励。雷暴不可普通打断，危险等级Deadly；不受静电的旋羽风暴增幅影响，仍沿用既有防御和下一技能减伤。单火可在第三回合降层取消雷暴，第四回合自然结束，无奖励。

主30/30全员通关、31.2分钟，水T对照10/10全员通关、31.1分钟；主T最低62.95%。单火10/10全员通关、33.8分钟；无火10/10全员通关、36.5分钟、各释放一次雷暴。逆属性土T10/10最终通关，仅2/10全员存活，不作为稳定承伤基准。同配装强制不处理对照只将`RemovalElement`改为Earth，10/10全员通关、31.4分钟，装备与触发前轨迹精确一致。跨队伍时长还受装备词条和属性克制影响，不能全算为机制收益。

`MonsterPhaseRounds.Breaks`仅计主动清零，`Expiries`仅计自然窗口结束，雷暴释放由`BossSkillUses`与提交事件计数；`LinkedHits`为消层行动数，`WaterDamage`不使用。已有LV2冻结房间缺少新增字段时保留原无雷暴规则。雷暴预兆、意图与增长记录持久化，重建后仍在出手前核验层数。

相关服务测试298项、模拟器测试59项通过。正式配置复核11局与候选完整战斗/回合/阶段事实一致；风LV1、风LV2与火、水、土LV4各3局相对本轮开始时的当前装备基线精确回归。参数见[风LV3报告](../../docs/t1-wind-deep-lv3-balance.json)，复现输入、装备和逐回合指纹见[模拟记录](../../docs/t1-wind-deep-lv3-simulation.json)。完整121条轨迹保存在同目录`t1-wind-deep-lv3-round-traces.json.gz`，含80局最终候选、11局接入复核、15局回归及15局旧基线；复用种子不算独立样本。LV4循环现已接入并标定，LV5～LV10毕业挑战规则现已统一接入。

## 风深层 LV4 校准

风LV4已接入固定风暴循环并标定。每人四把深层武器（输出与生存各两把）加六把Lv1/3/5过渡武器（各两把），全满突破、24条装备技能全Lv10。五波生命55万／65万／75万／85万／150万，攻击215／265／310／350／390，防御均为0。Boss自身第6／16／26……回合开启四回合领域，初始4层、前两个机制回合末各+3层，上限5层；三名火角色每轮累计10次有效消层，在第四回合清零，下一完整回合起获得三回合20%直接增伤。雷暴沿用150%全体伤害及出手前降层取消规则，清零、超时或释放均不移动下轮时间。风T、光奶、三火输出主队30/30全员通关，平均29.8分钟，Boss平均30.2回合；坦克最低53.94%。水T对照10/10全员通关、30.3分钟；双火、单火、无火对照各10/10全员通关，分别33.5／37.2／41.4分钟。双火、单火降层取消雷暴后自然结束，无清零奖励；无火周期承受雷暴。同配装强制不处理对照10/10全员通关、30.3分钟，说明跨配装耗时差还包含装备与属性克制，不能全计为机制收益。全部不使用药水、魂印、合剂；逆属性土T对照10/10最终通关、0/10全员存活，不作为稳定承伤目标。见 [风LV4参数与验证](../../docs/t1-wind-deep-lv4-balance.json)。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code windfury-spire --depth 4 --stages deep-four --composition knight,priest,mage,hunter,rogue --weapon-elements Wind,Light,Fire,Fire,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output wind-lv4.json
```

每轮独立重置静电、增长与预兆，固定周期不受处理结果影响；角色技能冷却跨波和跨周期累计。三火第四回合清零，前两轮三回合奖励完整，第三轮由击杀时间决定实际长度。双火与单火每轮第二、第三机制回合降层取消雷暴，第四回合自然结束；无火每轮第二机制回合释放雷暴，死亡截断最后一轮除外。旧LV2／LV3冻结规则缺少周期字段时继续一次70%生命触发。

参数见[风LV4报告](../../docs/t1-wind-deep-lv4-balance.json)，逐回合指纹、配置和装备输入见[模拟记录](../../docs/t1-wind-deep-lv4-simulation.json)，完整轨迹保存在同目录`t1-wind-deep-lv4-round-traces.json.gz`。本轮保留风LV1～LV3及火、水、土LV4的当前装备基线，接入后逐回合回归；旧报告原数据和源散列继续保留历史记录。

## 光深层 LV1 校准

光LV1已完成五波单怪接入与标定：棱核怨灵、护庭晶构体、失序光纹卫、裂隙引流者与棱核守望者。五波生命38万／46.5万／55万／61万／102万，攻击150／185／210／240／280，防御均为0。十槽光区域Lv1/3/5过渡装备（4/3/3）、全满突破、五把双技能Lv10加五把双技能Lv7，原生十级T、奶、三输出全Auto基准30/30局全员通关，平均29.8分钟，Boss30.3回合；坦克最低58.31%，全队平均使用0.67瓶治疗药水，不使用魂印、合剂或操作药水。晶壳护持与琉辉护幕减伤20%，第3回合起间隔8回合，持续之后两个完整玩家回合，可打断、可驱散或自然结束；LV1不要求暗属性，不包含反射与成功易伤奖励。土T、光奶、暗/风/火输出混合队与全光无驱散队各10/10全员通关，分别约27.7与30.2分钟；跨配装时长差包含装备词条和属性克制。光LV1～LV4已标定，LV5～LV10挑战规则已统一接入，详见[框架第6节](../../docs/dungeon-depth-mastery-framework.md#6-lv5-与通用突破材料)。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code dawn-core --depth 1 --stages deep-entry --composition knight,priest,mage,hunter,rogue --weapon-elements Light,Light,Light,Light,Light --starting-potions 2 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output light-lv1.json
```

混合队使用`--weapon-elements Earth,Light,Dark,Wind,Fire`。无驱散队使用同一全光装备盘，提供只含`mage-arcane-bolt`、`mage-frost-bolt`、`mage-scorch`、`mage-arcane-domain`的`mage-no-dispel`原生技能配置，通过`--talent-builds`指定文件，并使用`--composition knight,priest,mage-no-dispel,hunter,rogue`；`Nodes`为空，必须核对`Builds.EquippedSkills`确实没有`mage-spellbreak`。

局部时钟、随机目标持久化和状态生命周期沿用现有生产执行器，无新增战斗结算逻辑。参数见 [光LV1报告](../../docs/t1-light-deep-lv1-balance.json)，样本指纹与复现输入见 [模拟记录](../../docs/t1-light-deep-lv1-simulation.json)，85条完整轨迹保存在`t1-light-deep-lv1-round-traces.json.gz`；包含复用种子的正式复核与旧本回归，不是85个独立随机样本。


## 光深层 LV2 校准

光LV2已按每人一把三技能Lv10深层武器，加九把满突破Lv1/3/5过渡武器（各三把，五把双技能Lv10、四把双技能Lv7）标定。五波生命43万／52万／61万／68万／114万，攻击165／205／240／275／310，防御均为0。Boss首次降至70%生命后，下一完整玩家回合开启一次四回合琉辉反镜，替换LV1周期护幕；初始三档，每档反射2%实际直接伤害，每人每回合原始反伤上限为回合开始时有效最大生命的5%乘剩余档数，多段与二连共享额度。一次有效驱散直接解除，每名角色每回合首次有效暗属性直接伤害先降一档再反射，清零或驱散的整次行动免反伤；主动处理后下一完整回合起三回合Boss受到直接伤害+20%。自然结束无奖励、不追加惩罚。土T、光奶、水法师、暗猎人、火盗贼主队30/30全员通关，平均30.0分钟，Boss30.1回合，坦克最低31.50%；仅驱散、仅暗伤、不处理三组各10/10全员通关，分别30.9／30.1／31.6分钟，单暗均第三机制回合清零。全部无药水、魂印或合剂。跨配装与移除技能会改变伤害和循环，时长差不全归因于机制。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code dawn-core --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Water,Dark,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output light-lv2.json
```

仅驱散使用`Earth,Light,Water,Wind,Fire`；仅暗伤使用主队属性并改用`mage-no-dispel`原生技能配置；不处理同时换Wind猎人并移除法术反制。无驱散配置沿用上节四个法师技能和空`Nodes`，必须同时传`--talent-builds`与对应composition，核验实际装备技能。

`MonsterPhaseRounds.LinkedHits`计有效暗伤削弱行动，`Breaks`计主动解除，`Expiries`计自然结束，`WaterDamage`不使用。派生反伤事件为`SkillCode=light-reflection`、`ActionKind=Mechanic`；反伤不再乘属性、暴击或浮动。角色回合原始额度和暗伤去重为隐藏CurrentRound状态，提交前保存，回合末清理，不显示在玩家状态栏。整次技能待反伤在事务内完成，重建不刷新已用额度。

正式配置14局与候选完整事实一致，光LV1及四元素LV4各3局逐回合回归一致；参数见[光LV2报告](../../docs/t1-light-deep-lv2-balance.json)，完整输入、装备与指纹见[模拟记录](../../docs/t1-light-deep-lv2-simulation.json)。104条完整轨迹保存在`t1-light-deep-lv2-round-traces.json.gz`，含60最终样本、14接入复核、15旧基线、15回归；复用种子不视为独立样本。该段为LV2标定记录；当前光LV1～LV4已标定。


## 光深层 LV3 校准

光LV3已按每人各一把区域深层输出／生存武器，加八把Lv1/3/5过渡武器（2/3/3），全满突破、22条装备技能全Lv10标定。五波生命50万／60万／70万／79万／133万，攻击185／230／270／310／320，防御均为0。Boss首次降至70%生命后，下一完整玩家回合开启一次四回合反镜，初始四档；前三个机制回合末各增加一级圣光增幅，每档反射比例由2%逐步提高至3.5%，每档原始反伤上限由最大生命的5%提高至8%，均乘剩余强度，不补回已消层。一次有效驱散清除全部反镜与增幅；每名角色每回合首次有效暗属性直接伤害消一档，成功行动免待反伤，下一完整回合起三回合Boss受到直接伤害+20%。自然结束清理反镜与增幅，无奖励、不追加惩罚。土T、光奶、暗法师、暗猎人、火盗贼主队30/30全员通关，平均30.2分钟，Boss30.3回合，坦克最低9.34%；仅暗伤、仅驱散、单暗三组各10/10全员通关，分别30.3／33.5／32.2分钟。双暗均第二机制回合解除，单暗均第四回合解除并获完整三回合奖励；仅驱散与单暗坦克最低3.35%与2.61%，生存余量较小。不处理对照0/10通关，反镜四回合自然结束后无新增失败惩罚。全部不使用药水、魂印或合剂；跨装备及移除技能的耗时差包含配装和职业循环变化。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code dawn-core --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Dark,Dark,Fire --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output light-lv3.json
```

无驱散对照需替换法师为`mage-no-dispel`，并以`--talent-builds`载入[模拟记录](../../docs/t1-light-deep-lv3-simulation.json)中的原生技能配置；仅暗伤为土／光／暗／暗／火，单暗为土／光／水／暗／火，无处理为土／光／水／风／火。仅驱散采用土／光／水／风／火且保留原生法师。

正式配置17局与候选完整事实一致；光LV1、LV2和四元素LV4各3局逐回合回归一致。参数见[光LV3报告](../../docs/t1-light-deep-lv3-balance.json)，123条完整轨迹保存在`t1-light-deep-lv3-round-traces.json.gz`，包括70最终样本、17接入复核、18旧基线、18回归，复用种子不视为独立样本。以上为LV3标定记录；LV4黎明循环已完成接入与标定，见下节。


## 光深层 LV4 校准

光LV4黎明循环已按每人四把区域深层武器（输出、生存各两把）与六把Lv1/3/5主掉落（各两把），十槽全满突破、24条装备技能全Lv10标定。五波生命51万／61万／71万／80万／135万，攻击200／245／290／320／330，防御均为0。Boss自身第6回合起，每10回合开启四完整回合反镜，不再附加70%生命触发；每轮六档反镜、零级圣光增幅。前三机制回合末各增长一级，每档反射比例为2%／2.5%／3%／3.5%，每档每人回合原始反伤上限标定为最大生命的3%／4%／5%／6%，均乘当前剩余强度，不补回已消层。一次有效驱散直接解除；每名角色每回合首次有效暗属性直接伤害消一档，成功整次行动免待反伤，下一完整回合起三回合Boss受到直接伤害+20%。自然结束清理反镜与增幅，无奖励、不追加惩罚，提前处理不移动下一轮。土T、光奶、三暗输出主队30/30全员通关，平均30.6分钟，Boss30.2回合，坦克最低10.76%；仅三暗和双暗各10/10全员通关，分别30.9／31.3分钟，每轮分别第二／第三机制回合清零，双暗坦克最低仅0.46%。纯驱散无补给9/10全员通关，每人两瓶治疗药水对照仍为9/10，连续拖到第四回合驱散的冷却错位仍有团灭风险；不保证无暗同门槛稳定Auto。单暗无驱散与不处理各0/10通关。主队和其余无补给对照不使用药水、魂印、合剂或操作药水；跨配装和技能声明影响耗时，不能全归因于机制。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code dawn-core --depth 4 --stages deep-four --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Light,Dark,Dark,Dark --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output light-lv4.json
```

无驱散对照需替换法师为`mage-no-dispel`，并以`--talent-builds`载入[模拟记录](../../docs/t1-light-deep-lv4-simulation.json)内附原生技能配置；仅三暗为土／光／暗／暗／暗，双暗为土／光／暗／暗／火，单暗为土／光／水／暗／火，不处理为土／光／水／风／火。仅驱散采用土／光／水／风／火且保留原生法师，药水对照另设`--starting-potions 2`。

正式配置23局与候选完整事实一致；光LV1～LV3及四元素LV4各3局前后回归一致。参数见[光LV4报告](../../docs/t1-light-deep-lv4-balance.json)，155条完整轨迹包含90条最终样本、23条接入复核、21条旧基线和21条回归，复用种子不视为独立样本；另有66局前期试算不混入最终统计。服务端全量1318项、反镜专项37项（全量子集）、模拟器59项通过。未重启服务器或修改玩家存档；新房间加载Revision6，已有房间继续冻结旧规则，LV5～LV10挑战规则已统一接入。

## 暗深层LV1复现（2026-10-02）

全暗十槽过渡装备主基准30/30全员通关，平均30.1分钟，五波约11.8／14.2／16.0／18.2／30.4回合。装备全部满突破，五把双技能Lv10、五把双技能Lv7；允许每人两瓶治疗药水，实际全队平均使用两瓶，不使用魂印、合剂或操作药水。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code plague-crypt-depths --depth 1 --stages deep-entry --composition knight,priest,mage,hunter,rogue --weapon-elements Dark,Dark,Dark,Dark,Dark --starting-potions 2 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output dark-lv1.json
```

混合队使用`--weapon-elements Earth,Light,Dark,Wind,Fire`，10/10全员通过、约25.7分钟。无净化对照保留同全暗装备，将`priest`替换为`priest-no-cleanse`，并以`--talent-builds`加载以下原生配置；保留治疗、群疗、启示和圣光弹，仅移除净化，不修改职业冷却：

```json
{
  "priest-no-cleanse": {
    "Role": "priest",
    "Nodes": {},
    "Skills": ["acolyte-holy-bolt", "acolyte-heal", "acolyte-group-heal", "acolyte-revelation"]
  }
}
```

无净化10/10全员通过、约29.7分钟，但坦克最低仅2.49%。普通毒最多一层，之后两回合末读取施放时有效攻击快照跳伤，可净化、刷新保留较强值；LV1没有生命上限侵蚀或满层死亡。技能采用怪物自身初始时钟，持久化全局冷却跨波延续。暗LV1～LV4现已标定，六属性基础四层均已完成；LV5～LV10挑战规则已接入，未逐层战斗标定。

完整输入、同种子净化对照与91条压缩轨迹见 [暗LV1参数](../../docs/t1-dark-deep-lv1-balance.json) 和 [模拟记录](../../docs/t1-dark-deep-lv1-simulation.json)。11局正式配置与候选逐回合一致，其他五本LV4各3局与修改前基线一致。


## 暗深层 LV2 一次播毒标定

每人一把区域深层武器（三技能Lv10）和九把Lv1／3／5主掉落（各三把），十槽满突破；五把过渡武器双技能Lv10、四把双技能Lv7。主队只有光猎人提供属性解毒，暗治疗保留原生净化。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code plague-crypt-depths --depth 2 --stages deep-two --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Dark,Water,Light,Wind --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output dark-lv2.json
```

Boss首次降至70%生命后下一完整回合播毒一次：固定前排，四回合毒层1→2→3→4，每回合末先毒伤后增长；第四回合结束自然清毒、无奖励。一次有效净化或累计Boss最大生命2%的实际光属性直接伤害（23000点）清除全部剧毒，下一完整回合起三回合15%直接减伤及此Boss普通毒免疫。部分光伤不降层，DOT与反伤不计进度；LV2不侵蚀生命上限、不触发满层死亡。

主队30/30全员通关、平均30.5分钟，五波约12.0／14.4／16.5／18.2／30.8回合。单光猎人、单光法师无净化各10/10全员通关并在第2～4回合解毒；仅净化10/10全员通过，其中9次解毒、1次真实冷却错位自然结束；无光无净化10/10全员通过。所有组无药水、魂印、合剂或操作药水。更换元素及移除净化均改变配装和Auto行动，时长差不全部归因于机制。

阶段统计沿用通用字段：`MonsterPhaseRounds.WaterDamage`在暗本表示累计实际光伤，`Activations`是播毒次数，`Breaks`是主动解毒次数，`Expiries`是自然结束次数；不要按字段旧名解读为水属性。完整记录已按光伤事件独立复算。

[暗LV2参数](../../docs/t1-dark-deep-lv2-balance.json)与[模拟记录](../../docs/t1-dark-deep-lv2-simulation.json)保存配置、配装与逐回合审计；123条完整轨迹含70条最终候选、17条正式配置复核、18条修改前基线和18条回归，复用种子不计独立样本。时长显示修正前的85局不混入最终统计。正式复核与候选逐回合一致，暗LV1和其他五本LV4每组3局与修改前一致。以上为Revision4的LV2历史记录；暗LV4标定时Revision6已标定LV1～LV4，LV4固定循环及独立面板已完成；LV2本轮三局前后回归一致。LV5～LV10挑战规则已统一接入。

## 暗深层 LV3 腐巢侵蚀标定

每人两把不同深层武器（输出／生存各一把）与八把Lv1／3／5主掉落（2／3／3），十槽满突破，22条装备技能均Lv10。主队土T、暗奶、光法师、光猎人、风盗贼，原生净化保留。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code plague-crypt-depths --depth 3 --stages deep-three --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Dark,Light,Light,Wind --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output dark-lv3.json
```

暗LV3腐巢侵蚀已按每人两把不同区域深层武器（输出、生存各一把）与八把Lv1/3/5主掉落（2/3/3），十槽全满突破、22条装备技能全Lv10标定。五波生命50万／62万／71万／80万／135万，攻击185／235／270／305／310，防御均为0。Boss首次降至70%生命后下一完整回合播毒一次，四完整回合1→2→3→4层，三层／四层降低最大生命10%／20%；未解除则第四回合末先跳四层毒伤，再增至五层使目标死亡。一次净化或累计54000点实际光属性直接伤害（Boss最大生命4%）清除毒及侵蚀，立即恢复上限但不补血，下一完整回合起获得三回合净蚀庇护。土T、暗奶、双光输出、风盗贼主队30/30全员通关，平均30.1分钟，Boss30.4回合；29次净化、1次光伤兜底，坦克最低2.24%。双光无净化10/10全员通关，全部第三机制回合解毒、约30.0分钟，坦克最低4.94%。仅净化4/10全员通关、另1局减员通关和5局团灭，6局真实冷却错位导致五层死亡；单光无净化10/10减员通关，无光无净化0/10通关。全部无药水、魂印、合剂或操作药水。见 [暗LV3参数与验证](../../docs/t1-dark-deep-lv3-balance.json)。以上为Revision5的LV3历史标定记录；暗LV4标定时Revision6、已标定[1,2,3,4]，LV4固定循环及独立面板已完成，LV1～LV3本轮各三局与修改前逐回合一致。LV5～LV10挑战规则已统一接入。

无净化对照沿用上节`priest-no-cleanse`及所附`--talent-builds`。纯净化／不处理元素为`Earth,Dark,Water,Wind,Dark`，单光为`Earth,Dark,Water,Light,Wind`。每次解除一次性清除全部毒与侵蚀，累计光伤不足不降层。侵蚀按开始时正常上限取整，不复乘，恢复不补血；所有逐回合人物有效上限与伤害、治疗事件已独立审计。

通用阶段字段`WaterDamage`在暗本仍表示实际光伤；`LinkedHits`在LV3表示五层机制致死次数，普通攻击或DOT先杀死目标不增加该数；`Expiries`为0，不再四层自然清除。失败组耗时为失败尝试时长，不能视为正常刷装备周期。

[暗LV3模拟记录](../../docs/t1-dark-deep-lv3-simulation.json)附完整输入、历史运行时代码、源码哈希和审计；129条完整轨迹含70条最终候选、17条正式复核、21条旧基线和21条回归，复用种子不计独立样本。首轮15局调参另列，不混入最终统计。暗LV1／LV2和其他五本LV4均与修改前逐回合一致。

## 暗深层 LV4 瘟疫循环标定

暗LV4瘟疫循环已按每人四把区域深层武器（输出、生存各两把）与六把Lv1/3/5主掉落（各两把），十槽全满突破、24条装备技能全Lv10标定。五波生命52万／63万／72万／82万／140万，攻击205／255／295／325／330，防御均为0。Boss自身第6／16／26……回合周期播毒，固定间隔10回合，替换一次70%生命触发；每轮重新锁定存活前排、重取攻击与正常生命上限，毒层和累计光伤归零。保留四完整回合倒计时、三／四层最大生命降低10%／20%及五层致死。一次有效净化或累计84000点实际光属性直接伤害（Boss最大生命6%）全部解毒，立即恢复上限但不补血；下一完整回合起三回合净蚀庇护，直接减伤15%并免疫此Boss普通毒。提前解除与目标死亡不移动下轮，目标死亡不即时转移，Boss死亡取消后续机制。土T、暗奶、三光输出主队30/30全员通关，平均30.6分钟、Boss31.1回合，90轮全部解除（69次净化、21次光伤），坦克最低17.08%。同装备三光无净化10/10全员通关，30轮全部光伤解除，坦克最低10.30%。双光无净化7/10通关且全部减员；无光仅净化4/10全员通关、6/10团灭；无光无净化0/10通关。全部无治疗药水、魂印、合剂或操作药水。

五波主队平均12.0／14.3／16.4／18.3／31.1回合，全副本91.9回合。主队解毒第1／2／3／4回合分别30／2／56／2次，三光无净化分别0／1／26／3次。解毒奖励以Boss与目标存活为前提；主队84份奖励完整三回合、6份因Boss倒下取消，三光无净化28份完整、2份因Boss倒下取消。

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --dungeon-code plague-crypt-depths --depth 4 --stages deep-four --composition knight,priest,mage,hunter,rogue --weapon-elements Earth,Dark,Light,Light,Light --starting-potions 0 --soul-loadouts none --runs 30 --seed-start 7213 --trace --output dark-lv4.json
```

无净化沿用上节的原生`priest-no-cleanse`及`--talent-builds`输入；同装备三光为`Earth,Dark,Light,Light,Light`，双光为`Earth,Dark,Light,Light,Wind`，仅净化／不处理为`Earth,Dark,Water,Wind,Dark`。每组对照10局，种子7213～7222；主队30局7213～7242。纯净化六局团灭，记录真实冷却和目标顺序，不人为保证Auto覆盖每轮。双光通过的七局也均减员。失败组平均耗时包含失败尝试，跨属性装备和技能变化影响职业行动分配，不能全部归因于机制收益。

阶段字段`WaterDamage`在暗本表示当前循环内的实际光伤，下轮归零；`Activations`统计播毒轮数，`Breaks`统计主动解除轮数，`LinkedHits`在LV3／LV4表示五层机制致死次数。普通攻击或DOT提前杀死目标不增加致死计数，LV4`Expiries`为0。逐轮审计按绑定目标、真实光伤事件、攻击快照、毒伤／叠层顺序、有效生命上限、奖励和死亡取消独立复算。

[暗LV4参数](../../docs/t1-dark-deep-lv4-balance.json)与[模拟记录](../../docs/t1-dark-deep-lv4-simulation.json)保存正式和候选配置、每人四深层／六过渡武器、24条技能、历史运行时来源及审计；[135条压缩完整轨迹](../../docs/t1-dark-deep-lv4-round-traces.json.gz)含70条最终候选、17条正式配置复核、24条修改前基线、24条回归，复用种子不计独立样本。首轮15局调参另列，不混入最终统计。17局正式样本与候选完整事实一致；暗LV1～LV3及其他五本LV4各3局前后逐回合一致。

服务端全量1355项、暗剧毒专项32项（含11项LV4，属于全量子集）、模拟器59项通过。固定周期与目标选择、每轮进度与快照重取、末回合解除优先于致死、完整奖励、服务重建、重复结算及缺少周期字段的冻结LV2／LV3继续一次触发均已覆盖。LV4标定时Revision6、已标定[1,2,3,4]；更新编译后的服务并重新加载配置后新房间生效，旧房间保持冻结规则。本轮未重启运行服务、执行数据库迁移或修改玩家存档。六属性LV1～LV4全部完成，LV5～LV10继承LV4真实机制，采用生命15%、攻击10%的毕业挑战成长及分层突破奖励，不逐层战斗标定。
