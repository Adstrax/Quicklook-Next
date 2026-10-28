// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuickLook.Plugin.XMindViewer;

/// <summary>
/// A dependency-free mind-map surface. It intentionally uses plain WPF
/// primitives instead of WebView2 so XMind previews open quickly and do not
/// create another browser profile/cache for every document.
/// </summary>
public sealed class XMindPanel : UserControl
{
    private const double CanvasMargin = 48;
    private const double ColumnGap = 68;
    private const double SiblingGap = 14;

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _message;
    private readonly TextBlock _sheetTitle;
    private readonly TextBlock _sheetCounter;
    private readonly TextBlock _zoomText;
    private readonly Button _previousSheet;
    private readonly Button _nextSheet;
    private readonly List<TopicLayout> _topicLayouts = [];
    private readonly List<Connector> _connectors = [];
    private readonly FontFamily _fontFamily =
        new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei, SimSun");

    private IReadOnlyList<XMindSheet> _sheets = [];
    private int _sheetIndex;
    private double _zoom = 1;
    private bool _pendingInitialFit;
    private bool _panning;
    private Point _panStart;
    private double _panHorizontalOffset;
    private double _panVerticalOffset;

    public XMindPanel()
    {
        Focusable = true;
        Background = Brushes.Transparent;

        _sheetTitle = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 460,
        };
        _sheetTitle.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");

