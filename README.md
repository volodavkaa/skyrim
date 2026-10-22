# Skyrim Mod Packer

[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![WPF-UI Fluent](https://img.shields.io/badge/UI-WPF--UI%20Fluent-blue.svg)](https://wpfui.lepo.co/)
[![Tests](https://img.shields.io/badge/Tests-62%20Passed-brightgreen.svg)]()
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()

> **Skyrim Mod Packer** — професійна десктопна утиліта на C# 12 / .NET 8 / WPF для авторів модів The Elder Scrolls V: Skyrim (Special Edition, Anniversary Edition, VR). Автоматизує пошук, фільтрацію та пакування асетів зброї/броні/предметів безпосередньо з робочої теки `Data\` у готові до завантаження на Nexus Mods архіви.

---

## 🇬🇧 English Quick Summary

**Skyrim Mod Packer** automates creating Nexus-ready `.zip` mod archives for Skyrim:
- **Zero-touch scanning**: Scans `Data/meshes` and `Data/textures` matching keywords and discovers `.esp`/`.esm`/`.esl` plugins.
- **Nexus-Compliant Archive Structure**: The archive root directly contains `meshes/`, `textures/`, and the plugin file (ready for Mod Organizer 2, Vortex, and manual install).
- **Interactive File Tree Preview**: 3-state checkboxes, instant file size calculation, individual exclusion of unwanted meshes or textures.
- **Pipeline Architecture**: Extensible pipeline (`IPackStep`): Scan ➔ Filter ➔ Validate ➔ Copy ➔ Archive.
- **Safe & Non-Destructive**: Read-only operations on Skyrim `Data/`; game assets are never modified or removed.
- **Modern Fluent UI**: Dark/Light theme, English & Ukrainian localization, Drag-and-Drop support, live Serilog event console.

---

## 🇺🇦 Основні можливості

| Можливість | Опис |
|---|---|
| **Автодетекція Skyrim** | Автоматичний пошук шляху до гри через Windows Registry, `libraryfolders.vdf` Steam та стандартні шляхи встановлення. |
| **Профілі модів** | Збереження налаштувань для кожного моду (назва, версія, автор, ключові слова, плагін, цільова папка). |
| **Автоінкремент версій** | Підтримка семантичного версіонування (SemVer) з автоматичним підвищенням (Patch / Minor / Major) при кожній збірці. |
| **Інтерактивне дерево файлів** | Попередній перегляд знайдених файлів у вигляді ієрархічного дерева з 3-позиційними чекбоксами (вибрано, частково, знято) та підрахунком розміру. |
| **Nexus Mods сумісність** | Архіви формуються з кореневими теками `meshes/`, `textures/` та плагіном у корені, нормалізованими прямими слешами (`/`) для сумісності з MO2 та Vortex. |
| **Контрольні суми (SHA-256)** | Автоматичний розрахунок хешу кожного сформованого архіву та збереження в історії збірок. |
| **Drag-and-Drop** | Перетягування у вікно програми теки `Data\`, теки моду (додає ключове слово) або файлу плагіна. |
| **Локалізація UA / EN** | Миттєве динамічне перемикання між українською та англійською мовами без перезапуску. |
| **Теми оформлення** | Fluent Design із підтримкою темної, світлої та системної тем (WPF-UI v3). |
| **Безпека даних** | Суворо недеструктивний режим: оригінальні файли гри лише читаються та ні за яких умов не видаляються. |

---

## 🏛 Архітектура рішення

Проєкт побудований за принципами **Clean Architecture** та **SOLID**:

```
SkyrimModPacker/
├── src/
│   ├── SkyrimModPacker.Core/             # Чисті доменні моделі, інтерфейси та валідатори
│   │   ├── Models/                      # ModProfile, ScannedFile, AppSettings, BuildRecord
│   │   ├── Interfaces/                  # IPackStep, IArchiver, IModScanner, ISkyrimLocator...
│   │   ├── Common/                      # Result<T>, FileTreeNode, FileTreeBuilder
│   │   └── Validation/                  # ModProfileValidator
│   │
│   ├── SkyrimModPacker.Application/      # Конвеєр та кроки обробки (Use Cases)
│   │   ├── Pipeline/                    # PackingPipeline (оркестрація кроків за Order)
│   │   ├── Steps/                       # ScanStep(100), FilterStep(200), ValidateStep(300),
│   │   │                                # CopyStep(400), ArchiveStep(500)
│   │   └── Services/                    # VersionIncrementService
│   │
│   ├── SkyrimModPacker.Infrastructure/   # Реалізація доступу до даних, ОС та утиліт
│   │   ├── Scanning/                    # ModScanner (паралельний пошук файлів з точними масками)
│   │   ├── Detection/                   # SkyrimLocator (Registry + Steam VDF + Standard paths)
│   │   ├── Archiving/                   # ZipArchiver (System.IO.Compression, normalized entry paths)
│   │   ├── Persistence/                 # JsonProfileRepository, JsonSettingsRepository, JsonBuildHistoryRepository
│   │   └── Logging/                     # InMemoryLogSink (Serilog sink + UI streaming service)
│   │
│   └── SkyrimModPacker.UI/              # Презентаційний шар (WPF + MVVM + WPF-UI v3)
│       ├── ViewModels/                  # BuildViewModel, ProfilesViewModel, SettingsViewModel, LogViewModel
│       ├── Views/Pages/                 # BuildPage, ProfilesPage, SettingsPage, LogPage
│       ├── Services/                    # LocalizationService, NavigationService
│       ├── Resources/                   # Strings.uk.xaml, Strings.en.xaml
│       └── App.xaml / MainWindow.xaml    # DI Container (Host), Global Exception Handlers
│
└── tests/
    └── SkyrimModPacker.Tests/           # 62 модульних та інтеграційних тести (xUnit, NSubstitute)
```

---

## 🚀 Конвеєр збірки (Packing Pipeline)

Процес формування моду розбитий на ізольовані кроки (`IPackStep`), зареєстровані в DI-контейнері:

```mermaid
graph LR
    A[100: ScanStep] --> B[200: FilterStep]
    B --> C[300: ValidateStep]
    C --> D[400: CopyStep]
    D --> E[500: ArchiveStep]
```

1. **ScanStep (Order 100)**: Виконує сканування теки `Data\meshes` та `Data\textures` за ключовими словами, шукає файл плагіна.
2. **FilterStep (Order 200)**: Застосовує вибір користувача з дерева файлів (`FileTreeNode`), виключаючи незняті прапорці.
3. **ValidateStep (Order 300)**: Перевіряє наявність обов'язкових файлів, чинні шляхи та валідність налаштувань архіву.
4. **CopyStep (Order 400)**: Копіює відібрані файли у тимчасову ізольовану директорію зі збереженням структури.
5. **ArchiveStep (Order 500)**: Стискає файли в `.zip`, нормалізує імена записів до стандарту Unix/Zip (`/`), рахує хеш SHA-256 та записує подію в історію.

---

## 💻 Інструкція користувача

### 1. Первинне налаштування
1. Запустіть програму. Перейдіть у розділ **Налаштування** (або натисніть `Ctrl+,`).
2. Якщо Skyrim встановлено через Steam, шлях до `Data\` буде знайдено автоматично. Якщо ні — скористайтеся кнопкою «Огляд» або перетягніть папку `Data` прямо у вікно.
3. Оберіть бажану мову (Українська / English) та тему оформлення.

### 2. Створення профілю моду
1. Відкрийте розділ **Профілі** (`Ctrl+2`) та натисніть «Створити профіль».
2. Заповніть:
   - **Назва моду**: наприклад, `NordicSilverSword`.
   - **Ключові слова**: папки або частини імен файлів у meshes/textures (наприклад, `nordic_sword, silversword`).
   - **Файл плагіна**: ім'я файлу (наприклад, `NordicSilverSword.esp`).
   - **Версія**: стартова версія (наприклад, `1.0.0`) та перемикач «Автоінкремент».
3. Натисніть «Зберегти профіль».

### 3. Попередній перегляд та збірка
1. У розділі **Збірка** (`Ctrl+1`) оберіть ваш профіль.
2. Натисніть **«Сканувати»** (або `F5`). Програма побудує дерево всіх знайдених асетів.
3. За потреби зніміть прапорці з непотрібних файлів (наприклад, тестових текстур чи робочих бекапів).
4. Натисніть **«Зібрати мод»** (`Ctrl+B`).
5. Після завершення готовий архів з'явиться у вихідній папці (за замовчуванням — на Робочому столі), а в інтерфейсі з'явиться кнопка швидкого відкриття папки з архівом.

---

## 🛠 Керівництво для розробників

### Вимоги
- Windows 10/11 x64
- .NET 8.0 SDK або новіший
- Visual Studio 2022 (v17.8+) або Visual Studio Code / Rider

### Збірка та запуск тестів

```bash
# Клонування або перехід у папку проєкту
cd c:\Users\volod\OneDrive\Desktop\Projects\skyrim

# Відновлення пакетів та компіляція
dotnet build SkyrimModPacker.sln

# Запуск модульних та інтеграційних тестів
dotnet test SkyrimModPacker.sln
```

### Публікація автономного виконуваного файлу (Self-Contained Single-File)

Для створення єдиного автономного `.exe` файлу, який не вимагає встановленого .NET Runtime у користувача:

```bash
dotnet publish src/SkyrimModPacker.UI/SkyrimModPacker.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

Готовий файл `SkyrimModPacker.UI.exe` буде знаходитись у теці `publish/win-x64/`.

---

## 🧩 Гайд з розширення (Extension Guide)

### Як додати новий крок у конвеєр пакування
Кожен крок реалізує інтерфейс `IPackStep` з `SkyrimModPacker.Core.Interfaces`:

```csharp
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;

public class MyCustomStep : IPackStep
{
    // Порядок виконання у ланцюжку (наприклад, 450 — після копіювання, але до архівації)
    public int Order => 450;
    public string Name => "MyCustomStep";

    public async Task<Result> ExecuteAsync(PackingContext context, CancellationToken ct = default)
    {
        // Доступ до файлів, профілю та робочих директорій через context
        // Наприклад, перевірка .nif заголовків або додавання readme.txt
        return Result.Ok();
    }
}
```

Зареєструйте новий крок у `SkyrimModPacker.Application/DependencyInjection.cs`:
```csharp
services.AddTransient<IPackStep, MyCustomStep>();
```
Він автоматично підхопиться `PackingPipeline` у відповідній послідовності.

### Як додати інший архіватор (наприклад, 7-Zip / SharpCompress)
Реалізуйте інтерфейс `IArchiver`:

```csharp
public class SevenZipArchiver : IArchiver
{
    public string Extension => ".7z";

    public async Task<Result<ArchiveResult>> ArchiveAsync(
        string sourceDirectory,
        string destinationArchiveFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Ваша логіка стиснення у формат .7z
    }
}
```
Та зареєструйте реалізацію в `SkyrimModPacker.Infrastructure/DependencyInjection.cs`.

---

## 📄 Ліцензія

Проєкт поширюється за ліцензією MIT. Модифікуйте та використовуйте вільно для розробки та публікації власних модів!
