# PC Monitor вЂ” C# refactoring

## Р СѓСЃСЃРєРёР№

`MainWindow.xaml.cs` Р±РѕР»СЊС€Рµ РЅРµ СЏРІР»СЏРµС‚СЃСЏ РјРѕРЅРѕР»РёС‚РѕРј. Р’ Р±Р°Р·РѕРІРѕРј С„Р°Р№Р»Рµ РѕСЃС‚Р°РІР»РµРЅС‹ С‚РѕР»СЊРєРѕ СЃРѕСЃС‚РѕСЏРЅРёРµ РѕРєРЅР°, P/Invoke-РєРѕРЅС‚СЂР°РєС‚С‹ Рё РєРѕРЅСЃС‚СЂСѓРєС‚РѕСЂ. Р РµР°Р»СЊРЅС‹Рµ РѕР±СЂР°Р±РѕС‚С‡РёРєРё РїРµСЂРµРЅРµСЃРµРЅС‹ РІ partial-С„Р°Р№Р»С‹ С‚РѕРіРѕ Р¶Рµ РєР»Р°СЃСЃР°:

- `MainWindow.Lifecycle.cs` вЂ” Р·Р°РїСѓСЃРє, Р·Р°РєСЂС‹С‚РёРµ, СЃРѕРіР»Р°СЃРёРµ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ Рё Р»РµРЅРёРІРѕ Р·Р°РіСЂСѓР¶Р°РµРјС‹Рµ РІРєР»Р°РґРєРё;
- `MainWindow.Tray.cs` вЂ” СЃРёСЃС‚РµРјРЅС‹Р№ С‚СЂРµР№ Рё РЅР°РІРёРіР°С†РёСЏ РёР· С‚СЂРµСЏ;
- `MainWindow.HotKeys.cs` вЂ” РіРѕСЂСЏС‡РёРµ РєР»Р°РІРёС€Рё, РїРѕР»РЅРѕСЌРєСЂР°РЅРЅС‹Р№ СЂРµР¶РёРј Рё OSD;
- `MainWindow.Localization.cs` вЂ” СЏР·С‹Рє, СЂРµР¶РёРјС‹ РёРЅС‚РµСЂС„РµР№СЃР°, РґРѕСЃС‚СѓРї Рє С‚РµСЃС‚Р°Рј Рё РїРѕРґСЃРІРµС‚РєР° РЅР°РІРёРіР°С†РёРё;
- `MainWindow.Monitoring.cs` вЂ” РїРѕС‚РѕРє СЃРЅРёРјРєРѕРІ СЃРµРЅСЃРѕСЂРѕРІ, СЃС‚Р°С‚СѓСЃ, С‚СЂРµРІРѕРіРё Рё РїР»РёС‚РєРё;
- `MainWindow.Settings.cs` вЂ” РґРµР№СЃС‚РІРёСЏ РЅР°СЃС‚СЂРѕРµРє, CSV, СЌРєСЃРїРѕСЂС‚, СЃРѕРѕР±С‰РµРЅРёСЏ Рё РїРµСЂРµР·Р°РїСѓСЃРє СЃ РїСЂР°РІР°РјРё Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°.

РџРµСЂРµРЅРѕСЃ РЅРµ РјРµРЅСЏРµС‚ РїСѓР±Р»РёС‡РЅС‹Р№ XAML-РєРѕРЅС‚СЂР°РєС‚: РёРјРµРЅР° РѕР±СЂР°Р±РѕС‚С‡РёРєРѕРІ Рё `x:Name` СЃРѕС…СЂР°РЅРµРЅС‹. Partial-С„Р°Р№Р»С‹ РёСЃРїРѕР»СЊР·СѓСЋС‚ РѕРґРЅРѕ СЃРѕСЃС‚РѕСЏРЅРёРµ `MainWindow`, РїРѕСЌС‚РѕРјСѓ РїРµСЂРµС…РѕРґ Рє РѕС‚РґРµР»СЊРЅС‹Рј СЃРµСЂРІРёСЃР°Рј РјРѕР¶РЅРѕ РІС‹РїРѕР»РЅСЏС‚СЊ РїРѕ РѕРґРЅРѕРјСѓ С„СѓРЅРєС†РёРѕРЅР°Р»СЊРЅРѕРјСѓ Р±Р»РѕРєСѓ Р±РµР· РјР°СЃСЃРѕРІРѕРіРѕ РёР·РјРµРЅРµРЅРёСЏ XAML.

## English (UK / US)

`MainWindow.xaml.cs` is no longer a monolith. The base file contains only window state, P/Invoke contracts, and construction. The working handlers are split into focused partial files for lifecycle, tray, hotkeys/OSD, localisation and navigation, monitoring, and settings/actions.

The XAML contract is preserved: handler names and `x:Name` fields remain unchanged. This keeps the refactor behaviour-preserving while making each functional area independently editable and ready for later service extraction.

## дё­ж–‡

`MainWindow.xaml.cs` е·ІдёЌе†ЌжЇеЌ•дЅ“ж–‡д»¶гЂ‚еџєзЎЂж–‡д»¶еЏЄдїќз•™зЄ—еЏЈзЉ¶жЂЃгЂЃP/Invoke еҐ‘зє¦е’Њжћ„йЂ е‡Ѕж•°гЂ‚е®ћй™…е¤„зђ†йЂ»иѕ‘е·ІжЊ‰з”џе‘Ѕе‘ЁжњџгЂЃзі»з»џж‰з›гЂЃеї«жЌ·й”®/OSDгЂЃжњ¬ењ°еЊ–дёЋеЇји€ЄгЂЃз›‘жЋ§гЂЃи®ѕзЅ®дёЋж“ЌдЅњж‹†е€†е€°з‹¬з«‹зљ„ partial ж–‡д»¶дё­гЂ‚

XAML еҐ‘зє¦дїќжЊЃдёЌеЏпјљдє‹д»¶е¤„зђ†е™ЁеђЌз§°е’Њ `x:Name` е­—ж®µеќ‡е·Ідїќз•™гЂ‚иї™ж ·еЏЇд»ҐењЁдёЌе¤§и§„жЁЎдї®ж”№ XAML зљ„жѓ…е†µдё‹йЂђдёЄжЏђеЏ–жњЌеЉЎгЂ‚

## Validation

- Rust core: tests and release build pass.
- Rust GUI crate: `cargo check` passes.
- Localisation catalogs: `ru`, `en-GB`, `en-US`, and `zh` each contain 519 canonical keys with no missing or extra keys.
- C# partial files: braces balanced and UTF-8 checked.
- WPF build: not claimed as successful because the machine has the .NET 10 runtime but no installed .NET SDK (`dotnet --info` reports `No SDKs were found`).