        _sheetCounter = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.62,
        };
        _sheetCounter.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");

        _previousSheet = ToolbarButton(
            "\uE76B",
            TranslationHelper.Get("XMind_PreviousSheet", failsafe: "Previous sheet",
                domain: "QuickLook.Plugin.XMindViewer"),
            (_, _) => ChangeSheet(-1));
        _nextSheet = ToolbarButton(
            "\uE76C",
            TranslationHelper.Get("XMind_NextSheet", failsafe: "Next sheet",
                domain: "QuickLook.Plugin.XMindViewer"),
            (_, _) => ChangeSheet(1));

        var zoomOut = ToolbarButton(
            "\uE71F",
            TranslationHelper.Get("XMind_ZoomOut", failsafe: "Zoom out",
                domain: "QuickLook.Plugin.XMindViewer"),
            (_, _) => SetZoom(_zoom / 1.15));
        var zoomIn = ToolbarButton(
            "\uE8A3",
            TranslationHelper.Get("XMind_ZoomIn", failsafe: "Zoom in",
                domain: "QuickLook.Plugin.XMindViewer"),
            (_, _) => SetZoom(_zoom * 1.15));
        var fit = new Button
        {
            Content = TranslationHelper.Get(
                "XMind_Fit",
                failsafe: "Fit to window",
                domain: "QuickLook.Plugin.XMindViewer"),
            FontSize = 12,
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(4, 0, 4, 0),
            Background = Tint(0x22, 0x5A, 0x9C, 0xE8),
            BorderBrush = Tint(0x44, 0x73, 0xAF, 0xF0),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        fit.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");
        fit.Click += (_, _) => FitToWindow();

        _zoomText = new TextBlock
        {
            MinWidth = 44,
            Margin = new Thickness(3, 0, 3, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Opacity = 0.68,
        };
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");

        var toolbarLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbarLeft.Children.Add(new Border
        {
            Background = Tint(0xD9, 0x35, 0x78, 0xC2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "XMind",
                Foreground = Brushes.White,
                FontFamily = _fontFamily,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            },
        });
        toolbarLeft.Children.Add(_previousSheet);
        toolbarLeft.Children.Add(_sheetTitle);
        toolbarLeft.Children.Add(_sheetCounter);
        toolbarLeft.Children.Add(_nextSheet);

        var toolbarRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbarRight.Children.Add(zoomOut);
        toolbarRight.Children.Add(_zoomText);
        toolbarRight.Children.Add(zoomIn);
        toolbarRight.Children.Add(fit);

        var toolbarGrid = new Grid();
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition());
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarGrid.Children.Add(toolbarLeft);
        Grid.SetColumn(toolbarRight, 1);
        toolbarGrid.Children.Add(toolbarRight);

        var toolbar = new Border
        {
            Height = 46,
            Padding = new Thickness(14, 0, 12, 0),
            Margin = new Thickness(12, 10, 12, 7),
            CornerRadius = new CornerRadius(10),
            Background = Tint(0x16, 0x5A, 0x9C, 0xE8),
            BorderBrush = Tint(0x26, 0x7A, 0xB4, 0xF0),
            BorderThickness = new Thickness(1),
            Child = toolbarGrid,
        };

        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            LayoutTransform = _scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            PanningMode = PanningMode.Both,
            Padding = new Thickness(12, 0, 12, 12),
            Background = Brushes.Transparent,
        };
        _scroll.PreviewMouseWheel += Scroll_PreviewMouseWheel;
        _scroll.PreviewMouseLeftButtonDown += Scroll_PreviewMouseLeftButtonDown;
        _scroll.PreviewMouseLeftButtonUp += Scroll_PreviewMouseLeftButtonUp;
        _scroll.PreviewMouseMove += Scroll_PreviewMouseMove;
        _scroll.PreviewMouseDoubleClick += (_, _) =>
        {
            FitToWindow();
            _scroll.Focus();
        };
        _scroll.SizeChanged += (_, _) =>
        {
            if (!_pendingInitialFit)
                return;
            _pendingInitialFit = false;
            Dispatcher.BeginInvoke(new Action(FitToWindow));
        };

        _message = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(36),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");

        var content = new Grid();
        content.Children.Add(_scroll);
        content.Children.Add(_message);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(toolbar);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        Content = root;

        UpdateSheetNavigation();
        UpdateZoomText();
    }

    public void Apply(IReadOnlyList<XMindSheet> sheets)
    {
        _sheets = sheets ?? [];
        _sheetIndex = 0;
        _message.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;

        if (_sheets.Count == 0 || _sheets[0].RootTopic == null)
        {
            ShowError(TranslationHelper.Get(
                "XMind_NoTopics",
                failsafe: "This mind map contains no topics.",
                domain: "QuickLook.Plugin.XMindViewer"));
            return;
        }

        RenderCurrentSheet();
    }

    public void ShowError(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Collapsed;
        _sheets = [];
        UpdateSheetNavigation();
    }

    private void RenderCurrentSheet()
    {
        _canvas.Children.Clear();
        _topicLayouts.Clear();
        _connectors.Clear();

        if (_sheets.Count == 0)
            return;

        var sheet = _sheets[_sheetIndex];
        _sheetTitle.Text = sheet.Title;
        _sheetTitle.ToolTip = sheet.Title;
        UpdateSheetNavigation();

        var topicCount = 0;
        var root = MeasureTopic(sheet.RootTopic, 0, ref topicCount);
        if (root == null)
        {
            ShowError(TranslationHelper.Get(
                "XMind_NoTopics",
                failsafe: "This mind map contains no topics.",
                domain: "QuickLook.Plugin.XMindViewer"));
            return;
        }

        var rootChildren = root.Children;
        for (var i = 0; i < rootChildren.Count; i++)
            SetSide(rootChildren[i], i % 2 == 0 ? Side.Right : Side.Left);

        var maxRight = new Dictionary<int, double>();
        var maxLeft = new Dictionary<int, double>();
        foreach (var item in _topicLayouts.Where(item => item.Depth > 0))
        {
            var widths = item.Side == Side.Right ? maxRight : maxLeft;
            widths[item.Depth] = Math.Max(
                widths.GetValueOrDefault(item.Depth),
                item.Width);
        }

        var maxDepth = _topicLayouts.Max(item => item.Depth);
        var leftSpan = Span(maxLeft, maxDepth);
        var rightSpan = Span(maxRight, maxDepth);
        var rootCenterX = CanvasMargin + leftSpan + (leftSpan > 0 ? ColumnGap : 0) + root.Width / 2;
        var leftEdge = rootCenterX - root.Width / 2 - (leftSpan > 0 ? ColumnGap : 0);
        var rightEdge = rootCenterX + root.Width / 2 + (rightSpan > 0 ? ColumnGap : 0);

        var leftRight = new Dictionary<int, double>();
        var rightLeft = new Dictionary<int, double>();
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            if (maxLeft.TryGetValue(depth, out var leftWidth))
            {
                leftRight[depth] = leftEdge;
                leftEdge -= leftWidth + ColumnGap;
            }

            if (maxRight.TryGetValue(depth, out var rightWidth))
            {
                rightLeft[depth] = rightEdge;
                rightEdge += rightWidth + ColumnGap;
            }
        }

        var leftChildren = rootChildren.Where(item => item.Side == Side.Left).ToList();
        var rightChildren = rootChildren.Where(item => item.Side == Side.Right).ToList();
        var leftHeight = TotalHeight(leftChildren);
        var rightHeight = TotalHeight(rightChildren);
        var contentHeight = Math.Max(Math.Max(leftHeight, rightHeight), root.Height);
        var rootCenterY = CanvasMargin + contentHeight / 2;
        root.X = rootCenterX - root.Width / 2;
        root.Y = rootCenterY - root.Height / 2;

        PlaceSide(rightChildren, rightHeight, rootCenterY, rightLeft);
        PlaceSide(leftChildren, leftHeight, rootCenterY, leftRight);
        foreach (var child in rootChildren)
            _connectors.Add(new Connector(root, child));

        var canvasWidth = Math.Max(
            rootCenterX + root.Width / 2 + (rightSpan > 0 ? ColumnGap : 0) + rightSpan,
            CanvasMargin * 2 + root.Width);
        var canvasHeight = Math.Max(contentHeight + CanvasMargin * 2, root.Height + CanvasMargin * 2);
        _canvas.Width = Math.Max(1, canvasWidth);
        _canvas.Height = Math.Max(1, canvasHeight);

        AddConnectors();

        foreach (var item in _topicLayouts.OrderByDescending(item => item.Depth))
        {
            Canvas.SetLeft(item.Visual, item.X);
            Canvas.SetTop(item.Visual, item.Y);
            _canvas.Children.Add(item.Visual);
        }

        _pendingInitialFit = true;
        Dispatcher.BeginInvoke(new Action(FitToWindow));
    }

    private void PlaceSide(
        IReadOnlyList<TopicLayout> children,
        double sideHeight,
        double rootCenterY,
        IReadOnlyDictionary<int, double> columnPositions)
    {
        var top = rootCenterY - sideHeight / 2;
        foreach (var child in children)
        {
            PlaceSubtree(child, top, columnPositions);
            top += child.SubtreeHeight + SiblingGap;
        }
    }

    private void PlaceSubtree(
        TopicLayout layout,
        double top,
        IReadOnlyDictionary<int, double> columnPositions)
    {
        if (layout.Side == Side.Right)
            layout.X = columnPositions[layout.Depth];
        else
            layout.X = columnPositions[layout.Depth] - layout.Width;

        if (layout.Children.Count == 0)
        {
            layout.Y = top + (layout.SubtreeHeight - layout.Height) / 2;
            return;
        }

        var childHeight = TotalHeight(layout.Children);
        var childTop = top + (layout.SubtreeHeight - childHeight) / 2;
        foreach (var child in layout.Children)
        {
            PlaceSubtree(child, childTop, columnPositions);
            childTop += child.SubtreeHeight + SiblingGap;
        }

        var firstCenter = layout.Children[0].Y + layout.Children[0].Height / 2;
        var last = layout.Children[^1];
        var lastCenter = last.Y + last.Height / 2;
        layout.Y = (firstCenter + lastCenter) / 2 - layout.Height / 2;

        foreach (var child in layout.Children)
            _connectors.Add(new Connector(layout, child));
    }

    private TopicLayout MeasureTopic(XMindTopic topic, int depth, ref int topicCount)
    {
        if (topic == null || depth > 96 || topicCount++ > 50000)
            return null;

        var fontSize = depth switch
        {
            0 => 16,
            1 => 14,
            _ => 13,
        };
        var fontWeight = depth <= 1 ? FontWeights.SemiBold : FontWeights.Normal;
        var maxWidth = depth switch
        {
            0 => 252,
            1 => 224,
            _ => 196,
        };
        var typeface = new Typeface(_fontFamily, FontStyles.Normal, fontWeight, FontStretches.Normal);
        var formatted = new FormattedText(
            string.IsNullOrWhiteSpace(topic.Title) ? "Untitled topic" : topic.Title,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth - 28,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        var width = Math.Clamp(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 28, 88, maxWidth);
        var height = Math.Max(
            depth == 0 ? 52 : 38,
            Math.Ceiling(formatted.Height) + (depth == 0 ? 22 : 16));

        var text = new TextBlock
        {
            Text = formatted.Text,
            FontFamily = _fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = height - 14,
        };

        if (depth == 0)
        {
            text.Foreground = Brushes.White;
        }
        else
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");
        }

        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(depth == 0 ? 14 : 9),
            Padding = new Thickness(13, 7, 13, 7),
            Background = depth switch
            {
                0 => Tint(0xE8, 0x2E, 0x72, 0xB8),
                1 => Tint(0x2E, 0x5A, 0x9C, 0xE8),
                _ => Tint(0x18, 0x5A, 0x9C, 0xE8),
            },
            BorderBrush = depth switch
            {
                0 => Tint(0x66, 0xB8, 0xDB, 0xFF),
                1 => Tint(0x66, 0x71, 0xAE, 0xF4),
                _ => Tint(0x3D, 0x71, 0xAE, 0xF4),
            },
            BorderThickness = new Thickness(1),
            Child = text,
            ToolTip = topic.Title,
        };

        var layout = new TopicLayout(topic, depth, border, width, height);
        _topicLayouts.Add(layout);

        foreach (var childTopic in topic.Children)
        {
            var child = MeasureTopic(childTopic, depth + 1, ref topicCount);
            if (child != null)
                layout.Children.Add(child);
        }

        return layout;
    }

    private void AddConnectors()
    {
        foreach (var connector in _connectors)
        {
            var parent = connector.Parent;
            var child = connector.Child;
            var right = connector.Side == Side.Right;
            var start = new Point(
                right ? parent.X + parent.Width : parent.X,
                parent.Y + parent.Height / 2);
            var end = new Point(
                right ? child.X : child.X + child.Width,
                child.Y + child.Height / 2);
            var controlDistance = Math.Max(24, Math.Abs(end.X - start.X) * 0.46);
            var controlOne = new Point(
                start.X + (right ? controlDistance : -controlDistance),
                start.Y);
            var controlTwo = new Point(
                end.X - (right ? controlDistance : -controlDistance),
                end.Y);

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.BezierTo(controlOne, controlTwo, end, isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();

            var path = new Path
            {
                Data = geometry,
                Stroke = Tint(0x88, 0x63, 0xA7, 0xEA),
                StrokeThickness = parent.Depth == 0 ? 2.2 : 1.55,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(path);
        }
    }

    private void ChangeSheet(int delta)
    {
        if (_sheets.Count == 0)
            return;

        _sheetIndex = (_sheetIndex + delta + _sheets.Count) % _sheets.Count;
        RenderCurrentSheet();
    }

    private void UpdateSheetNavigation()
    {
        var multiple = _sheets.Count > 1;
        _previousSheet.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        _nextSheet.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        _sheetCounter.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        _sheetCounter.Text = multiple
            ? $"{_sheetIndex + 1} / {_sheets.Count}"
            : string.Empty;
    }

    private void FitToWindow()
    {
        if (_canvas.Width <= 1 || _canvas.Height <= 1)
            return;

        var viewportWidth = Math.Max(1, _scroll.ViewportWidth - 32);
        var viewportHeight = Math.Max(1, _scroll.ViewportHeight - 18);
        if (_scroll.ActualWidth > 1)
            viewportWidth = Math.Max(1, _scroll.ActualWidth - 32);
        if (_scroll.ActualHeight > 1)
            viewportHeight = Math.Max(1, _scroll.ActualHeight - 18);

        var fit = Math.Min(viewportWidth / _canvas.Width, viewportHeight / _canvas.Height);
        SetZoom(Math.Clamp(fit, 0.18, 1.25));
        _scroll.ScrollToHome();
        _scroll.ScrollToLeftEnd();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.15, 3);
        _scale.ScaleX = _zoom;
        _scale.ScaleY = _zoom;
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        _zoomText.Text = $"{_zoom * 100:0}%";
    }

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        e.Handled = true;
        SetZoom(e.Delta > 0 ? _zoom * 1.12 : _zoom / 1.12);
    }

    private void Scroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
            return;

        _panning = true;
        _panStart = e.GetPosition(_scroll);
        _panHorizontalOffset = _scroll.HorizontalOffset;
        _panVerticalOffset = _scroll.VerticalOffset;
        _scroll.CaptureMouse();
        _scroll.Cursor = Cursors.SizeAll;
    }

    private void Scroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_panHorizontalOffset - (current.X - _panStart.X));
        _scroll.ScrollToVerticalOffset(_panVerticalOffset - (current.Y - _panStart.Y));
    }

    private void Scroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
    }

    private void EndPan()
    {
        if (!_panning)
            return;

        _panning = false;
        _scroll.ReleaseMouseCapture();
        _scroll.Cursor = Cursors.Arrow;
    }

    private Button ToolbarButton(string glyph, string toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = _fontFamily,
            FontSize = 12,
            Width = 29,
            Height = 29,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = toolTip,
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");
        button.Click += click;
        return button;
    }

    private static double Span(IReadOnlyDictionary<int, double> widths, int maxDepth)
    {
        var span = 0d;
        var columns = 0;
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            if (!widths.TryGetValue(depth, out var width))
                continue;
            span += width;
            columns++;
        }

        return span + Math.Max(0, columns - 1) * ColumnGap;
    }

    private static double TotalHeight(IReadOnlyList<TopicLayout> items)
    {
        if (items.Count == 0)
            return 0;
        return items.Sum(item => item.SubtreeHeight) + (items.Count - 1) * SiblingGap;
    }

    private static void SetSide(TopicLayout layout, Side side)
    {
        layout.Side = side;
        foreach (var child in layout.Children)
            SetSide(child, side);
    }

    private static SolidColorBrush Tint(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private enum Side
    {
        Left = -1,
        Right = 1,
    }

    private sealed class TopicLayout(
        XMindTopic topic,
        int depth,
        Border visual,
        double width,
        double height)
    {
        public XMindTopic Topic { get; } = topic;
        public int Depth { get; } = depth;
        public Border Visual { get; } = visual;
        public double Width { get; } = width;
        public double Height { get; } = height;
        public List<TopicLayout> Children { get; } = [];
        public Side Side { get; set; } = Side.Right;
        public double X { get; set; }
        public double Y { get; set; }

        public double SubtreeHeight
        {
            get
            {
                if (Children.Count == 0)
                    return Height;
                return Math.Max(
                    Height,
                    Children.Sum(child => child.SubtreeHeight) + (Children.Count - 1) * SiblingGap);
            }
        }
    }

    private sealed record Connector(TopicLayout Parent, TopicLayout Child)
    {
        public Side Side => Child.Side;
    }
}
