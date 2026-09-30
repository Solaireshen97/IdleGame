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

The checked regression fixture is `Game.Server.Tests/Fixtures/BattleRoundTrace.seed7213.json`: six rounds, seed 7213, two characters attacking at 10, a 50% critical chance on the first actor, monster attacks at 4, configured all-party damage/weakness and a two-round skill cooldown, followed by a second wave. Tests compare complete normalized rounds, not just reruns of the current code. After an intentional semantic change, inspect the first differing round's events/state and then explicitly regenerate the fixture:

```powershell
$env:IDLEGAME_UPDATE_BATTLE_TRACE = '1'
dotnet test Game.Server.Tests --filter FullyQualifiedName~SeededMultiRoundEncounterTrace
Remove-Item Env:/IDLEGAME_UPDATE_BATTLE_TRACE
```

Review the fixture diff before accepting the new baseline, then rerun with the variable absent. Normal tests never update the fixture.
