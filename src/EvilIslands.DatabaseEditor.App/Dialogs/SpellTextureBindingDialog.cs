using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public sealed class SpellTextureBindingDialog : Window
{
    private readonly string _spell, _original;
    private readonly MmpImage _sourceImage;
    private byte[] _candidate;
    private readonly TextBox _name = new() { Margin = new(4), MinWidth = 240 };
    private readonly TextBox _folderName = new() { Text = "spell-textures-edited", Margin = new(4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(4) };
    private readonly TextureImageView _preview = new() { Height = 180 };
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true, MinHeight = 100 };
    private List<SpellTextureOverride> _rows = [];
    private Dictionary<string, byte[]> _textures = new(StringComparer.Ordinal);
    private bool _dirty;

    public SpellTextureBindingDialog(string spell, string original, byte[] data)
    {
        _spell = SpellTexturePack.Normalize(spell); _original = SpellTexturePack.Normalize(SpellVisualEffects.BaseName(original));
        _candidate = data; _sourceImage = SpellTexturePack.ValidateTexture(data);
        Title = "Отдельная текстура заклинания"; Width = 820; Height = 730; MinWidth = 640; MinHeight = 500;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource(typeof(Window));
        WindowStyle = WindowStyle.None; ShowInTaskbar = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new(0),
            CornerRadius = new(0), ResizeBorderThickness = new(6), UseAeroCaptionButtons = false });
        _name.Text = "fx_" + _spell + "_" + Path.GetFileNameWithoutExtension(_original) + ".mmp";
        var frame = new Border { Background = (Brush)FindResource("WindowBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new(1) };
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new(44) }); layout.RowDefinitions.Add(new());
        frame.Child = layout; Content = frame;
        var titleBar = new DockPanel { Background = (Brush)FindResource("SidebarBrush") }; layout.Children.Add(titleBar);
        var chrome = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(chrome, Dock.Right); titleBar.Children.Add(chrome);
        void ToggleSize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        Button Chrome(string caption, string tooltip, Action action)
        {
            var button = new Button { Content = caption, ToolTip = tooltip, Width = 44, Height = 42, Style = (Style)FindResource("ChromeButton") };
            button.Click += (_, _) => action(); chrome.Children.Add(button); return button;
        }
        Chrome("—", "Свернуть", () => WindowState = WindowState.Minimized);
        var maximize = Chrome("□", "Развернуть", ToggleSize);
        Chrome("×", "Закрыть", () => Close());
        StateChanged += (_, _) => { maximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            maximize.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть"; };
        titleBar.Children.Add(new TextBlock { Text = Title, Margin = new(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2) ToggleSize(); else DragMove(); };
        var root = new DockPanel { Margin = new(12) }; Grid.SetRow(root, 1); layout.Children.Add(root);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_status);
        var export = new WrapPanel(); bottom.Children.Add(export);
        export.Children.Add(new TextBlock { Text = "Новая папка:", VerticalAlignment = VerticalAlignment.Center }); export.Children.Add(_folderName);
        export.Children.Add(Button("Экспорт пакета…", Export)); export.Children.Add(Button("Закрыть", () => Close()));
        var top = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        top.Children.Add(new TextBlock { Text = $"{_spell}: {_original}", FontWeight = FontWeights.SemiBold, Margin = new(4) });
        top.Children.Add(new TextBlock { Text = "Новое имя применяется только к этому заклинанию вместо выбранного атласа. Движение, сетка кадров и смешивание остаются игровыми. Нужен addon с поддержкой этих привязок; после установки требуется перезапуск игры.", TextWrapping = TextWrapping.Wrap, Margin = new(4) });
        var commands = new WrapPanel(); top.Children.Add(commands);
        commands.Children.Add(Button("Открыть существующий пакет…", Open));
        commands.Children.Add(Button("Выбрать изменённый MMP…", Import));
        top.Children.Add(new TextBlock { Text = "Имя копии в RES (латиница, .mmp):", Margin = new(4) }); top.Children.Add(_name);
        var images = new Grid(); images.ColumnDefinitions.Add(new()); images.ColumnDefinitions.Add(new());
        var source = new TextureImageView { Height = 180 }; SetImage(source, _sourceImage); images.Children.Add(source);
        Grid.SetColumn(_preview, 1); images.Children.Add(_preview); top.Children.Add(images); SetImage(_preview, _sourceImage);
        top.Children.Add(new TextBlock { Text = $"Слева исходник, справа копия · {_sourceImage.Width} × {_sourceImage.Height}. Для новой картинки сохраните позиции кадров и прозрачность.", TextWrapping = TextWrapping.Wrap, Margin = new(4) });
        var actions = new WrapPanel(); top.Children.Add(actions);
        actions.Children.Add(Button("Добавить / обновить привязку", Add)); actions.Children.Add(Button("Удалить выбранную привязку", Remove));
        foreach (var (title, property) in new[] { ("Заклинание", "Spell"), ("Исходный атлас", "Original"), ("Новая текстура", "Replacement") })
            _list.Columns.Add(new DataGridTextColumn { Header = title, MinWidth = 180, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _list.Height = 180; top.Children.Add(_list);
        Closing += (_, e) => { if (_dirty && !ConfirmDiscard()) e.Cancel = true; };
        Refresh();
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new(4), Padding = new(10, 7, 10, 7) };
        button.Click += (_, _) => action(); return button;
    }
    private static void SetImage(TextureImageView view, MmpImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, image.PremultipliedAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze(); view.Bitmap = bitmap;
    }
    private bool ConfirmDiscard() => new ConfirmDialog("Закрыть без экспорта?", "Изменённые привязки ещё не сохранены в пакет.", "База и исходные RES не изменялись.", "Закрыть без экспорта") { Owner = this }.ShowDialog() == true;
    private void Refresh() { _list.ItemsSource = _rows.ToArray(); _status.Text = $"Привязок: {_rows.Count}" + (_dirty ? " · не экспортированы" : ""); }
    private void Add()
    {
        try
        {
            var entry = new SpellTextureOverride(_spell, _original, SpellTexturePack.Normalize(_name.Text.Trim()));
            var updated = _rows.Where(r => r.Spell != _spell || r.Original != _original).Append(entry).ToList();
            _ = SpellTexturePack.Serialize(updated);
            if (_textures.TryGetValue(entry.Replacement, out var existing) && !existing.AsSpan().SequenceEqual(_candidate) &&
                updated.Any(r => r.Replacement == entry.Replacement && r != entry))
                throw new InvalidDataException("Это имя уже используется другой привязкой. Выберите отдельное имя.");
            _rows = updated; _textures[entry.Replacement] = _candidate.ToArray(); _dirty = true; Refresh();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Remove()
    {
        if (_list.SelectedItem is not SpellTextureOverride row) return;
        _rows.Remove(row); _dirty = true; Refresh();
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Текстура MMP|*.mmp", Title = "Готовая текстура с прежней сеткой кадров" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > MmpPreviewDecoder.MaximumInputBytes) throw new InvalidDataException("MMP превышает лимит размера.");
            var data = File.ReadAllBytes(dialog.FileName); var image = SpellTexturePack.ValidateTexture(data);
            if (image.Width != _sourceImage.Width || image.Height != _sourceImage.Height) throw new InvalidDataException("Сохраните размер исходного атласа и расположение кадров.");
            _candidate = data; SetImage(_preview, image); _status.Text = "Картинка выбрана. Нажмите «Добавить / обновить привязку», затем экспортируйте пакет.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Open()
    {
        if (_dirty && !ConfirmDiscard()) return;
        var dialog = new OpenFolderDialog { Title = "Папка пакета с config/spell-textures.tsv и res/spell-textures.pak" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var pack = SpellTexturePack.Open(dialog.FolderName); _rows = pack.Rows.ToList(); _textures = pack.Textures; _dirty = false; Refresh();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Export()
    {
        string name = _folderName.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { _status.Text = "Укажите имя новой папки пакета."; return; }
        var dialog = new OpenFolderDialog { Title = "Каталог для новой папки пакета" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string folder = Path.Combine(dialog.FolderName, name); SpellTexturePack.Export(folder, _rows, _textures);
            _dirty = false; _status.Text = "Пакет проверен: " + folder + "\nДля изменения текстуры откройте его res/spell-textures.pak в RES-менеджере. В игру ничего не установлено.";
        }
        catch (Exception ex) { _status.Text = "Экспорт не завершён: " + ex.Message; }
    }
}
