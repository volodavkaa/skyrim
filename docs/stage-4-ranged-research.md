# Дослідження перед додаванням луків та арбалетів

Джерела: встановлений Mutagen.Bethesda.Skyrim **0.53.1** (публічні інтерфейси через reflection) і читання встановлених `Skyrim.esm` / `Dawnguard.esm` без запису. Вивід: `artifacts/audit/stage-4-research.txt`; код проби: `artifacts/stage-4-probe/Program.cs`. Дослідження виконано після melee-типів і перед увімкненням Bow/Crossbow у редакторі.

WEAP DNAM містить окремі AnimationType Bow=7 та Crossbow=9. HuntingBow: `013985:Skyrim.esm`; DLC1CrossBow: `000801:Dawnguard.esm`, DLC1DwarvenCrossBow: `00B647:Dawnguard.esm`. Enhanced-варіанти мають додаткові зачарування й посилання. Назва, що містить Crossbow, не визначає тип: у Dawnguard є пастки з AnimationType Bow.

Окрім спільних Damage/Weight/Value/Speed/Reach, треба зберігати RangeMin/RangeMax, SightFOV, AttackAnimation, NumProjectiles, AnimationAttackMult, прапорці, Skill/Resist, Stagger, Critical, звуки атаки/циклу/екіпірування, EquipmentType, ScopeModel, ObjectEffect, Keywords, WNAM і VMAD. У 0.53.1 WEAP не має окремого поля Projectile: не слід вигадувати його чи автоматично створювати AMMO/PROJ. Боєприпаси, поведінка анімацій і сумісність NIF не належать до поточного редагованого набору.

Реалізація має клонувати весь запис правильного ванільного типу і змінювати лише заявлені поля. Конвертація melee↔ranged зміною одного enum заборонена. Для арбалета треба оголошувати Dawnguard і його батьківські master-залежності, зберігати джерело шаблону як PluginName + локальний FormID, коректно розв'язувати WNAM і не включати ванільні DLC-моделі до ZIP. Відсутній DLC означає відсутні шаблони, а не підстановку меча.

Допустимість зачарування перевіряється за типом ENCH та його посиланнями; список відомих типових несумісностей не замінює перевірку ефектів у грі. Геометрія, стрільба, перезарядка, натяг, прицілювання й анімації потребують окремого тесту Skyrim; він наразі пропущений за рішенням користувача.

Додатково прочитано ванільний TemperWeaponIronSword: верстат `CraftingSmithingSharpeningWheel` (`088108:Skyrim.esm`), вихід 1, умови `(EPTemperingItemIsEnchanted != 1) OR HasPerk(ArcaneBlacksmith)`, perk `05218E:Skyrim.esm`. Самого довільного HasPerk недостатньо для повного сценарію покращення зачарованих предметів.
