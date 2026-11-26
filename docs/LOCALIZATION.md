# PC Monitor — localization workflow

## Русский

Новая локализация добавляется по стабильному семантическому ключу, а не по русской фразе.

1. Добавьте константу в `src/PcMonitor/Core/Settings/LocalizationKeys.cs`.
2. Добавьте один и тот же ключ во все четыре файла `src/PcMonitor/Localization/`:
   - `ru.json`;
   - `en-GB.json`;
   - `en-US.json`;
   - `zh.json`.
3. Используйте в C# `LocalizationManager.Get(LocalizationKeys.Key)` для текста и `LocalizationManager.Format(...)` для строк с параметрами.
4. Не добавляйте `if (language == ...)` и не используйте переводимую фразу как ключ.
5. Перед сборкой запускайте `tools/validate-localization.ps1`.

Правило: ключ должен описывать назначение текста (`status.temperature.exceeded`), а не его текущий перевод. Поэтому изменение формулировки выполняется только в JSON-каталогах.

## English (UK / US)

Add new translations by stable semantic key, never by using a Russian sentence as a code key.

1. Add a constant to `LocalizationKeys.cs`.
2. Add the same key to all four JSON catalogs.
3. Use `LocalizationManager.Get(...)` for labels and `LocalizationManager.Format(...)` for parameterized messages.
4. Do not add language-condition branches in UI code.
5. Run `tools/validate-localization.ps1` before building.

## 中文

新的本地化内容必须使用稳定的语义键，而不是使用俄语句子作为代码键。

1. 在 `LocalizationKeys.cs` 中添加常量。
2. 将相同的键添加到四个 JSON 目录。
3. 普通文本使用 `LocalizationManager.Get(...)`，带参数文本使用 `LocalizationManager.Format(...)`。
4. 不要在 UI 代码中添加按语言分支。
5. 构建前运行 `tools/validate-localization.ps1`。

## Current migration status

The main window navigation, tray, OSD, monitoring status, alarms, export, CSV, and error messages use stable keys. Legacy source-string lookup remains supported for existing views and backward compatibility while the remaining feature views are migrated incrementally.
