using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CortexFX.Controls;
using Xunit;

namespace CortexFX.Tests;

public class ToolCardInteractionTests
{
    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void KeyboardInvokesExistingMouseHandlerOnceOnRelease(Key key) => RunSta(() =>
    {
        var card = new ToolCard();
        int invocations = 0;
        card.MouseLeftButtonUp += (sender, args) =>
        {
            Assert.Same(card, sender);
            Assert.Equal(MouseButton.Left, args.ChangedButton);
            invocations++;
        };

        SendKeyDown(card, key);
        SendKeyDown(card, key);
        Assert.Equal(0, invocations);
        SendKeyUp(card, key);
        SendKeyUp(card, key);
        Assert.Equal(1, invocations);
    });

    [Fact]
    public void DisabledCardAndUnrelatedKeysDoNotInvoke() => RunSta(() =>
    {
        var card = new ToolCard();
        int invocations = 0;
        card.MouseLeftButtonUp += (_, _) => invocations++;
        SendKeyDown(card, Key.Escape);
        SendKeyUp(card, Key.Escape);
        SendKeyDown(card, Key.Enter);
        card.IsEnabled = false;
        SendKeyUp(card, Key.Enter);
        SendKeyDown(card, Key.Space);
        SendKeyUp(card, Key.Space);
        card.IsEnabled = true;
        SendKeyUp(card, Key.Enter);

        Assert.Equal(0, invocations);
    });

    [Fact]
    public void LosingFocusCancelsKeyboardPress() => RunSta(() =>
    {
        var card = new ToolCard();
        int invocations = 0;
        card.MouseLeftButtonUp += (_, _) => invocations++;
        SendKeyDown(card, Key.Space);
        card.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, card, null)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent
        });
        SendKeyUp(card, Key.Space);
        Assert.Equal(0, invocations);
    });

    [Fact]
    public void AutomationExposesNamedInvokableButtonAndRespectsDisabledState() => RunSta(() =>
    {
        var card = new ToolCard { Title = "PDF to Word" };
        int invocations = 0;
        card.MouseLeftButtonUp += (_, _) => invocations++;
        var peer = UIElementAutomationPeer.CreatePeerForElement(card)!;

        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.Equal("PDF to Word", peer.GetName());
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        provider.Invoke();
        Assert.Equal(1, invocations);

        card.IsEnabled = false;
        Assert.Throws<ElementNotEnabledException>(() => provider.Invoke());
        Assert.Equal(1, invocations);
    });

    [Fact]
    public void AutomationFromWorkerThreadInvokesOnCardDispatcher() => RunSta(() =>
    {
        var card = new ToolCard();
        var peer = UIElementAutomationPeer.CreatePeerForElement(card)!;
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        int ownerThread = Environment.CurrentManagedThreadId;
        int? invocationThread = null;
        card.MouseLeftButtonUp += (_, _) => invocationThread = Environment.CurrentManagedThreadId;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try { provider.Invoke(); }
            catch (Exception ex) { workerError = ex; }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "Automation worker timed out.");
        Assert.Null(workerError);
        Assert.Null(invocationThread);

        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.Equal(ownerThread, invocationThread);
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Tool card interaction check timed out.");
        Assert.Null(error);
    }

    private static void SendKeyDown(ToolCard card, Key key) => SendKey(card, key, Keyboard.KeyDownEvent);

    private static void SendKeyUp(ToolCard card, Key key) => SendKey(card, key, Keyboard.KeyUpEvent);

    private static void SendKey(ToolCard card, Key key, RoutedEvent routedEvent) =>
        card.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            new KeyboardPresentationSource(), Environment.TickCount, key)
        {
            RoutedEvent = routedEvent
        });

    private sealed class KeyboardPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
