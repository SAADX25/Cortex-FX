using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CortexFX.Controls
{
    public partial class ToolCard : UserControl
    {
        private Key _activationKey = Key.None;
        private bool _mousePressed;

        public ToolCard()
        {
            InitializeComponent();
            IsEnabledChanged += (_, _) =>
            {
                _activationKey = Key.None;
                _mousePressed = false;
                UpdateInteraction();
            };
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ToolCardAutomationPeer(this);

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            UpdateInteraction();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _mousePressed = false;
            UpdateInteraction();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            if (!IsEnabled) return;
            Focus();
            _mousePressed = true;
            UpdateInteraction();
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            _mousePressed = false;
            UpdateInteraction();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (IsEnabled && (e.Key is Key.Enter or Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
            {
                // Holding the key keeps the pressed state; release invokes only once.
                if (_activationKey == Key.None)
                {
                    _activationKey = e.Key;
                    UpdateInteraction();
                }
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == _activationKey && _activationKey != Key.None)
            {
                _activationKey = Key.None;
                UpdateInteraction();
                e.Handled = true;
                if (IsEnabled && Keyboard.Modifiers == ModifierKeys.None) Activate();
                return;
            }
            base.OnKeyUp(e);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            _activationKey = Key.None;
            UpdateInteraction();
        }

        private void Activate()
        {
            if (!IsEnabled) return;
            // Preserve the existing MainWindow routed handler for mouse, keyboard and UIA.
            RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = MouseLeftButtonUpEvent,
                Source = this
            });
        }

        private void UpdateInteraction()
        {
            bool pressed = IsEnabled && (_mousePressed || _activationKey != Key.None);
            bool hover = IsEnabled && IsMouseOver;
            double scale = pressed ? 0.985 : hover ? 1.01 : 1;
            double lift = pressed ? 0 : hover ? -2 : 0;
            Animate(CardScale, ScaleTransform.ScaleXProperty, scale);
            Animate(CardScale, ScaleTransform.ScaleYProperty, scale);
            Animate(CardTranslation, TranslateTransform.YProperty, lift);
        }

        private static void Animate(Animatable target, DependencyProperty property, double value)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                target.BeginAnimation(property, null);
                target.SetValue(property, value);
                return;
            }

            target.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
        }

        private sealed class ToolCardAutomationPeer(ToolCard owner)
            : FrameworkElementAutomationPeer(owner), IInvokeProvider
        {
            protected override string GetClassNameCore() => nameof(ToolCard);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

            public void Invoke()
            {
                if (!owner.Dispatcher.CheckAccess())
                {
                    // Direct automation clients can call from outside the WPF UI thread.
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(owner.Activate));
                    return;
                }
                if (!owner.IsEnabled) throw new ElementNotEnabledException();
                owner.Activate();
            }
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(ToolCard), new PropertyMetadata(string.Empty));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty IconDataProperty =
            DependencyProperty.Register("IconData", typeof(Geometry), typeof(ToolCard), new PropertyMetadata(null));

        public Geometry IconData
        {
            get { return (Geometry)GetValue(IconDataProperty); }
            set { SetValue(IconDataProperty, value); }
        }

        public static readonly DependencyProperty IconBackgroundProperty =
            DependencyProperty.Register("IconBackground", typeof(Brush), typeof(ToolCard), new PropertyMetadata(Brushes.Gray));

        public static readonly DependencyProperty IconForegroundProperty =
            DependencyProperty.Register("IconForeground", typeof(Brush), typeof(ToolCard), new PropertyMetadata(Brushes.White));

        public Brush IconForeground
        {
            get { return (Brush)GetValue(IconForegroundProperty); }
            set { SetValue(IconForegroundProperty, value); }
        }

        public Brush IconBackground
        {
            get { return (Brush)GetValue(IconBackgroundProperty); }
            set { SetValue(IconBackgroundProperty, value); }
        }
    }
}
