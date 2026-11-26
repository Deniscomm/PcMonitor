using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PcMonitor.Core.Testing;

/// <summary>Краткая запись для истории авто-тестов.</summary>
public sealed record AutoTestHistoryEntry(
    DateTime Started,
    double DurationSeconds,
    int Score,
    int Good,
    int Attention,
    int Bad,
    string Summary);

/// <summary>Запись отчётов авто-теста: HTML, текстовый файл и история.</summary>
public static class HtmlReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string HistoryPath => Path.Combine(AppSettings.HistoryDirectory, "autotests.json");

    /// <summary>Сохраняет отчёт в HTML и текстовом виде, добавляет запись в историю.</summary>
    public static string Save(AutoTestReport report)
    {
        string stamp = report.Started.ToString("yyyyMMdd-HHmmss");
        string htmlPath = Path.Combine(AppSettings.ReportsDirectory, $"autotest-{stamp}.html");
        string textPath = Path.Combine(AppSettings.ReportsDirectory, $"autotest-{stamp}.txt");

        File.WriteAllText(htmlPath, BuildHtml(report), new UTF8Encoding(true));
        File.WriteAllText(textPath, BuildText(report), new UTF8Encoding(true));
        AppendHistory(report);
        return htmlPath;
    }

    private static void AppendHistory(AutoTestReport report)
    {
        try
        {
            List<AutoTestHistoryEntry> history = LoadHistory();
            history.Insert(0, new AutoTestHistoryEntry(report.Started, report.Duration.TotalSeconds,
                report.Score, report.GoodCount, report.AttentionCount, report.BadCount, report.Summary));

            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history.Take(50).ToList(), JsonOptions));
        }
        catch
        {
            // История — не критичные данные.
        }
    }

    public static List<AutoTestHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<AutoTestHistoryEntry>>(File.ReadAllText(HistoryPath), JsonOptions)
                       ?? new List<AutoTestHistoryEntry>();
        }
        catch
        {
            // Повреждённая история игнорируется.
        }

        return new List<AutoTestHistoryEntry>();
    }

    // ============================ HTML ============================

    private static string BuildHtml(AutoTestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>Авто-тест компьютера — Мониторинг ПК</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{background:#0f1117;color:#e8ecf4;font-family:Segoe UI,Arial,sans-serif;margin:0;padding:28px;}");
        sb.AppendLine("h1{font-size:22px;margin:0 0 6px;} h2{font-size:17px;margin:26px 0 10px;color:#bcd0f0;}");
        sb.AppendLine(".sub{color:#8c97ac;font-size:13px;margin-bottom:18px;}");
        sb.AppendLine(".card{background:#191d27;border:1px solid #2c3344;border-radius:10px;padding:16px;margin:12px 0;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px;}");
        sb.AppendLine("th,td{text-align:left;padding:7px 10px;border-bottom:1px solid #232a3a;}");
        sb.AppendLine("th{color:#8c97ac;font-weight:600;}");
        sb.AppendLine(".badge{display:inline-block;padding:2px 9px;border-radius:10px;font-size:12px;font-weight:600;}");
        sb.AppendLine(".good{background:#123a28;color:#41d18a;} .attention{background:#3a2a10;color:#ffb020;}");
        sb.AppendLine(".bad{background:#3a1418;color:#ff5f6d;} .info{background:#1d2735;color:#8fb6ff;}");
        sb.AppendLine(".score{font-size:44px;font-weight:700;color:#4c9aff;}");
        sb.AppendLine(".muted{color:#8c97ac;} pre{white-space:pre-wrap;font-size:12px;color:#9aa5bb;}");
        sb.AppendLine("details{margin-top:8px;} summary{cursor:pointer;color:#8c97ac;}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Авто-тест компьютера</h1>");
        sb.AppendLine($"<div class=\"sub\">{E(report.Machine)} · пользователь {E(report.Operator)} · " +
                      $"{report.Started:dd.MM.yyyy HH:mm:ss} · длительность {report.Duration:mm\\:ss} · " +
                      $"права администратора: {(report.Elevated ? "да" : "нет")}</div>");

        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"<div class=\"score\">{report.Score}/100</div>");
        sb.AppendLine($"<div>{E(report.Summary)}</div>");
        sb.AppendLine($"<div class=\"muted\" style=\"margin-top:8px\">Хорошо: {report.GoodCount} · " +
                      $"обратить внимание: {report.AttentionCount} · проблемы: {report.BadCount}</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Выводы по комплектующим</h2>");
        sb.AppendLine("<div class=\"card\"><table><tr><th>Статус</th><th>Комплектующая</th><th>Что именно</th>" +
                      "<th>Подробности</th><th>Что делать</th></tr>");
        foreach (Verdict verdict in report.Verdicts)
        {
            sb.AppendLine($"<tr><td><span class=\"badge {Class(verdict.Level)}\">{E(verdict.LevelText)}</span></td>" +
                          $"<td>{E(verdict.Component)}</td><td>{E(verdict.Title)}</td>" +
                          $"<td>{E(verdict.Detail)}</td><td>{E(verdict.Recommendation)}</td></tr>");
        }

        sb.AppendLine("</table></div>");

        sb.AppendLine("<h2>Состав системы</h2>");
        sb.AppendLine("<div class=\"card\"><table><tr><th>Комплектующая</th><th>Модель</th><th>Характеристики</th><th>Обнаружено</th></tr>");
        foreach (ComponentInfo component in report.Components)
        {
            sb.AppendLine($"<tr><td>{E(KindName(component.Kind))}</td><td>{E(component.Name)}</td>" +
                          $"<td>{E(component.Details)}</td><td>{(component.Present ? "да" : "нет")}</td></tr>");
        }

        sb.AppendLine("</table></div>");

        sb.AppendLine("<h2>Замеры по фазам</h2>");
        foreach (AutoTestPhase phase in report.Phases)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<b>{E(phase.Name)}</b> <span class=\"muted\">— {E(phase.Description)}, " +
                          $"{phase.Duration.TotalSeconds:0} с" +
                          (phase.Errors > 0 ? $", <span class=\"badge bad\">ошибок {phase.Errors}</span>" : string.Empty) +
                          "</span>");
            if (!string.IsNullOrWhiteSpace(phase.Notes))
                sb.AppendLine($"<div class=\"muted\" style=\"margin:6px 0 10px\">{E(phase.Notes)}</div>");

            if (phase.Metrics.Count > 0)
            {
                sb.AppendLine("<table><tr><th>Датчик</th><th>Минимум</th><th>Среднее</th><th>Максимум</th></tr>");
                foreach (MetricSummary metric in phase.Metrics.Values.Where(m => m.HasData))
                {
                    sb.AppendLine($"<tr><td>{E(metric.Name)}</td><td>{E(metric.MinText)}</td>" +
                                  $"<td>{E(metric.AverageText)}</td><td>{E(metric.MaxText)}</td></tr>");
                }

                sb.AppendLine("</table>");
            }
            else
            {
                sb.AppendLine("<div class=\"muted\">Замеров нет.</div>");
            }

            if (phase.Log.Count > 0)
            {
                sb.AppendLine("<details><summary>Подробности фазы</summary><pre>");
                foreach (string line in phase.Log) sb.AppendLine(E(line));
                sb.AppendLine("</pre></details>");
            }

            sb.AppendLine("</div>");
        }

        sb.AppendLine("<h2>Журнал</h2><div class=\"card\"><pre>");
        foreach (string line in report.Log) sb.AppendLine(E(line));
        sb.AppendLine("</pre></div>");

        sb.AppendLine("<div class=\"sub\">Отчёт создан приложением «Мониторинг ПК».</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string BuildText(AutoTestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("АВТО-ТЕСТ КОМПЬЮТЕРА — Мониторинг ПК");
        sb.AppendLine(new string('=', 100));
        sb.AppendLine($"Дата: {report.Started:dd.MM.yyyy HH:mm:ss}   Длительность: {report.Duration:mm\\:ss}");
        sb.AppendLine($"Машина: {report.Machine}   Права администратора: {(report.Elevated ? "да" : "нет")}");
        sb.AppendLine($"Оценка: {report.Score}/100 — {report.Summary}");
        sb.AppendLine();

        sb.AppendLine("ВЫВОДЫ");
        sb.AppendLine(new string('-', 100));
        foreach (Verdict verdict in report.Verdicts)
        {
            sb.AppendLine($"[{verdict.LevelText.ToUpperInvariant()}] {verdict.Component}: {verdict.Title}");
            sb.AppendLine($"    {verdict.Detail}");
            sb.AppendLine($"    Совет: {verdict.Recommendation}");
        }

        sb.AppendLine();
        sb.AppendLine("СОСТАВ СИСТЕМЫ");
        sb.AppendLine(new string('-', 100));
        foreach (ComponentInfo component in report.Components)
            sb.AppendLine($"{KindName(component.Kind),-16}{component.Name,-40}{component.Details}");

        foreach (AutoTestPhase phase in report.Phases)
        {
            sb.AppendLine();
            sb.AppendLine($"ФАЗА: {phase.Name} ({phase.Description}) — {phase.Duration.TotalSeconds:0} с, ошибок {phase.Errors}");
            sb.AppendLine(new string('-', 100));
            if (!string.IsNullOrWhiteSpace(phase.Notes)) sb.AppendLine(phase.Notes);

            foreach (MetricSummary metric in phase.Metrics.Values.Where(m => m.HasData))
                sb.AppendLine($"  {metric.Name,-32} мин {metric.MinText,-14} сред {metric.AverageText,-14} макс {metric.MaxText}");
        }

        sb.AppendLine();
        sb.AppendLine("ЖУРНАЛ");
        sb.AppendLine(new string('-', 100));
        foreach (string line in report.Log) sb.AppendLine(line);

        return sb.ToString();
    }

    private static string Class(VerdictLevel level) => level switch
    {
        VerdictLevel.Good => "good",
        VerdictLevel.Attention => "attention",
        VerdictLevel.Bad => "bad",
        _ => "info",
    };

    private static string KindName(ComponentKind kind) => kind switch
    {
        ComponentKind.Cpu => "Процессор",
        ComponentKind.Gpu => "Видеокарта",
        ComponentKind.Ram => "Оперативная память",
        ComponentKind.StorageNvme => "SSD NVMe",
        ComponentKind.StorageSata => "SSD/накопитель SATA",
        ComponentKind.StorageHdd => "Жёсткий диск",
        ComponentKind.Motherboard => "Мат. плата",
        ComponentKind.Fans => "Вентиляторы",
        ComponentKind.Npu => "NPU",
        ComponentKind.Network => "Сеть",
        ComponentKind.Rgb => "Подсветка",
        _ => kind.ToString(),
    };

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string E(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
