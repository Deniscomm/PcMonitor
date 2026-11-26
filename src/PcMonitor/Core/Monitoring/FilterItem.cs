using System.ComponentModel;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Флажок фильтра по типу датчика.</summary>
public sealed class FilterItem : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private string _label;
    private int _count = -1;

    public FilterItem(SensorType type)
    {
        Type = type;
        _label = SensorCatalog.TypeName(type);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SensorType Type { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            Raise();
        }
    }

    public string Label
    {
        get => _label;
        private set
        {
            if (_label == value) return;
            _label = value;
            Raise();
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            RefreshLanguage();
            Raise();
        }
    }

    public void RefreshLanguage()
    {
        Label = Count < 0
            ? LocalizationManager.T(SensorCatalog.TypeName(Type))
            : $"{LocalizationManager.T(SensorCatalog.TypeName(Type))} ({Count})";
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Пункт списка устройств.</summary>
public sealed class HardwareFilterItem
{
    public HardwareFilterItem(string label, string? key)
    {
        Label = label;
        Key = key;
    }

    /// <summary>Полное название устройства или null для «все устройства».</summary>
    public string? Key { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
