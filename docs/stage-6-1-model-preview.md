# 6.1 — 3D-перегляд та виправлення діагностик редактора

Дата: 24 жовтня 2026 року. За уточненням користувача обсяг обмежено **3D-переглядом**. Розміщення зброї у світі не реалізовували. Нових залежностей немає; Mutagen.Bethesda.Skyrim залишається **0.53.1**. Наявні незакомічені результати етапу 5 збережено; початкова копія перед цим завданням: `artifacts/backups/stage-6-20261024-024147`.

## Виправлення CS0103 перед початком 3D

Користувач повідомив про `RecipeIdError` у `WeaponsViewModel.RecipeValidation.cs` та `WeaponsViewModel.Wizard.cs`. Звичайна Debug-збірка і початковий DesignTimeBuild проходили без помилок; згенерований MVVM-код містив `RecipeIdError`. Отже, надана діагностика не відтворилася звичайним компілятором, і точну причину початкового стану language server не встановлено.

Чотири властивості — `RecipeIdError`, `RecipeCountError`, `RecipeWorkbenchError`, `RecipeIngredientsError` — оголошено явно через `SetProperty`. Вони залишаються доступними для WPF binding та зберігають сповіщення про зміни, але їх оголошення більше не залежить від оновлення source generator у редакторі. Інші MVVM-генератори й архітектура не змінені. Після цього виконано повну регресію **889/889** (`artifacts/audit/stage-6-editor-fix.trx`).

Після додавання нових Core-типів окремий DesignTimeBuild виявив старі Debug reference-збірки, тоді як Release проходив. Стандартна Debug-збірка оновила ці посилання; повторний DesignTimeBuild пройшов. Це окреме спостереження, а не доказ причини початкових CS0103.

## Як відкрити

1. Відкрити проєкт і перейти на крок **«Моделі й текстури»**.
2. Натиснути **«3D-перегляд моделі»**.
3. Перемикатися між першою й третьою особою кнопками у вікні. Використовуються поточні шляхи редактора, включно з незбереженими призначеннями.
4. Перетягувати мишкою для обертання; використовувати коліщатко або відстань для масштабу. Є слайдери обертання й освітлення, перемикач текстур і скидання ракурсу.

Поточний вигляд і шлях NIF залишаються видимими після завантаження. Перегляд не зберігає предмет, не змінює ESP/ESL, моделі, текстури або проєктний JSON. Файли з BSA читаються в пам’ять. Для відсутньої першої моделі не підставляється третя. Помилка завантаження очищує стару сцену; застаріле завершення запиту не може повернути попередню модель.

## Дослідження та реалізація

