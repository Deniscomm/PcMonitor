using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace PcMonitor.Core;

public static class LocalizationManager
{
    private sealed class OriginalText
    {
        public OriginalText(string value) => Value = value;
        public string Value { get; set; }
        public string LastApplied { get; set; } = string.Empty;
        public bool HasApplied { get; set; }
    }

    private sealed class RefreshState
    {
        public RefreshState(DependencyObject root) => Root = new WeakReference<DependencyObject>(root);
        public WeakReference<DependencyObject> Root { get; }
        public DispatcherTimer? Timer { get; set; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, OriginalText> Originals = new();
    private static readonly ConditionalWeakTable<DependencyObject, RefreshState> RefreshTimers = new();
    private static string _language = "ru";
    private static readonly Dictionary<string, string> Ru = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        ["\u041c\u043e\u043d\u0438\u0442\u043e\u0440\u0438\u043d\u0433 \u041f\u041a"] = "PC Monitor",
        ["\u041b\u0451\u0433\u043a\u0438\u0439 \u0440\u0435\u0436\u0438\u043c"] = "Easy mode",
        ["\u041f\u0440\u043e\u0444\u0438"] = "Pro",
        ["\u041e\u0431\u0437\u043e\u0440"] = "Overview",
        ["\u0414\u0430\u0442\u0447\u0438\u043a\u0438"] = "Sensors",
        ["\u0421\u0438\u0441\u0442\u0435\u043c\u0430"] = "System",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u041f\u041a"] = "PC check",
        ["\u0410\u0432\u0442\u043e-\u0442\u0435\u0441\u0442"] = "Auto test",
        ["\u041d\u0430\u0433\u0440\u0443\u0437\u043a\u0430"] = "Stress",
        ["\u0420\u0435\u043d\u0434\u0435\u0440\u044b"] = "Renders",
        ["\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438"] = "Settings",
        ["\u0418\u043d\u0442\u0435\u0440\u0444\u0435\u0439\u0441"] = "Interface",
        ["\u042f\u0437\u044b\u043a"] = "Language",
        ["\u0422\u0435\u043c\u0430"] = "Theme",
        ["\u0422\u0451\u043c\u043d\u0430\u044f"] = "Dark",
        ["\u0421\u0435\u0440\u0430\u044f"] = "Gray",
        ["\u0421\u0432\u0435\u0442\u043b\u0430\u044f"] = "Light",
        ["\u0421\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c \u043d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438"] = "Save settings",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0434\u0430\u043d\u043d\u044b\u0445"] = "Open data folder",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0436\u0443\u0440\u043d\u0430\u043b\u044b"] = "Open logs",
        ["\u041e\u043f\u0440\u043e\u0441 \u0434\u0430\u0442\u0447\u0438\u043a\u043e\u0432"] = "Sensor polling",
        ["\u041e\u043a\u043d\u043e \u0438\u0441\u0442\u043e\u0440\u0438\u0438 \u0433\u0440\u0430\u0444\u0438\u043a\u0430, \u0442\u043e\u0447\u0435\u043a"] = "Chart history window, points",
        ["\u041c\u0430\u043a\u0441\u0438\u043c\u0430\u043b\u044c\u043d\u0430\u044f \u0434\u043e\u0441\u0442\u043e\u0432\u0435\u0440\u043d\u0430\u044f \u0442\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430, \u00b0C"] = "Maximum plausible temperature, \u00b0C",
        ["\u041f\u043e\u0440\u043e\u0433\u0438 \u0442\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440 \u043f\u043e \u043a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u044f\u043c"] = "Temperature thresholds by category",
        ["\u041a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u044f"] = "Category",
        ["\u041f\u0440\u0435\u0434\u0443\u043f\u0440\u0435\u0436\u0434\u0435\u043d\u0438\u0435"] = "Warning",
        ["\u041a\u0440\u0438\u0442\u0438\u0447\u043d\u043e"] = "Critical",
        ["\u041e\u0442\u043e\u0431\u0440\u0430\u0436\u0435\u043d\u0438\u0435 \u0438 \u043f\u043e\u0432\u0435\u0434\u0435\u043d\u0438\u0435"] = "Display and behavior",
        ["\u0422\u0440\u0435\u0439 \u0438 \u0430\u0432\u0442\u043e\u0437\u0430\u043f\u0443\u0441\u043a"] = "Tray and startup",
        ["\u0421\u0432\u043e\u0440\u0430\u0447\u0438\u0432\u0430\u0442\u044c \u043e\u043a\u043d\u043e \u0432 \u0442\u0440\u0435\u0439 \u0432\u043c\u0435\u0441\u0442\u043e \u043f\u0430\u043d\u0435\u043b\u0438 \u0437\u0430\u0434\u0430\u0447"] = "Minimize the window to the tray instead of the taskbar",
        ["\u0417\u0430\u043a\u0440\u044b\u0442\u0438\u0435 \u043e\u043a\u043d\u0430 \u0443\u0431\u0438\u0440\u0430\u0435\u0442 \u043f\u0440\u0438\u043b\u043e\u0436\u0435\u043d\u0438\u0435 \u0432 \u0442\u0440\u0435\u0439 (\u0432\u044b\u0445\u043e\u0434 \u2014 \u0447\u0435\u0440\u0435\u0437 \u043c\u0435\u043d\u044e \u0437\u043d\u0430\u0447\u043a\u0430)"] = "Closing the window moves the app to the tray (exit from the tray menu)",
        ["\u041f\u043e\u043a\u0430\u0437\u044b\u0432\u0430\u0442\u044c \u043f\u043e\u0434\u0441\u043a\u0430\u0437\u043a\u0443 \u043f\u0440\u0438 \u0441\u0432\u043e\u0440\u0430\u0447\u0438\u0432\u0430\u043d\u0438\u0438 \u0432 \u0442\u0440\u0435\u0439"] = "Show a hint when minimizing to the tray",
        ["\u0417\u0430\u043f\u0443\u0441\u043a\u0430\u0442\u044c \u043f\u0440\u0438 \u0432\u0445\u043e\u0434\u0435 \u0432 Windows"] = "Start with Windows",
        ["\u0417\u0430\u043f\u0443\u0441\u043a\u0430\u0442\u044c \u0441\u0440\u0430\u0437\u0443 \u0441\u0432\u0451\u0440\u043d\u0443\u0442\u044b\u043c \u0432 \u0442\u0440\u0435\u0439"] = "Start minimized to the tray",
        ["\u0421\u0432\u0435\u0440\u043d\u0443\u0442\u044c \u0432 \u0442\u0440\u0435\u0439 \u0441\u0435\u0439\u0447\u0430\u0441"] = "Minimize to tray now",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u0430\u0432\u0442\u043e\u0437\u0430\u043f\u0443\u0441\u043a"] = "Check startup",
        ["\u041f\u0430\u043f\u043a\u0430 \u0434\u0430\u043d\u043d\u044b\u0445"] = "Data folder",
        ["\u0418\u0437\u043c\u0435\u043d\u0438\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0434\u0430\u043d\u043d\u044b\u0445\u2026"] = "Change data folder\u2026",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0434\u0430\u043d\u043d\u044b\u0435"] = "Open data",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u043e\u0442\u0447\u0451\u0442\u044b"] = "Open reports",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0438\u0441\u0442\u043e\u0440\u0438\u044e"] = "Open history",
        ["\u0423\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0430"] = "Devices",
        ["\u0422\u0438\u043f\u044b \u0434\u0430\u0442\u0447\u0438\u043a\u043e\u0432"] = "Sensor types",
        ["\u0422\u043e\u043b\u044c\u043a\u043e \u0434\u0430\u0442\u0447\u0438\u043a\u0438 \u0441 \u0434\u0430\u043d\u043d\u044b\u043c\u0438"] = "Only sensors with data",
        ["\u0421\u043a\u0440\u044b\u0432\u0430\u0442\u044c \u0434\u0430\u0442\u0447\u0438\u043a\u0438-\u043f\u043e\u0440\u043e\u0433\u0438"] = "Hide threshold sensors",
        ["\u0421\u043a\u0440\u044b\u0432\u0430\u0442\u044c \u043d\u0435\u0434\u043e\u0441\u0442\u043e\u0432\u0435\u0440\u043d\u044b\u0435 \u0437\u043d\u0430\u0447\u0435\u043d\u0438\u044f"] = "Hide implausible values",
        ["\u041f\u043e\u0438\u0441\u043a:"] = "Search:",
        ["\u0423\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u043e"] = "Device",
        ["\u0422\u0438\u043f"] = "Type",
        ["\u0414\u0430\u0442\u0447\u0438\u043a"] = "Sensor",
        ["\u0417\u043d\u0430\u0447\u0435\u043d\u0438\u0435"] = "Value",
        ["\u041c\u0438\u043d\u0438\u043c\u0443\u043c"] = "Minimum",
        ["\u041c\u0430\u043a\u0441\u0438\u043c\u0443\u043c"] = "Maximum",
        ["\u0415\u0434."] = "Unit",
        ["\u0413\u0440\u0430\u0444\u0438\u043a: \u0432\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0434\u0430\u0442\u0447\u0438\u043a \u0432 \u0442\u0430\u0431\u043b\u0438\u0446\u0435"] = "Chart: select a sensor in the table",
        ["\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c \u043f\u043b\u0438\u0442\u043a\u0443"] = "Add tile",
        ["\u041d\u0430\u0431\u043e\u0440 \u043f\u043e \u0443\u043c\u043e\u043b\u0447\u0430\u043d\u0438\u044e"] = "Default set",
        ["\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c \u0438\u0441\u0442\u043e\u0440\u0438\u044e"] = "Reset history",
        ["\u041f\u0435\u0440\u0438\u043e\u0434 \u0433\u0440\u0430\u0444\u0438\u043a\u0430"] = "Chart period",
        ["\u041e\u0441\u043d\u043e\u0432\u043d\u044b\u0435 \u043a\u043e\u043c\u043f\u043b\u0435\u043a\u0442\u0443\u044e\u0449\u0438\u0435"] = "Main components",
        ["\u041e\u043f\u0435\u0440\u0430\u0442\u0438\u0432\u043d\u0430\u044f \u043f\u0430\u043c\u044f\u0442\u044c \u2014 \u0441\u043b\u043e\u0442\u044b \u0438 \u043e\u0431\u044a\u0451\u043c"] = "RAM \u2014 slots and capacity",
        ["\u041d\u0430\u043a\u043e\u043f\u0438\u0442\u0435\u043b\u0438 \u0438 SMART"] = "Drives and SMART",
        ["\u0424\u0430\u043a\u0442\u0438\u0447\u0435\u0441\u043a\u0438 \u043e\u0431\u043d\u0430\u0440\u0443\u0436\u0435\u043d\u043e"] = "Actually detected",
        ["\u0421\u043b\u043e\u0442"] = "Slot",
        ["\u041c\u043e\u0434\u0435\u043b\u044c / \u0442\u0438\u043f"] = "Model / type",
        ["\u041e\u0431\u044a\u0451\u043c \u0438 \u0441\u043a\u043e\u0440\u043e\u0441\u0442\u044c"] = "Capacity and speed",
        ["\u041c\u043e\u0434\u0435\u043b\u044c"] = "Model",
        ["\u0428\u0438\u043d\u0430 / \u043e\u0431\u044a\u0451\u043c / \u0442\u043e\u043c\u0430"] = "Bus / capacity / volumes",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c"] = "Copy",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c \u0432\u0441\u0451"] = "Copy all",
        ["\u0412\u0441\u0435 \u043a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u0438"] = "All categories",
        ["\u0421\u043e\u0431\u0441\u0442\u0432\u0435\u043d\u043d\u044b\u0439 \u0430\u0434\u0440\u0435\u0441 (\u043e\u0434\u043d\u043e\u043f\u043e\u0442\u043e\u0447\u043d\u044b\u0439)"] = "Own address (single-threaded)",
        ["\u041f\u043e\u0442\u043e\u043a\u0438 (0 \u2014 \u0432\u0441\u0435 \u044f\u0434\u0440\u0430)"] = "Threads (0 \u2014 all cores)",
        ["\u041f\u0430\u043c\u044f\u0442\u044c \u0442\u0435\u0441\u0442\u0430, \u041c\u0411"] = "Test memory, MB",
        ["\u0424\u0430\u0439\u043b \u0442\u0435\u0441\u0442\u0430 \u0434\u0438\u0441\u043a\u0430, \u041c\u0411"] = "Disk test file, MB",
        ["\u0417\u0430\u043f\u0443\u0441\u0442\u0438\u0442\u044c \u0442\u0435\u0441\u0442"] = "Start test",
        ["\u0412\u0441\u0435 \u0442\u0435\u0441\u0442\u044b"] = "All tests",
        ["\u0421\u0442\u043e\u043f"] = "Stop",
        ["\u042d\u043a\u0441\u043f\u043e\u0440\u0442 \u043e\u0442\u0447\u0451\u0442\u0430"] = "Export report",
        ["\u041e\u0448\u0438\u0431\u043a\u0438"] = "Errors",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU temperature",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU load",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u043f\u0430\u043c\u044f\u0442\u0438"] = "Memory load",
        ["\u041c\u043e\u0449\u043d\u043e\u0441\u0442\u044c \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "GPU power",
        ["\u041f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440"] = "Processor",
        ["\u0412\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u0430"] = "Graphics card",
        ["\u041c\u0430\u0442\u0435\u0440\u0438\u043d\u0441\u043a\u0430\u044f \u043f\u043b\u0430\u0442\u0430"] = "Motherboard",
        ["\u041e\u043f\u0435\u0440\u0430\u0442\u0438\u0432\u043d\u0430\u044f \u043f\u0430\u043c\u044f\u0442\u044c"] = "RAM",
        ["\u0420\u0443\u0441\u0441\u043a\u0438\u0439"] = "Russian",
        ["\u041a\u0438\u0442\u0430\u0439\u0441\u043a\u0438\u0439"] = "Chinese",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c \u0432\u0441\u0451"] = "Copy all",
        ["\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c"] = "Refresh",
        ["\u041f\u0430\u0443\u0437\u0430"] = "Pause",
        ["\u0413\u043e\u0442\u043e\u0432\u043e"] = "Done",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u043a\u043e\u043c\u043f\u043e\u043d\u0435\u043d\u0442\u044b"] = "Check components",
        ["\u0421\u0440\u0430\u0432\u043d\u0435\u043d\u0438\u0435 \u043a\u043e\u043c\u043f\u043b\u0435\u043a\u0442\u0443\u044e\u0449\u0438\u0445"] = "Component comparison",
        ["\u041f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u044c\u0441\u043a\u0438\u0439 \u0432\u0432\u043e\u0434"] = "User input",
        ["\u0421\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435"] = "State",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430"] = "Temperature",
        ["\u0418\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f \u043f\u0440\u0438\u043c\u0435\u043d\u044f\u044e\u0442\u0441\u044f \u0441\u0440\u0430\u0437\u0443 \u043f\u043e\u0441\u043b\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u044f \u0438 \u0441\u043e\u0445\u0440\u0430\u043d\u044f\u044e\u0442\u0441\u044f \u0432 portable-\u043f\u0430\u043f\u043a\u0435 data."] = "Changes apply immediately after saving and are stored in the portable data folder.",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430 \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "GPU temperature",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "GPU load",
        ["\u0412\u0435\u043d\u0442\u0438\u043b\u044f\u0442\u043e\u0440\u044b (\u043c\u0430\u043a\u0441.)"] = "Fans (max)",
        ["\u041d\u0430\u043a\u043e\u043f\u0438\u0442\u0435\u043b\u0438 (\u043c\u0430\u043a\u0441. \u0442\u0435\u043c\u043f.)"] = "Drives (max. temperature)",
        ["\u0421\u0435\u0442\u044c: \u043f\u0440\u0438\u0451\u043c"] = "Network: download",
        ["\u0427\u0430\u0441\u0442\u043e\u0442\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU frequency",
        ["\u041f\u0430\u043c\u044f\u0442\u044c \u0437\u0430\u043d\u044f\u0442\u043e"] = "Memory used",
    };
    private static readonly Dictionary<string, string> EnUs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> EnGb = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Zh = new(StringComparer.Ordinal)
    {
        ["\u041c\u043e\u043d\u0438\u0442\u043e\u0440\u0438\u043d\u0433 \u041f\u041a"] = "\u7535\u8111\u76d1\u63a7",
        ["\u041b\u0451\u0433\u043a\u0438\u0439 \u0440\u0435\u0436\u0438\u043c"] = "\u7b80\u6613\u6a21\u5f0f",
        ["\u041f\u0440\u043e\u0444\u0438"] = "\u4e13\u4e1a\u6a21\u5f0f",
        ["\u041e\u0431\u0437\u043e\u0440"] = "\u6982\u89c8",
        ["\u0414\u0430\u0442\u0447\u0438\u043a\u0438"] = "\u4f20\u611f\u5668",
        ["\u0421\u0438\u0441\u0442\u0435\u043c\u0430"] = "\u7cfb\u7edf",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u041f\u041a"] = "\u7535\u8111\u68c0\u67e5",
        ["\u0410\u0432\u0442\u043e-\u0442\u0435\u0441\u0442"] = "\u81ea\u52a8\u6d4b\u8bd5",
        ["\u041d\u0430\u0433\u0440\u0443\u0437\u043a\u0430"] = "\u538b\u529b\u6d4b\u8bd5",
        ["\u0420\u0435\u043d\u0434\u0435\u0440\u044b"] = "\u6e32\u67d3",
        ["\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438"] = "\u8bbe\u7f6e",
        ["\u0418\u043d\u0442\u0435\u0440\u0444\u0435\u0439\u0441"] = "\u754c\u9762",
        ["\u042f\u0437\u044b\u043a"] = "\u8bed\u8a00",
        ["\u0422\u0435\u043c\u0430"] = "\u4e3b\u9898",
        ["\u0422\u0451\u043c\u043d\u0430\u044f"] = "\u6df1\u8272",
        ["\u0421\u0435\u0440\u0430\u044f"] = "\u7070\u8272",
        ["\u0421\u0432\u0435\u0442\u043b\u0430\u044f"] = "\u6d45\u8272",
        ["\u0421\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c \u043d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438"] = "\u4fdd\u5b58\u8bbe\u7f6e",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0434\u0430\u043d\u043d\u044b\u0445"] = "\u6253\u5f00\u6570\u636e\u6587\u4ef6\u5939",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0436\u0443\u0440\u043d\u0430\u043b\u044b"] = "\u6253\u5f00\u65e5\u5fd7",
        ["\u041e\u043f\u0440\u043e\u0441 \u0434\u0430\u0442\u0447\u0438\u043a\u043e\u0432"] = "\u4f20\u611f\u5668\u8f6e\u8be2",
        ["\u041e\u043a\u043d\u043e \u0438\u0441\u0442\u043e\u0440\u0438\u0438 \u0433\u0440\u0430\u0444\u0438\u043a\u0430, \u0442\u043e\u0447\u0435\u043a"] = "\u56fe\u8868\u5386\u53f2\u7a97\u53e3\uff08\u70b9\uff09",
        ["\u041c\u0430\u043a\u0441\u0438\u043c\u0430\u043b\u044c\u043d\u0430\u044f \u0434\u043e\u0441\u0442\u043e\u0432\u0435\u0440\u043d\u0430\u044f \u0442\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430, \u00b0C"] = "\u6700\u5927\u53ef\u4fe1\u6e29\u5ea6\uff08\u00b0C\uff09",
        ["\u041f\u043e\u0440\u043e\u0433\u0438 \u0442\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440 \u043f\u043e \u043a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u044f\u043c"] = "\u6309\u7c7b\u522b\u8bbe\u7f6e\u6e29\u5ea6\u9608\u503c",
        ["\u041a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u044f"] = "\u7c7b\u522b",
        ["\u041f\u0440\u0435\u0434\u0443\u043f\u0440\u0435\u0436\u0434\u0435\u043d\u0438\u0435"] = "\u8b66\u544a",
        ["\u041a\u0440\u0438\u0442\u0438\u0447\u043d\u043e"] = "\u4e25\u91cd",
        ["\u041e\u0442\u043e\u0431\u0440\u0430\u0436\u0435\u043d\u0438\u0435 \u0438 \u043f\u043e\u0432\u0435\u0434\u0435\u043d\u0438\u0435"] = "\u663e\u793a\u4e0e\u884c\u4e3a",
        ["\u0422\u0440\u0435\u0439 \u0438 \u0430\u0432\u0442\u043e\u0437\u0430\u043f\u0443\u0441\u043a"] = "\u6258\u76d8\u4e0e\u542f\u52a8",
        ["\u0421\u0432\u043e\u0440\u0430\u0447\u0438\u0432\u0430\u0442\u044c \u043e\u043a\u043d\u043e \u0432 \u0442\u0440\u0435\u0439 \u0432\u043c\u0435\u0441\u0442\u043e \u043f\u0430\u043d\u0435\u043b\u0438 \u0437\u0430\u0434\u0430\u0447"] = "\u6700\u5c0f\u5316\u7a97\u53e3\u5230\u6258\u76d8\u800c\u4e0d\u662f\u4efb\u52a1\u680f",
        ["\u0417\u0430\u043a\u0440\u044b\u0442\u0438\u0435 \u043e\u043a\u043d\u0430 \u0443\u0431\u0438\u0440\u0430\u0435\u0442 \u043f\u0440\u0438\u043b\u043e\u0436\u0435\u043d\u0438\u0435 \u0432 \u0442\u0440\u0435\u0439 (\u0432\u044b\u0445\u043e\u0434 \u2014 \u0447\u0435\u0440\u0435\u0437 \u043c\u0435\u043d\u044e \u0437\u043d\u0430\u0447\u043a\u0430)"] = "\u5173\u95ed\u7a97\u53e3\u65f6\u79fb\u5165\u6258\u76d8\uff08\u901a\u8fc7\u6258\u76d8\u83dc\u5355\u9000\u51fa\uff09",
        ["\u041f\u043e\u043a\u0430\u0437\u044b\u0432\u0430\u0442\u044c \u043f\u043e\u0434\u0441\u043a\u0430\u0437\u043a\u0443 \u043f\u0440\u0438 \u0441\u0432\u043e\u0440\u0430\u0447\u0438\u0432\u0430\u043d\u0438\u0438 \u0432 \u0442\u0440\u0435\u0439"] = "\u6700\u5c0f\u5316\u5230\u6258\u76d8\u65f6\u663e\u793a\u63d0\u793a",
        ["\u0417\u0430\u043f\u0443\u0441\u043a\u0430\u0442\u044c \u043f\u0440\u0438 \u0432\u0445\u043e\u0434\u0435 \u0432 Windows"] = "\u767b\u5f55 Windows \u65f6\u542f\u52a8",
        ["\u0417\u0430\u043f\u0443\u0441\u043a\u0430\u0442\u044c \u0441\u0440\u0430\u0437\u0443 \u0441\u0432\u0451\u0440\u043d\u0443\u0442\u044b\u043c \u0432 \u0442\u0440\u0435\u0439"] = "\u542f\u52a8\u65f6\u76f4\u63a5\u6700\u5c0f\u5316\u5230\u6258\u76d8",
        ["\u0421\u0432\u0435\u0440\u043d\u0443\u0442\u044c \u0432 \u0442\u0440\u0435\u0439 \u0441\u0435\u0439\u0447\u0430\u0441"] = "\u7acb\u5373\u6700\u5c0f\u5316\u5230\u6258\u76d8",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u0430\u0432\u0442\u043e\u0437\u0430\u043f\u0443\u0441\u043a"] = "\u68c0\u67e5\u542f\u52a8\u9879",
        ["\u041f\u0430\u043f\u043a\u0430 \u0434\u0430\u043d\u043d\u044b\u0445"] = "\u6570\u636e\u6587\u4ef6\u5939",
        ["\u0418\u0437\u043c\u0435\u043d\u0438\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0434\u0430\u043d\u043d\u044b\u0445\u2026"] = "\u66f4\u6539\u6570\u636e\u6587\u4ef6\u5939\u2026",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0434\u0430\u043d\u043d\u044b\u0435"] = "\u6253\u5f00\u6570\u636e",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u043e\u0442\u0447\u0451\u0442\u044b"] = "\u6253\u5f00\u62a5\u544a",
        ["\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0438\u0441\u0442\u043e\u0440\u0438\u044e"] = "\u6253\u5f00\u5386\u53f2\u8bb0\u5f55",
        ["\u0423\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0430"] = "\u8bbe\u5907",
        ["\u0422\u0438\u043f\u044b \u0434\u0430\u0442\u0447\u0438\u043a\u043e\u0432"] = "\u4f20\u611f\u5668\u7c7b\u578b",
        ["\u0422\u043e\u043b\u044c\u043a\u043e \u0434\u0430\u0442\u0447\u0438\u043a\u0438 \u0441 \u0434\u0430\u043d\u043d\u044b\u043c\u0438"] = "\u4ec5\u663e\u793a\u6709\u6570\u636e\u7684\u4f20\u611f\u5668",
        ["\u0421\u043a\u0440\u044b\u0432\u0430\u0442\u044c \u0434\u0430\u0442\u0447\u0438\u043a\u0438-\u043f\u043e\u0440\u043e\u0433\u0438"] = "\u9690\u85cf\u9608\u503c\u4f20\u611f\u5668",
        ["\u0421\u043a\u0440\u044b\u0432\u0430\u0442\u044c \u043d\u0435\u0434\u043e\u0441\u0442\u043e\u0432\u0435\u0440\u043d\u044b\u0435 \u0437\u043d\u0430\u0447\u0435\u043d\u0438\u044f"] = "\u9690\u85cf\u4e0d\u53ef\u4fe1\u503c",
        ["\u041f\u043e\u0438\u0441\u043a:"] = "\u641c\u7d22\uff1a",
        ["\u0423\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u043e"] = "\u8bbe\u5907",
        ["\u0422\u0438\u043f"] = "\u7c7b\u578b",
        ["\u0414\u0430\u0442\u0447\u0438\u043a"] = "\u4f20\u611f\u5668",
        ["\u0417\u043d\u0430\u0447\u0435\u043d\u0438\u0435"] = "\u6570\u503c",
        ["\u041c\u0438\u043d\u0438\u043c\u0443\u043c"] = "\u6700\u5c0f\u503c",
        ["\u041c\u0430\u043a\u0441\u0438\u043c\u0443\u043c"] = "\u6700\u5927\u503c",
        ["\u0415\u0434."] = "\u5355\u4f4d",
        ["\u0413\u0440\u0430\u0444\u0438\u043a: \u0432\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0434\u0430\u0442\u0447\u0438\u043a \u0432 \u0442\u0430\u0431\u043b\u0438\u0446\u0435"] = "\u56fe\u8868\uff1a\u8bf7\u5728\u8868\u683c\u4e2d\u9009\u62e9\u4f20\u611f\u5668",
        ["\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c \u043f\u043b\u0438\u0442\u043a\u0443"] = "\u6dfb\u52a0\u78c1\u8d34",
        ["\u041d\u0430\u0431\u043e\u0440 \u043f\u043e \u0443\u043c\u043e\u043b\u0447\u0430\u043d\u0438\u044e"] = "\u9ed8\u8ba4\u5e03\u5c40",
        ["\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c \u0438\u0441\u0442\u043e\u0440\u0438\u044e"] = "\u91cd\u7f6e\u5386\u53f2",
        ["\u041f\u0435\u0440\u0438\u043e\u0434 \u0433\u0440\u0430\u0444\u0438\u043a\u0430"] = "\u56fe\u8868\u5468\u671f",
        ["\u041e\u0441\u043d\u043e\u0432\u043d\u044b\u0435 \u043a\u043e\u043c\u043f\u043b\u0435\u043a\u0442\u0443\u044e\u0449\u0438\u0435"] = "\u4e3b\u8981\u786c\u4ef6",
        ["\u041e\u043f\u0435\u0440\u0430\u0442\u0438\u0432\u043d\u0430\u044f \u043f\u0430\u043c\u044f\u0442\u044c \u2014 \u0441\u043b\u043e\u0442\u044b \u0438 \u043e\u0431\u044a\u0451\u043c"] = "\u5185\u5b58 \u2014 \u63d2\u69fd\u4e0e\u5bb9\u91cf",
        ["\u041d\u0430\u043a\u043e\u043f\u0438\u0442\u0435\u043b\u0438 \u0438 SMART"] = "\u786c\u76d8\u4e0e SMART",
        ["\u0424\u0430\u043a\u0442\u0438\u0447\u0435\u0441\u043a\u0438 \u043e\u0431\u043d\u0430\u0440\u0443\u0436\u0435\u043d\u043e"] = "\u5b9e\u9645\u68c0\u6d4b\u5230",
        ["\u0421\u043b\u043e\u0442"] = "\u63d2\u69fd",
        ["\u041c\u043e\u0434\u0435\u043b\u044c / \u0442\u0438\u043f"] = "\u578b\u53f7 / \u7c7b\u578b",
        ["\u041e\u0431\u044a\u0451\u043c \u0438 \u0441\u043a\u043e\u0440\u043e\u0441\u0442\u044c"] = "\u5bb9\u91cf\u4e0e\u901f\u5ea6",
        ["\u041c\u043e\u0434\u0435\u043b\u044c"] = "\u578b\u53f7",
        ["\u0428\u0438\u043d\u0430 / \u043e\u0431\u044a\u0451\u043c / \u0442\u043e\u043c\u0430"] = "\u603b\u7ebf / \u5bb9\u91cf / \u5377",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c"] = "\u590d\u5236",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c \u0432\u0441\u0451"] = "\u5168\u90e8\u590d\u5236",
        ["\u0412\u0441\u0435 \u043a\u0430\u0442\u0435\u0433\u043e\u0440\u0438\u0438"] = "\u6240\u6709\u7c7b\u522b",
        ["\u041f\u043e\u0442\u043e\u043a\u0438 (0 \u2014 \u0432\u0441\u0435 \u044f\u0434\u0440\u0430)"] = "\u7ebf\u7a0b\uff080 \u2014 \u6240\u6709\u6838\u5fc3\uff09",
        ["\u041f\u0430\u043c\u044f\u0442\u044c \u0442\u0435\u0441\u0442\u0430, \u041c\u0411"] = "\u6d4b\u8bd5\u5185\u5b58\uff08MB\uff09",
        ["\u0424\u0430\u0439\u043b \u0442\u0435\u0441\u0442\u0430 \u0434\u0438\u0441\u043a\u0430, \u041c\u0411"] = "\u78c1\u76d8\u6d4b\u8bd5\u6587\u4ef6\uff08MB\uff09",
        ["\u0417\u0430\u043f\u0443\u0441\u0442\u0438\u0442\u044c \u0442\u0435\u0441\u0442"] = "\u5f00\u59cb\u6d4b\u8bd5",
        ["\u0412\u0441\u0435 \u0442\u0435\u0441\u0442\u044b"] = "\u5168\u90e8\u6d4b\u8bd5",
        ["\u0421\u0442\u043e\u043f"] = "\u505c\u6b62",
        ["\u042d\u043a\u0441\u043f\u043e\u0440\u0442 \u043e\u0442\u0447\u0451\u0442\u0430"] = "\u5bfc\u51fa\u62a5\u544a",
        ["\u041e\u0448\u0438\u0431\u043a\u0438"] = "\u9519\u8bef",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU \u6e29\u5ea6",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU \u4f7f\u7528\u7387",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u043f\u0430\u043c\u044f\u0442\u0438"] = "\u5185\u5b58\u4f7f\u7528\u7387",
        ["\u041c\u043e\u0449\u043d\u043e\u0441\u0442\u044c \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "GPU \u529f\u8017",
        ["\u041f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440"] = "\u5904\u7406\u5668",
        ["\u0412\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u0430"] = "\u663e\u5361",
        ["\u041c\u0430\u0442\u0435\u0440\u0438\u043d\u0441\u043a\u0430\u044f \u043f\u043b\u0430\u0442\u0430"] = "\u4e3b\u677f",
        ["\u041e\u043f\u0435\u0440\u0430\u0442\u0438\u0432\u043d\u0430\u044f \u043f\u0430\u043c\u044f\u0442\u044c"] = "\u5185\u5b58",
        ["\u0420\u0443\u0441\u0441\u043a\u0438\u0439"] = "\u4fc4\u8bed",
        ["\u041a\u0438\u0442\u0430\u0439\u0441\u043a\u0438\u0439"] = "\u4e2d\u6587",
        ["\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u0442\u044c \u0432\u0441\u0451"] = "\u5168\u90e8\u590d\u5236",
        ["\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c"] = "\u5237\u65b0",
        ["\u041f\u0430\u0443\u0437\u0430"] = "\u6682\u505c",
        ["\u0413\u043e\u0442\u043e\u0432\u043e"] = "\u5b8c\u6210",
        ["\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u043a\u043e\u043c\u043f\u043e\u043d\u0435\u043d\u0442\u044b"] = "\u68c0\u67e5\u786c\u4ef6",
        ["\u0421\u0440\u0430\u0432\u043d\u0435\u043d\u0438\u0435 \u043a\u043e\u043c\u043f\u043b\u0435\u043a\u0442\u0443\u044e\u0449\u0438\u0445"] = "\u786c\u4ef6\u5bf9\u6bd4",
        ["\u041f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u044c\u0441\u043a\u0438\u0439 \u0432\u0432\u043e\u0434"] = "\u7528\u6237\u8f93\u5165",
        ["\u0421\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435"] = "\u72b6\u6001",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430"] = "\u6e29\u5ea6",
        ["\u0418\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f \u043f\u0440\u0438\u043c\u0435\u043d\u044f\u044e\u0442\u0441\u044f \u0441\u0440\u0430\u0437\u0443 \u043f\u043e\u0441\u043b\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u044f \u0438 \u0441\u043e\u0445\u0440\u0430\u043d\u044f\u044e\u0442\u0441\u044f \u0432 portable-\u043f\u0430\u043f\u043a\u0435 data."] = "\u4fdd\u5b58\u540e\u7acb\u5373\u5e94\u7528\u66f4\u6539\uff0c\u5e76\u4fdd\u5b58\u5728\u53ef\u643a\u6570\u636e\u6587\u4ef6\u5939\u4e2d\u3002",
        ["\u0421\u043e\u0431\u0441\u0442\u0432\u0435\u043d\u043d\u044b\u0439 \u0430\u0434\u0440\u0435\u0441 (\u043e\u0434\u043d\u043e\u043f\u043e\u0442\u043e\u0447\u043d\u044b\u0439)"] = "\u81ea\u6709\u5730\u5740\uff08\u5355\u7ebf\u7a0b\uff09",
        ["\u0422\u0435\u043c\u043f\u0435\u0440\u0430\u0442\u0443\u0440\u0430 \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "\u663e\u5361\u6e29\u5ea6",
        ["\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 \u0432\u0438\u0434\u0435\u043e\u043a\u0430\u0440\u0442\u044b"] = "\u663e\u5361\u8d1f\u8f7d",
        ["\u0412\u0435\u043d\u0442\u0438\u043b\u044f\u0442\u043e\u0440\u044b (\u043c\u0430\u043a\u0441.)"] = "\u98ce\u6247\uff08\u6700\u5927\uff09",
        ["\u041d\u0430\u043a\u043e\u043f\u0438\u0442\u0435\u043b\u0438 (\u043c\u0430\u043a\u0441. \u0442\u0435\u043c\u043f.)"] = "\u78c1\u76d8\uff08\u6700\u9ad8\u6e29\u5ea6\uff09",
        ["\u0421\u0435\u0442\u044c: \u043f\u0440\u0438\u0451\u043c"] = "\u7f51\u7edc\uff1a\u4e0b\u8f7d",
        ["\u0427\u0430\u0441\u0442\u043e\u0442\u0430 \u043f\u0440\u043e\u0446\u0435\u0441\u0441\u043e\u0440\u0430"] = "CPU \u9891\u7387",
        ["\u041f\u0430\u043c\u044f\u0442\u044c \u0437\u0430\u043d\u044f\u0442\u043e"] = "\u5df2\u7528\u5185\u5b58",
    };
    private static readonly Dictionary<string, string> LooseEn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["пауза"]="Pause", ["экспорт"]="Export", ["администратор"]="Administrator", ["ограниченный"]="Limited", ["доступ"]="access", ["сброс"]="Reset", ["журналы"]="Logs", ["перезапуск"]="Restart",
        ["применение"]="Applying", ["языка"]="language", ["сведения"]="information", ["памяти"]="memory", ["запустить"]="Start", ["запуск"]="Start", ["остановлено"]="Stopped", ["остановить"]="Stop", ["готов"]="Ready", ["готово"]="Done", ["выберите"]="Select", ["тест"]="test", ["тесты"]="tests", ["результаты"]="Results", ["результат"]="Result", ["журнал"]="Log",
        ["проверка"]="Check", ["проверить"]="Check", ["комплектующие"]="components", ["комплектующая"]="component", ["введено"]="entered", ["пользователем"]="by user", ["фактически"]="actual", ["обнаружено"]="detected", ["сравнить"]="Compare", ["процессор"]="CPU", ["видеокарта"]="GPU", ["материнская"]="motherboard", ["плата"]="board", ["оперативная"]="RAM", ["память"]="memory", ["накопители"]="drives", ["накопитель"]="drive", ["диск"]="disk", ["диски"]="disks", ["сеть"]="Network", ["адаптер"]="adapter",
        ["температура"]="temperature", ["температуры"]="temperatures", ["загрузка"]="load", ["мощность"]="power", ["частота"]="frequency", ["обороты"]="fan speed", ["напряжение"]="voltage", ["состояние"]="status", ["ошибки"]="errors", ["ошибка"]="error", ["данные"]="data", ["значение"]="value", ["значения"]="values", ["минимум"]="minimum", ["максимум"]="maximum", ["среднее"]="average", ["единица"]="unit", ["тип"]="type", ["модель"]="model", ["объём"]="capacity", ["объем"]="capacity", ["скорость"]="speed", ["время"]="time", ["секунд"]="seconds", ["секунда"]="second", ["минут"]="minutes", ["минута"]="minute", ["всего"]="total", ["свободно"]="free", ["занято"]="used",
        ["настройки"]="Settings", ["настройка"]="setting", ["сохранить"]="Save", ["открыть"]="Open", ["папка"]="folder", ["данных"]="data", ["отчёт"]="report", ["отчета"]="report", ["история"]="history", ["обновить"]="Refresh", ["копировать"]="Copy", ["отмена"]="Cancel", ["закрыть"]="Close", ["сохранено"]="saved", ["сброшены"]="reset", ["разрешение"]="resolution", ["описание"]="description", ["подробности"]="details", ["предупреждение"]="warning", ["критично"]="critical", ["успешно"]="successful", ["проблема"]="problem", ["информация"]="information", ["неизвестно"]="unknown", ["нет"]="no", ["да"]="yes", ["включена"]="enabled", ["выключена"]="disabled", ["включён"]="enabled", ["выключен"]="disabled", ["показать"]="Show", ["скрывать"]="Hide", ["положение"]="Position", ["слева"]="left", ["справа"]="right", ["сверху"]="top", ["снизу"]="bottom"
    };
    private static readonly Dictionary<string, string> LooseZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["авто"]="自动", ["автотест"]="自动测试", ["авто-тест"]="自动测试", ["всего"]="全部", ["компьютера"]="电脑", ["компьютер"]="电脑", ["проверяет"]="检查", ["проверяем"]="检查", ["максимальной"]="最大", ["нагрузкой"]="负载", ["нагрузка"]="负载", ["нагрузки"]="负载", ["показания"]="读数", ["показывает"]="显示", ["вывод"]="结果", ["понятный"]="清晰", ["отчёт"]="报告", ["отчета"]="报告", ["выберите"]="选择", ["выбранные"]="选定的", ["параметры"]="参数", ["длительность"]="持续时间", ["фаза"]="阶段", ["покой"]="空闲", ["файл"]="文件", ["пресет"]="预设", ["добавлять"]="添加", ["совмещённую"]="组合", ["совмещенную"]="组合", ["какие"]="哪些", ["проверять"]="检查", ["найдены"]="已找到", ["найдено"]="已找到", ["список"]="列表", ["описание"]="说明", ["состояние"]="状态", ["скорости"]="速度", ["износ"]="磨损", ["ошибки"]="错误", ["температуры"]="温度", ["частоты"]="频率", ["мощности"]="功耗", ["вентиляторы"]="风扇", ["программы"]="程序", ["режимы"]="模式", ["устройства"]="设备", ["приложение"]="应用", ["система"]="系统", ["интерфейс"]="界面", ["изменения"]="更改", ["применяются"]="应用", ["сразу"]="立即", ["после"]="之后", ["сохранения"]="保存", ["сохраняются"]="保存", ["папке"]="文件夹", ["данные"]="数据", ["данных"]="数据", ["вкладка"]="选项卡", ["поле"]="字段", ["поля"]="字段", ["введённое"]="输入的", ["введенное"]="输入的", ["фактически"]="实际", ["обнаруженное"]="检测到的", ["пользователем"]="用户", ["готово"]="完成", ["сравнить"]="比较", ["сравнение"]="比较", ["результаты"]="结果", ["журнал"]="日志", ["запуск"]="启动", ["запустить"]="启动", ["остановить"]="停止", ["остановлено"]="已停止", ["ошибка"]="错误", ["предупреждение"]="警告", ["успешно"]="成功", ["минимум"]="最小", ["максимум"]="最大", ["среднее"]="平均", ["значение"]="数值", ["единица"]="单位", ["модель"]="型号", ["тип"]="类型", ["объём"]="容量", ["объем"]="容量", ["скорость"]="速度", ["время"]="时间", ["секунд"]="秒", ["секунда"]="秒", ["минут"]="分钟", ["минута"]="分钟", ["включён"]="已启用", ["выключен"]="已禁用", ["показать"]="显示", ["скрывать"]="隐藏", ["положение"]="位置", ["слева"]="左侧", ["справа"]="右侧", ["сверху"]="顶部", ["снизу"]="底部", ["отмена"]="取消", ["закрыть"]="关闭", ["открыть"]="打开", ["сохранить"]="保存", ["обновить"]="刷新", ["копировать"]="复制", ["проверить"]="检查", ["комплектующие"]="硬件", ["процессор"]="处理器", ["видеокарта"]="显卡", ["материнская"]="主板", ["плата"]="主板", ["оперативная"]="内存", ["память"]="内存", ["накопители"]="硬盘", ["накопитель"]="硬盘", ["диск"]="磁盘", ["сеть"]="网络", ["адаптер"]="适配器", ["температура"]="温度", ["загрузка"]="使用率", ["мощность"]="功耗", ["частота"]="频率", ["обороты"]="转速", ["напряжение"]="电压", ["состояние"]="状态", ["ошибки"]="错误", ["ошибка"]="错误", ["данные"]="数据", ["да"]="是", ["нет"]="否", ["неизвестно"]="未知"
    };

    static LocalizationManager()
    {
        LoadJson("ru", Ru);
        LoadJson("en-US", EnUs);
        LoadJson("en-GB", EnGb);
        LoadJson("zh", Zh);
        AddSettingsTranslations();
        AddOverviewAndAboutTranslations();
        AddTileEditorTranslations();
        AddSystemTranslations();
        MergeFallback(EnUs);
        MergeFallback(EnGb);
        BuildReverseTextAliases();
    }

    /// <summary>Adds built-in pairs that are not yet present in a JSON catalog.</summary>
    private static void MergeFallback(Dictionary<string, string> target)
    {
        foreach ((string key, string value) in En) target.TryAdd(key, value);
    }

    private static void BuildReverseTextAliases()
    {
        AddReverseAliases(EnUs);
        AddReverseAliases(EnGb);
    }

    /// <summary>Maps English UI text back to its Russian source and Chinese translation.</summary>
    private static void AddReverseAliases(IReadOnlyDictionary<string, string> english)
    {
        foreach ((string source, string text) in english.ToArray())
        {
            if (!source.Any(c => c is >= '\u0400' and <= '\u04ff') || string.IsNullOrWhiteSpace(text)) continue;
            Ru.TryAdd(text, source);
            if (Zh.TryGetValue(source, out string? chinese)) Zh.TryAdd(text, chinese);
        }
    }

    private static void AddSettingsTranslations()
    {
        Add(En, ("Изменения применяются сразу после сохранения и сохраняются в portable-папке data.", "Changes apply immediately after saving and are stored in the portable data folder."),
            ("Интервал между опросами. Меньше интервал — больше нагрузка на систему.", "Polling interval. A shorter interval increases system load."),
            ("Пороги используются для подсветки, метки «перегрев» и звукового сигнала.", "Thresholds control highlighting, the overheating marker, and the alert sound."),
            ("Скрывать датчики-пороги (например, «Critical Temperature» накопителя)", "Hide threshold sensors (for example, a drive's Critical Temperature)"),
            ("Скрывать недостоверные значения неподключённых датчиков", "Hide implausible values from disconnected sensors"),
            ("Показывать только датчики со значениями", "Show only sensors with values"),
            ("Звуковой сигнал при превышении критического порога", "Play a sound when the critical threshold is exceeded"),
            ("Окно поверх остальных", "Keep the window on top"),
            ("Автозапуск", "Startup"),
            ("Запуск приложения при входе в Windows.", "Start the application when Windows starts."),
            ("Папки с данными (приложение переносимое)", "Data folders (portable application)"),
            ("Папку с программой можно перенести на любой диск — настройки, журналы и отчёты переедут вместе с ней. Данные можно держать отдельно: выбранный путь сохраняется в файле datapath.txt рядом с программой, а ключ --data <папка> задаёт его разово.", "The program folder can be moved to any drive; settings, logs, and reports move with it. Data can be stored separately: the selected path is saved in datapath.txt next to the program, while --data <folder> sets it for one run."),
            ("Своя папка журналов (пусто — по умолчанию)", "Custom log folder (empty = default)"),
            ("Стресс-тесты и бенчмарки", "Stress tests and benchmarks"),
            ("Папка для тестов диска (пусто — временная)", "Disk test folder (empty = temporary)"),
            ("Экранный оверлей (внешних приложений-стиль)", "On-screen overlay (external-app style)"),
            ("Окно поверх всех приложений, сквозное для кликов. Включение — кнопка OSD на панели или Ctrl+Alt+O.", "A window above all applications that passes clicks through. Enable it with the OSD button or Ctrl+Alt+O."),
            ("Показывать оверлей", "Show overlay"), ("Слева сверху", "Top left"), ("Справа сверху", "Top right"),
            ("Слева снизу", "Bottom left"), ("Справа снизу", "Bottom right"), ("Отступ по X / Y", "X / Y offset"),
            ("Прозрачность / кегль", "Opacity / font size"), ("Метрики в оверлее", "Overlay metrics"),
            ("Правила для отдельных датчиков", "Rules for individual sensors"), ("Сбросить все правила", "Reset all rules"),
            ("По умолчанию", "Default"), ("Накопители", "Drives"), ("Журналы", "Logs"), ("Отчёты", "Reports"),
            ("История", "History"), ("Файлы тестов диска", "Disk test files"),
            ("Режим: переносимый — данные лежат внутри папки программы.", "Mode: portable — data is stored inside the program folder."),
            ("Режим: данные вне папки программы, путь можно сменить кнопкой «Изменить папку данных…».", "Mode: data is outside the program folder; change the path with the Change data folder button."),
            ("настройки сохранены", "Settings saved"), ("правила сброшены", "Rules reset"),
            ("Оверлей включён", "Overlay enabled"), ("Оверлей выключен", "Overlay disabled"),
            ("Автозапуск включён", "Startup enabled"), ("Автозапуск выключен", "Startup disabled"),
            ("Не удалось применить настройки", "Could not apply settings"), ("Путь к программе", "Program path"),
            ("не удалось открыть журналы", "Could not open logs"), ("не удалось открыть папку", "Could not open folder"),
            ("Все правила для датчиков сброшены.", "All sensor rules have been reset."));

        Add(Zh, ("Изменения применяются сразу после сохранения и сохраняются в portable-папке data.", "保存后立即应用更改，并存储在便携版 data 文件夹中。"),
            ("Интервал между опросами. Меньше интервал — больше нагрузка на систему.", "轮询间隔。间隔越短，系统负载越高。"),
            ("Пороги используются для подсветки, метки «перегрев» и звукового сигнала.", "阈值用于高亮、过热标记和声音警报。"),
            ("Скрывать датчики-пороги (например, «Critical Temperature» накопителя)", "隐藏阈值传感器（例如硬盘的 Critical Temperature）"),
            ("Скрывать недостоверные значения неподключённых датчиков", "隐藏未连接传感器的不可信数值"),
            ("Показывать только датчики со значениями", "仅显示有数值的传感器"),
            ("Звуковой сигнал при превышении критического порога", "超过严重阈值时播放声音"),
            ("Окно поверх остальных", "窗口置顶"), ("Автозапуск", "启动"),
            ("Запуск приложения при входе в Windows.", "登录 Windows 时启动应用。"),
            ("Папки с данными (приложение переносимое)", "数据文件夹（便携应用）"),
            ("Папку с программой можно перенести на любой диск — настройки, журналы и отчёты переедут вместе с ней. Данные можно держать отдельно: выбранный путь сохраняется в файле datapath.txt рядом с программой, а ключ --data <папка> задаёт его разово.", "程序文件夹可以移动到任意磁盘，设置、日志和报告会随之移动。数据也可单独保存：路径写入程序旁的 datapath.txt，--data <文件夹> 可临时指定路径。"),
            ("Своя папка журналов (пусто — по умолчанию)", "自定义日志文件夹（留空为默认）"),
            ("Стресс-тесты и бенчмарки", "压力测试与基准测试"), ("Папка для тестов диска (пусто — временная)", "磁盘测试文件夹（留空为临时文件夹）"),
            ("Экранный оверлей (внешних приложений-стиль)", "屏幕叠加层（外部应用样式）"),
            ("Окно поверх всех приложений, сквозное для кликов. Включение — кнопка OSD на панели или Ctrl+Alt+O.", "窗口显示在所有应用之上并允许点击穿透。使用面板上的 OSD 按钮或 Ctrl+Alt+O 启用。"),
            ("Показывать оверлей", "显示叠加层"), ("Слева сверху", "左上"), ("Справа сверху", "右上"),
            ("Слева снизу", "左下"), ("Справа снизу", "右下"), ("Отступ по X / Y", "X / Y 偏移"),
            ("Прозрачность / кегль", "透明度 / 字号"), ("Метрики в оверлее", "叠加层指标"),
            ("Правила для отдельных датчиков", "单个传感器规则"), ("Сбросить все правила", "重置所有规则"),
            ("По умолчанию", "默认"), ("Накопители", "硬盘"), ("Журналы", "日志"), ("Отчёты", "报告"),
            ("История", "历史记录"), ("Файлы тестов диска", "磁盘测试文件"),
            ("Режим: переносимый — данные лежат внутри папки программы.", "模式：便携版 — 数据存储在程序文件夹中。"),
            ("Режим: данные вне папки программы, путь можно сменить кнопкой «Изменить папку данных…».", "模式：数据存储在程序文件夹外，可使用“更改数据文件夹”按钮修改路径。"),
            ("настройки сохранены", "设置已保存"), ("правила сброшены", "规则已重置"),
            ("Оверлей включён", "叠加层已启用"), ("Оверлей выключен", "叠加层已禁用"),
            ("Автозапуск включён", "启动已启用"), ("Автозапуск выключен", "启动已禁用"),
            ("Не удалось применить настройки", "无法应用设置"), ("Путь к программе", "程序路径"),
            ("не удалось открыть журналы", "无法打开日志"), ("не удалось открыть папку", "无法打开文件夹"),
            ("Все правила для датчиков сброшены.", "所有传感器规则已重置。"));
    }

    private static void AddSystemTranslations()
    {
        Add(En,
            ("Загрузка сведений о системе…", "Loading system information…"),
            ("Не удалось загрузить сведения о системе", "Could not load system information"),
            ("Видеокарты", "Graphics cards"),
            ("Компьютер и операционная система", "Computer and operating system"),
            ("Материнская плата и BIOS", "Motherboard and BIOS"),
            ("Тайминги памяти (SPD)", "Memory timings (SPD)"),
            ("читаются напрямую с модулей через SMBus", "Read directly from the modules via SMBus"),
            ("все физические диски с их томами", "All physical drives and their volumes"),
            ("все логические диски системы", "All logical system drives"),
            ("Тип корпуса", "Chassis type"), ("Операционная система", "Operating system"),
            ("Версия", "Version"), ("Архитектура", "Architecture"),
            ("Имя компьютера", "Computer name"), ("Пользователь", "User"),
            ("Установлена", "Installed"), ("Последняя загрузка", "Last boot"),
            ("Время работы", "Uptime"), ("Файл подкачки", "Page file"),
            ("Всего памяти", "Total memory"), ("Режим загрузки", "Boot mode"),
            ("Безопасная загрузка", "Secure Boot"), ("Производитель платы", "Board manufacturer"),
            ("Модель платы", "Board model"), ("Версия платы", "Board version"),
            ("Серийный номер платы", "Board serial number"), ("Производитель BIOS", "BIOS manufacturer"),
            ("Версия BIOS", "BIOS version"), ("Дата BIOS", "BIOS date"),
            ("Версия SMBIOS", "SMBIOS version"), ("Производитель", "Manufacturer"),
            ("Сокет", "Socket"),
            ("Ядра / потоки", "Cores / threads"), ("Базовая частота", "Base clock"),
            ("Кэш L2 / L3", "L2 / L3 cache"), ("Разрядность", "Address width"),
            ("Идентификатор", "Identifier"), ("Виртуализация", "Virtualization"),
            ("Текущая частота (макс. по ядрам)", "Current clock (maximum across cores)"),
            ("Температура сейчас", "Current temperature"), ("Потребление пакета", "Package power"),
            ("Слот", "Slot"), ("Модуль", "Module"), ("Объём", "Capacity"),
            ("SPD, МГц", "SPD, MHz"), ("BIOS, МГц", "BIOS, MHz"),
            ("Тип", "Type"), ("Форм-фактор", "Form factor"), ("Напряжение", "Voltage"),
            ("Всего установлено", "Total installed"),
            ("Заявленная скорость модулей (SMBIOS)", "Rated module speed (SMBIOS)"),
            ("Текущая частота (SMBIOS/BIOS)", "Current clock (SMBIOS/BIOS)"),
            ("Сопоставление скоростей", "Speed comparison"), ("Каналы", "Channels"),
            ("Тайминг", "Timing"), ("Устройство", "Device"), ("Драйвер", "Driver"),
            ("Дата драйвера", "Driver date"), ("Видеопамять", "Video memory"),
            ("Разрешение", "Resolution"), ("Потребление сейчас", "Current power"),
            ("Модель", "Model"), ("Шина", "Bus"), ("Тома", "Volumes"),
            ("Состояние", "Status"), ("Температура", "Temperature"),
            ("Серийный номер", "Serial number"), ("Том", "Volume"),
            ("Накопитель", "Drive"), ("Метка", "Label"),
            ("Файловая система", "File system"), ("Всего", "Total"),
            ("Свободно", "Free"), ("Занято", "Used"), ("Сеть", "Network"),
            ("Адаптер", "Adapter"), ("MAC", "MAC"), ("IPv4", "IPv4"),
            ("Шлюз", "Gateway"), ("DHCP", "DHCP"),
            ("настольный", "Desktop"), ("мини-корпус", "Mini-tower"),
            ("тонкий корпус", "Slim desktop"), ("ноутбук", "Laptop"),
            ("моноблок", "All-in-one"), ("не используется", "Not in use"),
            ("не удалось получить данные", "Unable to retrieve data"),
            ("недостаточно данных SMBIOS", "Insufficient SMBIOS data"),
            ("число каналов по SMBIOS не подтверждено", "Channel count is not confirmed by SMBIOS"),
            ("модули найдены, но тайминги не отдаются", "Modules found, but timings are unavailable"),
            ("нет данных — нужны права администратора и поддержка SPD", "No data — administrator rights and SPD support are required"),
            ("все физические диски с их томами", "All physical drives and their volumes"),
            ("нет томов", "No volumes"), ("накопители не найдены", "No drives found"),
            ("объём выделенной памяти не сообщён", "Dedicated memory capacity was not reported"),
            ("неизвестный слот", "Unknown slot"), ("не определён", "Not identified"),
            ("здоров", "Healthy"), ("внимание", "Warning"), ("нездоров", "Unhealthy"),
            ("неизвестно", "Unknown"),
            ("тип", "Type"), ("системный", "System"), ("свободно", "free"),
            ("включена", "Enabled"), ("выключена", "Disabled"),
            ("неизвестно", "Unknown"), ("нет данных", "No data"),
            ("нет", "No"), ("да", "Yes"));

        Add(Zh,
            ("Загрузка сведений о системе…", "正在加载系统信息…"),
            ("Не удалось загрузить сведения о системе", "无法加载系统信息"),
            ("Видеокарты", "显卡"),
            ("Компьютер и операционная система", "计算机与操作系统"),
            ("Материнская плата и BIOS", "主板与 BIOS"),
            ("Тайминги памяти (SPD)", "内存时序 (SPD)"),
            ("читаются напрямую с модулей через SMBus", "直接通过 SMBus 从内存模块读取"),
            ("все физические диски с их томами", "所有物理磁盘及其卷"),
            ("все логические диски системы", "系统中的所有逻辑磁盘"),
            ("Тип корпуса", "机箱类型"), ("Операционная система", "操作系统"),
            ("Версия", "版本"), ("Архитектура", "架构"),
            ("Имя компьютера", "计算机名"), ("Пользователь", "用户"),
            ("Установлена", "安装日期"), ("Последняя загрузка", "上次启动"),
            ("Время работы", "运行时间"), ("Файл подкачки", "页面文件"),
            ("Всего памяти", "总内存"), ("Режим загрузки", "启动模式"),
            ("Безопасная загрузка", "安全启动"), ("Производитель платы", "主板制造商"),
            ("Модель платы", "主板型号"), ("Версия платы", "主板版本"),
            ("Серийный номер платы", "主板序列号"), ("Производитель BIOS", "BIOS 制造商"),
            ("Версия BIOS", "BIOS 版本"), ("Дата BIOS", "BIOS 日期"),
            ("Версия SMBIOS", "SMBIOS 版本"), ("Производитель", "制造商"),
            ("Сокет", "插槽"),
            ("Ядра / потоки", "核心 / 线程"), ("Базовая частота", "基准频率"),
            ("Кэш L2 / L3", "L2 / L3 缓存"), ("Разрядность", "位宽"),
            ("Идентификатор", "标识符"), ("Виртуализация", "虚拟化"),
            ("Текущая частота (макс. по ядрам)", "当前频率（核心最大值）"),
            ("Температура сейчас", "当前温度"), ("Потребление пакета", "封装功耗"),
            ("Слот", "插槽"), ("Модуль", "模块"), ("Объём", "容量"),
            ("SPD, МГц", "SPD，MHz"), ("BIOS, МГц", "BIOS，MHz"),
            ("Тип", "类型"), ("Форм-фактор", "外形规格"), ("Напряжение", "电压"),
            ("Всего установлено", "已安装总数"),
            ("Заявленная скорость модулей (SMBIOS)", "模块标称速度 (SMBIOS)"),
            ("Текущая частота (SMBIOS/BIOS)", "当前频率 (SMBIOS/BIOS)"),
            ("Сопоставление скоростей", "速度对比"), ("Каналы", "通道"),
            ("Тайминг", "时序"), ("Устройство", "设备"), ("Драйвер", "驱动程序"),
            ("Дата драйвера", "驱动日期"), ("Видеопамять", "显存"),
            ("Разрешение", "分辨率"), ("Потребление сейчас", "当前功耗"),
            ("Модель", "型号"), ("Шина", "总线"), ("Тома", "卷"),
            ("Состояние", "状态"), ("Температура", "温度"),
            ("Серийный номер", "序列号"), ("Том", "卷"),
            ("Накопитель", "磁盘"), ("Метка", "卷标"),
            ("Файловая система", "文件系统"), ("Всего", "总计"),
            ("Свободно", "可用"), ("Занято", "已用"), ("Сеть", "网络"),
            ("Адаптер", "适配器"), ("MAC", "MAC"), ("IPv4", "IPv4"),
            ("Шлюз", "网关"), ("DHCP", "DHCP"),
            ("настольный", "台式机"), ("мини-корпус", "迷你机箱"),
            ("тонкий корпус", "超薄机箱"), ("ноутбук", "笔记本电脑"),
            ("моноблок", "一体机"), ("не используется", "未使用"),
            ("не удалось получить данные", "无法获取数据"),
            ("недостаточно данных SMBIOS", "SMBIOS 数据不足"),
            ("число каналов по SMBIOS не подтверждено", "SMBIOS 未确认内存通道数"),
            ("модули найдены, но тайминги не отдаются", "已找到内存模块，但无法读取时序"),
            ("нет данных — нужны права администратора и поддержка SPD", "无数据——需要管理员权限和 SPD 支持"),
            ("нет томов", "没有卷"), ("накопители не найдены", "未找到磁盘"),
            ("объём выделенной памяти не сообщён", "未报告专用显存容量"),
            ("неизвестный слот", "未知插槽"), ("не определён", "未识别"),
            ("здоров", "正常"), ("внимание", "警告"), ("нездоров", "异常"),
            ("неизвестно", "未知"),
            ("тип", "类型"), ("системный", "系统"), ("свободно", "可用"),
            ("включена", "已启用"), ("выключена", "已禁用"),
            ("неизвестно", "未知"), ("нет данных", "无数据"),
            ("нет", "否"), ("да", "是"));
    }

    private static void AddOverviewAndAboutTranslations()
    {
        Add(En,
            ("Период графика", "Chart period"),
            ("10 секунд", "10 seconds"), ("30 секунд", "30 seconds"), ("60 секунд", "60 seconds"),
            ("2 минуты", "2 minutes"), ("5 минут", "5 minutes"), ("10 минут", "10 minutes"),
            ("За всё время", "All time"),
            ("Красивые живые плитки для датчиков компьютера.", "Beautiful live tiles for your PC sensors."),
            ("Участники", "Participants"),
            ("Deniscomm — Разработчик", "Deniscomm — Developer"),
            ("Manus AI — Программист", "Manus AI — Programmer"),
            ("DeepSeek AI — соавтор интерфейса", "DeepSeek AI — Interface co-author"));

        Add(Zh,
            ("Период графика", "图表时间范围"),
            ("10 секунд", "10 秒"), ("30 секунд", "30 秒"), ("60 секунд", "60 秒"),
            ("2 минуты", "2 分钟"), ("5 минут", "5 分钟"), ("10 минут", "10 分钟"),
            ("За всё время", "全部时间"),
            ("Красивые живые плитки для датчиков компьютера.", "电脑传感器的精美实时卡片。"),
            ("Участники", "参与者"),
            ("Deniscomm — Разработчик", "Deniscomm — 开发者"),
            ("Manus AI — Программист", "Manus AI — 程序员"),
            ("DeepSeek AI — соавтор интерфейса", "DeepSeek AI — 界面共同作者"));
    }

    private static void AddTileEditorTranslations()
    {
        Add(En,
            ("Плитка дашборда", "Dashboard tile"), ("Что показывать", "Metric to display"),
            ("Поиск по названию метрики, устройства или датчика", "Search metrics, devices, or sensors"),
            ("Своё название (необязательно)", "Custom title (optional)"), ("Вид плитки", "Tile style"),
            ("Значение", "Value"), ("Круговая шкала", "Gauge"), ("Полоса", "Bar"), ("График", "Graph"),
            ("Ширина, px", "Width, px"), ("Высота, px", "Height, px"),
            ("Порог, жёлтый", "Warning threshold"), ("Порог, красный", "Critical threshold"),
            ("Показывать график истории", "Show history chart"),
            ("Пороги задаются в единицах метрики: например, 70 и 85 для температур, 100 для процентов.",
                "Thresholds use the metric's units; for example, 70 and 85 for temperature, or 100 for percent."),
            ("Отмена", "Cancel"), ("Сохранить", "Save"), ("Выберите метрику для плитки.", "Select a metric for the tile."),
            ("Плитка", "Tile"), ("Сводка", "Summary"), ("Датчик", "Sensor"),
            ("порог", "threshold"), ("недостоверно", "implausible"),
            ("Hot Spot видеокарты", "GPU hot spot"),
            ("Температура мат. платы", "Motherboard temperature"), ("Мат. плата", "Motherboard"),
            ("Обороты вентилятора GPU", "GPU fan speed"), ("Видеопамять занято", "Video memory used"),
            ("Видеопамять всего", "Total video memory"), ("Память свободно", "Free memory"),
            ("Память всего", "Total memory"), ("Вентиляторы платы (макс.)", "Motherboard fans (max)"),
            ("Напряжение платы (макс.)", "Motherboard voltage (max)"), ("Температура накопителей", "Drive temperature"),
            ("Чтение с дисков", "Disk read"), ("Запись на диски", "Disk write"),
            ("Напряжение ядра", "Core voltage"), ("Температура мат. платы", "Motherboard temperature"),
            ("Загрузка памяти", "Memory load"), ("Память занято", "Memory used"),
            ("Вентиляторы (макс.)", "Fans (max)"), ("Температура накопителей", "Drive temperature"),
            ("Загрузка видеокарты", "GPU load"), ("Частота ядра GPU", "GPU core clock"),
            ("Частота памяти GPU", "GPU memory clock"), ("Мощность видеокарты", "GPU power"),
            ("Температура видеокарты", "GPU temperature"), ("Загрузка процессора", "CPU load"),
            ("Температура процессора", "CPU temperature"), ("Частота процессора", "CPU frequency"),
            ("Мощность процессора", "CPU power"), ("Оперативная память", "RAM"),
            ("Процессор", "CPU"), ("Видеокарта", "GPU"), ("Накопители", "Drives"), ("Охлаждение", "Cooling"));

        Add(En,
            ("Мин", "Min"), ("Макс", "Max"),
            ("Пользователь вручную разрешил учитывать это значение,\nдаже если оно выходит за автоматический диапазон.", "The user allowed this value to be included,\neven when it is outside the automatic range."),
            ("Пользователь вручную пометил значение как недостоверное;\nоно исключено из сводок и тревог.", "The user marked this value as implausible;\nit is excluded from summaries and alerts."),
            ("Выберите «Доверие: автоматическое», чтобы вернуть проверку диапазона.", "Choose ‘Trust: automatic’ to restore range checks."),
            ("Это может скрыть реальный перегрев — применяйте осторожно.", "This may hide actual overheating; use with care."),
            ("Это настроенный порог устройства, а не измерение:\nтакое значение не участвует в сводке и тревогах.", "This is a configured device threshold, not a measurement;\nit is excluded from summaries and alerts."),
            ("Внимание: значение вне допустимого диапазона —\nвероятно, датчик не подключён. Оно не влияет\nна сводку и сигнал тревоги.", "Warning: this value is outside the plausible range;\nthe sensor may be disconnected. It is excluded from\nsummaries and alerts."),
            ("Датчик помечен как неважный: не влияет на сводку.", "This sensor is marked as unimportant and excluded from summaries."));

        Add(Zh,
            ("Плитка дашборда", "仪表盘卡片"), ("Что показывать", "显示内容"),
            ("Поиск по названию метрики, устройства или датчика", "搜索指标、设备或传感器"),
            ("Своё название (необязательно)", "自定义名称（可选）"), ("Вид плитки", "卡片样式"),
            ("Значение", "数值"), ("Круговая шкала", "圆形仪表"), ("Полоса", "条形"), ("График", "图表"),
            ("Ширина, px", "宽度，px"), ("Высота, px", "高度，px"),
            ("Порог, жёлтый", "警告阈值"), ("Порог, красный", "严重阈值"),
            ("Показывать график истории", "显示历史图表"),
            ("Пороги задаются в единицах метрики: например, 70 и 85 для температур, 100 для процентов.",
                "阈值使用指标单位，例如温度可设为 70 和 85，百分比可设为 100。"),
            ("Отмена", "取消"), ("Сохранить", "保存"), ("Выберите метрику для плитки.", "请选择卡片指标。"),
            ("Плитка", "卡片"), ("Сводка", "汇总"), ("Датчик", "传感器"),
            ("порог", "阈值"), ("недостоверно", "不可靠"),
            ("Hot Spot видеокарты", "显卡热点温度"),
            ("Температура мат. платы", "主板温度"), ("Мат. плата", "主板"),
            ("Обороты вентилятора GPU", "显卡风扇转速"), ("Видеопамять занято", "已用显存"),
            ("Видеопамять всего", "显存总量"), ("Память свободно", "可用内存"),
            ("Память всего", "内存总量"), ("Вентиляторы платы (макс.)", "主板风扇（最大值）"),
            ("Напряжение платы (макс.)", "主板电压（最大值）"), ("Температура накопителей", "硬盘温度"),
            ("Чтение с дисков", "磁盘读取"), ("Запись на диски", "磁盘写入"),
            ("Напряжение ядра", "核心电压"), ("Загрузка памяти", "内存使用率"),
            ("Память занято", "已用内存"), ("Вентиляторы (макс.)", "风扇（最大值）"),
            ("Загрузка видеокарты", "显卡使用率"), ("Частота ядра GPU", "显卡核心频率"),
            ("Частота памяти GPU", "显存频率"), ("Мощность видеокарты", "显卡功耗"),
            ("Температура видеокарты", "显卡温度"), ("Загрузка процессора", "CPU 使用率"),
            ("Температура процессора", "CPU 温度"), ("Частота процессора", "CPU 频率"),
            ("Мощность процессора", "CPU 功耗"), ("Оперативная память", "内存"),
            ("Процессор", "处理器"), ("Видеокарта", "显卡"), ("Накопители", "硬盘"), ("Охлаждение", "散热"));

        Add(Zh,
            ("Мин", "最小"), ("Макс", "最大"),
            ("Пользователь вручную разрешил учитывать это значение,\nдаже если оно выходит за автоматический диапазон.", "用户已允许计入此数值，\n即使它超出自动范围。"),
            ("Пользователь вручную пометил значение как недостоверное;\nоно исключено из сводок и тревог.", "用户已将此数值标记为不可靠；\n它不会计入汇总和警报。"),
            ("Выберите «Доверие: автоматическое», чтобы вернуть проверку диапазона.", "选择“信任：自动”以恢复范围检查。"),
            ("Это может скрыть реальный перегрев — применяйте осторожно.", "这可能会掩盖实际过热情况，请谨慎使用。"),
            ("Это настроенный порог устройства, а не измерение:\nтакое значение не участвует в сводке и тревогах.", "这是设备配置的阈值，而非测量值；\n不会计入汇总和警报。"),
            ("Внимание: значение вне допустимого диапазона —\nвероятно, датчик не подключён. Оно не влияет\nна сводку и сигнал тревоги.", "警告：此数值超出合理范围，传感器可能未连接。\n它不会影响汇总和警报。"),
            ("Датчик помечен как неважный: не влияет на сводку.", "此传感器已标记为不重要，不计入汇总。"));
    }

    private static void Add(Dictionary<string, string> target, params (string Key, string Value)[] values)
    {
        foreach ((string key, string value) in values) target[key] = value;
    }

    private static void LoadJson(string name, Dictionary<string, string> target)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Localization", name + ".json");
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is null) return;
            foreach (var pair in loaded) target[pair.Key] = pair.Value;
        }
        catch { }
    }

    public static string Language => Volatile.Read(ref _language);

    /// <summary>Returns the catalog for a language code.</summary>
    private static IReadOnlyDictionary<string, string> MapFor(string language) => language switch
    {
        "ru" or "ru-RU" => Ru,
        "zh-CN" => Zh,
        "en-GB" => EnGb,
        _ => EnUs,
    };

    /// <summary>Gets a translated value by a stable semantic key.</summary>
    public static string Get(string key, string? language = null)
    {
        string lang = language ?? _language;
        IReadOnlyDictionary<string, string> map = MapFor(lang);
        return map.TryGetValue(key, out string? value) ? value : key;
    }

    /// <summary>Formats a translated value by a stable semantic key.</summary>
    public static string Format(string key, string? language = null, params object[] args)
    {
        return string.Format(FormatCulture(language ?? _language), Get(key, language), args);
    }

    public static string T(string source, string? language = null)
    {
        string lang = language ?? _language;
        return TranslateText(source, MapFor(lang));
    }

    public static string SystemValue(string value, string? language = null)
    {
        string lang = language ?? _language;
        if (lang is "ru" or "ru-RU" || string.IsNullOrEmpty(value)) return value;
        bool zh = lang == "zh-CN";
        string result = T(value, lang)
            .Replace("64-разрядная", zh ? "64 位" : "64-bit", StringComparison.Ordinal)
            .Replace("32-разрядная", zh ? "32 位" : "32-bit", StringComparison.Ordinal)
            .Replace("нет данных — нужны права администратора и поддержка SPD", zh ? "无数据——需要管理员权限和 SPD 支持" : "No data — administrator rights and SPD support are required", StringComparison.Ordinal)
            .Replace("Тайминг", zh ? "时序" : "Timing", StringComparison.Ordinal)
            .Replace("ГБ", "GB", StringComparison.Ordinal)
            .Replace("МБ", "MB", StringComparison.Ordinal)
            .Replace("МГц", "MHz", StringComparison.Ordinal)
            .Replace("Вт", "W", StringComparison.Ordinal)
            .Replace(" В", " V", StringComparison.Ordinal)
            .Replace("бит", zh ? "位" : "bits", StringComparison.Ordinal)
            .Replace("об/мин", "RPM", StringComparison.Ordinal)
            .Replace("мс", "ms", StringComparison.Ordinal)
            .Replace("байт", zh ? "字节" : "bytes", StringComparison.Ordinal)
            .Replace("системный", zh ? "系统" : "system", StringComparison.Ordinal)
            .Replace("свободно", zh ? "可用" : "free", StringComparison.Ordinal)
            .Replace("здоров", zh ? "正常" : "healthy", StringComparison.Ordinal)
            .Replace("включена", zh ? "已启用" : "enabled", StringComparison.Ordinal)
            .Replace("выключена", zh ? "已禁用" : "disabled", StringComparison.Ordinal)
            .Replace("неизвестный слот", zh ? "未知插槽" : "unknown slot", StringComparison.Ordinal)
            .Replace("нет данных", zh ? "无数据" : "No data", StringComparison.Ordinal)
            .Replace("не определён", zh ? "未检测到" : "not detected", StringComparison.Ordinal)
            .Replace("не найдены", zh ? "未找到" : "not found", StringComparison.Ordinal);
        if (string.Equals(result.Trim(), "да", StringComparison.OrdinalIgnoreCase)) return zh ? "是" : "yes";
        if (string.Equals(result.Trim(), "нет", StringComparison.OrdinalIgnoreCase)) return zh ? "否" : "no";
        return result;
    }

    public static string StressText(string key, string source)
    {
        string translated = T(source);
        if (_language is "ru" or "ru-RU" || !translated.Any(c => c is >= '\u0400' and <= '\u04ff'))
            return translated;

        string id = key.Replace("stress.", string.Empty, StringComparison.Ordinal)
            .Replace(".name", string.Empty, StringComparison.Ordinal)
            .Replace(".description", string.Empty, StringComparison.Ordinal);
        string readable = string.Join(" ", id.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
        return key.EndsWith(".description", StringComparison.Ordinal)
            ? $"Stress test: {readable}."
            : $"Stress test — {readable}";
    }
    public static string Interval(int milliseconds, string? language = null)
    {
        string lang = language ?? _language;
        string unit = lang == "zh-CN" ? "秒" : lang is "ru" or "ru-RU" ? "с" : "s";
        return (milliseconds / 1000d).ToString("0.#", FormatCulture(lang)) + " " + unit;
    }
    public static string FormatNumber(double value, string? language = null, string format = "0.#") => value.ToString(format, FormatCulture(language ?? _language));
    public static string FormatTemperature(double value, string? language = null) => FormatNumber(value, language) + " °C";
    public static string FormatDateTime(DateTime value, string? language = null)
    {
        string lang = language ?? _language;
        string pattern = lang == "en-US" ? "M/d/yyyy h:mm:ss tt" : lang is "ru" or "ru-RU" ? "dd.MM.yyyy HH:mm:ss" : "dd/MM/yyyy HH:mm:ss";
        return value.ToString(pattern, FormatCulture(lang));
    }
    private static CultureInfo FormatCulture(string language) => language == "en-US" ? CultureInfo.GetCultureInfo("en-US") : language == "en-GB" ? CultureInfo.GetCultureInfo("en-GB") : language == "zh-CN" ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.GetCultureInfo("ru-RU");
    public static string Shown(int shown, int total, int withData)
    {
        if (_language == "zh-CN") return $"{shown} 个传感器（{withData} 个有数据）";
        if (_language is "ru" or "ru-RU") return $"{shown} {RussianPlural(shown, "датчик", "датчика", "датчиков")} ({withData} с данными)";
        return $"{shown} {(_language == "en-GB" ? "sensors" : "sensors")} ({withData} with data)";
    }

    private static string RussianPlural(int value, string one, string few, string many)
    {
        int n = Math.Abs(value) % 100;
        int last = n % 10;
        return n is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many;
    }
    public static string ChartSelected(string hardware, string type, string sensor) => T("График", _language) + $": {hardware} · {T(type, _language)}: {sensor}";
    public static void Apply(DependencyObject root, string language)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        Volatile.Write(ref _language, language);
        IReadOnlyDictionary<string, string>? map = MapFor(language);
        Visit(root, map, new HashSet<DependencyObject>());

        if (!RefreshTimers.TryGetValue(root, out _))
        {
            RefreshState state = new(root);
            DispatcherTimer timer = new(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                if (!state.Root.TryGetTarget(out DependencyObject? current) ||
                    current is DispatcherObject { Dispatcher.HasShutdownStarted: true })
                {
                    StopRefresh(state, current);
                    return;
                }

                IReadOnlyDictionary<string, string>? currentMap = MapFor(Language);
                Visit(current, currentMap, new HashSet<DependencyObject>());
            }, root.Dispatcher);
            state.Timer = timer;
            RefreshTimers.Add(root, state);
            timer.Start();
        }
    }

    /// <summary>Останавливает фоновое обновление локализации для закрываемого корня.</summary>
    public static void Release(DependencyObject root)
    {
        if (RefreshTimers.TryGetValue(root, out RefreshState? state))
        {
            StopRefresh(state, root);
        }
    }

    private static void StopRefresh(RefreshState state, DependencyObject? root)
    {
        state.Timer?.Stop();
        state.Timer = null;
        if (root is not null)
            RefreshTimers.Remove(root);
    }
    private static void Visit(DependencyObject node, IReadOnlyDictionary<string,string>? map, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node) || node is ComboBox { Name: "LanguageBox" or "ThemeBox" }) return;
        if (node is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty))
            text.Text = Translate(text, text.Text, map);
        if (node is ContentControl content && content.Content is string value &&
            !BindingOperations.IsDataBound(content, ContentControl.ContentProperty))
            content.Content = Translate(content, value, map);
        if (node is HeaderedContentControl header && header.Header is string headerValue &&
            !BindingOperations.IsDataBound(header, HeaderedContentControl.HeaderProperty))
            header.Header = Translate(header, headerValue, map);
        if (node is DataGrid grid && map is not null) foreach (DataGridColumn column in grid.Columns) if (column.Header is string columnHeader) column.Header = TranslateText(columnHeader, map);
        if (node is ComboBox box)
        {
            foreach (object item in box.Items)
                if (item is DependencyObject child) Visit(child, map, visited);
        }
        foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) Visit(d, map, visited);
        if (node is Visual or Visual3D) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i), map, visited);
    }
    private static string Translate(DependencyObject node, string value, IReadOnlyDictionary<string,string>? map)
    {
        OriginalText stored = Originals.GetValue(node, _ => new OriginalText(value));
        string original;
        if (stored.HasApplied && string.Equals(value, stored.LastApplied, StringComparison.Ordinal))
        {
            original = stored.Value;
        }
        else
        {
            // A dynamic value (for example the monitoring footer) may change
            // after the view was first localized. Treat that new value as the
            // source instead of restoring the text that happened to be there
            // during initialization.
            original = CanonicalSource(value, value);
            stored.Value = original;
        }

        string translated = map is null ? original : TranslateText(original, map);
        stored.LastApplied = translated;
        stored.HasApplied = true;
        return translated;
    }

    private static string CanonicalSource(string current, string stored)
    {
        if (current.Any(c => c is >= '\u0400' and <= '\u04ff')) return current;
        foreach (var pair in EnUs) if (pair.Value == current) return pair.Key;
        foreach (var pair in EnGb) if (pair.Value == current) return pair.Key;
        foreach (var pair in Zh) if (pair.Value == current) return pair.Key;
        return stored;
    }

    private static string TranslateText(string value, IReadOnlyDictionary<string, string> map)
    {
        if (map.TryGetValue(value, out string? exact)) return exact;
        if (string.IsNullOrWhiteSpace(value) || !value.Any(c => c is >= '\u0400' and <= '\u04ff')) return value;

        // Translate the longest known phrases first. This keeps labels embedded in
        // descriptions localizable without adding another hardcoded UI branch.
        string result = value;
        foreach (var pair in map.OrderByDescending(p => p.Key.Length))
        {
            if (pair.Key.Length < 2 || !result.Contains(pair.Key, StringComparison.Ordinal)) continue;
            result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        // Finally translate individual Russian words from the shared fallback map.
        // New UI text should still be added to the JSON files; this fallback prevents
        // one missed compound label from leaving Cyrillic fragments on screen.
        // The Russian map is the source language: word substitution there would turn
        // Russian statuses into mixed English/Russian text.
        IReadOnlyDictionary<string, string>? fallback =
            ReferenceEquals(map, Zh) ? LooseZh : ReferenceEquals(map, Ru) ? null : LooseEn;
        if (fallback is null) return result;
        return System.Text.RegularExpressions.Regex.Replace(result, "[А-Яа-яЁё]+", match =>
            fallback.TryGetValue(match.Value, out string? translated) ? translated : match.Value);
    }
}
