using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CortexFX.Core.Radial;
using CortexFX.Views.Radial;
using Xunit;

namespace CortexFX.Tests;

public sealed class RadialDragInputTests
{
    [Theory]
    [InlineData("folder")]
    [InlineData("exe")]
    [InlineData("msi")]
    [InlineData("lnk")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("mixed-folder")]
    [InlineData("mixed-exe")]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("non-file")]
    [InlineData("malformed")]
    [InlineData("unavailable-format")]
    public void InvalidDragClosesWithoutErrorAndNextValidDropWorks(string scenario) => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var wheel = fixture.Wheel();
        var surface = (RadialSurface)wheel.Content;
        int calls = 0; string? target = null;
        wheel.ActionInvoked += (action, selection) =>
        { calls++; target = action.Target; Assert.Equal(fixture.Pdf, Assert.Single(selection.Paths)); };
        Send(wheel, fixture.Data(fixture.Pdf), DragDrop.DragEnterEvent);
        Assert.NotEmpty(surface.Actions);
        wheel.Fail("Previous conversion failed");

        IDataObject invalid = scenario switch
        {
            "folder" => fixture.Data(fixture.Folder),
            "exe" or "msi" or "lnk" or "unknown" => fixture.Data(fixture.File("file." + scenario)),
            "missing" => fixture.Data(Path.Combine(fixture.Root, "missing.pdf")),
            "mixed-folder" => fixture.Data(fixture.Pdf, fixture.Folder),
            "mixed-exe" => fixture.Data(fixture.Pdf, fixture.File("application.exe")),
            "empty" => fixture.Data(),
            "too-many" => fixture.Data(Enumerable.Range(0, 101).Select(i => Path.Combine(fixture.Root, i + ".pdf")).ToArray()),
            "non-file" => new DataObject(DataFormats.Text, "dragged text"),
            "malformed" => new DataObject(DataFormats.FileDrop, 42),
            "unavailable-format" => fixture.Data(fixture.File("book.epub")),
            _ => throw new ArgumentException(scenario)
        };
        foreach (var routedEvent in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
        {
            var args = Send(wheel, invalid, routedEvent);
            Assert.Equal(DragDropEffects.None, args.Effects);
            Assert.True(args.Handled);
            AssertRejected(wheel);
            Assert.Equal(0, calls);
        }
        wheel.OnDragReleased();
        var valid = fixture.Data(fixture.Pdf);
        Assert.Equal(DragDropEffects.Copy, Send(wheel, valid, DragDrop.DragEnterEvent).Effects);
        Assert.Equal(DragDropEffects.Copy, Send(wheel, valid, DragDrop.DragOverEvent).Effects);
        Assert.Equal(DragDropEffects.Copy, Send(wheel, valid, DragDrop.DropEvent).Effects);
        Assert.True(wheel.Busy);
        Assert.Equal("working", surface.State);
        Assert.Equal(1, calls);
        Assert.Equal("docx", target);
    });

    [Theory]
    [InlineData(RadialMode.Convert, "folder")]
    [InlineData(RadialMode.Convert, "application.exe")]
    [InlineData(RadialMode.Tools, "folder")]
    [InlineData(RadialMode.Tools, "application.exe")]
    [InlineData(RadialMode.Tools, "document.docx")]
    public void OpeningUnsupportedPathsNeverShowsAnEmptyWheel(RadialMode mode, string name) => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var wheel = fixture.Wheel();
        string path = name == "folder" ? fixture.Folder : fixture.File(name);
        wheel.Open(mode, new(300, 300), [path]);
        AssertRejected(wheel);
    });

    [Fact]
    public void DropRechecksAFileThatDisappearedAfterDragEnter() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var wheel = fixture.Wheel();
        int calls = 0; wheel.ActionInvoked += (_, _) => calls++;
        var data = fixture.Data(fixture.Pdf);
        Send(wheel, data, DragDrop.DragEnterEvent);
        Send(wheel, data, DragDrop.DragOverEvent);
        File.Delete(fixture.Pdf);
        Assert.Equal(DragDropEffects.None, Send(wheel, data, DragDrop.DropEvent).Effects);
        AssertRejected(wheel);
        Assert.Equal(0, calls);
    });

    [Fact]
    public void AnotherDragCannotClearOrInterruptAnActiveConversion() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var wheel = fixture.Wheel();
        int calls = 0; wheel.ActionInvoked += (_, _) => calls++;
        var data = fixture.Data(fixture.Pdf);
        Send(wheel, data, DragDrop.DragEnterEvent);
        Send(wheel, data, DragDrop.DropEvent);
        var selection = wheel.Selection;
        var surface = (RadialSurface)wheel.Content;
        var actions = surface.Actions;
        foreach (var routedEvent in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
        {
            Assert.Equal(DragDropEffects.None, Send(wheel, fixture.Data(fixture.Folder), routedEvent).Effects);
            Assert.True(wheel.Busy);
            Assert.Equal("working", surface.State);
            Assert.Same(selection, wheel.Selection);
            Assert.Same(actions, surface.Actions);
        }
        Assert.Equal(1, calls);
    });

    [Fact]
    public void QuickActionsStillAcceptRegularFilesWithoutAConversionFormat() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var wheel = fixture.Wheel();
        wheel.Open(RadialMode.Quick, new(300, 300), [fixture.Folder]);
        AssertRejected(wheel);
        Send(wheel, fixture.Data(fixture.File("data.bin")), DragDrop.DragEnterEvent);
        Assert.Equal(new[] { RadialOperation.Zip, RadialOperation.CopyPaths, RadialOperation.Sha256 },
            ((RadialSurface)wheel.Content).Actions.Select(a => a.Operation));
    });

    private static void AssertRejected(RadialWheelWindow wheel)
    {
        var surface = (RadialSurface)wheel.Content;
        Assert.False(wheel.IsVisible);
        Assert.False(wheel.Busy);
        Assert.Null(wheel.Selection);
        Assert.Null(surface.Selection);
        Assert.Null(surface.Icon);
        Assert.Null(surface.ToolTip);
        Assert.Empty(surface.Actions);
        Assert.Equal("idle", surface.State);
        Assert.Equal("", surface.ActiveActionLabel);
        foreach (string timer in new[] { "_dismiss", "_ticker" })
            Assert.False(((DispatcherTimer)typeof(RadialWheelWindow).GetField(timer, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wheel)!).IsEnabled);
    }

    private static DragEventArgs Send(RadialWheelWindow wheel, IDataObject data, RoutedEvent routedEvent)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy,
            (DependencyObject)wheel.Content, new Point(270, 103)]);
        args.RoutedEvent = routedEvent;
        wheel.RaiseEvent(args);
        return args;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CortexFX_DragInput_" + Guid.NewGuid().ToString("N"));
        public string Folder { get; }
        public string Pdf { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Folder = Path.Combine(Root, "Folder.pdf"); Directory.CreateDirectory(Folder);
            Pdf = File("paper.pdf");
        }
        public string File(string name)
        {
            string path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "fixture; no conversion process runs in this test");
            return path;
        }
        public DataObject Data(params string[] paths)
        {
            var data = new DataObject(); data.SetData(DataFormats.FileDrop, paths, false); return data;
        }
        public RadialWheelWindow Wheel() => new(new(new FakeEngines { OptionalSupported = true, OptionalInstalled = false }.Router,
            new FakeConfig(Root)), new());
        public void Dispose() => Directory.Delete(Root, true);
    }
}