# Завдання 2.4 — дослідження аналізу NIF/DDS

Дата дослідження: 23.10.2026. Виконано тільки 2.4: аудит наявного коду й бібліотек, читання заголовків локальних приймальних ресурсів та аналіз першоджерел. Аналізатор у програму не додано, NIF/DDS не переписано, нових залежностей не встановлено.

## Висновок

**DDS-заголовки та відомі формати можна перевіряти засобами .NET без нових бібліотек. Для надійного читання графа NIF потрібен окремий парсер; у встановленому Mutagen 0.53.1 такого API не знайдено.** Перевірка матеріалів SE/AE потребує спільного аналізу геометрії, шейдерів, текстур і overrides плагіна. Жодна з цих перевірок сама по собі не підтверджує вигляд у грі.

Виявлено практичний ризик у приймальних NIF попередніх етапів: `first.nif` та `third.nif` мають user version 12 і Bethesda stream version **83**, тоді як схема nifxml позначає Skyrim SE комбінацією **20.2.0.7 / 12 / 100**, а Skyrim — **20.2.0.7 / 12 / 83**. Це ознака LE-варіанта, яку слід перевірити перед використанням у SE/AE; заголовок не дає достатніх підстав ані гарантувати сумісність, ані оголошувати всі блоки непридатними. Автоматичну конвертацію не проводили. [Схема версій nifxml](https://raw.githubusercontent.com/niftools/nifxml/develop/nif.xml).

## Фактичний стан репозиторію

Початкова база — commit `950f62e`. На початку вже були незакомічені результати 2.3: `README.md`, `WeaponModelTests.cs`, нові `WeaponProjectTests.ModelPackingValidation.cs` та звіт 2.3. Їх збережено. Резервна копія відстежуваних src/tests/docs/README та нових файлів у цих каталогах: `artifacts/backups/stage-2-4-20261023-210734`.

Перевірено `.csproj`, фактичний `obj/project.assets.json`, XML-документацію встановлених пакетів і публічні типи трьох Mutagen DLL. Версія пакетів — **0.53.1**, версія збірок — **0.53.1.0**. Reflection прочитав Core — 809, Kernel — 10, Skyrim — 8320 експортованих типів; типів із маркерами NIF/DDS у назвах не виявлено. Сам пошук назв не є доказом відсутності будь-якого прихованого API; висновок ґрунтується також на призначенні бібліотеки, локальній документації та коді використання.

| Наявний компонент | Що реально робить | Межа |
|---|---|---|
| `PluginBuilderService.Models.cs`, WEAP/STAT у Mutagen | Читає/змінює MODL, WNAM і записи STAT | Шлях у плагіні не є розібраним NIF |
| `WeaponProjectService.Resources.cs` | Перевіряє шлях, розширення, наявність і непорожність; копіює ресурси | Не перевіряє сигнатуру чи внутрішню структуру NIF/DDS |
| `WeaponProjectService.Models.cs` | Читає loose-файл / потік BSA для доступності | Успішне відкриття потоку не доводить коректність ресурсу |
| `WeaponProjectService.ResourceCatalog.cs` | Походження, реєстрація, використання WEAP/STAT, ZIP | Використання DDS усередині NIF поки невідоме |
| Mutagen `Model`, `AlternateTexture`, `TextureSet` | Має дані plugin overrides текстур | Не розбирає геометрію чи shader-блоки NIF |
| .NET `FileStream`, `BinaryReader`, `BinaryPrimitives` | Дозволяє власне обмежене читання заголовків | Потрібні власні правила валідації; готового NIF-парсера немає |

Локальна reflection-перевірка підтвердила `Model.AlternateTextures`, `AlternateTexture.Name/Index/NewTexture` і поля TXST (`Diffuse`, `NormalOrGloss`, `Environment`, `Height` та інші). Отже, майбутній звіт про фактичні текстури має враховувати і NIF, і TXST/alternate textures відповідного предмета. Це висновок для майбутньої реалізації; поточний каталог не розширювався.

Офіційна документація Mutagen описує asset links як типізовані шляхи у записах і окремо — роботу з архівами. Поточні онлайн-приклади можуть відрізнятися від API 0.53.1; при реалізації орієнтиром залишаються локальні збірки. [Asset Links](https://mutagen-modding.github.io/Mutagen/plugins/AssetLink/), [Archives](https://mutagen-modding.github.io/Mutagen/Archives/).

## Можливості за напрямами

### Текстурні посилання, геометрія та шейдери NIF

Типова структурна ціль: shape → shader property → texture set. `BSLightingShaderProperty` посилається на `BSShaderTextureSet`; `BSEffectShaderProperty` також має власні текстурні рядки. Значення слота залежить від шейдера й прапорців, тому `_n`, `_e` чи ім'я файла — лише підказка. `BSTriShape` містить опис вершин, кількості, індекси трикутників і посилання на shader/skin; схема змінюється з версією. [nifxml](https://raw.githubusercontent.com/niftools/nifxml/develop/nif.xml).

Пропонований результат майбутнього аналізатора:

- Для кожного посилання — NIF, номер блока/shape, shader, слот, початковий рядок, нормалізований відносний шлях і джерело ефективного призначення (NIF або override плагіна).
- Для геометрії — тип shape, кількості вершин/трикутників, bounds, наявність UV/normal/tangent/color/skin і помилки індексів чи нечислових координат. Це структурні діагностики, не оцінка зовнішнього вигляду.
- Для шейдерів — тип і сирі flags, alpha, UV-параметри, зв'язок із текстурами; для невідомих блоків — явне неповне покриття.

Пошук ASCII-рядків `.dds` у байтах NIF не підходить для достовірного списку залежностей: він не встановлює зв'язок із shape, активність слота, overrides і повноту аналізу. Відсутність знайденого рядка не означає відсутність текстури. Також не слід трактувати збереження невідомих блоків бібліотекою як успішну перевірку цих блоків.

**Оцінка:** тільки наявними прикладними бібліотеками повний розбір не забезпечений. Власний парсер графа — значний окремий проєкт із високою вартістю підтримки. Проста зміна числа версії NIF не є конвертацією геометрії.

### DDS-заголовки

Без декодування зображення можна визначити: сигнатуру, розміри, mip levels, тип поверхні, legacy FourCC або DXGI format, cubemap/volume/array, бітові маски й частину alpha-метаданих.

Мінімальні структурні перевірки: 128 байтів, magic `DDS `, розмір `DDS_HEADER` 124, розмір `DDS_PIXELFORMAT` 32; за прапорця FOURCC і коду `DX10` потрібні 148 байтів. Це перевірка контейнера, не пікселів. [Microsoft DDS Programming Guide](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dx-graphics-dds-pguide).

Далі варто перевіряти ненульові ширину/висоту, осмислену кількість mip levels, узгодженість dimension/depth/array та overflow під час обчислень. Невиставлені деякими writers прапорці CAPS/PIXELFORMAT/MIPMAPCOUNT не варто автоматично прирівнювати до зіпсованого файла. [DDS_HEADER](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-header).

Legacy формат визначається за flags/FourCC або RGB bit count і масками. Невідомий чи неоднозначний код слід показувати як непідтримуваний із сирим значенням, не вгадувати формат. [DDS_PIXELFORMAT](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-pixelformat).

DX10 extension додає `dxgiFormat`, resource dimension, cube flag, array size та alpha metadata. Cubemap array size означає число кубів, кожен із шістьма гранями; для volume діють інші правила. [DDS_HEADER_DXT10](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-header-dxt10).

Для відомих BC-форматів перевірка достатньої довжини payload можлива без декомпресії: на кожному mip рівні врахувати округлену вгору кількість блоків 4×4, розмір блока, грані/шари/глибину та виконати checked-арифметику. Це запропонований крок, а не реалізована перевірка поточної програми. Для невідомого формату очікуваний розмір не обчислювати навмання. [BC7](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/bc7-format), [DDS cubemaps](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-file-layout-for-cubic-environment-maps).

### DDS-формати та сумісність матеріалів SE/AE

| Група | Що можна встановити | Чого цього недостатньо довести |
|---|---|---|
| DXT1 / DXT3 / DXT5 | Legacy BC1 / BC2 / BC3 | Правильність alpha, normal/gloss та конкретного shader-слота |
| ATI1/BC4, ATI2/BC5 | Канальність і signed/unsigned варіант за відомим кодом | Що шейдер очікує саме таке пакування каналів |
| DX10 BC7 | UNORM / sRGB / typeless за DXGI | Що матеріал читає ресурс у потрібному колірному просторі |
| RGB(A), BGRA, luminance | Розрядність і канали за масками | Сумісність усіх legacy variants із рушієм |
| Cubemap / array / volume | Тип ресурсу із заголовка | Правильне використання в конкретному матеріалі зброї |

Зіставлення legacy кодів підтверджує Microsoft DDS loader, а значення DXGI описує окремий enum. Це специфікація графічного формату, не таблиця гарантованої підтримки Skyrim. [DDSTextureLoader](https://github.com/microsoft/DirectXTK/wiki/DDSTextureLoader), [DXGI_FORMAT](https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format).

Моя пропозиція для SE/AE: розділити **валідність файла**, **підтримку його аналізатором**, **відповідність shader-слоту** та **перевірку в рушії**. BC5 не слід автоматично схвалювати для кожної normal map: потрібне очікуване пакування каналів і alpha/gloss. BC7-формат не визначає призначення текстури. Порожній необов'язковий слот може бути коректним; відсутній обов'язковий ресурс визначається тільки в контексті підтримуваного шейдера. Для модифікованих shader stacks потрібні окремі профілі; універсального підтвердження для ENB/Community Shaders або всіх версій AE це дослідження не дає.

## Порівняння способів реалізації

| Варіант | Можливості | Залежності / інтеграція | Рекомендація |
|---|---|---|---|
| Власний обмежений DDS reader на .NET 8 | Заголовки, відомі формати, довжина payload | Нових пакетів не потрібно | Найменший окремий крок після дозволу на реалізацію |
| `ousnius/NiflySharp`, пакет **Nifly 1.1.0** | Managed NIF reader, граф блоків, дані shapes/shaders | Пакет має net8.0; залежність `miniball-devel0 >= 1.0.4`; у репозиторії GPL-3.0 | Основний кандидат для окремого read-only proof of concept після погодження залежностей |
| `ousnius/nifly` C++ | NIF load, shapes, shader/texture helpers | Native build та C# wrapper/interop, x64-доставка, GPL-3.0 | Альтернатива, якщо managed-кандидат не покриє потрібні файли |
| Microsoft DirectXTex | DDS metadata, decoding та processing | C++ library/interop або окремий інструмент; MIT | Для майбутнього preview/декодування; зайве для простого заголовка |
| Власний NIF reader за схемою | Контроль над вузькою підтримкою | Велика власна реалізація, версійність і тестовий корпус; аудит ліцензії при використанні чужої схеми/коду | Не рекомендується як мінімальна зміна |

Джерела кандидатів: [NiflySharp README](https://github.com/ousnius/NiflySharp), [Nifly 1.1.0 і залежності](https://www.nuget.org/packages/Nifly/1.1.0), [проєкт і PackageId](https://raw.githubusercontent.com/ousnius/NiflySharp/main/NiflySharp/NiflySharp.csproj), [LICENSE](https://raw.githubusercontent.com/ousnius/NiflySharp/main/LICENSE), [nifly](https://github.com/ousnius/nifly), [DirectXTex](https://github.com/microsoft/DirectXTex).

**Назви пакетів неоднозначні:** NuGet `niflysharp` — інший SWIG/native wrapper; він не є перевіреним тут managed-пакетом `Nifly` від `ousnius/NiflySharp`. Залежність не слід вибирати лише за схожою назвою. [Опис іншого пакета](https://www.nuget.org/packages/niflysharp/).

Це порівняння опублікованих можливостей і вихідного коду, а не проведений benchmark чи інтеграційний тест кандидатів. Перед підключенням потрібно зафіксувати версію/commit, перевірити транзитивні залежності та умови поширення. GPL/MIT наведені як позначення джерел; правову сумісність із моделлю поширення цього застосунку не встановлено.

У прочитаному `NiflySharp.NifFile` наявні `Load(Stream)` і граф `Blocks`; успіх завантаження не тотожний строгій валідації: код може прийняти відсутній footer, а підготовка даних перебудовує частину представлення в пам'яті. При read-only інтеграції потрібні власні діагностики повноти й явна заборона виклику `Save`/оптимізації. [NifFile.cs](https://raw.githubusercontent.com/ousnius/NiflySharp/main/NiflySharp/NifFile.cs).

## Перевірка на локальних приймальних ресурсах

Окремий тимчасовий console probe у `artifacts/stage-2-4-probe` використовує тільки .NET і вже наявний Infrastructure. Restore виконано виключно з локального NuGet cache, без нових пакетів. Прочитано тільки фіксовані початкові поля; граф блоків NIF, texture strings, геометрію, shaders та pixel payload не розбирали.

| Файл у `artifacts/acceptance/stage-2-1-final/Stage21` | Розмір, байтів | Спостереження |
|---|---:|---|
| `meshes/Stage21/first.nif` | 996709 | 20.2.0.7, little endian, user 12, stream 83, header block count 26 |
| `meshes/Stage21/third.nif` | 996709 | Той самий SHA-256 і заголовок, що й first.nif |
| `textures/weapons/Fang of Namira_Dark Daedric Dagger/BaseColor.dds` | 5592580 | 2048×2048, 12 mip levels, DX10, DXGI 98 = BC7_UNORM, 2D, array size 1 |
| `textures/weapons/Fang of Namira_Dark Daedric Dagger/Emissive.dds` | 5592580 | 2048×2048, 12 mip levels, DX10, DXGI 98 = BC7_UNORM, 2D, array size 1 |
| `textures/weapons/Fang of Namira_Dark Daedric Dagger/Normal.dds` | 5592580 | 2048×2048, 12 mip levels, DX10, DXGI 98 = BC7_UNORM, 2D, array size 1 |
| `textures/weapons/Fang of Namira_Dark Daedric Dagger/bronze_e.dds` | 2097440 | 512×512, 10 mip levels, DXT5, caps2 `0xFE00` — cubemap і всі шість face flags |

Інтерпретація DXGI 98 та cubemap flags базується на [Microsoft DXGI](https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format) і [DDS_HEADER](https://learn.microsoft.com/en-us/windows/win32/direct3ddds/dds-header). Назви BaseColor/Normal/Emissive не вважаються доказом фактичного призначення в NIF. Правильність mip payload та pixel data цим probe не доведена.

У всіх шести файлів SHA-256 до й після читання збігся. Результати reflection, сирі значення заголовків і повні хеші: [report.json](../artifacts/research/stage-2-4/report.json). Ресурси з папки Skyrim під час probe не відкривалися; використано тільки наявні копії у workspace.

Відтворення локального досліду:

~~~powershell
dotnet restore artifacts/stage-2-4-probe/probe.csproj --source C:\Users\volod\.nuget\packages --ignore-failed-sources
dotnet run --project artifacts/stage-2-4-probe/probe.csproj -c Release --no-restore -- 'artifacts/acceptance/stage-2-1-final/Stage21' 'artifacts/research/stage-2-4'
~~~

Probe і JSON — ігноровані локальні артефакти, не компонент продукту. Probe спеціально обмежено відомими fixtures; його не слід використовувати як універсальний валідатор.

## Запропоновані межі майбутньої реалізації

1. Окремо погодити обмежену read-only перевірку DDS. Не змінювати імпорт/пакування до визначення політики: пошкоджений контейнер, непідтримуваний формат і матеріал із непідтвердженою сумісністю — різні стани.
2. За окремим погодженням залежностей провести Nifly proof of concept на LE/SE і custom корпусі. Доступ до джерел тільки на читання; результат — DTO без API запису. Повне читання із BSA потребує обмежень розпакованого розміру та придатного seekable потоку/тимчасової копії поза грою.
3. Додати явні стани Parsed / UnsupportedVersion / UnsupportedBlock / Invalid / ReadFailed / Cancelled та ознаку повноти. Не змішувати `Found/Missing/CheckFailed` ресурсу з результатом аналізу його вмісту.
4. Для шляхів текстур застосувати чинні захисні правила: жодних абсолютних, UNC, traversal, ADS чи посилань за межі дозволеного кореня. Нормалізація не повинна приховувати небезпечний початковий шлях. Ванільні залежності перевіряти через resolver, не копіювати автоматично.
5. Обмежити розміри, кількості, довжини рядків і час; перевіряти block offsets та overflow. Для стороннього parser без переривання розглянути worker process: сам CancellationToken не зупинить довгий синхронний виклик.
6. Поєднати NIF-посилання з TXST overrides; показувати всіх користувачів shared DDS. Невизначені залежності не дозволяють автоматично визнати зареєстрований ресурс зайвим або виключити його із ZIP.

Можливі майбутні точки розширення: read-only контракт у Core, окремий сервіс Infrastructure, проєкція діагностик у наявному каталозі UI. Це план, не створені класи чи затверджений масштабний рефакторинг.

## Необхідні тести після окремого погодження реалізації

- DDS: короткі/truncated headers, неправильні magic/size, DX10 без extension, DXT1/3/5, BC4/5, BC7 UNORM/sRGB, маски RGB, unknown FourCC/DXGI, нульові/великі dimensions, overflow, mip truncation, cube faces, arrays і volume, нестандартні flags.
- NIF: LE83/SE100/невідомі версії, обрізані block tables, завеликі counts/strings, невідомі блоки, неправильні links, декілька shapes і спільний texture set, effect shader, порожні optional slots, TXST overrides, UV/tangent/skin inconsistencies, triangle indices поза діапазоном.
- Інтеграція: ті самі ресурси loose/BSA, помилка читання окремо від Missing, unsafe texture paths, скасування, кеш після зовнішньої зміни, незмінність SHA джерел, відсутність викликів Save, shared NIF/DDS, усі режими ESL/staging та старі проєкти.
- Відповідність матеріалів: контрольні приклади із відомим shader/slot/channel layout і ручний огляд у дозволених інструментах; фінальна ігрова перевірка залишається окремою.

Нових production-функцій у 2.4 немає, тому нові автоматичні unit-тести аналізатора не створювалися. Дослідницький probe не зараховується як розширення набору xUnit.

## Перевірки й змінені файли

Повторна повна Release-регресія: **667 успішних, 0 невдалих, 0 пропущених**; [TRX 2.4](../artifacts/audit/stage-2-4-regression.trx). Release-збірка: **0 помилок, 0 попереджень**. Використано чинний output `artifacts/stage-2-3-build`; код і тести 2.3 при цьому не редагувалися.

Зміни саме 2.4:

- `docs/stage-2-4-nif-dds-research.md` — цей звіт.
- `README.md` — доповнено розділом 2.4; попередній текст 2.3 збережено.

Локальні невідстежувані Git артефакти: `artifacts/stage-2-4-probe/probe.csproj`, `Program.cs`, `artifacts/research/stage-2-4/report.json`, регресійний TRX та резервна копія. `.csproj` продукту і залежності не змінювалися.

Новий плагін у 2.4 не створювався; SSEEdit повторно не запускався, оскільки досліджувані заголовки NIF/DDS не перевіряються перевіркою записів ESP. Попередні 4 записи / 0 помилок SSEEdit для 2.2 не є підтвердженням сумісності NIF83 або матеріалів. Skyrim не запускався відповідно до домовленості.

Кандидатів не встановлено й не виконано на ресурсах: їхню практичну повноту ще треба перевірити після дозволу. Підключення нових бібліотек або реалізація аналізатора потребує окремого погодження. Наступне завдання основного плану — **3.1, незбережені зміни**; його не розпочато. Після цього звіту — очікування команди «далі».
