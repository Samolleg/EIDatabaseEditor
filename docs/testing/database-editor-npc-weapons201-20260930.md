# Editor201 — NPC weapon preview

Task DBE-NPC-WEAPON-PREVIEW-20260930. Полный отчёт, identities, before/after, команды и ограничения: `../../work/investigations/dbe-npc-weapon-preview-20260930/REPORT.md`.

Подтверждено headless: ZombieFemaleF14 StrongMan1, оба topor instances, точные TA pixels вместо body atlas, исходный UV и animated attachments; negative in-memory StrongMan0/unknown; sword+shield/twohand/ranged/MOB; equipment33/42 material/Cancel/ApplyUndo и соседний slot. Publish201 DLL совпадает с проверенной DLL;19-file host manifest/7current Modules сохранены. Игровые файлы/current исполнителем не изменены. No-visible-window assertions и неизменные пользовательские preferences/cache.

Ручной gate: два топора и их металл в обычном preview/обоих draft/animation editor после смены NPC/clip/material, сравнение с игрой; сохранить другие поля при Apply/Undo. Полное игровое состояние, временные эффекты, динамический выбор оружия и прежние root-space/constraints acceptances не проверены. См. REPORT с точными шагами.
