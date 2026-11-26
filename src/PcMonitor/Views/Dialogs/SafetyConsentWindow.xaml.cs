using System.Windows;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Окно предупреждения о рисках нагрузочных тестов.</summary>
public partial class SafetyConsentWindow : Window
{
    private string _language;
    private readonly bool _acceptedInfoOnly;

    /// <summary>Признак того, что пользователь принял условия.</summary>
    public bool Accepted { get; private set; }

    /// <summary>Язык, выбранный в окне.</summary>
    public string SelectedLanguage => _language;

    public SafetyConsentWindow(string language, bool acceptedInfoOnly = false)
    {
        _language = NormalizeLanguage(language);
        _acceptedInfoOnly = acceptedInfoOnly;
        InitializeComponent();
        if (_acceptedInfoOnly)
        {
            LanguageButton.Visibility = Visibility.Collapsed;
            RestrictionText.Visibility = Visibility.Collapsed;
            DeclineButton.Visibility = Visibility.Collapsed;
            AcceptButton.Visibility = Visibility.Collapsed;
            AcceptedText.Visibility = Visibility.Visible;
        }

        ApplyLanguage(_language);
    }

    private static string NormalizeLanguage(string language) => language switch
    {
        "en-GB" or "en-US" or "zh-CN" or "ru" => language,
        _ => "ru",
    };

    private void ApplyLanguage(string language)
    {
        Title = LocalizationManager.Get(LocalizationKeys.ConsentTitle, language);
        TitleText.Text = LocalizationManager.Get(
            _acceptedInfoOnly ? LocalizationKeys.ConsentHeaderInfo : LocalizationKeys.ConsentHeader, language);
        MessageText.Text = LocalizationManager.Get(LocalizationKeys.ConsentMessage, language);
        DisclaimerText.Text = LocalizationManager.Get(LocalizationKeys.ConsentDisclaimer, language);
        RestrictionText.Text = LocalizationManager.Get(LocalizationKeys.ConsentRestriction, language);
        AcceptedText.Text = LocalizationManager.Get(LocalizationKeys.ConsentAccepted, language);
        AcceptButton.Content = LocalizationManager.Get(LocalizationKeys.ConsentAccept, language);
        DeclineButton.Content = LocalizationManager.Get(LocalizationKeys.ConsentDecline, language);
        LanguageButton.Content = LocalizationManager.Get(LocalizationKeys.ConsentLanguage, language);
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _language = _language switch
        {
            "ru" => "en-GB",
            "en-GB" => "en-US",
            "en-US" => "zh-CN",
            _ => "ru",
        };
        ApplyLanguage(_language);
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Accepted && DialogResult is null)
            DialogResult = false;
        base.OnClosing(e);
    }
}
