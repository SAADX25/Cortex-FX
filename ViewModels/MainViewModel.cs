using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CortexFX.ViewModels;

/// <summary>
/// Top-level navigation: which screen is visible (dashboard, convert, tools).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // Navigation

    [ObservableProperty]
    private bool _isDashboardVisible = true;

    [ObservableProperty]
    private bool _isConversionViewVisible;

    [ObservableProperty]
    private bool _isVideoCompressorVisible;

    [ObservableProperty]
    private bool _isSettingsVisible;

    [ObservableProperty]
    private string _currentToolTitle = "Select Tool";

    [ObservableProperty]
    private string _versionText = "v1.6.0";

    [ObservableProperty]
    private bool _isTopNavVisible;

    // Child ViewModels

    public ConversionViewModel Conversion { get; }
    public AudioEditorViewModel AudioEditor { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(ConversionViewModel conversion, AudioEditorViewModel audioEditor, SettingsViewModel settings)
    {
        Conversion = conversion;
        AudioEditor = audioEditor;
        Settings = settings;
    }

    // Navigation Commands

    [RelayCommand]
    private void NavigateToDashboard()
    {
        HideAllViews();
        IsDashboardVisible = true;
        IsTopNavVisible = false;
        CurrentToolTitle = "Select Tool";
    }

    [RelayCommand]
    private void NavigateToConversion(string? category)
    {
        HideAllViews();
        IsConversionViewVisible = true;
        IsTopNavVisible = true;

        if (!string.IsNullOrEmpty(category))
        {
            Conversion.CategoryFilter = category;
            Conversion.RefreshAvailableFormats();
            CurrentToolTitle = $"{category} Converter";
        }
        else
        {
            CurrentToolTitle = "Universal Converter";
        }
    }

    [RelayCommand]
    private void NavigateToVideoCompressor()
    {
        HideAllViews();
        IsVideoCompressorVisible = true;
        IsTopNavVisible = true;
        CurrentToolTitle = "Video Compressor";
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsVisible = false;
    }

    // Private

    private void HideAllViews()
    {
        IsDashboardVisible = false;
        IsConversionViewVisible = false;
        IsVideoCompressorVisible = false;
        IsSettingsVisible = false;
        AudioEditor.Close();
    }
}
