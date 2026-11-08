# Preview180 — тень оригинала при сравнении

2026-09-25. Source: EvilIslands.DatabaseEditor, branch feature/structured-list-content-builder, base c3809f07ec3bdcb837fea17f5050cad4ca3eff9a plus preserved dirty work; result commit N/A (not requested).

Scope: comparison dialog for generated current-animation variant. Checkbox «Тень оригинала» enables translucent blue original over solid candidate using the same frame. Enabling while showing original calculates the candidate. Showing original clears the checkbox; turning off preserves current frame and camera. No serialization, Kimodo inference or game changes. Existing SetComparison renderer reused.

Release UiSmoke build: zero warnings/errors. --adb-crud PASS, including six asynchronous original/variant switches without scene recreation and ghost bounds matching independently displayed original at frames 0, 7, 12. Solid candidate geometry remains attached; off clears ghost meshes and retains frame. Existing ADB/RES readback and external-change checks pass; installed source unchanged.

Editor process absent before Publish-Current.ps1 -PackagePath. Installed sole handoff/current version 0.7.0-preview.180; manifest19/19 and retained19/19 hashes verified (Kimodo and user files retained, Modules copied unchanged). Prepared artifact locator: workspace work/investigations/database-editor-ghost180/package-path.txt.

Manual: generate variant, enable «Тень оригинала» in comparison; blue original and solid variant should play/scrub together. Toggle off and switch original/variant; frame and camera should remain stable. Interactive transparency/readability at user DPI not verified, nor any gameplay. No interactive launch, game install, commit, merge or push. Toggle explicitly requested by user; no game/config toggle.
