# Preview 169: высота корня новой анимации

Исходное воспроизведение: у `unhufe` клип `twerk1` имел первый `hp.Z=0`, тогда как `cidle09` имеет `hp.Z=0,994170`. При создании клипа в Т-позе редактор брал нулевой корень из BON, и Kimodo прибавлял движение к этому нулю. Диагностические значения и отделение истории `twerk1` от воспроизводимого дефекта: `work/investigations/database-editor-anm-root-height-20260924/REPORT.md` общего workspace.

Изменение: при `CreateAnimationAsync(..., bindPose:true)` корневая позиция берётся из первого кадра выбранной опорной анимации; повороты и морфы исходной позы по-прежнему сбрасываются. Обычное копирование не изменено. Окно Kimodo позволяет выбрать опорную анимацию и показывает её стартовую высоту; если текущий клип существенно ниже `cidle09` этой модели, предлагается `cidle09`. Разность положений SOMA переносится относительно первого кадра с прежним преобразованием осей и масштабом. Старые клипы не меняются автоматически.

Проверка на текущих read-only ресурсах JamaisVu135 (все тестовые записи — только во временные файлы):

```powershell
dotnet build tests/EvilIslands.DatabaseEditor.UiSmoke/EvilIslands.DatabaseEditor.UiSmoke.csproj -c Release --no-restore
dotnet run --project tests/EvilIslands.DatabaseEditor.UiSmoke/EvilIslands.DatabaseEditor.UiSmoke.csproj -c Release --no-build -- --animation-root-height 'C:\GOG Games\Evil Islands\Starter\MODS\JamaisVu135\res\database.res' '<папка реального вывода Kimodo>'
dotnet run --project tests/EvilIslands.DatabaseEditor.UiSmoke/EvilIslands.DatabaseEditor.UiSmoke.csproj -c Release --no-build -- --animation-edit 'C:\GOG Games\Evil Islands\Starter\MODS\JamaisVu135\res\database.res' '<временная папка результатов>'
dotnet run --project tests/EvilIslands.DatabaseEditor.UiSmoke/EvilIslands.DatabaseEditor.UiSmoke.csproj -c Release --no-build -- --anm-layout 'C:\GOG Games\Evil Islands\Starter\MODS\JamaisVu135\res\database.res'
```

Целевой `--animation-root-height` проверяет: предложение исправного опорного клипа вместо плохого `twerk1`, создание Т-позы и обычной копии, 30 кадров реального вывода Kimodo с ожидаемыми X/Y/Z и масштабом, намеренное снижение таза, отдельный трёхкадровый сценарий подъёма и опускания корня, побайтовую сохранность посторонних клипов, вложенную структуру ANM и повторное чтение временных отдельного ANM и RES независимым парсером дорожек. Получено: начальный `hp.Z=0,994170`, минимальный после реального движения `0,951842`; сохранённые значения совпадают с предпросмотром.

Границы: исходный 120-кадровый Kimodo-вывод для `twerk1` отсутствует; существующий `twerk1` не исправлен автоматически и активные ресурсы игры не изменены. Сборка и автоматические тесты не подтверждают воспроизведение новой анимации в игре. После сохранения новой анимации нужен отдельный игровой тест с точной моделью и командой; погружение при обычной анимации 1 — отдельное расследование.

Поставка 2026-09-24: `handoff/current` обновлён штатным `tools/Publish-Current.ps1` после подтверждения, что редактор закрыт. ProductVersion `0.7.0-preview.169+c3809f07ec3bdcb837fea17f5050cad4ca3eff9a`, SHA-256 EXE `529ABE99FF5434A5C3B94CD2FB53839092987DCAE2C51224E31AA96D3340FF62`. Манифест 19/19 совпадает, семь файлов МАЛАХИТА и 19 сторонних файлов Kimodo сохранились. Ветка source `feature/structured-list-content-builder`, HEAD `c3809f07ec3bdcb837fea17f5050cad4ca3eff9a`, рабочее дерево содержит ранее существовавшие несвязанные правки; нового коммита, merge и push нет. Установленные игровые `database.res`, `unhufe.res` и отдельный `unhufe.anm` остались с прежними SHA-256 (см. диагностический отчёт). Runtime toggle: N/A — новый переключатель не запрашивался и не добавлялся. Ручная проверка в игре не проводилась.
