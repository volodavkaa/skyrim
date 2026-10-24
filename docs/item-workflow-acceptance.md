# Успадкування, кілька предметів і доступний 3D-перегляд

Дата: 24.10.2026. Реалізовано запит щодо ванільних властивостей, списку предметів, зрозумілих дій, доступу до 3D та складу ресурсів проєкту.

## Актуальна збірка

Автономний EXE: `publish/item-workflow/SkyrimModPacker.UI.exe`. Окремий .NET Runtime не потрібний. Створено офлайн з уже наявними пакетами та кешованим runtime 8.0.16. Нових залежностей немає, Mutagen.Bethesda.Skyrim залишається 0.53.1.

Інші каталоги `publish/` і старі EXE є попередніми збірками. Поточні функції перевірено саме в `publish/item-workflow/`. Звичайна Release-збірка для розробки: `artifacts/item-workflow-build/SkyrimModPacker.UI.exe`.

## Сценарій роботи

1. Відкрити «Зброя», створити проєкт або відкрити його `mod-project.json`.
2. У постійному блоці «Предмети цього проєкту» натиснути «Додати зброю». Вибрати тип і ванільний шаблон. Це відкриває редактор нового предмета; запис з’явиться у списку після збереження.
3. Призначити NIF першої та/або третьої особи. Імпорт копіює ресурс у проєкт; призначення записується у плагін кнопкою «Зберегти вибрану зброю».
4. Натиснути «Переглянути в 3D» у блоці предметів або біля поля моделі. Перегляд враховує незбережене призначення та не зберігає предмет.
5. У характеристиках переглянути початковий шаблон, дев’ять звукових посилань WEAP, Keyword та відмінності характеристик. Звуки й Keyword походять із запису плагіна, а не з NIF.
6. Зберегти предмет. Додавання наступної зброї залишає попередні предмети у проєкті. «Дублювати зброю» одразу зберігає копію вибраного збереженого предмета та його рецептів; «Редагувати» лише відкриває параметри. Перехід від незбережених змін захищений наявним діалогом збереження/відкидання.
7. На перевірці та перед ZIP видно склад усього проєкту: усі предмети, характеристики, моделі та кількість рецептів. ZIP включає весь плагін і всі зареєстровані власні файли по одному разу.

## Успадкування і відновлення

- Клонування починається з повного ванільного WEAP. Незаповнені поля запиту редагування зберігають поточні значення; зокрема окрема зміна моделі не очищає звуки, Keyword, опис або зачарування та не скидає швидкість/досяжність.
- Для програмного API пропущене поле відрізняється від явно заданого `AttackSound = null`, `Keywords = []`, порожнього опису чи явного очищення зачарування. UI передає поточні значення редактора явно.
- Показано AttackSound, AttackSound2D, AttackLoopSound, AttackFailSound, IdleSound, EquipSound, UnequipSound, PickUpSound і PutDownSound; для посилання видно EditorID і джерело/FormID. Успадковані, змінені та вилучені значення розрізняються. Відсутній звук у самому шаблоні показано як `—` зі станом успадкування.
- Є окремі дії відновлення звуків, Keyword і характеристик. Вони змінюють редактор та скасовуються через Undo; запис у плагін відбувається при збереженні. Характеристики не перейменовують предмет і не змінюють його EditorID чи моделі. Моделі відновлюються на кроці моделей наявними окремими кнопками.
- Для збереженого предмета використовується початковий шаблон із наявних метаданих проєкту, а не перший елемент списку шаблонів. Порівняння характеристик теж використовує цей шаблон.
- Старі/імпортовані проєкти без походження залишаються сумісними. UI чесно показує невідомий шаблон; можна явно вибрати шаблон для відновлення. Відновлення звуків/Keyword саме по собі не вигадує історичне походження предмета.
- Карта звуків входить до чернетки та історії; стара чернетка без карти бере незмінені додаткові звуки з вибраного запису/шаблону.

## Незалежність предметів і ресурси

- Дублікат отримує нові FormID та глобально унікальні EditorID для WEAP, копій власного STAT і COBJ. NextFormID просувається без перенумерації існуючих записів; вихід за компактний діапазон блокує операцію до публікації.
- Рецепти копіюються як окремі записи з новим вихідним предметом. Власний STAT копіюється окремо; ванільний STAT може безпечно залишатися спільним, а зміна його призначення використовує наявний механізм окремого запису.
- NIF/DDS спільні без дублювання байтів. Для заміни моделі одного предмета є наявний імпорт окремої копії. Небезпечна заміна/видалення ресурсу зі збереженими користувачами блокується.
- Походження дубліката та плагін публікуються з наявними перевірками змін/блокуваннями; при помилці запису плагіна метадані відкочуються.
- Видалення з UI прибирає вибраний WEAP і його власні рецепти; інші посилання можуть блокувати видалення. Інші предмети та спільні файли не видаляються. Старий виклик API без явного `deleteRecipes: true` зберігає колишній захист від посилань рецептів.
- «Ресурси проєкту» пояснює призначення власних NIF/DDS. Видно тип, відносний шлях, наявність, користувачів зі збережених записів, стан ZIP і невідоме/відсутнє використання. Ванільні залежності показані окремо. Зареєстрований невикористаний файл теж включається до ZIP.
- Аналіз зв’язків NIF → DDS каталогом не додано. DDS не називається невикористаним лише через відсутність прямих WEAP-посилань. Наявний консервативний захист DDS з моделями збережено. Додатково заборонено видаляти NIF, призначений у поточному незбереженому редакторі.

## Перевірки

