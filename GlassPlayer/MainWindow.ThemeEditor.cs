using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>
/// Theme studio: pick, create, edit, duplicate, delete, import and export themes. Edits preview live on the whole app;
/// nothing is stored until you press Save.
/// </summary>
public partial class MainWindow
{
    ThemeDef? draft;
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    bool previewHooked;

    void QueuePreview()
    {
        if (!previewHooked) { previewTimer.Tick += (_, _) => { previewTimer.Stop(); ApplyLook(); }; previewHooked = true; }
        previewTimer.Stop();
        previewTimer.Start();
    }

    FrameworkElement BuildThemes()
    {
        var page = NewPage();
        var b = page.Body;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(ActionButton("New theme", () => StartEdit(CurrentTheme.Clone("My theme")), true, ""));
        head.Children.Add(ActionButton("Import…", ImportTheme, false, ""));
        b.Children.Add(PageHeader("Theme studio", "Make LiquidCloud yours. Changes preview live; save to keep them.", head));

        var active = activeThemeId ?? cfg.Theme;
        b.Children.Add(SectionTitle("Themes", "Click to switch. Right-click for more."));
        b.Children.Add(CardGrid(ThemeStore.All.Select(t => ThemeCard(t, t.Id == active, t.Id == cfg.Theme))));

        var editorSlot = new StackPanel { Name = "EditorSlot" };
        b.Children.Add(editorSlot);
        themeEditorSlot = editorSlot;
        if (draft != null) RenderEditor();
        return page.Scroll;
    }

    StackPanel? themeEditorSlot;

