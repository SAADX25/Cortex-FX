using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CortexFX.Core.Configuration;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Views.Radial;
using Microsoft.Win32;

namespace CortexFX.Core.Radial;

public sealed class RadialWheelController : IDisposable
{
    private readonly Window _owner;
    private readonly TrayIconService _tray;
    private readonly IAppConfiguration _configuration;
    private readonly RadialConversionService _conversion;
    private readonly RadialSettings _settings = RadialSettings.Load();
    private readonly NativeDragMonitor _monitor;
    private readonly RadialWheelWindow _wheel;
    private readonly MenuItem _enabled;
    private readonly MenuItem _cancel;
    private RadialSettingsWindow? _settingsWindow;
    private CancellationTokenSource? _cancellation;
    private Window? _actionDialog;
    private bool _disposed;
    private bool _updatingMenu;

    public RadialWheelController(Window owner, TrayIconService tray, IAppConfiguration configuration,
        RadialConversionService conversion, RadialActionCatalog catalog)
    {
        _owner = owner; _tray = tray; _configuration = configuration; _conversion = conversion;
        _wheel = new(catalog, _settings);
        _monitor = new(_settings);
        _monitor.Triggered += (mode, point) => { if (!_disposed && !_wheel.Busy) _wheel.Open(mode, point); };
        _monitor.Released += _wheel.OnDragReleased;
        _wheel.ActionInvoked += Execute;
        _wheel.CancelRequested += Cancel;
        var open = new MenuItem { Header = "Open conversion wheel…" };
        open.Click += (_, _) => ChooseFiles(RadialMode.Convert);
        var settings = new MenuItem { Header = "Radial wheel settings…" };
        settings.Click += (_, _) => ShowSettings();
        _enabled = new MenuItem { Header = "Enable drag wheel", IsCheckable = true, IsChecked = _settings.Enabled };
        _enabled.Click += (_, _) =>
        {
            if (_updatingMenu) return;
            bool previous = _settings.Enabled;
            try { _settings.Enabled = _enabled.IsChecked; _settings.Save(); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            { _settings.Enabled = previous; _enabled.IsChecked = previous; _tray.Notify("Preferences could not be saved", error.Message, true); }
        };
        _cancel = new MenuItem { Header = "Cancel wheel conversion", IsEnabled = false };
        _cancel.Click += (_, _) => Cancel();
        foreach (var item in new[] { open, settings, _enabled, _cancel }) _tray.AddFeatureItem(item);
        _monitor.Start();
    }

    public void ShowSettings()
    {
        if (_disposed) return;
        _tray.RestoreFromTray();
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new(_settings, _configuration.FFmpegPath) { Owner = _owner };
        _settingsWindow.SettingsSaved += () =>
        {
            _updatingMenu = true; _enabled.IsChecked = _settings.Enabled; _updatingMenu = false;
        };
        _settingsWindow.PreviewRequested += ChooseFiles;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    public void ChooseFiles(RadialMode mode)
    {
        if (_disposed) return;
        if (_wheel.Busy) { _tray.Notify("Conversion in progress", "Wait for the current conversion or cancel it from the tray."); return; }
        var picker = new OpenFileDialog { Title = "Choose files for the Cortex FX wheel", Multiselect = true };
        if (picker.ShowDialog() != true) return;
        NativeWheel.GetCursorPos(out var point);
        try { _wheel.Open(mode, new(point.X, point.Y), picker.FileNames); }
        catch (Exception error) when (error is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
        { _tray.Notify("Unable to open files", error.Message, true); }
    }

    private async void Execute(RadialAction action, RadialSelection selection)
    {
        _cancellation = new CancellationTokenSource(); _cancel.IsEnabled = true;
        var progress = new Progress<RadialProgress>(update => { if (!_disposed && _wheel.Busy) _wheel.SetProgress(update); });
        try
        {
            if (action.Operation is RadialOperation.CompressImage or RadialOperation.CompressPdf or RadialOperation.CropImage)
            {
                // Let OLE finish the drop before opening a modal customization window.
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_disposed) return;
                _wheel.SuspendForOptions();
                if (action.Operation == RadialOperation.CropImage)
                {
                    var dialog = new RadialCropWindow(selection.Paths[0]) { Owner = _wheel, Topmost = true };
                    _actionDialog = dialog;
                    if (dialog.ShowDialog() != true) { _wheel.CancelOptions(); return; }
                    action = action with { Crop = dialog.Options };
                }
                else
                {
                    var dialog = new RadialCompressionWindow(selection) { Owner = _wheel, Topmost = true };
                    _actionDialog = dialog;
                    if (dialog.ShowDialog() != true) { _wheel.CancelOptions(); return; }
                    action = action with { Compression = dialog.Options };
                }
                _actionDialog = null;
                if (_disposed) return;
                _wheel.ResumeAfterOptions();
                _cancellation.Token.ThrowIfCancellationRequested();
            }
            var result = await _conversion.ExecuteAsync(selection, action, _settings, progress, _cancellation.Token);
            if (_disposed) return;
            string message = result.Message;
            if (result.ClipboardText is not null)
            {
                try { Clipboard.SetText(result.ClipboardText); }
                catch (ExternalException) { message = result.Outputs.Count > 0 ? "Files saved; clipboard is currently busy." : "Clipboard is busy. Try copying the paths again."; }
            }
            _wheel.Complete(message);
            ConsoleLogger.Success("Radial", $"{action.Label}: {message}");
            if (_settings.Notifications) _tray.Notify(message, result.Outputs.FirstOrDefault() ?? message);
        }
        catch (Exception error)
        {
            ConsoleLogger.Error("Radial", error.ToString());
            if (!_disposed)
            {
                _wheel.ResumeAfterOptions();
                _wheel.Fail(error.Message);
                if (_settings.Notifications) _tray.Notify("Wheel conversion stopped", error.Message, true);
            }
        }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null;
            _actionDialog = null;
            if (!_disposed) _cancel.IsEnabled = false;
        }
    }
    private void Cancel() { _cancellation?.Cancel(); _actionDialog?.Close(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _monitor.Dispose(); Cancel(); _settingsWindow?.Close(); _wheel.Dispose();
    }
}
