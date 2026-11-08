using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polyline = System.Windows.Shapes.Polyline;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog
{
    private static string _kimodoExecutable = Environment.GetEnvironmentVariable("KIMODO_GENERATE")
        ?? Path.Combine(AppContext.BaseDirectory, "Kimodo", "bin", "kmd-generate.exe");
    private static string _kimodoMotion = Environment.GetEnvironmentVariable("KIMODO_SOMA_GGUF")
        ?? Path.Combine(AppContext.BaseDirectory, "Kimodo", "motion.gguf");
    private static string _kimodoText = Environment.GetEnvironmentVariable("KIMODO_TEXT_BUNDLE")
        ?? Path.Combine(AppContext.BaseDirectory, "Kimodo", "text", "Llama-3-Kimodo-Q4_K.gguf");
    private static readonly KimodoDraftPreferencesStore DraftPreferences = new();
    private readonly List<string> _workingClips = new();
    private readonly Dictionary<string, FigureAnimationClip> _generatedBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationAuthoringSettings> _workingSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _workingSavedProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _workingClipPicker = new() { Width = 275, Margin = new Thickness(4), ToolTip = "Переключить клип в этом же окне. Все правки остаются в одном черновом ANM." };
    private bool _settingWorkingClip;

    internal static (string Executable, string Motion, string Text) KimodoPaths =>
        (_kimodoExecutable, _kimodoMotion, _kimodoText);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint capacity);

    internal static string KimodoCompatiblePath(string path)
    {
        if (path.All(character => character <= 127)) return path;
        // A short *file* name truncates .gguf to .GGU. Kimodo identifies its
        // monolithic text model by extension, so shorten only the directory.
        bool file = File.Exists(path);
        string leaf = file ? Path.GetFileName(path) : "";
        if (file && leaf.Any(character => character > 127))
            throw new IOException("Kimodo требует латинское имя файла модели или промпта.");
        string source = file ? Path.GetDirectoryName(path)! : path;
        var shortPath = new StringBuilder(32768);
        uint length = GetShortPathName(source, shortPath, (uint)shortPath.Capacity);
        if (length == 0 || length >= shortPath.Capacity || shortPath.ToString().Any(character => character > 127))
            throw new IOException("Kimodo не может открыть путь с кириллицей: для этой папки недоступно короткое имя Windows (8.3). Укажите файлы в папке с латинским путём.");
        return file ? Path.Combine(shortPath.ToString(), leaf) : shortPath.ToString();
    }

    private sealed class KimodoWindow() : LuaToolWindow("Создать черновую анимацию по тексту")
    { public void Add(UIElement content) => Body.Children.Add(content); }

    internal static string SuggestKimodoClipId(string template, IEnumerable<string> existing)
    {
        string stem = Regex.Replace(template, "[^A-Za-z0-9_]", "_").Trim('_');
        if (stem.Length == 0) stem = "clip";
        if (!char.IsAsciiLetter(stem[0])) stem = "a" + stem;
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        for (int number = 1; ; number++)
        {
            string ending = number == 1 ? "_ai" : "_ai" + number.ToString(CultureInfo.InvariantCulture);
            string candidate = stem[..Math.Min(stem.Length, 15 - ending.Length)] + ending;
            if (!names.Contains(candidate)) return candidate;
        }
    }

    internal static bool TryReadKimodoStep(string line, int total, out int completed)
    {
        completed = 0;
        var match = Regex.Match(line, @"^profile motion\.diffusion_step index=(\d+) ms=");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= total)
            return false;
        completed = total - index;
        return true;
    }

    internal static ulong ResolveKimodoSeed(ulong requested) => requested == 0
        ? (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue)
        : requested;

    internal static ulong NextKimodoSeed(ulong previous) => previous == ulong.MaxValue ? 1 : previous + 1;

    internal static async Task VerifyKinematicGeneratorAsync(string executable)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--capabilities");
        using var process = Process.Start(start) ?? throw new IOException("Не удалось проверить генератор Kimodo.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        { if (!process.HasExited) process.Kill(true); throw new TimeoutException("Генератор Kimodo не ответил на проверку возможностей."); }
        string capabilities = await process.StandardOutput.ReadToEndAsync();
        if (process.ExitCode != 0 || !capabilities.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Contains("spatial-v1"))
            throw new InvalidDataException("Этот kmd-generate.exe не поддерживает Kinematic Constraints Config (spatial-v1). Выберите проверенный генератор новой версии.");
    }

    internal static async Task<string> SuggestKimodoReference(FigureAnimationSet animations, string current)
    {
        string? idle = animations.Names.FirstOrDefault(name => name.Equals("cidle09", StringComparison.OrdinalIgnoreCase));
        if (idle is null || idle.Equals(current, StringComparison.OrdinalIgnoreCase)) return current;
        var currentClip = await animations.LoadAsync(current);
        var idleClip = await animations.LoadAsync(idle);
        if (!idleClip.Tracks.TryGetValue("hp", out var idleRoot) || idleRoot.Positions.Length == 0) return current;
        if (!currentClip.Tracks.TryGetValue("hp", out var currentRoot) || currentRoot.Positions.Length == 0) return idle;
        if (!animations.Bones.TryGetValue("ll1", out var upper) ||
            !animations.Bones.TryGetValue("ll2", out var lower) ||
            !animations.Bones.TryGetValue("ll3", out var foot)) return current;
        float leg = upper.Length() + lower.Length() + foot.Length();
        return leg > 0 && currentRoot.Position(0).Z < idleRoot.Position(0).Z - leg * .5f ? idle : current;
    }

    private void InitializeKimodo(StackPanel sidebar)
    {
        _workingClips.Add(_name);
        _workingClipPicker.ItemsSource = _workingClips;
        _workingClipPicker.SelectedItem = _name;
        _workingClipPicker.SelectionChanged += async (_, _) =>
        {
            if (_settingWorkingClip || _workingClipPicker.SelectedItem is not string selected || selected == _name) return;
            await Run(() => SwitchWorkingClip(selected));
        };
        sidebar.Children.Insert(0, _workingClipPicker);
        var button = ActionButton("✦ Создать движение по тексту…", () => { });
        button.ToolTip = "Kimodo SOMA30 создаёт новый редактируемый клип. Установленные с редактором генератор и веса выбраны по умолчанию; исходная анимация сохраняется.";
        button.Click += async (_, _) => await Run(() => CreateKimodoDraft(false));
        sidebar.Children.Insert(0, button);
        var variant = ActionButton("✦ Вариант текущей анимации…", () => { });
        variant.ToolTip = "Создать движение по тексту и сравнить с текущим. Применение к кадрам/частям с прежним ID, без записи на диск.";
        variant.Click += async (_, _) => await Run(() => CreateKimodoDraft(true));
        sidebar.Children.Insert(1, variant);
        var reroll = ActionButton("↻ Ещё вариант с другим seed", () => { });
        reroll.ToolTip = "Повторить последний запрос и настройки этой модели с seed + 1. Новый результат останется черновиком в этом же окне; сохранение ANM выполняется отдельно.";
        reroll.Click += async (_, _) => await Run(() => CreateKimodoDraft(DraftPreferences.Load().ReplaceCurrent, true));
        sidebar.Children.Insert(2, reroll);
        var replay = ActionButton("↶ Импортировать сохранённый результат Kimodo…", () => { });
        replay.ToolTip = "Повторный перенос уже рассчитанных потоков SOMA30 из истории генерации. Kimodo не запускается повторно.";
        replay.Click += async (_, _) => await Run(ReplayKimodoHistory);
        sidebar.Children.Insert(3, replay);
    }

    private async Task CreateKimodoDraft(bool replaceCurrent, bool quickReroll = false)
    {
        var remembered = DraftPreferences.Load();
        if (quickReroll && (!remembered.HasRun || !remembered.Model.Equals(_scene.Model, StringComparison.OrdinalIgnoreCase)))
            quickReroll = false;
        string sourceName = quickReroll && _editor.Preview().Names.Contains(remembered.Template, StringComparer.OrdinalIgnoreCase)
            ? remembered.Template : _name;
        var dialog = new KimodoWindow { Owner = this, Width = 760, Height = 670 };
        if (replaceCurrent) dialog.Title = "Сгенерировать вариант — " + _name;
        var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        var advanced = new StackPanel();
        var prompt = new TextBox { Text = remembered.Prompt, AcceptsReturn = true, Height = 82, TextWrapping = TextWrapping.Wrap,
            ToolTip = "Одно короткое действие по-английски, например: A person walks slowly forward." };
        var avoid = new TextBox { Text = remembered.Avoid, AcceptsReturn = true, Height = 55, TextWrapping = TextWrapping.Wrap };
        var constraints = new TextBox { Text = remembered.KinematicConstraintsJson, AcceptsReturn = true,
            Height = 170, MinWidth = 650, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var executable = new TextBox { Text = string.IsNullOrWhiteSpace(remembered.Executable) ? _kimodoExecutable : remembered.Executable };
        var motion = new TextBox { Text = string.IsNullOrWhiteSpace(remembered.Motion) ? _kimodoMotion : remembered.Motion };
        var text = new TextBox { Text = string.IsNullOrWhiteSpace(remembered.Text) ? _kimodoText : remembered.Text };
        var frames = new TextBox { Text = remembered.HasRun ? remembered.Frames : replaceCurrent ? Math.Clamp(_loadedClip?.FrameCount ?? 60, 2, 1000).ToString() : "60" };
        var steps = new TextBox { Text = remembered.Steps };
        ulong.TryParse(remembered.Seed, out ulong requestedSeed);
        ulong.TryParse(remembered.LastUsedSeed, out ulong lastUsedSeed);
        var seed = new TextBox { Text = quickReroll && (requestedSeed != 0 || lastUsedSeed != 0)
            ? NextKimodoSeed(lastUsedSeed != 0 ? lastUsedSeed : requestedSeed).ToString(CultureInfo.InvariantCulture)
            : remembered.Seed };
        var id = new TextBox { Text = SuggestKimodoClipId(sourceName, _editor.Preview().Names) };
        var title = new TextBox { Text = remembered.Title };
        var playback = new ComboBox { ItemsSource = new[] { "Повтор на месте — танец, покой", "Одно действие с перемещением — шаг, прыжок, удар" }, SelectedIndex = remembered.Playback is 0 or 1 ? remembered.Playback : 0 };
        var endpointNames = _editor.Preview().Names.Where(name => name.Equals(sourceName, StringComparison.OrdinalIgnoreCase) || name.Contains("idle", StringComparison.OrdinalIgnoreCase)).ToArray();
        var endpoints = new ComboBox { ItemsSource = endpointNames, IsEnabled = !replaceCurrent };
        endpoints.IsEnabled = !replaceCurrent && string.IsNullOrWhiteSpace(constraints.Text);
        constraints.TextChanged += (_, _) => endpoints.IsEnabled = !replaceCurrent && string.IsNullOrWhiteSpace(constraints.Text);
        endpoints.SelectedItem = endpointNames.FirstOrDefault(name => name.Equals(remembered.EndpointReference, StringComparison.OrdinalIgnoreCase))
            ?? endpointNames.FirstOrDefault(name => name.Equals("cidle09", StringComparison.OrdinalIgnoreCase)) ?? sourceName;
        KimodoDraftPreferences Snapshot(bool ran) => new()
        {
            Model = _scene.Model, Template = sourceName, Prompt = prompt.Text, Avoid = avoid.Text,
            KinematicConstraintsJson = constraints.Text,
            Frames = frames.Text, Steps = steps.Text, Seed = seed.Text, LastUsedSeed = remembered.LastUsedSeed, Title = title.Text,
            EndpointReference = endpoints.SelectedItem as string ?? sourceName,
            Executable = executable.Text, Motion = motion.Text, Text = text.Text,
            Playback = playback.SelectedIndex, ReplaceCurrent = replaceCurrent, HasRun = ran
        };
        void Field(string label, string hint, TextBox box)
        { panel.Children.Add(Paragraph(label)); panel.Children.Add(box); panel.Children.Add(Paragraph(hint)); }
        void FileField(string label, TextBox box, string filter)
        {
            advanced.Children.Add(Paragraph(label));
            var row = new DockPanel(); var browse = ActionButton("Обзор…", () => { });
            browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (picker.ShowDialog(dialog) == true) box.Text = picker.FileName; };
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box); advanced.Children.Add(row);
        }
        panel.Children.Add(Paragraph(replaceCurrent
            ? "Опишите желаемое движение целиком. Kimodo не получает исходную анимацию: это новый вариант по тексту, который можно смешать с текущим клипом. После генерации выберите кадры, части тела и силу изменения. До принятия оригинал не изменяется."
            : "Опишите одно действие по-английски. Результат откроется как новый черновой клип для покадровой правки. Исходная анимация не изменится."));
        Field("Действие персонажа", "Например: A person walks slowly forward. Укажите направление, темп и действие; короткий запрос обычно понятнее модели.", prompt);
        Field("Чего избегать", "Kimodo не имеет отдельного negative prompt: этот текст добавляется к основному запросу как инструкция Avoid. Не гарантирует исключение движения.", avoid);
        var constraintPanel = new StackPanel { Margin = new Thickness(4) };
        constraintPanel.Children.Add(Paragraph("JSON версии 1: Kimodo Y вверх, X/Z по земле, метры, кадры 0…Кадров−1. Цели влияют на инференс; игровые опоры и точность проверяйте после импорта. JSON отключает приведение начала и конца к анимации покоя; для точных переходов оставьте JSON пустым."));
        constraintPanel.Children.Add(constraints);
        var constraintButtons = new WrapPanel();
        var rootPreview = new Canvas { Width = 320, Height = 180, Background = Brushes.Black,
            ToolTip = "Вид сверху: X вправо, Z вверх. Это заданные точки корня, а не обещание точной траектории Kimodo." };
        void DrawRootPreview(KinematicResolved resolved)
        {
            rootPreview.Children.Clear();
            var roots = resolved.NativeTargets.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("root ", StringComparison.Ordinal))
                .Select(line => line.Split(' '))
                .Select(fields => (Frame: int.Parse(fields[1], CultureInfo.InvariantCulture),
                    X: double.Parse(fields[2], CultureInfo.InvariantCulture),
                    Z: double.Parse(fields[3], CultureInfo.InvariantCulture))).ToArray();
            if (roots.Length == 0) return;
            double minX = roots.Min(r => r.X), maxX = roots.Max(r => r.X),
                minZ = roots.Min(r => r.Z), maxZ = roots.Max(r => r.Z);
            double span = Math.Max(.1, Math.Max(maxX - minX, maxZ - minZ));
            var points = new PointCollection(roots.Select(r => new Point(
                20 + (r.X - minX) / span * 280, 160 - (r.Z - minZ) / span * 140)));
            rootPreview.Children.Add(new Polyline { Points = points, Stroke = Brushes.LightGreen, StrokeThickness = 2 });
            for (int index = 0; index < roots.Length; index++)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.Yellow };
                Canvas.SetLeft(dot, points[index].X - 3.5); Canvas.SetTop(dot, points[index].Y - 3.5);
                rootPreview.Children.Add(dot);
                var label = new TextBlock { Text = roots[index].Frame.ToString(CultureInfo.InvariantCulture), Foreground = Brushes.White };
                Canvas.SetLeft(label, points[index].X + 4); Canvas.SetTop(label, points[index].Y - 12);
                rootPreview.Children.Add(label);
            }
        }
        void ConstraintButton(string caption, Action action) => constraintButtons.Children.Add(ActionButton(caption, () =>
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or JsonException)
            { MessageBox.Show(dialog, ex.Message, "Ошибка Kinematic Constraints", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }));
        ConstraintButton("Пример траектории", () =>
        {
            int count = int.Parse(frames.Text, CultureInfo.InvariantCulture);
            constraints.Text = JsonSerializer.Serialize(new { version = 1, space = "kimodo_y_up_meters",
                root2d = new[] { new { frame = 0, x = 0f, z = 0f },
                    new { frame = count - 1, x = 0f, z = .5f } } }, new JsonSerializerOptions { WriteIndented = true });
        });
        ConstraintButton("Загрузить…", () =>
        { var picker = new OpenFileDialog { Filter = "JSON|*.json" }; if (picker.ShowDialog(dialog) == true) constraints.Text = File.ReadAllText(picker.FileName); });
        ConstraintButton("Сохранить…", () =>
        { var picker = new SaveFileDialog { Filter = "JSON|*.json" }; if (picker.ShowDialog(dialog) == true) File.WriteAllText(picker.FileName, constraints.Text, new UTF8Encoding(false)); });
        ConstraintButton("Поза текущего кадра", () =>
        {
            int count = int.Parse(frames.Text, CultureInfo.InvariantCulture);
            int frame = CurrentFrame;
            if (frame < 0 || frame >= count) throw new ArgumentException("Текущий кадр вне длины нового движения.");
            JsonObject node = string.IsNullOrWhiteSpace(constraints.Text)
                ? new JsonObject { ["version"] = 1, ["space"] = "kimodo_y_up_meters" }
                : JsonNode.Parse(constraints.Text) as JsonObject ?? throw new InvalidDataException("Ожидался объект JSON.");
            if (node["key_poses"] is not JsonArray poses)
            { poses = new JsonArray(); node["key_poses"] = poses; }
            poses.Add(new JsonObject { ["frame"] = frame, ["source_clip"] = _name, ["source_frame"] = frame });
            string proposed = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            _ = KinematicConstraintsConfig.Parse(proposed, count).ResolveAsync(_editor.Preview(), count,
                _scene.Animations, _scene.Meshes).GetAwaiter().GetResult();
            constraints.Text = proposed;
        });
        ConstraintButton("Проверить", () =>
        {
            int count = int.Parse(frames.Text, CultureInfo.InvariantCulture);
            var resolved = KinematicConstraintsConfig.Parse(constraints.Text, count)
                .ResolveAsync(_editor.Preview(), count, _scene.Animations, _scene.Meshes).GetAwaiter().GetResult();
            DrawRootPreview(resolved);
            MessageBox.Show(dialog, $"Проверено: {resolved.RootCount} точек корня и {resolved.JointCount} целей суставов SOMA30.",
                "Kinematic Constraints", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        constraintPanel.Children.Add(constraintButtons);
        constraintPanel.Children.Add(rootPreview);
        panel.Children.Add(new Expander { Header = "Kinematic Constraints Config (JSON)", Content = constraintPanel,
            IsExpanded = !string.IsNullOrWhiteSpace(remembered.KinematicConstraintsJson) });
        panel.Children.Add(Paragraph("Как проигрывать результат")); panel.Children.Add(playback);
        panel.Children.Add(Paragraph("Повтор на месте убирает накопленный уход по горизонтали и сводит последние кадры с началом. Приседания и подъёмы сохраняются. Для ходьбы по траектории выберите одно действие."));
        panel.Children.Add(Paragraph("Высота над землёй определяется автоматически по скелету и геометрии стоп. Приседания и отрыв от земли переносятся из движения Kimodo; выбирать опорную анимацию не нужно."));
        if (!replaceCurrent)
        {
            panel.Children.Add(Paragraph("Начало и конец из анимации покоя")); panel.Children.Add(endpoints);
            panel.Children.Add(Paragraph("При пустом JSON точные первый и последний кадры берутся из выбранного клипа той же модели, соседние кадры плавно смешиваются. При заполненном JSON эта опора отключена, чтобы не переписать цели. Качество переходов проверьте в просмотре."));
            Field("ID новой анимации", "Имя для ANM и игрового ADB: до 15 латинских букв, цифр и _, уникальное. После сохранения ANM зарегистрируйте клип в игровом списке ADB.", id);
            Field("Русское название", "Подпись в списке редактора для удобного поиска; технический ID остаётся отдельным.", title);
        }
        else panel.Children.Add(Paragraph("После принятия сохранятся ID «" + _name + "», число кадров и запись ADB. Генерируемое движение будет подогнано по длительности выбранного участка."));
        var numbers = new WrapPanel();
        foreach (var (label, box, hint) in new[] {
            ("Кадров", frames, "Длина клипа; 60 кадров ≈ 2 с при 30 кадр/с."),
            ("Шагов", steps, "Качество генерации; больше шагов — дольше ожидание."),
            ("Seed", seed, "0 — выбрать случайный seed. После создания его номер появится в редакторе и истории; введённое число даст повторяемый результат.") })
        {
            var group = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
            group.Children.Add(Paragraph(label)); group.Children.Add(box); group.Children.Add(Paragraph(hint)); numbers.Children.Add(group);
        }
        panel.Children.Add(numbers);
        advanced.Children.Add(Paragraph("Пути уже настроены для установленного Kimodo. Изменяйте их только для другого комплекта моделей."));
        FileField("kmd-generate.exe", executable, "Программа Kimodo|kmd-generate.exe|Программы|*.exe");
        FileField("Модель SOMA RP/SEED (*.gguf)", motion, "Модель GGUF|*.gguf");
        FileField("Текстовый GGUF или папка комплекта", text, "Текстовый GGUF|*.gguf|Все файлы|*.*");
        var chooseFolder = ActionButton("Выбрать папку текстового комплекта…", () => { });
        chooseFolder.Click += (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) text.Text = picker.FolderName; };
        advanced.Children.Add(chooseFolder);
        panel.Children.Add(new Expander { Header = "Пути генератора и моделей", Content = advanced, IsExpanded = false, Margin = new Thickness(4, 8, 4, 0) });
        var ok = ActionButton("Создать движение", () => dialog.DialogResult = true);
        DockPanel.SetDock(ok, Dock.Bottom);
        dialog.Add(ok);
        dialog.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        bool accepted = quickReroll || dialog.ShowDialog() == true;
        try { DraftPreferences.Save(Snapshot(accepted || remembered.HasRun)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _message.Text = "Не удалось сохранить настройки генерации: " + ex.Message; }
        if (!accepted) return;

        KimodoGenerationHistory? history = null;
        string? temporary = null;
        bool inferenceCompleted = false;
        try
        {
        string effectivePrompt = KimodoDraftPreferences.EffectivePrompt(prompt.Text, avoid.Text);
        if (string.IsNullOrWhiteSpace(prompt.Text) || effectivePrompt.Length > 2000 || effectivePrompt.IndexOf('\0') >= 0)
            throw new ArgumentException("Описание должно содержать от 1 до 2000 символов.");
        if (!File.Exists(executable.Text) || !File.Exists(motion.Text) || !(File.Exists(text.Text) || Directory.Exists(text.Text)))
            throw new FileNotFoundException("Укажите существующие kmd-generate.exe, SOMA GGUF и текстовый комплект Kimodo.");
        if (!int.TryParse(frames.Text, out int count) || count is < 2 or > 1000 ||
            !int.TryParse(steps.Text, out int diffusion) || diffusion is < 1 or > 200 ||
            !ulong.TryParse(seed.Text, out ulong requestedSeedForRun))
            throw new ArgumentException("Кадров: 2–1000; шагов: 1–200; seed: неотрицательное целое число.");
        ulong randomSeed = ResolveKimodoSeed(requestedSeedForRun);
        KinematicResolved? resolvedConstraints = null;
        if (!string.IsNullOrWhiteSpace(constraints.Text))
        {
            resolvedConstraints = await KinematicConstraintsConfig.Parse(constraints.Text, count)
                .ResolveAsync(_editor.Preview(), count, _scene.Animations, _scene.Meshes);
            await VerifyKinematicGeneratorAsync(executable.Text);
        }
        _kimodoExecutable = executable.Text; _kimodoMotion = motion.Text; _kimodoText = text.Text;
        string newId = id.Text.Trim();
        FigureAnimationEditor.ValidateNewId(newId);
        // Validate the new ID before spending time on inference.
        var created = new FigureAnimationEditor(_editor.Preview());
        var footGeometry = created.KimodoFootGeometry(_scene.Meshes);
        await created.CreateAnimationAsync(sourceName, newId, count, true);
        await created.ValidateKimodoTargetAsync(newId, count);
        if (!replaceCurrent && resolvedConstraints is null)
            await created.ValidateGeneratedEndpointReferenceAsync(newId, endpoints.SelectedItem as string ?? sourceName);
        var mode = playback.SelectedIndex == 0 ? KimodoPlayback.StationaryLoop : KimodoPlayback.Once;
        if (resolvedConstraints is not null && mode == KimodoPlayback.StationaryLoop)
            throw new ArgumentException("Для JSON-целей выберите «Одно действие с перемещением»: закрытие цикла изменяет заданные позиции.");
        temporary = Path.Combine(Path.GetTempPath(), "db-editor-kimodo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var progress = new KimodoWindow { Owner = this, Width = 570, Height = 245 };
        var progressText = Paragraph($"Seed {randomSeed} · подготовка моделей и текста…");
        var progressBar = new ProgressBar { Minimum = 0, Maximum = diffusion, Height = 16, IsIndeterminate = true, Margin = new Thickness(4, 6, 4, 6) };
        var progressHint = Paragraph("Во время построения движения появится процент завершённых шагов. Подготовка текста и запись клипа не имеют точного процента.");
        var cancelButton = ActionButton("Отменить генерацию", progress.Close);
        DockPanel.SetDock(cancelButton, Dock.Bottom);
        progress.Add(cancelButton);
        var progressPanel = new StackPanel(); progressPanel.Children.Add(progressText); progressPanel.Children.Add(progressBar); progressPanel.Children.Add(progressHint);
        progress.Add(progressPanel);
        using var cancelled = new CancellationTokenSource();
        bool finished = false;
        progress.Closing += (_, _) => { if (!finished) cancelled.Cancel(); };
        var clock = Stopwatch.StartNew();
        int completedSteps = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            int done = Volatile.Read(ref completedSteps);
            progressText.Text = done == 0
                ? $"Seed {randomSeed} · Kimodo готовит модели и текст · прошло {clock.Elapsed:mm\\:ss}"
                : $"Seed {randomSeed} · движение: {done}/{diffusion} шагов ({done * 100 / diffusion}%) · прошло {clock.Elapsed:mm\\:ss}";
            if (done > 0) { progressBar.IsIndeterminate = false; progressBar.Value = done; }
        };
        progress.Show();
        timer.Start();
        try
        {
            string promptFile = Path.Combine(temporary, "prompt.txt");
            string output = Path.Combine(temporary, "motion");
            await File.WriteAllTextAsync(promptFile, effectivePrompt, new UTF8Encoding(false));
            string spatialFile = Path.Combine(temporary, "spatial-targets.txt");
            if (resolvedConstraints is not null)
                await File.WriteAllTextAsync(spatialFile, resolvedConstraints.NativeTargets, new UTF8Encoding(false));
            Directory.CreateDirectory(output);
            string historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Редактор БД от Атамана", "generated-motions", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            var runRecord = new KimodoGenerationRecord
            {
                Model = _scene.Model, Template = sourceName, Id = newId, DisplayName = title.Text, Prompt = effectivePrompt,
                UserPrompt = prompt.Text, Avoid = avoid.Text,
                KinematicConstraintsJson = constraints.Text, ResolvedSpatialTargets = resolvedConstraints?.NativeTargets ?? "",
                EndpointReference = replaceCurrent || resolvedConstraints is not null ? "" : endpoints.SelectedItem as string ?? sourceName,
                Frames = count, Steps = diffusion, Seed = randomSeed, Playback = mode, ReplaceCurrent = replaceCurrent,
                SourceAnmSha256 = KimodoGenerationHistory.Sha(_editor.ExportBytes()),
                SkeletonSha256 = FigureAnimationEditor.SkeletonFingerprint(_editor.Preview()),
                FootGeometrySha256 = KimodoGenerationHistory.GeometrySha(footGeometry),
                EditorVersion = FileVersionInfo.GetVersionInfo(typeof(AnimationEditorDialog).Assembly.Location).ProductVersion ?? "",
                GeneratorPath = executable.Text, MotionWeightsPath = motion.Text, TextWeightsPath = text.Text
            };
            history = await Task.Run(() => KimodoGenerationHistory.Begin(historyPath, runRecord));
            var start = new ProcessStartInfo(executable.Text) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["KIMODO_PROFILE"] = "1";
            foreach (string arg in new[] { KimodoCompatiblePath(motion.Text), KimodoCompatiblePath(text.Text), KimodoCompatiblePath(promptFile), count.ToString(), diffusion.ToString(), randomSeed.ToString(), KimodoCompatiblePath(output) })
                start.ArgumentList.Add(arg);
            if (resolvedConstraints is not null)
            {
                start.ArgumentList.Add("--spatial"); start.ArgumentList.Add(KimodoCompatiblePath(spatialFile));
                start.ArgumentList.Add(resolvedConstraints.TextGuidance.ToString("R", CultureInfo.InvariantCulture));
                start.ArgumentList.Add(resolvedConstraints.ConstraintGuidance.ToString("R", CultureInfo.InvariantCulture));
            }
            cancelled.Token.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new IOException("Kimodo не запустился.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = Task.Run(async () =>
            {
                var errors = new StringBuilder();
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (TryReadKimodoStep(line, diffusion, out int done))
                        Volatile.Write(ref completedSteps, done);
                    else if (!line.StartsWith("profile ", StringComparison.Ordinal) && errors.Length < 4000)
                        errors.AppendLine(line);
                }
                return errors.ToString();
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancelled.Token);
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                if (File.Exists(Path.Combine(output, KimodoGenerationHistory.RootStream)) &&
                    File.Exists(Path.Combine(output, KimodoGenerationHistory.RotationStream)))
                    try { history.CaptureRaw(output); } catch (IOException) { /* Keep the temporary output. */ }
                if (cancelled.IsCancellationRequested) throw;
                throw new TimeoutException("Kimodo не завершил генерацию за 20 минут.");
            }
            string result = await stdout, error = await stderr;
            inferenceCompleted = process.ExitCode == 0;
            // Keep the raw streams before validating stdout, reading SOMA30, converting or diagnosing.
            if (File.Exists(Path.Combine(output, KimodoGenerationHistory.RootStream)) &&
                File.Exists(Path.Combine(output, KimodoGenerationHistory.RotationStream)))
                history.CaptureRaw(output);
            cancelled.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("Kimodo: " + (error.Length > 0 ? error[..Math.Min(500, error.Length)] : "код " + process.ExitCode));
            var match = Regex.Match(result, @"generated (\d+) frames with (\d+) joints");
            if (!match.Success || int.Parse(match.Groups[1].Value) != count || int.Parse(match.Groups[2].Value) != KimodoDraft.SomaJoints)
                throw new InvalidDataException("Kimodo вернул движение иной длины или не SOMA30.");
            progressText.Text = $"Движение: {diffusion}/{diffusion} шагов (100%) · прошло {clock.Elapsed:mm\\:ss}. Записываю клип…";
            progressBar.IsIndeterminate = false; progressBar.Value = diffusion;
            var issues = await history.ImportAsync(created, _scene.Meshes);
            cancelled.Token.ThrowIfCancellationRequested();
            try { DraftPreferences.Save(Snapshot(true) with { LastUsedSeed = randomSeed.ToString(CultureInfo.InvariantCulture) }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _message.Text = "Не удалось сохранить последний seed: " + ex.Message; }
            if (replaceCurrent)
            {
                finished = true; timer.Stop(); progress.Close();
                await ReviewKimodoVariant(await created.Preview().LoadAsync(newId), history.DirectoryPath, randomSeed);
                return;
            }
            AnimationNames.Shared.Save(_scene.Model, newId, title.Text);
            finished = true;
            timer.Stop();
            progress.Close();
            await ShowImportedKimodo(created, newId, mode, issues, history.DirectoryPath, randomSeed);
        }
        finally
        {
            finished = true;
            timer.Stop();
            if (progress.IsVisible) progress.Close();
            if (Directory.Exists(temporary) &&
                (history?.Record.RootStreamSha256 is not null ||
                 !File.Exists(Path.Combine(temporary, "motion", KimodoGenerationHistory.RootStream)) &&
                 !File.Exists(Path.Combine(temporary, "motion", KimodoGenerationHistory.RotationStream))))
                Directory.Delete(temporary, true);
        }
        }
        catch (OperationCanceledException)
        {
            try { history?.Mark("cancelled", "Отменено пользователем."); } catch (IOException) { /* raw temporary directory remains available below */ }
            _message.Text = "Создание движения отменено. Исходная анимация не изменена." +
                (history is null ? "" : " История: " + history.DirectoryPath) +
                (temporary is not null && Directory.Exists(temporary) ? " Временные потоки: " + temporary : "");
        }
        catch (Exception ex)
        {
            if (history is not null && history.Record.Status is ("prepared" or "generated"))
                try { history.Mark(history.Record.Status == "generated" && inferenceCompleted ? "import-failed" :
                    inferenceCompleted ? "history-write-failed" : "generation-failed", ex.Message); }
                catch { /* preserve raw temporary directory and report its location */ }
            string retained = (history is null ? "" : "\nИстория: " + history.DirectoryPath) +
                (temporary is not null && Directory.Exists(temporary) ? "\nВременные потоки: " + temporary : "");
            _message.Text = "Не удалось создать движение: " + ex.Message + retained;
            MessageBox.Show(this, ex.Message + retained, "Создание движения не удалось", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ReplayKimodoHistory()
    {
        var picker = new OpenFolderDialog { Title = "Выберите папку сохранённого результата Kimodo (generation.json)" };
        if (picker.ShowDialog(this) != true) return;
        var history = KimodoGenerationHistory.Open(picker.FolderName);
        history.VerifySource(_scene.Model, _editor);
        var record = history.Record;
        if (!record.Template.Equals(_name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Откройте исходную анимацию «" + record.Template + "» для повторного импорта.");
        var created = new FigureAnimationEditor(_editor.Preview());
        await created.CreateAnimationAsync(_name, record.Id, record.Frames, true);
        await created.ValidateKimodoTargetAsync(record.Id, record.Frames);
        _ = created.KimodoFootGeometry(_scene.Meshes);
        var issues = await history.ImportAsync(created, _scene.Meshes);
        if (record.ReplaceCurrent)
            await ReviewKimodoVariant(await created.Preview().LoadAsync(record.Id), history.DirectoryPath, record.Seed);
        else
        {
            AnimationNames.Shared.Save(_scene.Model, record.Id, record.DisplayName);
            await ShowImportedKimodo(created, record.Id, record.Playback, issues, history.DirectoryPath, record.Seed);
        }
    }

    internal string WorkingClipName => _name;
    internal int WorkingClipCount => _workingClips.Count;
    internal UnitModelPreviewView WorkingPreview => _preview;
    internal string WorkingGenerationSummary => _generationSummary.Text;

    internal async Task ShowImportedKimodo(FigureAnimationEditor created, string id, KimodoPlayback mode,
        IReadOnlyList<AnimationIssue> issues, string history, ulong? usedSeed = null)
    {
        // Keep one editor and one ANM history. A generated clip is an undoable addition,
        // not a child window with a divergent copy of its parent's pending edits.
        var baseline = await created.Preview().LoadAsync(id);
        _editor.AdoptSavedBytes(created.ExportBytes());
        _generatedBaselines[id] = baseline;
        if (!_workingClips.Contains(id, StringComparer.OrdinalIgnoreCase)) _workingClips.Add(id);
        _workingClipPicker.Items.Refresh();
        await SwitchWorkingClip(id);
        _preview.EditorLoop = mode == KimodoPlayback.StationaryLoop;
        _generationSummary.Text = (mode == KimodoPlayback.StationaryLoop
            ? "Создан повтор на месте: убран горизонтальный уход, сведён стык кадров."
            : "Создано одно действие. Траектория сохранена, повтор выключен.") +
            (issues.Count == 0 ? "\nЧисловая проверка не обнаружила резких скачков. Это не проверка в игре."
                : $"\nОсталось замечаний: {issues.Count}. Проверьте кадры во вкладке «Проверка» перед сохранением.") +
            (usedSeed is null ? "" : $"\nSeed: {usedSeed.Value}.") +
            "\nИсходное движение и настройки сохранены:\n" + history;
        _message.Text = "Новый клип «" + id + "» открыт в этом окне." +
            (usedSeed is null ? "" : $" Seed: {usedSeed.Value}.") +
            " Предыдущий клип доступен в списке сверху; обе анимации сохраняются вместе обычной кнопкой «Сохранить».";
    }

    internal async Task SwitchWorkingClip(string id)
    {
        if (!_editor.Preview().Names.Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Клип отсутствует в текущем состоянии ANM. Повторите действие или выберите другой клип.");
        if (_name.Equals(id, StringComparison.OrdinalIgnoreCase)) return;
        if (_loadedClip is not null) _workingSettings[_name] = Settings();
        if (_savedProject is not null) _workingSavedProjects[_name] = _savedProject;
        _name = id;
        _originalClip = _generatedBaselines.GetValueOrDefault(id);
        _savedProject = _workingSavedProjects.GetValueOrDefault(id);
        _loadingSettings = true;
        try
        {
            _pins.Clear(); _leftFoot.IsChecked = false; _rightFoot.IsChecked = false;
            _editor.MotionPins = new Dictionary<string, System.Numerics.Vector3>();
            _before.IsChecked = false; _ghost.IsChecked = false;
            _editor.Contacts = [];
            _editor.LockedParts.Clear(); _editor.UnlockedLimbs.Clear();
            _poses.Clear(); RefreshLibrary();
            if (_workingSettings.TryGetValue(id, out var saved))
            {
                foreach (var pin in saved.Pins) _pins[pin.Key] = pin.Value;
                _editor.Contacts = saved.Contacts.ToArray();
                _editor.LockedParts.UnionWith(saved.LockedParts);
                _editor.UnlockedLimbs.UnionWith(saved.UnlockedLimbs);
                _poses.AddRange(saved.Poses); RefreshLibrary();
                _leftFoot.IsChecked = _pins.ContainsKey("ll3"); _rightFoot.IsChecked = _pins.ContainsKey("rl3");
                _ground.IsChecked = saved.Ground ?? true; _showPoints.IsChecked = saved.ShowPoints ?? true;
                _smooth.Text = saved.SmoothRadius.ToString(); _limits.IsChecked = saved.Limits;
                _preview.EditorFps = saved.Fps; _preview.EditorLoop = saved.Loop;
                _trajectoryCheck.IsChecked = saved.Trajectory; _onionCheck.IsChecked = saved.Onion;
            }
        }
        finally { _loadingSettings = false; }
        Title = "Редактирование анимации — " + id;
        _settingWorkingClip = true;
        try { _workingClipPicker.SelectedItem = id; }
        finally { _settingWorkingClip = false; }
        await Refresh();
    }
}
