# ADB Russian flag selection — Preview176
2026-09-24. Source: EvilIslands.DatabaseEditor, branch feature/structured-list-content-builder, base c3809f07ec3bdcb837fea17f5050cad4ca3eff9a plus preserved dirty work. Result commit N/A; no commit/merge/push authorized or performed.

Owner reproduction: weapon/event flags required unexplained hexadecimal editing. Implemented Russian weapon and event checkboxes, low/high equipment eligibility, action-dependent exclusive modifier lists, advanced collapsed raw codes and bidirectional synchronization. Unknown bits persist; unknown modifier values explicitly displayed. Action changes retain modifier bits and ask user to review their new meaning. No automatic change to defaults, active game data or native runtime.

Definitions: work/eirebuild-local/Game/adFormat.h; LOW/HIGH shape equipment grouping and eligibility: UnitServer.cpp HaveLowShapes/HaveHighShapes, Unit.cpp CanPlayAnimation. This proves mapping against reference implementation, not current addon-specific overrides. Special variant number is distinct from ADB entry index.

Validation: Release UiSmoke build (includes App/Core), zero warnings/errors. --adb-crud on current installed database input, temporary writes under work/investigations/database-editor-flags176: PASS. Added hidden UI cases for weapon and equipment toggles, retained unknown bits, idle -> attack modifier mapping, raw -> UI synchronization and independently parsed exported ADB record. Existing CRUD/save/backup/discard and ANM delete standalone/RES preservation regressions PASS. Installed input archive unchanged by regression.

Prepared with publish --no-build from tested Release; 19-file manifest. Locator: work/investigations/database-editor-flags176/package-path.txt. Current editor running PID33216, installation awaits user closing it. Only sole handoff/current to update through Publish-Current.ps1. Kimodo/user files retained; module copied unchanged. No game installation; Malachite source untouched. No new runtime toggle (UI editing controls expressly requested).

Manual checks: select idle/attack and compare modifier lists; toggle applicable weapons/events, Apply and save on copy, reopen and check choices. Check wrapping at user DPI. Gameplay effect and addon-specific semantics untested; parent animation gameplay acceptance remains open.

Delivery: owner confirmed closed; no editor process. Publish-Current.ps1 -PackagePath installed prepared176 in sole handoff/current; all19 manifest and19 retained file hashes verified. Product version 0.7.0-preview.176+c3809f07ec3bdcb837fea17f5050cad4ca3eff9a. No rebuild, game write, commit/merge/push. Manual DPI and gameplay checks remain open.
