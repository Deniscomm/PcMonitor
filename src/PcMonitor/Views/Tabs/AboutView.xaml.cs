using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace PcMonitor.Views;

public partial class AboutView : UserControl
{
    private const string LastUpdated = "23.11.2026";
    private const string Contact = "Deniscomm1@yandex.by";
    private const string Discord = "https://discord.gg/sfx7XxrNJu";

    public AboutView()
    {
        InitializeComponent();
    }

    public void ApplyLanguage(string language)
    {
        bool english = language is "en-US" or "en-GB";
        bool chinese = language == "zh-CN";

        AppNameSuffixText.Text = english ? " (PC Monitoring)" : chinese ? "（电脑监控）" : " (Мониторинг ПК)";
        AboutSummaryTitle.Text = english
            ? "PC monitoring and diagnostics"
            : chinese ? "电脑监控与诊断" : "Мониторинг и диагностика компьютера";
        AboutSummaryText.Text = english
            ? "PC Monitor helps you track your computer in real time, including CPU, GPU, memory, storage and fan temperatures, loads and clock speeds. It also provides detailed system information, live charts, data export and hardware testing tools."
            : chinese
                ? "PC Monitor 可实时监控处理器、显卡、内存、存储设备和风扇的温度、负载及频率，并提供详细的系统信息、实时图表、数据导出和硬件测试工具。"
                : "PC Monitor помогает следить за состоянием компьютера в реальном времени: показывает температуру, нагрузку и частоты процессора, видеокарты, памяти, накопителей и вентиляторов. Здесь доступны подробные сведения о системе, графики показателей, экспорт данных и инструменты проверки оборудования.";
        VersionText.Text = english ? "Version: 1.0.0" : chinese ? "版本：1.0.0" : "Версия: 1.0.0";
        LastUpdatedText.Text = english ? $"Last updated: {LastUpdated}" : chinese ? $"最后更新：{LastUpdated}" : $"Последнее обновление: {LastUpdated}";
        StatusText.Text = english ? "Status: Alpha" : chinese ? "状态：Alpha（内部测试版）" : "Статус: Альфа";
        ParticipantsTitleText.Text = english ? "Participants" : chinese ? "参与者" : "Участники";
        ParticipantDeveloperText.Text = english ? "Deniscomm — Developer" : chinese ? "Deniscomm — 开发者" : "Deniscomm — Разработчик";
        ParticipantProgrammerText.Text = english ? "Manus AI — Programmer" : chinese ? "Manus AI — 程序员" : "Manus AI — Программист";
        ParticipantCoauthorText.Text = english ? "DeepSeek AI — Interface co-author" : chinese ? "DeepSeek AI — 界面共同作者" : "DeepSeek AI — соавтор интерфейса";
        ContactText.Text = english ? $"Support: {Contact}" : chinese ? $"支持：{Contact}" : $"Поддержка: {Contact}";
        DiscordLinkButton.Content = english ? $"Discord: {Discord}" : chinese ? $"Discord：{Discord}" : $"Discord: {Discord}";
        DisclaimerText.Text = english
            ? "The author is not responsible for possible hardware damage or data loss resulting from the use of stress tests."
            : chinese
                ? "作者不对使用压力测试造成的设备损坏或数据丢失承担责任。"
                : "Автор не несет ответственности за возможные повреждения оборудования или потерю данных, возникшие в результате использования стресс-тестов.";
        AppNameLink.ToolTip = Discord;
    }

    private void AppNameLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ссылка остаётся доступной в подсказке, если браузер не удалось открыть.
        }
        e.Handled = true;
    }

    private void DiscordLinkButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(Discord) { UseShellExecute = true });
    }

}
