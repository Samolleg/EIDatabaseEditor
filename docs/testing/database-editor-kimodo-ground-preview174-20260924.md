# Automatic Kimodo grounding — Preview174, 2026-09-24

DBE-KIMODO-AUTOGROUND-20260924. Source EvilIslands.DatabaseEditor, feature/structured-list-content-builder at base c3809f07ec3bdcb837fea17f5050cad4ca3eff9a plus preserved dirty work. Result commit N/A. No commit/merge/push authorised/performed. Includes pending tested Preview173 deletion changes.

## Change and contract

Generation UI no longer asks for hip reference. ApplyKimodoGroundedDraftAsync maps source SOMA heel/toe support height onto transformed target foot mesh minimum each frame, with model leg scale, after rotation transfer. Root XY starts at zero and retains predicted movement (stationary mode retains its existing detrend/loop blend). Root Z becomes clearance minus target foot offset, independent of initial template root. Airborne clearance is retained, not snapped to floor. Stationary loop blends source clearance through the same tail as rotations and has exact endpoint root seam. Missing either foot mesh fails without mutating editor. One Undo restores whole import. Actual ground remains ANM Z0; no preview-only correction. Old ApplyKimodoDraftAsync remains lower-level rotation/relative trajectory stage and old regression reference; application generation calls grounded stage only.

SOMA heel/toe proxy uses bundled Kimodo skeleton.hpp leg offsets (NVIDIA Kimodo provenance/Apache-2.0 documented there). Source has joints, not a supplied sole mesh: heel sole height is inferred from toe-base local height. Small positive source clearance is preserved; negative source foot clearance is clamped to0. Target uses actual ll3/rl3 mesh vertices, bind positions and morph channel. No fixed0.9 offset, no fixed hip height, no reference-clip calibration. This assumption is explicit: not a full physical/contact solver, does not enforce hands/body contacts or prevent foot sliding in XY, and does not guarantee arbitrary generated action quality. Native ADB scaling/terrain and frame-interpolation effects require game acceptance.

## Evidence

Release build zero warnings/errors. --kimodo-ground installed database.res + retained real180-frame motion from preview171 passes:
- synthetic grounded crouch: hipZ0.699075..1.273503 including jump; original template heights-3,+5 give positions within2e-6;
- scale2: hipZ1.398150..2.547005, positions double within1e-5;
- airborne foot clearance0.4 * rig scale retained; crouch lowers hip >0.1;
- real motion: hipZ0.724395..0.981354, first foot clearance0.018912 (source clearance preserved, not claimed exact physical contact);
- missing-foot rejection atomic, single Undo/Redo, stationary root seam exact;
- standalone ANM and embedded RES saved/re-read on temporary files, raw nested hp parsed independently of FigureAnimationSet; every other clip/resource unchanged.

No active game RES/ADB/ANM, addon or Malachite modified. No new toggle. Existing clips unchanged; regenerate to use new placement. Preserve prompt/raw rotations/root streams, target foot geometry and algorithm name in generation history.

## Manual acceptance

Generate same180-frame prompt without selecting a reference; compare starting from low uspecial08 vs idle. Inspect crouch and jump, save generated ANM deliberately and test game playback on flat ground. Do not infer game verification from build/numeric checks. Parent game-motion acceptance remains open. Preview173 middle-ADB dormant-slot solution also remains unproven; deletion safety restriction unchanged.

Delivery: editor closed; Publish-Current.ps1 installed preview174 into sole handoff/current. Manifest19/19 verified, retained user/Modules/Kimodo26/26 byte-identical. EXE SHA C8950A202E99180A36BF4F122DF40AFEC4D206E538F96D818B95D150DFD6E190. ADB/ANM deletion regression rerun PASS on copies. No game writes/commit/merge/push. Only current delivered; older prepared173 not installed separately.
