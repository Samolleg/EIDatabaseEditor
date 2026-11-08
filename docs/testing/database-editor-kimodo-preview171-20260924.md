# Preview 171: stationary Kimodo cycles

Source: `feature/structured-list-content-builder`, HEAD `c3809f07ec3bdcb837fea17f5050cad4ca3eff9a`, existing dirty work preserved. No commit, merge, push, addon or active game-resource write. Parent DBE-KIMODO-GAME-MOTION-20260924 remains open for game acceptance. No native toggle added; playback intent is an explicit generation input.

## Reproduction and changes

Owner provided the actual twerk prompt, 180 frames, 180 diffusion steps, Seed 1. Running the installed CPU Kimodo succeeded. Raw outputs and exact prompt are retained privately in `work/investigations/database-editor-kimodo-preview171/raw-motion` at the workspace root. The old conversion reproduces the reported root seam 0.058953 exactly. Root first `(0.00679387,-0.01046463,0.99417007)`, Z minimum 0.737275. This case is different from old twerk1's zero initial hip height.

Explicit stationary mode removes the linear accumulated XY drift while retaining local sway, then smooths the final 2–12 frames (10% of the clip, capped at 12) toward the first pose. Root and rotations agree exactly at the seam. The vertical profile is unchanged outside that short tail; model-specific initial height is retained. One-shot mode retains the original motion and opens with Repeat off. Intra-clip jump checks remain enabled; cycle checks follow Repeat. This is a pose-continuous cycle, not a guarantee of matching derivatives, foot contacts, or artistic fidelity.

For the actual owner request: corrected root seam 0; Z range 0.737275–1.006518; all five cycle warnings eliminated, no threshold warnings remain. Previously the high tail reached 1.011824; the short seam transition deliberately changes the tail. No fixed 0.9-metre offset or vertical flattening is used.

The preview ground is now at ANM Z=0 instead of the bind mesh minimum (-0.944208 for this model). It cannot conceal a missing hip height by following the body. This changes only the reference plane and drag constraint, never saved frames on display. It is not a complete simulation of terrain/ADB-height/center-shift correction in the game.

The UI retains prompt, raw SOMA streams, generated nested track resource, reference anchor, exact conversion skeleton, seed/steps/frame count and source ANM hash under LocalAppData generated-motions. Result status and remaining diagnostics are displayed without an extra confirmation dialog. Old clips are not automatically rewritten.

## Validation

- Release UiSmoke build and application publish: zero warnings/errors.
- `--animation-root-height`: model anchor, genuine 30-frame SOMA output, synthetic vertical up/down, old clip reanchor, standalone ANM/RES and unrelated bytes PASS.
- `--kimodo-loop`: synthetic 60 frames and actual owner 180-frame generation PASS. Tests cover one-shot travel, intact vertical profile before the seam tail, root/rotation closure, atomic Undo/Redo, other clip bytes, both save targets and independent raw nested ANM root parsing after disk readback. History track export matches generated tracks.
- `--animation-edit`: human, orc and bat PASS, including ANM/RES, copy, conflicts, locks, hidden WPF storyboard/ground/drag checks. Initial two runs encountered ephemeral uppercase rollback .tmp files. The test now waits at most two seconds for the exact expected file set, preserving the no-leftovers assertion without filtering files. Final full run PASS.
- Active hashes unchanged: database.res `094B3A592461B6AC95F93F17BFDC808F39D1341110B97DF200C30E77150A4EAD`; unhufe.res `E7FE2BF592F7507E94EF2EE8709F1AA63B2689187A69431116C1267CC0B55BCE`; loose unhufe.anm `5BBBCD4A0A56429D039D7CA6B7A27CBA840698F152E8C6816D8B9902E862A05C`.

## Delivery / remaining acceptance

Prepared package path is recorded privately in workspace `work/investigations/database-editor-kimodo-preview171/package-path.txt`; install only through Publish-Current.ps1 with editor closed. No new ordinary handoff folder. See roadmap for final current installation status.

Installed to sole handoff/current with editor closed through Publish-Current.ps1 -PackagePath. ProductVersion 0.7.0-preview.171; EXE SHA256 `8BA3F76EB9BA53DF34C5BC1BC43F2EDAB0039CB299D567B2CDD5944FA356E23B`. All 19 manifest files verified; all 26 retained Malachite/Kimodo files unchanged. Active resource hashes rechecked unchanged after installation. Nothing installed in game.

Manual: generate using the provided prompt/parameters and **Повтор на месте**, inspect the boundary and feet, save a new ANM clip and register the matching ADB name separately. Play several repeats in JamaisVu135 on the same unit. Verify position at the seam, crouching and contact with terrain. Active game files were not changed by this task; source twerk1/twerk2 remain unchanged. One-shot walking must still preserve intended travel.

Confidence limits: numerical conversion/save invariants are verified on the exact prompt. Actual game playback, continuous foot contact, visual quality, and arbitrary other prompts are not validated. The editor still does not emulate native ADB root-height scaling and center shift. Those cannot be inferred from passing save or cycle tests; no claim that every reported in-game displacement is eliminated.
