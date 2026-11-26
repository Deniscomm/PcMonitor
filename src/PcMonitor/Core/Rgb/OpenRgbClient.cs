using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace PcMonitor.Core.Rgb;

/// <summary>Устройство подсветки, которым можно управлять через OpenRGB.</summary>
public sealed record OpenRgbDevice(
    int Index,
    string Name,
    string Description,
    int LedCount,
    int ZoneCount,
    uint DirectModeValue,
    bool SupportsDirect,
    IReadOnlyList<string> Modes)
{
    public string Display => Description.Length > 0 ? $"{Name} ({Description})" : Name;
}

/// <summary>
/// Клиент OpenRGB SDK: позволяет приложению самому управлять подсветкой
/// (цвета, режимы) вместо «проверки на глаз». OpenRGB умеет работать с платами,
/// памятью, лентами ARGB и периферией, а его сервер принимает команды по сети.
///
/// Протокол: заголовок "ORGB", номер устройства, номер пакета и размер данных,
/// все числа — 32-битные little-endian.
/// </summary>
public static class OpenRgbClient
{
    private const string Magic = "ORGB";
    private const uint PacketRequestControllerCount = 0;
    private const uint PacketRequestControllerData = 1;
    private const uint PacketRequestProtocolVersion = 40;
    private const uint PacketSetClientName = 50;
    private const uint PacketResizeZone = 1000;
    private const uint PacketUpdateLeds = 1050;
    private const uint PacketSetCustomMode = 1100;

    public const int DefaultPort = 6742;

    /// <summary>Сервер OpenRGB отвечает на порту 6742.</summary>
    public static bool IsServerRunning(int port = DefaultPort)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(600)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Читает список устройств и их возможности.</summary>
    public static (IReadOnlyList<OpenRgbDevice> Devices, string Diagnostics) Enumerate(int port = DefaultPort)
    {
        var empty = (IReadOnlyList<OpenRgbDevice>)Array.Empty<OpenRgbDevice>();

        try
        {
            using var connection = Connect(port);
            if (connection is null) return (empty, "Сервер OpenRGB не отвечает на порту " + port);

            uint version = ReadProtocolVersion(connection, out string versionNote);
            int count = ReadControllerCount(connection);
            if (count <= 0) return (empty, "OpenRGB не сообщил ни одного устройства. " + versionNote);

            var devices = new List<OpenRgbDevice>();
            for (int index = 0; index < count; index++)
            {
                byte[]? data = Request(connection, (uint)index, PacketRequestControllerData, Array.Empty<byte>());
                if (data is null) continue;

                OpenRgbDevice? device = ParseController(index, data, version);
                if (device is not null) devices.Add(device);
            }

            return (devices, $"OpenRGB SDK версии {version}: устройств {devices.Count}");
        }
        catch (Exception ex)
        {
            return (empty, "Ошибка связи с OpenRGB: " + ex.Message);
        }
    }

    /// <summary>Устанавливает цвет всем светодиодам устройства (или только указанной зоны).</summary>
    public static string SetColor(OpenRgbDevice device, byte red, byte green, byte blue, int port = DefaultPort)
    {
        try
        {
            using var connection = Connect(port);
            if (connection is null) return "Сервер OpenRGB не отвечает";

            if (!device.SupportsDirect)
                return $"{device.Display}: у устройства нет режима прямого управления цветом";

            // Переключаемся в режим «Direct» и отправляем цвета всех светодиодов.
            byte[] modePayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(modePayload, device.DirectModeValue);
            Request(connection, (uint)device.Index, PacketSetCustomMode, modePayload);

            byte[] leds = new byte[device.LedCount * 4];
            for (int i = 0; i < device.LedCount; i++)
            {
                int offset = i * 4;
                leds[offset] = red;
                leds[offset + 1] = green;
                leds[offset + 2] = blue;
                leds[offset + 3] = 0;
            }

            byte[]? answer = Request(connection, (uint)device.Index, PacketUpdateLeds, leds);
            return answer is null
                ? $"{device.Display}: сервер не подтвердил установку цвета"
                : $"{device.Display}: цвет задан ({device.LedCount} светодиодов)";
        }
        catch (Exception ex)
        {
            return $"{device.Display}: ошибка — {ex.Message}";
        }
    }

