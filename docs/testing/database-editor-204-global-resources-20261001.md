# Database Editor 204: F5 и связанные ресурсы

Source: собственное dirty дерево редактора, branch
`feature/structured-list-content-builder`, HEAD
`c3809f07ec3bdcb837fea17f5050cad4ca3eff9a`, result commit N/A.
Toggle/config: N/A — новый toggle не запрашивался и не добавлялся.

Evidence: `work/investigations/global-resource-validation-20261001/REPORT.md`,
delivery.json, source-task.diff, финальные `*-final-dedup.log` и PNG страницы.
Expected: F5 на исходном r3 fixture находит две ошибочные daxe01/F14 без открытия
модели, repaired снимает именно эти ошибки. Observed: PASS на финальных App/Core.

Проверены реальный F5/service/VM/ValidationGrid, структурная БД, MMP missing/corrupt,
FIG malformed/unsupported, scope optional preview examples, cancel/switch/deferred,
source change/fresh read, preview loadouts/StrongMan/материалы и Apply/Cancel/Undo.
Все тесты headless, изолированные preferences/recovery, нормальный shutdown.
Live DB EA9F9087 read-only scan complete configured chain; подробные counts
отдельны от неполных fixtures. Новые resource findings не являются разрешением
на ремонт данных. Owner desktop/game confirmation отсутствует.

Поставка `work/handoffs/database-editor-204-global-resources-r2`;
App E35F2FAA / Core B568A7C9, полные SHA и manifest в delivery.json.
Не устанавливалось исполнителем; current203/игра/модули сохранены.
Merge/push не разрешены. Родительские runtime/визуальные gates остаются открыты.
