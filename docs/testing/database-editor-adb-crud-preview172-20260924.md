# ADB CRUD — Preview 172, 2026-09-24

Source: EvilIslands.DatabaseEditor, branch feature/structured-list-content-builder, base c3809f07ec3bdcb837fea17f5050cad4ca3eff9a plus existing dirty changes. Result commit N/A (not requested). No merge/push authorised or performed.

## Scope and result

DBE-ADB-CRUD-20260924: mandatory template removed; new records can start from explicit special/single/neutral defaults, probability 100, speed zero and events disabled. ANM frame count fills length. Optional copy retains settings and clears events. Delete affects ADB draft only, with default-No confirmation and explicit subsequent-index shift warning. Undo restores byte snapshots; closing without saving discards changes. ANM clips are not deleted. Existing backup/external-change checks retained.

## Verification

Release UiSmoke build: zero warnings/errors. --adb-crud against installed database.res passed on temporary copies; loader resolved actual ADB to databaseJmv.res -> unhufe.adb (278 records). Tests: empty ADB UI add without template, optional template UI add, exact ANM length/defaults, invalid append atomic rollback, middle/last/all deletion, stored-index renumbering with all remaining non-index bytes intact, undo, discard, RES save/reopen and every unrelated resource byte unchanged. Existing ADB regression passed edit/discard, unavailable-write retention, backup equality, no-op save and stale-source rejection. Installed source archive byte equality verified. No active game writes.

Release publish: version 0.7.0-preview.172, manifest 19/19 hashes verified. Package locator: work/investigations/database-editor-adb-crud-preview172/package-path.txt in parent workspace. Retained Modules/Kimodo 26-file baseline recorded. Current replacement pending editor closure (PID26312 observed). Only handoff/current authorised; no game install.

## Remaining acceptance / limits

Interactive confirmation dialog not clicked by automated tests. Manually select an ANM-only clip, add without template, delete/undo, save/reopen on a disposable resource copy. Deleting a middle record shifts later ADB numbers; scripts/saves are not rewritten. New default flags must be adjusted for intended game selection; these tests do not prove playback or animation quality. Kimodo in-game root/contact acceptance remains a separate open parent task. Malachite and runtime protocol unchanged. Toggle N/A: none added.

## Installation completed
2026-09-24: owner confirmed closure; no editor process present. Publish-Current.ps1 -PackagePath installed exact prepared preview172 into sole handoff/current. Manifest19/19 verified after copy; all26 retained user/Modules/Kimodo files byte-identical to immediate pre-install hashes. EXE SHA256 AF5B57EE33F980ADD278D70E9A22A2A436F226A973D59F6020698C55309A64CB. No rebuild, game install, source changes, commit/merge/push this continuation. Interactive/game acceptance remains as above.
