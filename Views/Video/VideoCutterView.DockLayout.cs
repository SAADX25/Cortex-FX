using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CortexFX.Core.Video;

namespace CortexFX.Views.Video;

public partial class VideoCutterView
{
    private CutterDockNode _dockLayout = CutterDockLayoutManager.LoadOrDefault();
    private bool _isDockLocked;
    private bool _pendingDockPanelDrag;
    private bool _isDraggingDockPanel;
    private FrameworkElement? _dockDragHeaderElement;
    private CutterPanelId _dragSourcePanel;
    private Point _dockDragStartPoint;

    private readonly Dictionary<CutterPanelId, FrameworkElement> _panelElements = new();
    private readonly Dictionary<CutterPanelId, Rect> _panelBoundsInHost = new();
    private Border? _dockPreviewBox;
    private Border? _dockFloatingPill;
    private TextBlock? _dockFloatingText;
    private CutterPanelId? _dockTargetPanel;
    private DockDropZone _dockDropZone = DockDropZone.None;
    private bool _dockTargetIsOuter;

    private void InitializeDockSystem()
    {
        _panelElements[CutterPanelId.Project] = ProjectPanelBorder;
        _panelElements[CutterPanelId.Program] = EditorLeftPanel;
        _panelElements[CutterPanelId.Timeline] = CutterTimelinePanel;
        RebuildDockLayout();
    }

