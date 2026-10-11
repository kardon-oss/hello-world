using CommunityToolkit.Mvvm.ComponentModel;
using OpenMouseConfig.Core.Definitions;

namespace OpenMouseConfig.ViewModels;

/// <summary>One DPI stage row: active-stage badge, slider and numeric input.</summary>
public sealed class DpiStageViewModel : ObservableObject
{
    private readonly DpiCapability _capability;
    private readonly Action<DpiStageViewModel> _dpiChanged;
    private readonly Action<DpiStageViewModel> _activated;
    private int _dpi;
    private bool _isActive;

    public DpiStageViewModel(int number, string color, DpiCapability capability, Action<DpiStageViewModel> dpiChanged, Action<DpiStageViewModel> activated)
    {
        Number = number;
        Color = color;
        _capability = capability;
        _dpiChanged = dpiChanged;
        _activated = activated;
        _dpi = capability.Min;
    }

    /// <summary>1-based stage number.</summary>
    public int Number { get; }

    /// <summary>Stage colour (hex), like the DPI LED colours on most mice.</summary>
    public string Color { get; }

    public int Min => _capability.Min;

    public int Max => _capability.Max;

    public int Step => _capability.Step;

    /// <summary>PageUp/PageDown and track clicks jump by this much.</summary>
    public int LargeStep => _capability.Step * 8;

    /// <summary>Snapped to the device's step and range; typing 1234 with step 50 becomes 1250.</summary>
    public int Dpi
    {
        get => _dpi;
        set
        {
            var snapped = _capability.Snap(value);
            if (SetProperty(ref _dpi, snapped))
            {
                _dpiChanged(this);
            }
            else if (snapped != value)
            {
                OnPropertyChanged(); // push the coerced value back into the TextBox
            }
        }
    }

    /// <summary>Bound to the stage badge (RadioButton). Exclusivity is handled by the owner.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
            {
                _activated(this);
            }
        }
    }

    /// <summary>Updates the row from device/profile data without triggering a write.</summary>
    public void Load(int dpi, bool isActive)
    {
        _dpi = _capability.Snap(dpi);
        _isActive = isActive;
        OnPropertyChanged(nameof(Dpi));
        OnPropertyChanged(nameof(IsActive));
    }

    public void Deactivate()
    {
        if (_isActive)
        {
            _isActive = false;
            OnPropertyChanged(nameof(IsActive));
        }
    }
}