    /// <summary>
    /// Печатает сырые данные описания устройства: шестнадцатеричный дамп и строки.
    /// Нужно, чтобы точно определить раскладку протокола у конкретной версии OpenRGB.
    /// </summary>
    public static string DumpDevice(int index, int port = DefaultPort)
    {
        var text = new StringBuilder();
        try
        {
            using var connection = Connect(port);
            if (connection is null) return "Сервер OpenRGB не отвечает";

            byte[]? data = Request(connection, (uint)index, PacketRequestControllerData, Array.Empty<byte>());
            if (data is null) return "Устройство не ответило";

            text.AppendLine($"Устройство {index}: получено {data.Length} байт");
            text.AppendLine("Строки, найденные в данных:");
            foreach (string found in ExtractStrings(data)) text.AppendLine("  " + found);

            text.AppendLine("Первые 320 байт:");
            for (int offset = 0; offset < Math.Min(320, data.Length); offset += 16)
            {
                var line = new StringBuilder($"  {offset:X4}: ");
                for (int i = 0; i < 16 && offset + i < data.Length; i++) line.Append($"{data[offset + i]:X2} ");
                line.Append("  ");
                for (int i = 0; i < 16 && offset + i < data.Length; i++)
                {
                    byte value = data[offset + i];
                    line.Append(value >= 32 && value < 127 ? (char)value : '.');
                }

                text.AppendLine(line.ToString());
            }

            text.AppendLine("32-битные значения подряд (смещение: значение):");
            for (int offset = 0; offset + 4 <= Math.Min(320, data.Length); offset += 4)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                text.Append($"{offset:X3}:{value} ");
                if ((offset / 4 + 1) % 8 == 0) text.AppendLine();
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("Ошибка: " + ex.Message);
        }

        return text.ToString();
    }

    private static IEnumerable<string> ExtractStrings(byte[] data)
    {
        var results = new List<string>();

        for (int offset = 0; offset + 2 < data.Length; offset++)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            if (length is < 2 or > 64) continue;
            if (offset + 2 + length > data.Length) continue;

            bool printable = true;
            for (int i = 0; i < length; i++)
            {
                byte value = data[offset + 2 + i];
                if (value != 0 && (value < 32 || value > 126))
                {
                    printable = false;
                    break;
                }
            }

            if (!printable) continue;

            string candidate = Encoding.ASCII.GetString(data, offset + 2, length).TrimEnd('\0');
            if (candidate.Length < 2) continue;
            if (results.Contains(candidate)) continue;

            results.Add($"[смещение {offset}] len={length}: {candidate}");
        }

