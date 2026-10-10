using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LyricCanvas.App.Rendering;
using LyricCanvas.Core;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;

namespace LyricCanvas.App.Settings;

/// <summary>编辑全局歌词颜色的渐变色标。</summary>
public sealed class GradientColorsWindow : Window
{
    private readonly ListBox _colorsList = new();
    private readonly List<string> _colors;
    private Button? _addButton;

    public IReadOnlyList<string> SelectedColors { get; private set; } = Array.Empty<string>();

    public GradientColorsWindow(IEnumerable<string> colors, string fallbackColor)
    {
        _colors = colors.ToList();
        if (_colors.Count == 0)
        {
            _colors.Add(fallbackColor);
        }

        Title = "歌词颜色渐变";
        Width = 540;
        Height = 430;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2));

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "歌词颜色",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(title);

        var editor = new Grid();
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(editor, 1);
        root.Children.Add(editor);

        _colorsList.Background = new SolidColorBrush(Color.FromRgb(0x29, 0x29, 0x2E));
        _colorsList.BorderBrush = new SolidColorBrush(Color.FromRgb(0x43, 0x43, 0x49));
        _colorsList.BorderThickness = new Thickness(1);
        _colorsList.SelectionChanged += (_, _) => UpdateButtons();
        Grid.SetColumn(_colorsList, 0);
        editor.Children.Add(_colorsList);

        var commands = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Width = 88 };
        AddCommand(commands, "添加", AddColor);
        AddCommand(commands, "更改", ChangeColor);
        AddCommand(commands, "删除", DeleteColor);
        AddCommand(commands, "上移", () => MoveColor(-1));
        AddCommand(commands, "下移", () => MoveColor(1));
        AddCommand(commands, "清除渐变", ClearColors, topMargin: 10);
        Grid.SetColumn(commands, 1);
        editor.Children.Add(commands);

        var hint = new TextBlock
        {
            Text = $"按列表顺序从左至右过渡，最多 {FramesConfig.MaxTextGradientColors} 个色标。一个颜色显示纯色；两个或更多颜色形成渐变。清除渐变会恢复上一级的纯色设置。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xAE)),
            Margin = new Thickness(0, 12, 0, 10),
        };
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = CreateButton("取消");
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = CreateButton("确定", primary: true);
        confirm.Margin = new Thickness(8, 0, 0, 0);
        confirm.Click += (_, _) =>
        {
            SelectedColors = _colors.ToArray();
            DialogResult = true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        RefreshList();
    }

    private readonly List<Button> _editButtons = new();

    private void AddCommand(Panel panel, string label, Action action, double topMargin = 0)
    {
        var button = CreateButton(label);
        button.Margin = new Thickness(0, topMargin, 0, 8);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        if (label == "添加")
        {
            _addButton = button;
        }
        if (label is "更改" or "删除" or "上移" or "下移")
        {
            _editButtons.Add(button);
        }
    }

    private static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Height = 34,
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = new SolidColorBrush(primary ? Colors.White : Color.FromRgb(0xE4, 0xE4, 0xE8)),
            Background = new SolidColorBrush(primary ? Color.FromRgb(0x43, 0x7B, 0xE8) : Color.FromRgb(0x36, 0x36, 0x3C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x4B, 0x53)),
            BorderThickness = new Thickness(1),
        };
        return button;
    }

    private void AddColor()
    {
        if (_colors.Count >= FramesConfig.MaxTextGradientColors)
        {
            return;
        }

        var selected = PickColor(_colorsList.SelectedIndex >= 0
            ? _colors[_colorsList.SelectedIndex]
            : _colors.LastOrDefault() ?? "#FFFFFFFF");
        if (selected is null)
        {
            return;
        }

        var insertionIndex = _colorsList.SelectedIndex >= 0 ? _colorsList.SelectedIndex + 1 : _colors.Count;
        _colors.Insert(insertionIndex, selected);
        RefreshList(insertionIndex);
    }

    private void ChangeColor()
    {
        var index = _colorsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var selected = PickColor(_colors[index]);
        if (selected is null)
        {
            return;
        }

        _colors[index] = selected;
        RefreshList(index);
    }

    private void DeleteColor()
    {
        var index = _colorsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _colors.RemoveAt(index);
        RefreshList(Math.Min(index, _colors.Count - 1));
    }

    private void MoveColor(int direction)
    {
        var index = _colorsList.SelectedIndex;
        var next = index + direction;
        if (index < 0 || next < 0 || next >= _colors.Count)
        {
            return;
        }

        (_colors[index], _colors[next]) = (_colors[next], _colors[index]);
        RefreshList(next);
    }

    private void ClearColors()
    {
        _colors.Clear();
        RefreshList();
    }

    private void RefreshList(int selectedIndex = -1)
    {
        _colorsList.Items.Clear();
        foreach (var hex in _colors)
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
            row.Children.Add(new Border
            {
                Width = 82,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = CreateBrush(hex),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5D)),
                BorderThickness = new Thickness(1),
            });
            row.Children.Add(new TextBlock
            {
                Text = hex,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Foreground,
            });
            _colorsList.Items.Add(new ListBoxItem { Content = row, Tag = hex });
        }

        if (selectedIndex >= 0 && selectedIndex < _colorsList.Items.Count)
        {
            _colorsList.SelectedIndex = selectedIndex;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_addButton is not null)
        {
            _addButton.IsEnabled = _colors.Count < FramesConfig.MaxTextGradientColors;
        }

        var index = _colorsList.SelectedIndex;
        _editButtons[0].IsEnabled = index >= 0;
        _editButtons[1].IsEnabled = index >= 0;
        _editButtons[2].IsEnabled = index > 0;
        _editButtons[3].IsEnabled = index >= 0 && index + 1 < _colors.Count;
    }

    private string? PickColor(string initial)
    {
        var color = ParseColor(initial);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B),
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dialog.Color.A:X2}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : null;
    }

    private static Brush CreateBrush(string hex)
        => LyricBrushFactory.CreateSolid(hex, Colors.White);

    private static Color ParseColor(string hex)
        => LyricBrushFactory.ParseColor(hex, Colors.White);
}
