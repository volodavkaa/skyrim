# Завдання 2.1 — незалежні моделі першої та третьої особи

Дата: 23 жовтня 2026 року. Виконано лише завдання 2.1. Завдання 2.2–2.4 не розпочато.

## Реалізовано

- Окремі поля і команди імпорту NIF для третьої та першої особи. Для кожної моделі показано фактичний шлях, походження, доступність і включення до ZIP. Українські та англійські написи додано до чинних словників.
- Третя особа використовує WEAP.Model.File (MODL). Перша — WEAP.FirstPersonModel (WNAM), який посилається на STAT.Model.File. Для власної першої особи створюється власний STAT зі збереженням решти полів початкового STAT.
- Власний STAT повторно використовується, коли інші записи поточного плагіна на нього не посилаються. Для спільного STAT застосовано копіювання перед зміною. Повторне призначення того самого шляху не створює новий запис. Після повернення до ванільного STAT попередній незадіяний керований STAT можна повторно використати.
- NIF не перезаписуються при повторному імпорті з іншого місця. Спільний файл можна призначити обом моделям через той самий зареєстрований шлях. Якщо імпортований шлях збігається з моделлю WEAP/STAT у Skyrim.esm, файл переноситься до meshes/<проєкт>/Imported, щоб не підміняти ванільний ресурс для інших предметів.
- Версія mod-project.json залишається 1. Додано необов’язкові VanillaTemplates (FormID зброї → FormID шаблону) та VanillaModels (початкові шляхи і посилання на STAT). Старі проєкти без цих полів відкриваються. Для старого/імпортованого проєкту без походження є окремий список явного вибору шаблону, який не перемикає редагований предмет.
- Метадані походження публікуються атомарно під блокуванням перед плагіном; помилка їх запису зупиняє запис плагіна. Якщо запис плагіна не вдався, попередні метадані відновлюються. Початковий шаблон не замінюється під час наступних редагувань. Зміна початкових ванільних шляхів/посилань у зовнішньо зміненому Skyrim.esm блокує відновлення з повідомленням.
- Старі запити без нових полів зберігають WNAM. Явне відновлення шаблону без WNAM прибирає необов’язкове посилання без серіалізації NULL.
- Виділення нового STAT зберігає компактні FormID, унікальні EditorID та NextFormID. Існуючі записи не перенумеровуються. Вичерпання компактного діапазону зупиняє операцію без публікації змін.
- Валідатор перевіряє тип, наявність і стан WNAM у плагіні та оголошених masters. Перевірка тимчасового плагіна додатково порівнює шлях моделі STAT.
- Перед ZIP перевіряються обидві моделі. Незареєстрована або відсутня власна перша NIF блокує пакування; посилання зі Skyrim-owned FormKey додатково звіряється з ванільним STAT, тому custom override не проходить як ванільний лише за власником FormKey. Спільна зареєстрована NIF додається один раз.
- Для ванільного ресурсу розрізняються Found, Missing, CheckFailed. Перевіряються loose-файли Data і Skyrim*.bsa наявним Mutagen; для знайденого запису BSA відкривається потік і читається байт. Помилка читання архіву не перетворюється на Missing.

## Перевірки

- Початковий контрольний прогін: 561/561, 0 невдалих, 0 пропущених.
- Додано 47 тестових випадків у двох нових файлах. Покрито незалежні комбінації моделей, спільний шлях, повторне використання і копіювання STAT, старі проєкти, відсутній WNAM, відновлення після переміщення, точний початковий знімок, змінений master, помилки метаданих і плагіна, небезпечні шляхи, межу FormID, недійсні посилання, відсутні/незареєстровані файли, конфлікт і скасування імпорту. П’ять комбінацій моделей перевірено з ESL/без ESL та зі staging/без staging (20 випадків).
- Фінальна повна Release-регресія: **608 успішних, 0 невдалих, 0 пропущених**. TRX: [stage-2-1-final.trx](../artifacts/audit/stage-2-1-final.trx).
- Фінальна Release-збірка: **0 помилок, 0 попереджень**. Нових залежностей немає, Mutagen.Bethesda.Skyrim залишається 0.53.1. Початкову помилку компіляції IStatic/Static виправлено; обмеження пісочниці на читання Windows SDK усунуто дозволеним запуском збірки поза нею.
- Release-EXE запущено. Відкрито фінальний приймальний проєкт, пройдено до кроку моделей; обидва фактичні шляхи, написи «Власна (проєкт); Знайдено; ZIP: Так» і кнопки відновлення візуально переглянуто українською в розгорнутому вікні. Окремої повної ручної DPI/англійської перевірки не проводилось; словники та WPF/VM перевіряються автоматично.

Команди:

~~~powershell
dotnet test SkyrimModPacker.sln -c Release --no-restore --verbosity minimal --logger 'trx;LogFileName=stage-2-1-final.trx' --results-directory artifacts/audit
dotnet build SkyrimModPacker.sln -c Release --no-restore --verbosity minimal
~~~

## Реальні ресурси та SSEEdit

Фінальний runner використав реальний IronSword із Skyrim.esm, наявну NIF користувача та чотири DDS лише для читання. Створено окремі third.nif і first.nif у workspace. Обидві ванільні моделі IronSword знайдено і відкрито з Skyrim - Meshes1.bsa. Чотири ZIP із власними моделями пройшли чинну перевірку складу і SHA-256: ESL увімкнено/вимкнено × staging увімкнено/вимкнено. У плагіні один WEAP 000800, один повторно використаний STAT 000801, NextFormID 000802. Контрольні суми шести використаних вихідних файлів (Skyrim.esm, NIF, чотири DDS) до і після збігаються. Це не хешування всієї гри.