        return results;
    }

    /// <summary>
    /// Устанавливает цвет устройству через штатный командный интерфейс OpenRGB.
    /// Так надёжнее, чем собирать двоичные пакеты: команда обрабатывается самим OpenRGB.
    /// </summary>
    public static string SetColorByCommand(OpenRgbDevice device, byte red, byte green, byte blue)
    {
        string? path = OpenRgbLauncher.Path;
        if (path is null) return "OpenRGB не найден — управление подсветкой недоступно";

        try
        {
            string color = $"{red:X2}{green:X2}{blue:X2}";
            var info = new System.Diagnostics.ProcessStartInfo(path,
                $"--device {device.Index} --mode Direct --color {color}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            };

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(20000);

            return $"{device.Display}: задан цвет {color}";
        }
        catch (Exception ex)
        {
            return $"{device.Display}: ошибка — {ex.Message}";
        }
    }

    /// <summary>Устанавливает режим подсветки по названию (например, «Breathing»).</summary>
    public static string SetModeByCommand(OpenRgbDevice device, string mode)
    {
        string? path = OpenRgbLauncher.Path;
        if (path is null) return "OpenRGB не найден — управление подсветкой недоступно";

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(path, $"--device {device.Index} --mode \"{mode}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            };

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(20000);

            return $"{device.Display}: включён режим «{mode}»";
        }
        catch (Exception ex)
        {
            return $"{device.Display}: ошибка — {ex.Message}";
        }
    }

    // ============================ Протокол ============================

    private static TcpClient? Connect(int port)
    {
        try
        {
            var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2)))
            {
                client.Dispose();
                return null;
            }

            client.NoDelay = true;
            byte[] name = Encoding.ASCII.GetBytes("Мониторинг ПК");

            // На представление клиента сервер не отвечает — ответ не ждём.
            Request(client, 0, PacketSetClientName, name, expectAnswer: false);
            return client;
        }
        catch
        {
            return null;
        }
    }

    private static uint ReadProtocolVersion(TcpClient client, out string note)
    {
        note = string.Empty;
        byte[]? answer = Request(client, 0, PacketRequestProtocolVersion, Array.Empty<byte>());
        if (answer is null || answer.Length < 4)
        {
            note = "Версия протокола не определена, используется раскладка по умолчанию.";
            return 3;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(answer);
    }

    private static int ReadControllerCount(TcpClient client)
    {
        byte[]? answer = Request(client, 0, PacketRequestControllerCount, Array.Empty<byte>());
        return answer is null || answer.Length < 4 ? 0 : (int)BinaryPrimitives.ReadUInt32LittleEndian(answer);
    }

    /// <summary>Отправляет пакет и читает ответ (для команд без ответа возвращает пустой массив).</summary>
    private static byte[]? Request(TcpClient client, uint deviceIndex, uint packetId, byte[] payload, bool expectAnswer = true)
    {
        NetworkStream stream = client.GetStream();

        byte[] header = new byte[16];
        Encoding.ASCII.GetBytes(Magic).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);

        stream.Write(header);
        if (payload.Length > 0) stream.Write(payload);
        stream.Flush();

        if (!expectAnswer) return Array.Empty<byte>();

        byte[] responseHeader = ReadExactly(stream, 16);
        if (responseHeader.Length < 16) return null;

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(12));
        if (size == 0) return Array.Empty<byte>();
        if (size > 16 * 1024 * 1024) return null;      // защита от мусора в потоке

        return ReadExactly(stream, (int)size);
    }

    private static byte[] ReadExactly(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;

        stream.ReadTimeout = 3000;
        while (read < count)
        {
            int portion = stream.Read(buffer, read, count - read);
            if (portion <= 0) break;
            read += portion;
        }

        return read == count ? buffer : buffer[..read];
    }

    /// <summary>
    /// Разбор описания устройства. Раскладка OpenRGB отличается между версиями SDK,
    /// поэтому имена и режимы берём из текстовых полей, а не по жёстким смещениям.
    /// </summary>
    private static OpenRgbDevice? ParseController(int index, byte[] data, uint version)
    {
        if (data.Length < 12) return null;

        List<string> strings = ExtractStrings(data).ToList();

        // Имя устройства — первая строка в данных (при её отсутствии берём запасное).
        string name = "Устройство " + index;
        foreach (string candidate in strings)
        {
            int colon = candidate.IndexOf(':');
            string text = colon >= 0 ? candidate[(colon + 1)..].Trim() : candidate.Trim();
            if (text.Length >= 3)
            {
                name = text;
                break;
            }
        }

        // Режимы ищем по известным названиям OpenRGB: так они читаются при любой версии.
        string[] knownModes =
        {
            "Direct", "Off", "Static", "Breathing", "Flashing", "Spectrum Cycle", "Rainbow",
            "Color Cycle", "Rainbow Wave", "Chase", "Chase Fade", "Wave", "Marquee", "Stack",
            "Starry Night", "Random", "Double Fade", "Fade", "Spectrum Wave", "Rainbow Breathing",
            "Rainbow Flashing", "Rainbow Chase", "Rainbow Marquee", "Rainbow Stack", "Rainbow Starry Night",
        };

        var modes = new List<string>();
        foreach (string candidate in strings)
        {
            int colon = candidate.IndexOf(':');
            string text = colon >= 0 ? candidate[(colon + 1)..].Trim() : candidate.Trim();

            foreach (string mode in knownModes)
            {
                if (string.Equals(text, mode, StringComparison.OrdinalIgnoreCase) && !modes.Contains(mode))
                    modes.Add(mode);
            }
        }

        bool direct = modes.Contains("Direct");
        return new OpenRgbDevice(index, name, string.Empty, 0, 0, 0, direct, modes);
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length) throw new InvalidOperationException("Данные устройства короче ожидаемого");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length) throw new InvalidOperationException("Строка выходит за границы данных");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        offset += 2;

        if (length == 0) return string.Empty;
        if (offset + length > data.Length) throw new InvalidOperationException("Строка выходит за границы данных");

        string text = Encoding.UTF8.GetString(data, offset, length).TrimEnd('\0');
        offset += length;
        return text;
    }
}