Вбудований WPF `Viewport3D` дає камеру, геометрію, матеріали й світло без нового пакета. Це API для готової геометрії, а не NIF-парсер. Використано власне обмежене read-only читання статичного SSE-піднабору; це не повний інтерпретатор NIF. Першоджерела: [Microsoft: створення WPF 3D-сцени](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-create-a-3-d-scene), [схема nifxml](https://raw.githubusercontent.com/niftools/nifxml/develop/nif.xml).

Підтримано:

- NIF `20.2.0.7`, little endian, user version 12, Bethesda stream 100.
- Сцену з `NiNode`, `BSFadeNode` і статичними `BSTriShape`.
- Ієрархічні translation/rotation/uniform scale, трикутники й UV; перехід від осей NIF до WPF.
- Дифузний слот `BSLightingShaderProperty` → `BSShaderTextureSet`.
- DDS BC1/DXT1, BC2/DXT3, BC3/DXT5, верхній mip-рівень. Є власне декодування RGB565/альфа, тому перегляд цих форматів не залежить від встановленого WIC DDS-кодека. Формати та алгоритми перевіряли за [Microsoft: BC-компресія](https://learn.microsoft.com/en-us/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-block-compression).
- Читання проєктних і Data loose-файлів, а також Skyrim/Dawnguard BSA. Зареєстрований, але відсутній файл проєкту не підміняється іншою копією з Data. Помилка читання BSA повідомляється як помилка, а не як доведена відсутність ресурсу.
- Фонове читання/декодування, скасування, захист від циклічного графа, некоректних індексів, NaN/Infinity, небезпечних шляхів і надмірних розмірів. Геометрія та матеріали WPF заморожуються; обертання не декодує DDS повторно.

Межі ресурсів: NIF до 64 MiB, до 4096 блоків, глибина графа до 64, до 8192 відвідувань графа, до 128 відображених shapes, 60 000 вершин і 120 000 трикутників. DDS — до 4 млн пікселів і 4096 по кожному виміру; сумарний бюджет декодованих текстур — 64 MiB.

## Межі підтримки

- **LE stream 83, FO4, skinning, контролери анімації, інші типи геометрії та particle-дані відхиляються.** Непідтримуваний об’єкт у видимому графі блокує сцену, щоб не показувати неповний предмет як повний.
- BC7, BC4/5, cubemap/volume/array DDS та непідтримувані DX10-варіанти не декодуються. Геометрія може залишитися видимою з нейтральним матеріалом та поясненням.
- Немає відтворення Havok, анімацій, скелета, Skyrim shader flags, normal/glow/environment maps, vertex colors або замін текстур через TXST у плагіні. Нормалі для освітлення обчислює WPF. Перегляд матеріалу наближений.
- Читаються фізичні loose-файли й зазначені ванільні BSA; повний порядок завантаження сторонніх архівів та віртуальна файлова система MO2 не моделюються.
- Успішне зображення не є повною валідацією NIF чи підтвердженням вигляду в грі. Розбір охоплює потрібні для цього перегляду поля, а не всі поля формату.
- Камера й освітлення — тимчасовий стан вікна. Помилки та обмеження не змінюють результат звичайного пакування.

Наявні приймальні ресурси попередніх етапів із stream 83 цим переглядом не конвертуються. Для подальшого розширення потрібні окремі перевірені піднабори або погоджений повніший NIF/DDS-стек; нових бібліотек у цьому завданні не встановлювали.

## Перевірка реальних моделей

Результати остаточної збірки: `artifacts/research/stage-6-20261024-032003/results.json`. Створено 14 знімків справжньої геометрії з текстурами; кілька ракурсів перевірено візуально.

| Шаблон | Третя особа | Перша особа |
|---|---|---|
| IronSword | Показано, 2 meshes / 2 textures | Показано, 2 / 2 |
| IronGreatsword | Показано, 1 / 1 | Показано, 1 / 1 |
| IronDagger | Показано, 2 / 2 | Показано, 2 / 2 |
| IronWarAxe | Показано, 2 / 2 | Показано, 2 / 2 |
| IronMace | Показано, 2 / 2 | Показано, 1 / 1 |
| IronBattleaxe | Показано, 1 / 1 | Показано, 1 / 1 |
| IronWarhammer | Показано, 1 / 1 | Показано, 1 / 1 |
| HuntingBow | Відмова: skinning | Відмова: skinning |
| DLC1CrossBow | Відмова: контролер анімації | Відмова: контролер анімації |

Підсумок: **14 відображених і 4 відхилених через відомі обмеження з 18 перевірених моделей**. Чотири відмови не зараховані як успішне відображення. SHA-256 перевірених Skyrim.esm, Dawnguard.esm, Skyrim - Meshes0.bsa та Skyrim - Meshes1.bsa до/після збіглися; фактичний список хешованих джерел є у JSON.

## Автоматичні перевірки та збірки

- Повна регресія після виправлення властивостей рецептів: **889/889**.
- Цільовий прогін NIF/DDS/WPF/команди перегляду: **28/28**; окремий тест розмірів/мов — **1/1**.
- Остаточна повна регресія: **924/924**, без failed/skipped (`artifacts/audit/stage-6-final.trx`). Це 889 попередніх і 35 нових тестових випадків, включно з DDS DX10, обома режимами альфа BC3, крайовими блоками DDS та закриттям вікна під час незавершеного запиту.
- Перевірено індекси, UV, складені перетворення, помилкові/усічені файли, анімацію/skinning, цикли, прозорість BC1, палітри BC1/2/3, альфа BC2/3, пам’ять, шляхи, скасування, пошкоджений BSA, відсутній ресурс проєкту, незалежність моделей і застарілі запити.
- WPF-тести підтверджують наявність відрендерених кольорових пікселів, матеріалів, зміну камери/світла, очищення сцени після помилки та збереження файлів/незбережених полів редактора.
- Вікно перевірено UK/EN на ширинах 540 і 960; знімки — `artifacts/ui-stage-6/model-preview-*.png`. Попередня матриця семикрокового майстра також входить у регресію.
- Остаточні стандартна Debug-збірка і Release-збірка: **0 помилок, 0 попереджень**. DesignTimeBuild після оновлення Debug-посилань: exit code **0**, діагностик немає. Release для запуску — `artifacts/stage-6-build/SkyrimModPacker.UI.exe`.
- Перевірку безпосередньо в Skyrim пропущено за вказівкою користувача. Нативне керування мишкою/вікном через computer-use і фізичний DPI монітора не перевіряли; взаємодії перевіряються WPF-тестами. Новий плагін цим завданням не створюється, SSEEdit повторно не запускався.

## Файли цього завдання

- `src/SkyrimModPacker.Core/Interfaces/IModelPreviewService.cs`
- `src/SkyrimModPacker.Core/Models/ModelPreview.cs`
- `src/SkyrimModPacker.Infrastructure/DependencyInjection.cs`
- `src/SkyrimModPacker.Infrastructure/Preview/SseNifReader.cs`
- `src/SkyrimModPacker.Infrastructure/Preview/DdsPreviewDecoder.cs`
- `src/SkyrimModPacker.Infrastructure/Preview/ModelPreviewService.cs`
- `src/SkyrimModPacker.UI/App.xaml.cs`
- `src/SkyrimModPacker.UI/Services/ModelPreviewPresenter.cs`
- `src/SkyrimModPacker.UI/Services/PreviewSceneRenderer.cs`
- `src/SkyrimModPacker.UI/Views/ModelPreviewWindow.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.Preview.cs`
- `src/SkyrimModPacker.UI/ViewModels/WeaponsViewModel.RecipeValidation.cs`
- `src/SkyrimModPacker.UI/Views/Pages/WeaponsPage.xaml`
- `src/SkyrimModPacker.UI/Resources/Strings.en.xaml`
- `src/SkyrimModPacker.UI/Resources/Strings.uk.xaml`
- `tests/SkyrimModPacker.Tests/PreviewFixtures.cs`
- `tests/SkyrimModPacker.Tests/ModelPreviewTests.cs`
- `tests/SkyrimModPacker.Tests/ModelPreviewWindowTests.cs`
- `tests/SkyrimModPacker.Tests/WeaponsViewModelTests.Preview.cs`
- `README.md`
- `docs/stage-6-1-model-preview.md`

Приймальний runner і знімки зберігаються в ignored `artifacts/`; вони не додають production-залежностей і не входять до ZIP моду.
