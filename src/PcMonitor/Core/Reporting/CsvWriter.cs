using System.Globalization;
using System.IO;
using System.Text;

namespace PcMonitor.Core;

/// <summary>Запись показаний датчиков в CSV.</summary>
public sealed class CsvWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _headerWritten;

    public CsvWriter(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(true));
        FilePath = path;
    }

    /// <summary>Путь к файлу журнала.</summary>
    public string FilePath { get; }

    /// <summary>Одна строка на датчик: время, железо, тип, датчик, значение, единица.</summary>
    public void WriteSnapshot(IReadOnlyList<SensorReading> readings, DateTime timestamp)
    {
        if (!_headerWritten)
        {
            _writer.WriteLine("Время;Железо;Группа;Тип;Датчик;Значение;Единица");
            _headerWritten = true;
        }

        string time = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (SensorReading r in readings)
        {
            if (r.Value is not { } value || float.IsNaN(value) || float.IsInfinity(value))
                continue;

            _writer.Write(time); _writer.Write(';');
            _writer.Write(Escape(r.Hardware)); _writer.Write(';');
            _writer.Write(Escape(r.HardwareGroup)); _writer.Write(';');
            _writer.Write(Escape(SensorCatalog.TypeName(r.SensorType))); _writer.Write(';');
            _writer.Write(Escape(r.SensorName)); _writer.Write(';');
            _writer.Write(value.ToString("0.####", CultureInfo.InvariantCulture)); _writer.Write(';');
            _writer.Write(Escape(r.Unit));
            _writer.WriteLine();
        }

        _writer.Flush();
    }

    /// <summary>Экспорт текущего состояния таблицы (по одной строке на датчик).</summary>
    public static void ExportTable(string path, IEnumerable<SensorRow> rows, DateTime timestamp)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));
        writer.WriteLine($"# Мониторинг ПК — снимок от {timestamp:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine("Железо;Группа;Тип;Датчик;Значение;Мин;Макс;Единица");
        foreach (SensorRow row in rows)
        {
            writer.Write(Escape(row.Hardware)); writer.Write(';');
            writer.Write(Escape(row.HardwareGroup)); writer.Write(';');
            writer.Write(Escape(row.TypeName)); writer.Write(';');
            writer.Write(Escape(row.Sensor)); writer.Write(';');
            writer.Write(Escape(row.ValueText)); writer.Write(';');
            writer.Write(Escape(row.MinText)); writer.Write(';');
            writer.Write(Escape(row.MaxText)); writer.Write(';');
            writer.Write(Escape(row.Unit));
            writer.WriteLine();
        }
    }

    private static string Escape(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    public void Dispose() => _writer.Dispose();
}
