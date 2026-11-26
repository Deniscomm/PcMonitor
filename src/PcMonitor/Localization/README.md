# Localization workflow

All user-facing strings belong in the four JSON files in this directory:

- `ru.json` — Russian source language;
- `en-GB.json` — English (UK);
- `en-US.json` — English (US);
- `zh.json` — Simplified Chinese.

Use the Russian text as the stable source key. Add the same key to all four files before building. Keep placeholders unchanged, including `{0}`, `{1}`, `<folder>`, keyboard shortcuts, units, and markup.

`LocalizationManager` loads JSON at runtime and applies translations centrally to the WPF visual tree. It first uses an exact key, then known embedded phrases, then a conservative word fallback for missed compound labels. The fallback is only a safety net; new strings must still be added to all JSON files.

Validation before Release:

1. Every JSON file must be valid UTF-8 JSON.
2. All four files must contain the same key set.
3. Non-Russian files must contain no Cyrillic values.
4. Build with `scripts\publish-portable.bat` or `dotnet publish -c Release`.
5. Launch the fresh output and inspect every tab in all four languages.