    private void RebuildDockLayout()
    {
        if (WorkspaceDockHost == null || FocusPreviewHost == null || _panelElements.Count < 3)
            return;

        foreach (FrameworkElement el in _panelElements.Values)
            DetachFromParentPanel(el);

        WorkspaceDockHost.Children.Clear();
        WorkspaceDockHost.RowDefinitions.Clear();
        WorkspaceDockHost.ColumnDefinitions.Clear();
        FocusPreviewHost.Children.Clear();

        if (_previewFocused)
        {
            WorkspaceDockHost.Visibility = Visibility.Collapsed;
            FocusPreviewHost.Visibility = Visibility.Visible;
            EditorLeftPanel.Margin = new Thickness(0);
            EditorLeftPanel.BorderThickness = new Thickness(0);
            FocusPreviewHost.Children.Add(EditorLeftPanel);
        }
        else
        {
            FocusPreviewHost.Visibility = Visibility.Collapsed;
            WorkspaceDockHost.Visibility = Visibility.Visible;
            EditorLeftPanel.Margin = new Thickness(0);
            EditorLeftPanel.BorderThickness = new Thickness(1);
            UIElement rootVisual = BuildDockVisualNode(_dockLayout);
            WorkspaceDockHost.Children.Add(rootVisual);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateAspectOverlay();
            RenderProgramCutMarkers();
            RenderJogTicks(_jogVisualOffset);
            if (!_previewFocused && EditorPanel.Visibility == Visibility.Visible)
                RenderTimeline();
        }));
    }

    private UIElement BuildDockVisualNode(CutterDockNode node)
    {
        if (node is CutterDockLeaf leaf)
        {
            FrameworkElement el = _panelElements[leaf.Panel];
            Grid.SetRow(el, 0);
            Grid.SetColumn(el, 0);
            Grid.SetRowSpan(el, 1);
            Grid.SetColumnSpan(el, 1);
            el.Margin = new Thickness(0);
            return el;
        }

        if (node is CutterDockSplit split)
        {
            var grid = new Grid();
            bool horiz = split.Orientation == Orientation.Horizontal;
            double r1 = Math.Clamp(split.Ratio, 0.15, 0.85);
            double r2 = 1.0 - r1;

            UIElement firstVisual = BuildDockVisualNode(split.First);
            UIElement secondVisual = BuildDockVisualNode(split.Second);

            var splitter = new GridSplitter
            {
                Background = BrushFrom("#121212"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false
            };

            if (horiz)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(r1, GridUnitType.Star), MinWidth = 155 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Pixel) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(r2, GridUnitType.Star), MinWidth = 155 });

                splitter.Width = 4;
                splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                splitter.VerticalAlignment = VerticalAlignment.Stretch;
                splitter.Cursor = Cursors.SizeWE;

                Grid.SetColumn(firstVisual, 0);
                Grid.SetColumn(splitter, 1);
                Grid.SetColumn(secondVisual, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r1, GridUnitType.Star), MinHeight = 125 });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4, GridUnitType.Pixel) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r2, GridUnitType.Star), MinHeight = 125 });

                splitter.Height = 4;
                splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                splitter.VerticalAlignment = VerticalAlignment.Stretch;
                splitter.Cursor = Cursors.SizeNS;

                Grid.SetRow(firstVisual, 0);
                Grid.SetRow(splitter, 1);
                Grid.SetRow(secondVisual, 2);
            }

            splitter.MouseEnter += (_, _) => splitter.Background = BrushFrom("#2D8CFF");
            splitter.MouseLeave += (_, _) => splitter.Background = BrushFrom("#121212");
            splitter.DragCompleted += (_, _) =>
            {
                double a = horiz ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
                double b = horiz ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
                double sum = a + b;
                if (sum > 20)
                {
                    split.Ratio = Math.Clamp(a / sum, 0.15, 0.85);
                    if (horiz)
                    {
                        grid.ColumnDefinitions[0].Width = new GridLength(split.Ratio, GridUnitType.Star);
                        grid.ColumnDefinitions[2].Width = new GridLength(1.0 - split.Ratio, GridUnitType.Star);
                    }
                    else
                    {
                        grid.RowDefinitions[0].Height = new GridLength(split.Ratio, GridUnitType.Star);
                        grid.RowDefinitions[2].Height = new GridLength(1.0 - split.Ratio, GridUnitType.Star);
                    }
                    CutterDockLayoutManager.Save(_dockLayout);
                }

                UpdateAspectOverlay();
                RenderProgramCutMarkers();
                RenderTimeline();
            };

            grid.Children.Add(firstVisual);
            grid.Children.Add(splitter);
            grid.Children.Add(secondVisual);
            return grid;
        }

        return new Grid();
    }

    private static void DetachFromParentPanel(FrameworkElement element)
    {
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
    }

    // Drag-to-Dock Panel Header Interaction

    private void DockPanelHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDockLocked || _previewFocused || IsExporting) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string tagStr) return;
        if (!Enum.TryParse(tagStr, out CutterPanelId panelId)) return;

        _pendingDockPanelDrag = true;
        _isDraggingDockPanel = false;
        _dragSourcePanel = panelId;
        _dockDragHeaderElement = fe;
        _dockDragStartPoint = e.GetPosition(EditorPanel);
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void DockPanelHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pendingDockPanelDrag && !_isDraggingDockPanel) return;

        Point pt = e.GetPosition(EditorPanel);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDockPanelDrag(applyDrop: false);
            return;
        }

        if (_pendingDockPanelDrag && !_isDraggingDockPanel)
        {
            Vector delta = pt - _dockDragStartPoint;
            if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
                return;

            _pendingDockPanelDrag = false;
            _isDraggingDockPanel = true;
            BeginDockPanelDragVisuals();
        }

        if (_isDraggingDockPanel)
            UpdateDockPanelDrag(pt);
    }

    private void DockPanelHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingDockPanelDrag || _isDraggingDockPanel)
        {
            EndDockPanelDrag(applyDrop: _isDraggingDockPanel);
            e.Handled = true;
        }
    }

    private void BeginDockPanelDragVisuals()
    {
        CapturePanelBounds();
        EnsureDockOverlayElements();
        DockDragOverlayCanvas.Visibility = Visibility.Visible;
        if (_panelElements.TryGetValue(_dragSourcePanel, out FrameworkElement? srcEl))
            srcEl.Opacity = 0.55;
    }

    private void CapturePanelBounds()
    {
        _panelBoundsInHost.Clear();
        foreach (var (id, el) in _panelElements)
        {
            if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                continue;
            try
            {
                Point topLeft = el.TranslatePoint(new Point(0, 0), EditorPanel);
                _panelBoundsInHost[id] = new Rect(topLeft.X, topLeft.Y, el.ActualWidth, el.ActualHeight);
            }
            catch { }
        }
    }

    private void EnsureDockOverlayElements()
    {
        if (_dockPreviewBox == null)
        {
            _dockPreviewBox = new Border
            {
                Background = BrushFrom("#302D8CFF"),
                BorderBrush = BrushFrom("#4EA1FF"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Visibility = Visibility.Collapsed
            };
            DockDragOverlayCanvas.Children.Add(_dockPreviewBox);
        }

        if (_dockFloatingPill == null)
        {
            _dockFloatingText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            _dockFloatingPill = new Border
            {
                Background = BrushFrom("#EB1B1E26"),
                BorderBrush = BrushFrom("#4EA1FF"),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                Child = _dockFloatingText,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black }
            };
            DockDragOverlayCanvas.Children.Add(_dockFloatingPill);
        }
    }

    private void UpdateDockPanelDrag(Point pt)
    {
        double hostW = EditorPanel.ActualWidth;
        double hostH = EditorPanel.ActualHeight;
        if (hostW <= 20 || hostH <= 20 || _dockPreviewBox == null || _dockFloatingPill == null || _dockFloatingText == null)
            return;

        _dockTargetPanel = null;
        _dockDropZone = DockDropZone.None;
        _dockTargetIsOuter = false;
        Rect previewRect = Rect.Empty;
        string hintAction = "Drag over a panel or edge";

        const double outerMargin = 22.0;
        if (pt.X <= outerMargin)
        {
            _dockDropZone = DockDropZone.Left;
            _dockTargetIsOuter = true;
            previewRect = new Rect(4, 4, hostW * 0.28, hostH - 8);
            hintAction = $"Dock {_dragSourcePanel} Left";
        }
        else if (pt.X >= hostW - outerMargin)
        {
            _dockDropZone = DockDropZone.Right;
            _dockTargetIsOuter = true;
            previewRect = new Rect(hostW * 0.72 - 4, 4, hostW * 0.28, hostH - 8);
            hintAction = $"Dock {_dragSourcePanel} Right";
        }
        else if (pt.Y <= outerMargin)
        {
            _dockDropZone = DockDropZone.Top;
            _dockTargetIsOuter = true;
            previewRect = new Rect(4, 4, hostW - 8, hostH * 0.36);
            hintAction = $"Dock {_dragSourcePanel} Top";
        }
        else if (pt.Y >= hostH - outerMargin)
        {
            _dockDropZone = DockDropZone.Bottom;
            _dockTargetIsOuter = true;
            previewRect = new Rect(4, hostH * 0.64 - 4, hostW - 8, hostH * 0.36);
            hintAction = $"Dock {_dragSourcePanel} Bottom";
        }
        else
        {
            foreach (var (id, rect) in _panelBoundsInHost)
            {
                if (!rect.Contains(pt)) continue;
                if (id == _dragSourcePanel)
                {
                    hintAction = $"Moving {_dragSourcePanel}";
                    break;
                }

                _dockTargetPanel = id;
                double nx = (pt.X - rect.X) / Math.Max(1, rect.Width);
                double ny = (pt.Y - rect.Y) / Math.Max(1, rect.Height);

                if (nx < 0.24)
                {
                    _dockDropZone = DockDropZone.Left;
                    previewRect = new Rect(rect.X, rect.Y, rect.Width * 0.45, rect.Height);
                    hintAction = $"Place {_dragSourcePanel} Left of {id}";
                }
                else if (nx > 0.76)
                {
                    _dockDropZone = DockDropZone.Right;
                    previewRect = new Rect(rect.X + rect.Width * 0.55, rect.Y, rect.Width * 0.45, rect.Height);
                    hintAction = $"Place {_dragSourcePanel} Right of {id}";
                }
                else if (ny < 0.24)
                {
                    _dockDropZone = DockDropZone.Top;
                    previewRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height * 0.45);
                    hintAction = $"Place {_dragSourcePanel} Above {id}";
                }
                else if (ny > 0.76)
                {
                    _dockDropZone = DockDropZone.Bottom;
                    previewRect = new Rect(rect.X, rect.Y + rect.Height * 0.55, rect.Width, rect.Height * 0.45);
                    hintAction = $"Place {_dragSourcePanel} Below {id}";
                }
                else
                {
                    _dockDropZone = DockDropZone.Center;
                    previewRect = rect;
                    hintAction = $"Swap {_dragSourcePanel} ↔ {id}";
                }
                break;
            }
        }

        if (!previewRect.IsEmpty && _dockDropZone != DockDropZone.None)
        {
            Canvas.SetLeft(_dockPreviewBox, previewRect.X);
            Canvas.SetTop(_dockPreviewBox, previewRect.Y);
            _dockPreviewBox.Width = Math.Max(16, previewRect.Width);
            _dockPreviewBox.Height = Math.Max(16, previewRect.Height);
            _dockPreviewBox.Visibility = Visibility.Visible;
        }
        else
        {
            _dockPreviewBox.Visibility = Visibility.Collapsed;
        }

        _dockFloatingText.Text = hintAction;
        double pillX = Math.Clamp(pt.X + 14, 6, Math.Max(6, hostW - 195));
        double pillY = Math.Clamp(pt.Y + 14, 6, Math.Max(6, hostH - 36));
        Canvas.SetLeft(_dockFloatingPill, pillX);
        Canvas.SetTop(_dockFloatingPill, pillY);
    }

    private void EndDockPanelDrag(bool applyDrop)
    {
        if (_panelElements.TryGetValue(_dragSourcePanel, out FrameworkElement? srcEl))
            srcEl.Opacity = 1.0;

        if (_dockDragHeaderElement is { IsMouseCaptured: true })
            _dockDragHeaderElement.ReleaseMouseCapture();

        _pendingDockPanelDrag = false;
        _isDraggingDockPanel = false;
        _dockDragHeaderElement = null;
        DockDragOverlayCanvas.Visibility = Visibility.Collapsed;
        if (_dockPreviewBox != null)
            _dockPreviewBox.Visibility = Visibility.Collapsed;

        if (!applyDrop || _dockDropZone == DockDropZone.None)
            return;

        if (_dockTargetIsOuter)
        {
            _dockLayout = CutterDockLayoutManager.DockOuterEdge(_dockLayout, _dragSourcePanel, _dockDropZone);
        }
        else if (_dockTargetPanel.HasValue && _dockTargetPanel.Value != _dragSourcePanel)
        {
            _dockLayout = CutterDockLayoutManager.DockRelative(
                _dockLayout, _dragSourcePanel, _dockTargetPanel.Value, _dockDropZone);
        }

        CutterDockLayoutManager.Save(_dockLayout);
        RebuildDockLayout();
        ShowTimelineToast("Workspace layout updated");
    }

    private void WorkspaceLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("StudioContextMenu") };

        MenuItem MakePresetItem(string label, CutterDockPreset preset)
        {
            var mi = new MenuItem { Header = label, Style = (Style)FindResource("StudioMenuItem") };
            mi.Click += (_, _) => ApplyDockPreset(preset);
            return mi;
        }

        menu.Items.Add(MakePresetItem("Editing (Default)", CutterDockPreset.Editing));
        menu.Items.Add(MakePresetItem("Premiere Classic (Left Bin)", CutterDockPreset.PremiereClassic));
        menu.Items.Add(MakePresetItem("Vertical / Reels", CutterDockPreset.VerticalMedia));
        menu.Items.Add(new Separator { Style = (Style)FindResource("StudioMenuSeparator") });

        var lockItem = new MenuItem
        {
            Header = _isDockLocked ? "✓ Panel Positions Locked" : "Lock Panel Positions",
            Style = (Style)FindResource("StudioMenuItem")
        };
        lockItem.Click += (_, _) =>
        {
            _isDockLocked = !_isDockLocked;
            ShowTimelineToast(_isDockLocked ? "Panels locked" : "Panels unlocked — drag tab headers to move");
        };
        menu.Items.Add(lockItem);

        var resetItem = new MenuItem
        {
            Header = "Reset Default Layout",
            Style = (Style)FindResource("StudioMenuItem")
        };
        resetItem.Click += (_, _) => ApplyDockPreset(CutterDockPreset.Editing);
        menu.Items.Add(resetItem);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void ApplyDockPreset(CutterDockPreset preset)
    {
        if (_previewFocused)
        {
            _previewFocused = false;
            ApplyPreviewFocusState();
        }
        _dockLayout = CutterDockLayoutManager.CreatePreset(preset);
        CutterDockLayoutManager.Save(_dockLayout);
        RebuildDockLayout();
        ShowTimelineToast($"Layout: {preset}");
    }
}