    FrameworkElement ThemeCard(ThemeDef t, bool active, bool isDefault)
    {
        // a tiny mock-up of the app in that theme: gradient, light blobs, a glass panel and an accent button
        var root = new StackPanel { Width = 200, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var bg = new LinearGradientBrush(t.Gradient1C, t.Gradient2C, 45);
        var r = new CornerRadius(Math.Max(6, t.Radius * .6));
        var preview = new Border
        {
            Height = 124, CornerRadius = r, Background = bg, ClipToBounds = true,
            BorderBrush = active ? Res("AccentBrush") : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(active ? 2 : 1), Effect = (System.Windows.Media.Effects.Effect)FindResource("SoftShadow")
        };
        var g = new Grid();
        g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 140, Height = 140, Margin = new Thickness(-60, -70, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Fill = new RadialGradientBrush(Themes.WithAlpha(t.PrimaryC, .7), Themes.WithAlpha(t.PrimaryC, 0)) });
        g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 130, Height = 130, Margin = new Thickness(0, 0, -50, -60), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Fill = new RadialGradientBrush(Themes.WithAlpha(t.SecondaryC, .6), Themes.WithAlpha(t.SecondaryC, 0)) });
        var panel = new Border
        {
            Margin = new Thickness(18, 18, 18, 18), CornerRadius = new CornerRadius(Math.Max(4, t.Radius * .45)),
            Background = new LinearGradientBrush(Themes.WithAlpha(t.GlassC, Math.Min(1, t.GlassOpacity * 1.6)), Themes.WithAlpha(t.GlassC, t.GlassOpacity * .4), 45),
            BorderBrush = new SolidColorBrush(Themes.WithAlpha(Colors.White, .25 + .4 * t.Glow)), BorderThickness = new Thickness(1)
        };
        var inner = new StackPanel { Margin = new Thickness(10) };
        inner.Children.Add(new Border { Height = 7, Width = 70, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(t.TextC), HorizontalAlignment = HorizontalAlignment.Left });
        inner.Children.Add(new Border { Height = 5, Width = 46, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Themes.WithAlpha(t.TextC, .5)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
        inner.Children.Add(new Border { Height = 18, Width = 52, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(t.AccentC), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) });
        panel.Child = inner;
        g.Children.Add(panel);
        preview.Child = g;
        root.Children.Add(preview);

        var label = new Grid { Margin = new Thickness(2, 10, 2, 0) };
        label.Children.Add(Txt(t.Name, 14, FontWeights.SemiBold));
        var tag = Txt(isDefault ? "Default" : t.BuiltIn ? "Built-in" : "Yours", 11, null, isDefault ? "AccentBrush" : "TextFaintBrush");
        tag.HorizontalAlignment = HorizontalAlignment.Right;
        tag.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(tag);
        root.Children.Add(label);

        Motion.Hoverable(root, 1.03, -3);
        root.MouseLeftButtonUp += (_, _) => ActivateTheme(t.Id);
        root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu();
            m.Items.Add(Mi("Use this theme", "", () => ActivateTheme(t.Id)));
            m.Items.Add(Mi("Set as default", "", () => { cfg.Theme = t.Id; cfg.Save(); Toast($"\"{t.Name}\" is your default theme", ""); Navigate("themes", null, false); }));
            m.Items.Add(new Separator());
            if (!t.BuiltIn) m.Items.Add(Mi("Edit", "", () => StartEdit(t.Clone(t.Name), t.Id)));
            m.Items.Add(Mi("Duplicate and edit", "", () => StartEdit(t.Clone(t.Name + " copy"))));
            m.Items.Add(Mi("Export…", "", () => ExportTheme(t)));
            if (!t.BuiltIn)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Mi("Delete", "", async () =>
                {
                    if (!await ShowModal($"Delete \"{t.Name}\"?", Txt("This theme will be removed. You can't undo this.", 13, null, "TextDimBrush", true), "Delete", "Cancel", true)) return;
                    ThemeStore.Delete(t.Id);
                    if (cfg.Theme == t.Id) { cfg.Theme = ThemeStore.DefaultId; cfg.Save(); }
                    if (activeThemeId == t.Id) activeThemeId = null;
                    ApplyLook();
                    Toast($"Deleted \"{t.Name}\"", "");
                    Navigate("themes", null, false);
                }));
            }
            OpenMenu(m, root);
        };
        return root;
    }

    void ActivateTheme(string id)
    {
        draft = null;
        previewTheme = null;
        activeThemeId = id;
        ApplyLook();
        var t = ThemeStore.Find(id);
        Toast($"Theme: {t.Name}. Right-click it to make it the default.", "");
        Navigate("themes", null, false);
    }

    string? editingId; // the user theme being edited (null = a new theme)

    void StartEdit(ThemeDef copy, string? existingId = null)
    {
        draft = copy;
        editingId = existingId;
        if (existingId != null) draft.Id = existingId;
        previewTheme = draft;
        ApplyLook();
        RenderEditor();
        if (themeEditorSlot != null) themeEditorSlot.BringIntoView();
    }

    void RenderEditor()
    {
        if (themeEditorSlot == null || draft == null) return;
        var d = draft;
        var slot = themeEditorSlot;
        slot.Children.Clear();

        var card = new Border { Style = S("GlassRaised"), Padding = new Thickness(26, 22, 26, 24), Margin = new Thickness(0, 20, 0, 0) };
        var sp = new StackPanel();
        card.Child = sp;
        settingsTarget = sp;

        var top = new Grid();
        top.Children.Add(Txt(editingId != null ? "Edit theme" : "New theme", 22, FontWeights.SemiBold));
        top.Children.Add(new TextBlock { Text = "Live preview", Foreground = Res("AccentBrush"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(top);

        var nameBox = new TextBox { Style = S("Field"), Tag = "Theme name", Text = d.Name, Margin = new Thickness(0, 14, 0, 0) };
        nameBox.TextChanged += (_, _) => d.Name = nameBox.Text;
        sp.Children.Add(nameBox);

        Section("COLORS");
        var colors = new WrapPanel();
        sp.Children.Add(colors);
        void ColorField(string label, Func<string> get, Action<string> set) => colors.Children.Add(ColorEditor(label, get, v => { set(v); QueuePreview(); }));
        ColorField("Main light", () => d.Primary, v => d.Primary = v);
        ColorField("Second light", () => d.Secondary, v => d.Secondary = v);
        ColorField("Accent", () => d.Accent, v => d.Accent = v);
        ColorField("Text", () => d.Text, v => d.Text = v);
        ColorField("Glass tint", () => d.Glass, v => d.Glass = v);
        ColorField("Background", () => d.Background, v => d.Background = v);
        ColorField("Gradient start", () => d.Gradient1, v => d.Gradient1 = v);
        ColorField("Gradient end", () => d.Gradient2, v => d.Gradient2 = v);

        Section("GLASS & LIGHT");
        SliderRow("Glass opacity", 0, .6, d.GlassOpacity, v => $"{v:P0}", v => { d.GlassOpacity = v; QueuePreview(); });
        SliderRow("Blur behind glass", 0, 40, d.Blur, v => $"{v:0}", v => { d.Blur = v; QueuePreview(); });
        SliderRow("Glow intensity", 0, 1, d.Glow, v => $"{v:P0}", v => { d.Glow = v; QueuePreview(); });
        SliderRow("Shadow intensity", 0, 1, d.Shadow, v => $"{v:P0}", v => { d.Shadow = v; QueuePreview(); });
        SliderRow("Corner radius", 0, 40, d.Radius, v => $"{v:0}", v => { d.Radius = v; QueuePreview(); });
        SliderRow("Gradient angle", 0, 360, d.GradientAngle, v => $"{v:0}°", v => { d.GradientAngle = v; QueuePreview(); });

        var buttons = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(ActionButton("Save", () => SaveDraft(false), true, ""));
        if (editingId != null) buttons.Children.Add(ActionButton("Save as new", () => SaveDraft(true), false, ""));
        buttons.Children.Add(ActionButton("Save and set as default", () => { SaveDraft(false); cfg.Theme = activeThemeId ?? cfg.Theme; cfg.Save(); Navigate("themes", null, false); }, false, ""));
        buttons.Children.Add(ActionButton("Cancel", () =>
        {
            draft = null; previewTheme = null; ApplyLook();
            Toast("Changes discarded", "");
            Navigate("themes", null, false);
        }, false, ""));
        sp.Children.Add(buttons);

        slot.Children.Add(card);
        Motion.Enter(card, 0, 16, Motion.Slow);
        settingsTarget = SettingsHost;
    }

    void SaveDraft(bool asNew)
    {
        if (draft == null) return;
        var t = asNew ? draft.Clone(draft.Name) : draft;
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "My theme";
        if (asNew || editingId == null) t.Id = Guid.NewGuid().ToString("N");
        ThemeStore.Put(t);
        draft = null;
        previewTheme = null;
        activeThemeId = t.Id;
        editingId = null;
        ApplyLook();
        Toast($"Saved \"{t.Name}\"", "");
        Navigate("themes", null, false);
    }

    /// <summary>Color row: swatch + hex field; clicking the swatch opens hue / saturation / brightness sliders.</summary>
    FrameworkElement ColorEditor(string label, Func<string> get, Action<string> set)
    {
        var box = new StackPanel { Width = 270, Margin = new Thickness(0, 10, 18, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var swatchBrush = new SolidColorBrush(ThemeDef.C(get(), Colors.Gray));
        var swatch = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = swatchBrush, BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "Adjust" };
        row.Children.Add(swatch);
        var name = Txt(label, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var hex = new TextBox { Style = S("Field"), Text = get(), FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0) };
        Grid.SetColumn(hex, 2);
        row.Children.Add(hex);
        box.Children.Add(row);

        var picker = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 4) };
        var (h0, s0, v0) = Themes.ToHsv(swatchBrush.Color);
        bool syncing = false;
        Slider Mk(double max, double val, Brush track)
        {
            var sl = new Slider { Style = S("Seek"), Minimum = 0, Maximum = max, Value = val, IsMoveToPointEnabled = true, Margin = new Thickness(0, 6, 0, 0) };
            var holder = new Grid();
            holder.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = track, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            holder.Children.Add(sl);
            picker.Children.Add(holder);
            return sl;
        }
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(Themes.FromHsv(i * 60, 1, 1), i / 6.0));
        var satTrack = new LinearGradientBrush(Colors.White, Themes.FromHsv(h0, 1, 1), 0);
        var valTrack = new LinearGradientBrush(Colors.Black, Themes.FromHsv(h0, s0, 1), 0);
        var hS = Mk(360, h0, rainbow);
        var sS = Mk(1, s0, satTrack);
        var vS = Mk(1, v0, valTrack);
        box.Children.Add(picker);

        void FromSliders()
        {
            if (syncing) return;
            var c = Themes.FromHsv(hS.Value, sS.Value, vS.Value);
            satTrack.GradientStops[1].Color = Themes.FromHsv(hS.Value, 1, 1);
            valTrack.GradientStops[1].Color = Themes.FromHsv(hS.Value, sS.Value, 1);
            swatchBrush.Color = c;
            syncing = true;
            hex.Text = Themes.Hex(c);
            syncing = false;
            set(Themes.Hex(c));
        }
        hS.ValueChanged += (_, _) => FromSliders();
        sS.ValueChanged += (_, _) => FromSliders();
        vS.ValueChanged += (_, _) => FromSliders();
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var text = hex.Text.Trim();
            if (!text.StartsWith("#")) text = "#" + text;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text);
                swatchBrush.Color = c;
                var (h, s, v) = Themes.ToHsv(c);
                syncing = true;
                hS.Value = h; sS.Value = s; vS.Value = v;
                syncing = false;
                set(Themes.Hex(c));
            }
            catch { }
        };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            bool open = picker.Visibility != Visibility.Visible;
            picker.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) Motion.Enter(picker, 0, 6, Motion.Base);
        };
        return box;
    }

    void ExportTheme(ThemeDef t)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "LiquidCloud theme|*.lctheme", FileName = t.Name + ".lctheme" };
        if (dlg.ShowDialog() != true) return;
        try { ThemeStore.Export(t, dlg.FileName); Toast($"Exported \"{t.Name}\"", ""); }
        catch (Exception ex) { Toast("Couldn't export: " + ex.Message, ""); }
    }

    void ImportTheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "LiquidCloud theme|*.lctheme;*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var t = ThemeStore.Import(dlg.FileName);
            activeThemeId = t.Id;
            ApplyLook();
            Toast($"Imported \"{t.Name}\"", "");
            Navigate("themes", null, false);
        }
        catch (Exception ex) { Toast("That file isn't a valid theme: " + ex.Message, ""); }
    }
}
