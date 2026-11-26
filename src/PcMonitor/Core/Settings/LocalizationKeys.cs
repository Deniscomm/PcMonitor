namespace PcMonitor.Core;

/// <summary>
/// Stable localization keys used by application code.
/// Values belong in Localization/*.json; do not use translated sentences as code keys.
/// </summary>
public static class LocalizationKeys
{
    public const string AppTitle = "ui.app.title";
    public const string ModeEasy = "ui.mode.easy";
    public const string ModePro = "ui.mode.pro";
    public const string NavOverview = "ui.nav.overview";
    public const string NavSensors = "ui.nav.sensors";
    public const string NavSystem = "ui.nav.system";
    public const string NavAutoTest = "ui.nav.auto_test";
    public const string NavStress = "ui.nav.stress";
    public const string NavRenders = "ui.nav.renders";
    public const string NavSettings = "ui.nav.settings";
    public const string NavAbout = "ui.nav.about";
    public const string Disclaimer = "ui.disclaimer";
    public const string MonitoringOnly = "status.monitoring_only";
    public const string TestingEnabled = "status.testing_enabled";
    public const string FullscreenOff = "status.fullscreen.off";
    public const string FullscreenOn = "status.fullscreen.on";
    public const string OsdEnabled = "status.osd.enabled";
    public const string OsdHidden = "status.osd.hidden";
    public const string TestsStopped = "status.tests.stopped";
    public const string Paused = "status.monitoring.paused";
    public const string MonitoringSummary = "status.monitoring.summary";
    public const string TemperaturesNormal = "status.temperature.normal";
    public const string TemperatureExceeded = "status.temperature.exceeded";
    public const string SensorTileAdded = "status.tile.sensor_added";
    public const string ActionResume = "ui.action.resume";
    public const string ActionPause = "ui.action.pause";
    public const string PollingPaused = "status.polling.paused";
    public const string PollingResumed = "status.polling.resumed";
    public const string MinMaxReset = "status.minmax.reset";
    public const string SensorExportTitle = "dialog.export.title";
    public const string SensorExported = "dialog.export.completed";
    public const string SaveFileFailed = "error.file.save";
    public const string StartCsvFailed = "error.csv.start";
    public const string OpenFolderFailed = "error.folder.open";
    public const string RestartAdminFailed = "error.restart.admin";
    public const string TrayOpen = "tray.open";
    public const string TrayExit = "tray.exit";
    public const string TrayOsd = "tray.osd";
    public const string TrayTitle = "tray.title";
    public const string TrayUnavailable = "tray.unavailable";
    public const string TrayHint = "tray.hint";
    public const string CsvStopped = "error.csv.stopped";
    public const string SensorError = "error.sensor";
    public const string ChangeLanguageFailed = "error.language.change";
    public const string ApplySettingsFailed = "error.settings.apply";
    public const string Safety = "ui.safety";

    /// <summary>Заголовок окна предупреждения о нагрузочных тестах.</summary>
    public const string ConsentTitle = "consent.title";

    /// <summary>Заголовок блока согласия.</summary>
    public const string ConsentHeader = "consent.header";

    /// <summary>Заголовок блока справки о безопасности.</summary>
    public const string ConsentHeaderInfo = "consent.headerInfo";

    /// <summary>Текст предупреждения о рисках.</summary>
    public const string ConsentMessage = "consent.message";

    /// <summary>Отказ от ответственности.</summary>
    public const string ConsentDisclaimer = "consent.disclaimer";

    /// <summary>Пояснение о последствиях принятия и отказа.</summary>
    public const string ConsentRestriction = "consent.restriction";

    /// <summary>Подтверждение принятой ответственности.</summary>
    public const string ConsentAccepted = "consent.accepted";

    /// <summary>Кнопка принятия условий.</summary>
    public const string ConsentAccept = "consent.accept";

    /// <summary>Кнопка отказа от условий.</summary>
    public const string ConsentDecline = "consent.decline";

    /// <summary>Подпись кнопки переключения языка в окне согласия.</summary>
    public const string ConsentLanguage = "consent.language";
}
