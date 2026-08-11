using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.ViewModels;

/// <summary>
/// Settings overlay (Windows Explorer context menu on/off).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isContextMenuRegistered;

    public SettingsViewModel()
    {
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
