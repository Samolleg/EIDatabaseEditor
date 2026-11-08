# Поиск внутри списка формы оружия и брони — версия 207

Задача `DBE-EQUIPMENT-SHAPE-SEARCH-20261008/POPUP`.

## Причина и исправление

В версии 206 поле поиска размещалось в основной панели **снаружи** ComboBox.
Пользователь подтвердил, что при раскрытии списка писать в этом поле нельзя.
Регрессия 206 проверяла текст и фильтрацию при закрытом списке, поэтому не
подтверждала нужный сценарий ввода при открытом popup.

Оба выбора формы используют `SearchableComboBox`, сохраняющий обычный ComboBox
и его контейнеры пунктов. Поиск теперь находится внутри `PART_Popup`, над
прокручиваемыми результатами, в том же поддереве захвата мыши. Заголовок не
прокручивается вместе с пунктами. При открытии фокус переходит в TextBox после
штатной навигации ComboBox. Пробел, Home/End и перемещение текстового курсора
обрабатывает TextBox; встроенный префиксный TextSearch ComboBox отключён.

Ввод оставляет список открытым и сохраняет выбранную форму, материал, черновик
и текущий предпросмотр. Стрелка вниз фокусирует первое совпадение; Enter из
поиска выбирает совпадение и закрывает список, Escape закрывает только список.
При отсутствии совпадений Enter ничего не выбирает. Текущая несовпадающая форма
сохраняется отдельно; навигация из поиска её пропускает. Кнопка × очищает запрос
и возвращает фокус в поиск, повторное открытие сохраняет запрос. Поиск по RU,
именам форм и моделям, названиям всех совместимых материалов и типам брони
сохранён из 206.

## Проверки итоговых DLL

Release build UiSmoke и зависимостей: 0 ошибок, 0 предупреждений.

`--equipment-editor` на установленной PZ/Dejavu `database.res` завершился PASS.
Исходные RES не записывались; регрессия проверяет неизменность исходной БД.
Приложение и нативный popup не показывались пользователю.

Проверка использовала реальное поддерево popup из итогового ControlTemplate в
двух скрытых `HwndSource`, без `WS_VISIBLE`; `Popup.IsOpen` подавлен локально
**только тестом**, а `ComboBox.IsDropDownOpen` действительно установлен.
Через WPF routed events в реальный TextBox вводились русский текст и пробел;
проверены синхронное обновление фильтра, сохранение открытого состояния и
черновика, Home/End, автоматический keyboard focus при открытии, фокус первого
совпадения по Down, Enter/Escape, повторное открытие, no-match Enter и очистка
с возвращением keyboard focus. Структурная проверка требует размещение TextBox
внутри Popup.Child и отсутствие прежнего внешнего поля.

Связанный регресс проверил RU/ID/модельные коды/несколько слов/регистр/ё-е,
сохранение формы и материала при фильтрации, пустой результат, семь типов брони,
выбор отфильтрованного пункта и построение предмета, сохранение запроса при
перелокализации материалов. Equipment regression проверил Human Hero и оба
оружейных слота ZombieFemaleF14, видимую сцену/анимацию в headless renderer,
Cancel, реальный Apply/Undo, сохранность соседних полей и исходника.

SHA-256:

| Артефакт | SHA-256 |
| --- | --- |
| EvilIslands.DatabaseEditor.dll | EB467D7781B459B0A04C6E1E3614511923E7A2B73A65E331411D7A47407C1300 |
| EvilIslands.DatabaseEditor.Core.dll | BA2CCE626C2CD54D07DDDBC113889971ABE0CA8D3C653EDB82EB1BF2418EF73D |
| EvilIslands.DatabaseEditor.UiSmoke.dll | 483146970E5542DABF9BA124BD79547098C0DEF3EC60C6D155DD8D24CDAC395C |

Product/file revision 207; стабильная assembly binding identity сохранена.
Core не менял логику в этой правке: новая identity связана с номером сборки.
Исходная ветка `feature/structured-list-content-builder`, HEAD
`403f155159376ea7c3fc25c3937dc51b100c95f9` плюс существующие локальные изменения;
нового коммита нет. Полная identity исходников, исходные копии и точечная дельта
сохранены в `work/investigations/equipment-popup-search-20261008` внутри проекта
редактора. Несвязанные изменения не заменялись.

## Установка

2026-10-08T13:27:49.7848473Z выполнен `tools/Publish-Current.ps1 -PackagePath`
из проверенного приватного candidate. Редактор и Kimodo были закрыты. Итоговые
App/Core совпали с DLL регрессии; после установки сверены все 19 payload файлов.
Семь файлов действующего модуля Малахита сохранены byte-for-byte; Kimodo/Qwen и
пользовательские файлы не входили в изменение host и сохранены штатным updater.
Комплект установлен в постоянный `handoff/current`, приложение не запускалось.
Rollback-копия штатного updater указана в `install.log`.

Источники правки:

- `src/EvilIslands.DatabaseEditor.App/Controls/SearchableComboBox.cs`
- `src/EvilIslands.DatabaseEditor.App/Dialogs/StructuredStringListEditorDialog.xaml`
- `src/EvilIslands.DatabaseEditor.App/Dialogs/StructuredStringListEditorDialog.xaml.cs`
- `src/EvilIslands.DatabaseEditor.App/Dialogs/StructuredStringListEditorDialog.ShapeSearch.cs`
- `tests/EvilIslands.DatabaseEditor.UiSmoke/EquipmentShapeSearchRegression.cs`
- `Directory.Build.props`, README и §8 PROJECT_KNOWLEDGE.

Доказательства: `build-final.log`, `equipment-regression-final.log`,
`build-identity.json`, `source-files.sha256.json`, `incremental-source.diff`,
`preserved-modules.json`, `publish-final.log`, `install.log`,
`install-receipt.json`, `headless-focus-v207/equipment.png` в приватном каталоге
`work/investigations/equipment-popup-search-20261008`.

## Практические границы

Headless HWND проверяет реальные template, focus и keyboard/text обработчики,
но не демонстрирует нативное размещение popup на рабочем столе, физический
захват/клик мышью или IME. Интерактивная проверка пользователем остаётся отдельной:
раскрыть форму оружия и брони, сразу напечатать RU/код модели, выбрать пункт
мышью и клавиатурой, повторно открыть и очистить запрос. Успех тестов не
объявляется подтверждением игрового поведения. Код модели в подсказке остаётся
расчётом по БД, а не доказательством существования ресурса.

Игровые файлы, модельные ресурсы, Малахит и его конфигурация не изменялись;
интерактивные приложения не запускались, merge/push/публикация не выполнялись.
