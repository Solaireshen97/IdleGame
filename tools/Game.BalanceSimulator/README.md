# Balance simulator

Run from the repository root. The simulator uses isolated in-memory SQLite databases and the production battle, monster, reward, weapon and skill services. A single seeded `Random` instance is shared by the services within each sample. Configuration and source hashes are recorded separately from combat results.

```powershell
dotnet run --project tools/Game.BalanceSimulator -- --stages starter --elements Fire --targets normal --runs 1 --seed-start 7213 --trace --output trace-a.json
```

Repeat the command with `--output trace-b.json`. Corresponding samples must have the same `BattleFingerprint` when configuration, seed, loadout and control policy are unchanged. `--trace` includes `RoundTrace`: ordered normalized round facts and a fingerprint for each round. Omit the flag to retain only the encounter fingerprint. Compare round fingerprints to locate the first combat divergence, then compare that round's `Data`.

Trace schema 1 preserves event order, source and target, damage, healing, status changes, remaining active states, skill and consumable cooldowns, healing-potion quotas, operation-potion modifiers, consumable buffs, HP, encounter run and waves. Actor identity uses character slots and monster wave/position, allowing database IDs to differ. Timestamps, global event IDs, storage IDs, optimistic concurrency versions and presentation text are excluded. Wall-clock-derived `CycleSeconds` and report `GeneratedAtUtc` are not fingerprint inputs. `SourceFiles` identify the implementation that produced a run, but are not mixed into the combat fingerprint so a behavior-preserving refactor remains comparable.

Default roles come from configured base professions. Each character equips the native skills actually unlocked at its simulated level: level 1 for `starter`, level 5 for `shop`, level 10 otherwise. The `Builds` output contains the exact equipped skills. Legacy role aliases remain accepted, but no promotion or inactive combat talent tree is applied. `--talent-builds` can select explicit native skills using existing profile `Role` and `Skills` fields; `Nodes` must be empty. Unknown, unavailable or empty equipped skill sets fail instead of silently producing a skill-free result. Historical reports using removed promotion/talent mechanics should not be treated as equivalent inputs.

For service-recreation replay, retain the same RNG instance (and its consumed state) while reconstructing services or DbContexts. Recreating `new Random(seed)` before each round changes the random stream and is not equivalent to restarting services within the same encounter.

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
