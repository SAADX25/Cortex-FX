using System.Windows;
using CortexFX.Core.Radial;
using Microsoft.Extensions.DependencyInjection;

namespace CortexFX;

public partial class MainWindow
{
    private RadialWheelController? _radialWheel;

    private void InitializeRadialWheel()
    {
        _radialWheel = new(this, _trayIcon!, _config,
            App.Services.GetRequiredService<RadialConversionService>(),
            App.Services.GetRequiredService<RadialActionCatalog>());
    }

    private void RadialWheelSettings_Click(object sender, RoutedEventArgs e) => _radialWheel?.ShowSettings();
    internal void DisposeRadialWheel() => _radialWheel?.Dispose();
}
