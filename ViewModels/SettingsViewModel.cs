using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CortexFX.ViewModels;

/// <summary>
/// ViewModel for the Settings overlay.
/// Manages the Windows context menu registration state.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isContextMenuRegistered;

    public SettingsViewModel()
    {
        // Read current state from registry
        _isContextMenuRegistered = RegistryManager.IsRegistered();
    }

    partial void OnIsContextMenuRegisteredChanged(bool value)
    {
        try
        {
            if (value)
            {
                RegistryManager.RegisterContextMenu();
            }
            else
            {
                RegistryManager.UnregisterContextMenu();
            }
        }
        catch (Exception ex)
        {
            // Revert on failure
            _isContextMenuRegistered = !value;
            OnPropertyChanged(nameof(IsContextMenuRegistered));
            throw new InvalidOperationException($"Registry operation failed: {ex.Message}", ex);
        }
    }
}