- Повна фінальна регресія: **977/977**, 0 failed, 0 skipped; `artifacts/audit/item-workflow-final.trx`. На початку було 924 тести; додано 53 випадки, включно з перевірками дев’яти звукових слотів, збереженням успадкованих полів, незалежними копіями/рецептами, відмовою без часткових записів, відновленням одним Undo та спільним ZIP.
- Старі тести очищення уточнено: очищення звуку/Keyword/зачарування тепер задається явно; перевірка відсутності NULL-підзаписів збережена.
- Debug-компіляція під час повної регресії та звичайна Release-збірка: 0 помилок, 0 попереджень. Автономний publish успішний. Фінальний DesignTimeBuild завершився з exit code 0, без діагностик.
- WPF: UK/EN, Dark/Light, сім кроків, різні ширини та масштаб. Кнопки додавання, дублювання та 3D перевіряються у видимій частині дерева кожного кроку; знімки — `artifacts/ui-item-workflow/`. Це перевірка компонування й рендерингу, а не дослідження з новими користувачами.
- Реальні masters і SSE NIF: `artifacts/acceptance/item-workflow-20261024-111321/results.json`. Два кінцеві ZIP: ESP та ESL; кожен має 4 WEAP (два мечі, кинджал, сокира), 4 власні STAT, 6 COBJ, 6 NIF без повторного копіювання спільних моделей. Усі 16 завантажень сцен першої/третьої особи успішні, звуки та Keyword збігаються з шаблонами. NextFormID — `0x80E`. SHA-256 Skyrim.esm, Dawnguard.esm і двох перевірених meshes BSA до/після збігаються. Текстури в цьому сценарії ванільні, до ZIP не включені.
- **SSEEdit 4.1.5f**: обидва плагіни витягнуто з кінцевих ZIP, завантажено з копією Skyrim.esm в ізольованому Data. Для ESP і ESL окремо виконано Check for Errors: **15 processed records, 0 errors** для кожного. Лог — `artifacts/acceptance/item-workflow-20261024-111321/xedit/SSEEdit-item-workflow.log`. Візуально підтверджено Next Object ID `0000080E` та ESL у заголовку ESL-плагіна. Шляхи ізоляції відповідають [документації xEdit](https://tes5edit.github.io/docs/2-overview.html#-2-8-1-command-line-switches).
- Через computer-use запущено саме автономний EXE з `publish/item-workflow/`. У його українському інтерфейсі відкрито приймальний проєкт, підтверджено список чотирьох предметів та початковий IronDagger. Кнопкою зі списку відкрито справжню текстуровану модель вибраного кинджала; перевірено третю особу та перемикання на `1stPersonIronDagger.nif`. SSEEdit закрито без редагування; SHA-256 його копій ESP/ESL збігаються з плагінами з ZIP (`artifacts/audit/item-workflow-sseedit.json`). Аудит збірки — `artifacts/audit/item-workflow-final.json`.

## Обмеження

- У 3D збережено статичний SSE-піднабір попереднього етапу: анімація, skinning, LE NIF та BC7 не підтримуються. Освітлення/матеріали наближені, усі ефекти Skyrim не відтворюються.
- Окремий вибір нового значення в UI наразі є для основного звуку атаки; інші вісім слотів показуються та відновлюються разом зі звуками шаблону. DetectionSoundLevel не є звуковим посиланням і окремо не редагується.
- Якщо старий проєкт не має відомого походження, відмінності від історичного шаблону визначити неможливо. Це показано явно.
- Рецепти з довільними сторонніми умовами дублюються зі збереженням даних, але наявний редактор таких умов не розширено. Інші залежності, наприклад LVLI, не копіюються автоматично до нового предмета.
- Видалення предмета не виконує загального прибирання всіх його колишніх ресурсів/записів; спільні та зареєстровані файли залишаються. Каталог спирається на збережений плагін, а не на всі зовнішні моди чи віртуальну файлову систему MO2.
- Skyrim не запускався за попередньою домовленістю. Нуль помилок SSEEdit не підтверджує вигляд/поведінку у грі або сумісність із повним стороннім load order.

## Змінені файли

Змінено 36 файлів відносно резервної копії перед цим запитом. Попередні незакомічені етапи не відкинуто. Резервна копія — `artifacts/backups/item-workflow-20261024-033543`.

- `docs/item-workflow-acceptance.md`
- `README.md`
- `src/SkyrimModPacker.Core/Interfaces/IPluginBuilderService.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/DuplicateWeaponRequest.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponDetails.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponEditorDraft.cs`
- `src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponRecord.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.Advanced.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.Duplication.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.Sounds.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginReferenceValidator.cs`
- `src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Duplication.cs`
- `src/SkyrimModPacker.UI/Resources/Strings.en.xaml`
- `src/SkyrimModPacker.UI/Resources/Strings.uk.xaml`
- `src/SkyrimModPacker.UI/ViewModels/ProjectResourceRow.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Advanced.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Comparison.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Drafts.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Inheritance.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Items.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Project.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Recovery.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.ResourceEditing.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Resources.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.UnsavedChanges.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Wizard.cs`
- `src/SkyrimModPacker.UI/Views/Pages/WeaponsPage.xaml`
- `tests/SkyrimModPacker.Tests/AdvancedPluginBuilderTests.cs`
- `tests/SkyrimModPacker.Tests/OptionalPluginTextTests.cs`
- `tests/SkyrimModPacker.Tests/PluginReferenceTests.cs`
- `tests/SkyrimModPacker.Tests/WeaponProjectTests.ItemWorkflow.cs`
- `tests/SkyrimModPacker.Tests/WeaponsPageTests.cs`
- `tests/SkyrimModPacker.Tests/WeaponsViewModelTests.ItemWorkflow.cs`
- `tests/SkyrimModPacker.Tests/WeaponsViewModelTests.Stage3Workflow.cs`
