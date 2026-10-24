# Етап 5 — майстер створення зброї та експорт

Дата: 24 жовтня 2026 року. Реалізовано завдання 5.1–5.5; етапи 6 і 7 не розпочиналися.

Збережено архітектуру, Mutagen.Bethesda.Skyrim **0.53.1** і попередні можливості редактора. Нових залежностей немає. На початку робоче дерево було чистим, HEAD `3c40a56`; додаткова копія початкових файлів: `artifacts/backups/stage-5-20261024-020903`. Комітів під час етапу не створювали.

## Реалізована поведінка

### 5.1. Сім кроків

Тип зброї й робочий проєкт → ванільний шаблон → моделі й текстури → характеристики → рецепти → перевірка → ZIP зі звітом.

Переходи зберігають введені дані. Збереження предмета на кроці рецептів є явною дією; рецепти необов’язкові. Непідтверджені зміни предмета або рецепта блокують продовження з поясненням. Рецепт можна зберегти або відкинути його чернетку. Некоректні EditorID, кількість, верстат та інгредієнти мають повідомлення біля полів. Заряд без зачарування повинен дорівнювати нулю.

Перед ZIP перевіряються ресурси й плагін; сам конвеєр повторює перевірки під час експорту. Поточний крок записується в незалежну чернетку. Додаткове поле `WizardSchemaVersion = 2` зберігає сумісність: старий п’ятий крок «Підсумок» відновлюється як шостий крок «Перевірка». Нові кроки рецептів і ZIP відновлюються без зміни індексу.

### 5.2. Особисті пресети

Атомарне сховище `weapon-presets.json` у каталозі даних програми. Зберігаються тип, шкода, вага, вартість, швидкість, досяжність, опис, Keywords, звук, зачарування та заряд. Назва предмета, EditorID, моделі й рецепти до пресета не входять.

Застосування доступне до нового предмета сумісного типу. Перед зміною полів перевіряються версія пресета, числові значення і наявність посилань серед актуальних підтримуваних ванільних записів. Невідоме або відсутнє посилання блокує всю операцію. Існуючий предмет не перезаписується застосуванням пресета. Дія підтримує Undo/Redo; сама вона не записує ESP. Є завантаження, створення і видалення пресетів. Дублікати назв відхиляються; пошкоджене сховище не замінюється порожнім.

### 5.3. Пакетний імпорт

Попередній список кількох NIF/DDS показує джерело, призначення в проєкті, конфлікти та роль файла. Для NIF можна вибрати першу особу, третю особу або імпорт без призначення. На кожний вигляд дозволено щонайбільше одну модель.

Попередній перегляд нічого не імпортує. План містить SHA-256 проєктного JSON і кожного джерела; зміни після перегляду вимагають нового перегляду. Контрольна сума перевіряється також після копіювання у staging. Конфлікт, зайнятий шлях або помилка одного файла блокують весь набір. Публікація використовує наявний транзакційний механізм ресурсів із відкатом файлів і метаданих. Перевірено помилку читання джерела та помилку публікації JSON після копіювання ресурсів.

NIF отримують окремий простір `meshes/<project>/Batch/<id>/`. DDS зберігають шлях під початковим `textures/`, якщо він є; для окремих DDS використовується каталог проєкту. Існуючі файли не перезаписуються. Призначення моделей після імпорту залишаються змінами чернетки до збереження предмета. Після збою можна повторити попередній перегляд; після успіху той самий план не запускається повторно. Зміна мови оновлює назви ролей, зберігаючи вибрані призначення.

### 5.4. Звіт усередині ZIP

Майстер вмикає `IncludeExportReport`; старі профілі за замовчуванням зберігають попередній формат архіву. Редагування й копіювання профілю зберігають цей прапорець.

`mod-report.json` містить створені записи й overrides з FormKey/EditorID, masters, зв’язки моделей і STAT, DDS, рецепти з інгредієнтами/кількостями/верстатами/умовами/Perk, результат перевірки плагіна, попередження, непідтримувані й пропущені перевірки. Маніфест ресурсів містить канонічні шляхи, розміри та SHA-256 фактично запакованих файлів, включно зі staged ESL-плагіном.

Спочатку створюється й незалежно перевіряється ZIP корисного вмісту. Його результат `PayloadStatus = Passed` записується у звіт. Після додавання звіту фінальний ZIP перевіряється повторно: точний набір записів, шляхи, довжини й SHA-256. До атомарної публікації повторно перевіряються джерела, сам архів і незмінність попереднього вихідного файла. Пошкоджений ZIP, збій або скасування на будь-якому проході залишають попередній архів. Звіт не містить власної контрольної суми або контрольної суми ZIP, щоб уникнути циклічного хешування; це явно зазначено в JSON. Історія збірок враховує доданий файл звіту.

### 5.5. Інтерфейс

UK/EN мають однакові набори з **367 ресурсних ключів**. Оновлено назви кроків, підказку про відновлення чернеток, повідомлення про рецепти, пресети й пакетний імпорт. Перевірка WPF охоплює сім кроків, обидві мови та теми Dark/Light, ширини 420/600/900/1200 DIP, масштаб редактора 100/130% і DPI кореневого visual 96/144/192. Перевіряється відсутність горизонтального переповнення, включно з довгими шляхами імпорту. Створено **156 PNG**: 140 основних ракурсів та 16 ракурсів після прокручування донизу. Вибрані знімки перевірено візуально на читабельність і контраст. Знімки: `artifacts/ui-stage-5/`; це рендеринг WPF, а не перемикання фізичного монітора Windows.

