using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamiteTts.Models;

namespace DynamiteTts.UI.Controls;

public partial class HotkeyCaptureBox : UserControl
{
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(
            nameof(Hotkey),
            typeof(HotkeyConfig),
            typeof(HotkeyCaptureBox),
            new FrameworkPropertyMetadata(
                HotkeyConfig.Default,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnHotkeyChanged));

    private bool _isListening;

    public HotkeyConfig Hotkey
    {
        get => (HotkeyConfig)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyCaptureBox()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyCaptureBox control)
        {
            control.UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        if (_isListening)
        {
            ShortcutTextBlock.Text = "Press desired shortcut keys...";
            ShortcutTextBlock.Foreground = (Brush)FindResource("AccentBrush");
            BorderContainer.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else
        {
            ShortcutTextBlock.Text = Hotkey != null ? Hotkey.ToString() : "None";
            ShortcutTextBlock.Foreground = (Brush)FindResource("TextPrimaryBrush");
            BorderContainer.BorderBrush = (Brush)FindResource("InputBorderBrush");
        }
    }

    private void OnBorderClick(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _isListening = true;
        UpdateDisplay();
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _isListening = true;
        UpdateDisplay();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _isListening = false;
        UpdateDisplay();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isListening) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or
                   Key.LeftAlt or Key.RightAlt or
                   Key.LeftShift or Key.RightShift or
                   Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        e.Handled = true;

        if (key == Key.Escape)
        {
            _isListening = false;
            UpdateDisplay();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= HotkeyModifiers.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= HotkeyModifiers.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= HotkeyModifiers.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= HotkeyModifiers.Win;

        Hotkey = new HotkeyConfig
        {
            Modifiers = modifiers,
            Key = key
        };

        _isListening = false;
        UpdateDisplay();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Hotkey = HotkeyConfig.Default;
        _isListening = false;
        UpdateDisplay();
    }
}
