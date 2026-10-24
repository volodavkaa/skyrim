# CS0103 / InitializeComponent у редакторі

## Відтворення та виправлення

Звичайна Debug-збірка проходила, але MSBuild-запит режиму редактора (`Compile`, `DesignTimeBuild=true`, `BuildingProject=false`, без прапорця Visual Studio) не додавав згенеровані XAML partial-класи до компіляції. Було 72 входи, без `SmartModWindow.g.cs`. Через це аналізатор не міг бачити `InitializeComponent` та іменовані елементи вікна.

У `SkyrimModPacker.UI.csproj` додано `PrepareWpfEditorMarkup`: перед `CoreCompile` для design-time запитів виконується штатний `MarkupCompilePass1`. Після виправлення — 82 входи, з одним `SmartModWindow.g.cs`, його `InitializeComponent`, `WorkArea`, `PreviewHost` та іншими полями. Звичайну компіляцію цей target не змінює. Заглушки, ручного копіювання generated-коду чи приховування CS0103 немає.

Додатково:

- Прибрано випадково збережений у Git `SkyrimModPacker.UI_0u1wzx2k_wpftmp.csproj` — службовий результат попередньої WPF-компіляції, не вихідний проєкт.
- Такі тимчасові проєкти виключено з Git, огляду файлів та відстеження VS Code.
- Некоректний окремий рядок у `.editorconfig` перетворено на коментар.
- Попередні версії файлів збережено в `artifacts/backups/compiler-editor-20261024-233406`.

## Перевірка без залежності від кешу

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify-WpfEditor.ps1
```

Скрипт створює нові каталоги проміжних результатів і перевіряє обидва режими `BuildingInsideVisualStudio=false/true`. Для кожного з 9 XAML code-behind файлів має бути рівно один свіжий generated partial-клас із `InitializeComponent`. Перевіряються відсутність дублікатів входів та невикористання старих `.g.cs` з кешу. Звіти лишаються в `artifacts/audit/wpf-editor/`.

Звичайні перевірки всього рішення:

```powershell
dotnet build SkyrimModPacker.sln -c Debug --no-restore -warnaserror
dotnet build SkyrimModPacker.sln -c Release --no-restore -warnaserror
```

Обидві збірки виконані з 0 помилок і 0 попереджень. Повний тестовий протокол: `artifacts/audit/compiler-editor-regression.trx`; зведення перевірок: `artifacts/audit/compiler-editor-verification.json`.

Для вже відкритого редактора після зміни проєкту може знадобитися перезапуск C# language server, щоб прибрати діагностику зі старого знімка. Автоматичну перевірку відкритого вікна виконати не вдалося: служба керування комп’ютером двічі завершилася з `trusted Node process exited unexpectedly; kernel reset`. Вікно редактора й буфери користувача примусово не закривалися.