## Перевірки

- Контрольна повна регресія: **888/888**, без failed/skipped (`artifacts/audit/stage-5-regression.trx`).
- Остаточна повна регресія: **889/889**, без failed/skipped (`artifacts/audit/stage-5-final.trx`); включає заповнені нові елементи UI, довгі шляхи й зміну мови пакетного імпорту. Додано 66 тестових випадків відносно попередніх 823.
- Приймальний прогін на встановлених masters із ZIP/ESL і звітами: **16/16**, `artifacts/acceptance/stage-5-20261024-022527/results.json`. Шаблони: IronGreatsword, IronDagger, IronWarAxe, IronMace, IronBattleaxe, IronWarhammer, HuntingBow, DLC1CrossBow; кожний у ESP та ESL. Контрольні суми Skyrim.esm, Update.esm і Dawnguard.esm до й після однакові.
- Додатковий окремий прогін IronSword: **2/2**, `artifacts/acceptance/stage-5-20261024-023545/results.json`; masters незмінні. Сукупно — **18 успішних архівів** для дев’яти шаблонів, усіх восьми підтримуваних типів зброї. Попередня додаткова спроба `stage-5-20261024-023225` завершилася `Access denied` під час створення/публікації staging-каталогу проєкту; повтор після основного прогону пройшов без зміни коду. Причину відмови ОС не встановлено; це не був успішний приймальний запуск.
- Після публікації додатково перевірено вміст звітів і SHA-256 записів готових ZIP; аудит — `artifacts/audit/stage-5-real-archive-audit.json`.
- Release-збірка: **0 помилок, 0 попереджень**; вихідні файли — `artifacts/stage-5-build/`.
- SSEEdit: виконати перевірку цього етапу не вдалося. Ініціалізація computer-use завершилася `windows sandbox failed: CreateProcessWithLogonW failed: 2`; запис спроби — `artifacts/audit/stage-5-sseedit-attempt.json`. Успішні перевірки попередніх етапів не є перевіркою нових артефактів.
- Skyrim не запускали відповідно до вказівки користувача.

## Відомі межі й неперевірені сценарії

- NIF не переписується; геометрія, анімація, внутрішні посилання на DDS і вміст DDS не перевіряються. Збереження відносних шляхів DDS саме по собі не доводить їх використання моделлю. Це зазначено у звіті експорту.
- Моделі й текстури пресетами не переносяться між проєктами. Відсутні або сторонні посилання пресета потрібно виправити перед застосуванням.
- Попередній план пакетного імпорту є тимчасовим; після перезапуску файли треба вибрати знову. Вже імпортовані ресурси та підтверджені шляхи моделей зберігаються звичайними механізмами проєкту/чернетки.
- Експорт зі звітом виконує два проходи ZIP, тому дорожчий за звичайний експорт. Службові staging-файли до фінального ZIP не входять.
- Фактичне перемикання Windows DPI між моніторами, інтерактивна перевірка в SSEEdit та ігрові сценарії залишаються неперевіреними.

## Змінені файли

Application:

- `src/SkyrimModPacker.Application/Steps/ArchiveStep.cs`

Core:

- `src/SkyrimModPacker.Core/Interfaces/IReportedArchiveService.cs`
- `src/SkyrimModPacker.Core/Interfaces/IWeaponPresetRepository.cs`
- `src/SkyrimModPacker.Core/Interfaces/IWeaponProjectService.cs`
- `src/SkyrimModPacker.Core/Models/ModProfile.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/BatchResourceImport.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponEditorDraft.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponPreset.cs`

Infrastructure:

- `src/SkyrimModPacker.Infrastructure/DependencyInjection.cs`
- `src/SkyrimModPacker.Infrastructure/Archivers/ReportedArchiveService.cs`
- `src/SkyrimModPacker.Infrastructure/Persistence/JsonWeaponPresetRepository.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Batch.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Resources.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.cs`

UI:

- `src/SkyrimModPacker.UI/Resources/Strings.en.xaml`
- `src/SkyrimModPacker.UI/Resources/Strings.uk.xaml`
- `src/SkyrimModPacker.UI/ViewModels/ProfilesViewModel.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Advanced.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Batch.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Drafts.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Packing.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Presets.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.RecipeValidation.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Recovery.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Wizard.cs`
- `src/SkyrimModPacker.UI/Views/Pages/WeaponsPage.xaml`

Тести й документація:

- `tests/SkyrimModPacker.Tests/WeaponPresetTests.cs`
- `tests/SkyrimModPacker.Tests/WeaponProjectTests.Batch.cs`
- `tests/SkyrimModPacker.Tests/WeaponProjectTests.Packing.cs`
- `tests/SkyrimModPacker.Tests/WeaponProjectTests.Reports.cs`
- `tests/SkyrimModPacker.Tests/WeaponsPageTests.cs`
- `tests/SkyrimModPacker.Tests/WeaponsViewModelTests.Stage5.cs`
- `tests/SkyrimModPacker.Tests/WeaponsViewModelTests.Wizard.cs`
- `README.md`
- `docs/stage-5-completion.md`