- [Машиночитаний приймальний звіт](../artifacts/acceptance/stage-2-1-final/report.json).
- [Фінальний ZIP з ESL і staging](../artifacts/acceptance/stage-2-1-final/archives/Stage21-stage-True-esl-True.zip).
- [Фінальний робочий проєкт](../artifacts/acceptance/stage-2-1-final/Stage21/mod-project.json).

SSEEdit 4.1.5f запущено з окремими Data, plugins.txt, кешем, журналом і копією Skyrim.esm у workspace. Параметри ізоляції відповідають [документації xEdit](https://tes5edit.github.io/docs/2-overview.html). Спочатку відкрито SSEView; для Check for Errors його перезапущено у стандартному SSEEdit. Ігрові файли не редагувались.

**Check for Errors: Processed Records: 3, Errors found: 0.** Візуально підтверджено ESL, Next Object ID 00000802, WEAP.Model = Stage21\third.nif, WNAM → SMP_FirstPerson_000800_000801 [STAT:FE000801], STAT.Model = Stage21\first.nif. Журнал: [SSEEdit-stage-2-1.log](../artifacts/acceptance/stage-2-1/xedit/SSEEdit-stage-2-1.log).

Перевірений SSEEdit плагін побайтово ідентичний плагіну в ZIP фінальної версії: SHA-256 **429992D249F291F3F2807E7AA2568923493E69BB9FBE5B3D5011EBE65B196794**. [Порівняння хешів](../artifacts/acceptance/stage-2-1-final/sseedit-plugin-hashes.json).

Керування вікнами SSEEdit і програми виконано через [computer-use](C:/Users/volod/.codex/plugins/cache/openai-bundled/computer-use/26.1016.61520/skills/computer-use/SKILL.md).

## Обмеження та неперевірені сценарії

- **Skyrim не запускався за рішенням користувача. Вигляд, хват, анімації, матеріали, фізика і робота в грі не підтверджені.** Наявна користувацька NIF за назвою є кинджалом, тоді як редактор наразі клонує одноручні мечі.
- Вміст NIF, текстурні посилання, геометрія і сумісність DDS не аналізуються; NIF/DDS не перетворюються.
- BSA-перевірка обмежена loose Data і Skyrim*.bsa; віртуальна файлова система менеджера модів та повний встановлений load order не відтворюються.
- Захист спільного STAT аналізує посилання поточного плагіна. Зовнішні плагіни, які залежать від нього, автоматично не скануються.
- Незадіяні власні STAT і зареєстровані ресурси автоматично не видаляються. Відновлення ванільної моделі не прибирає раніше імпортовану NIF зі списку ресурсів/ZIP; це зберігає безпеку спільних ресурсів і не розширює 2.1 до менеджера ресурсів.
- Метадані та ESP — два файли, а не єдина транзакція файлової системи. Відмови запису з відкатом перевірені; примусове завершення процесу між публікаціями може залишити невикористаний запис походження. Повне відновлення після аварій належить окремому етапу.
- Окремої самодостатньої публікації EXE та перевірки на чистій Windows у 2.1 не виконувалось. Попередня publish/stage-1-acceptance не замінювалась.

## Змінені файли цього завдання

Перелік порівняно з резервною копією робочого дерева на початку 2.1; попередні незакомічені зміни не скидались.

- README.md
- docs/stage-2-1-models.md
- src/SkyrimModPacker.Core/Interfaces/IPluginBuilderService.cs
- src/SkyrimModPacker.Core/Interfaces/IWeaponProjectService.cs
- src/SkyrimModPacker.Core/Models/PluginBuilder/CloneSwordRequest.cs
- src/SkyrimModPacker.Core/Models/PluginBuilder/UpdateSwordRequest.cs
- src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponDetails.cs
- src/SkyrimModPacker.Core/Models/PluginBuilder/WeaponModels.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.Advanced.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.Models.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginBuilderService.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginFileTransaction.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/PluginReferenceValidator.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Models.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Packing.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.Resources.cs
- src/SkyrimModPacker.Infrastructure/PluginBuilder/WeaponProjectService.cs
- src/SkyrimModPacker.UI/Resources/Strings.en.xaml
- src/SkyrimModPacker.UI/Resources/Strings.uk.xaml
- src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Advanced.cs
- src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Models.cs
- src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Resources.cs
- src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Wizard.cs
- src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.cs
- src/SkyrimModPacker.UI/Views/Pages/WeaponsPage.xaml
- tests/SkyrimModPacker.Tests/WeaponModelTests.cs
- tests/SkyrimModPacker.Tests/WeaponsViewModelTests.Models.cs

Допоміжні локальні артефакти, виключені .gitignore: artifacts/stage-2-1-runner/runner.csproj і Program.cs; artifacts/audit/stage-2-1-final.trx; artifacts/acceptance/stage-2-1-final; ізольований SSEEdit у artifacts/acceptance/stage-2-1/xedit. Резервна копія початкового стану: artifacts/backups/stage-2-1-20261023-184220.

Наступне завдання — 2.2, єдиний менеджер ресурсів. Реалізацію не розпочато; очікується команда «далі».
