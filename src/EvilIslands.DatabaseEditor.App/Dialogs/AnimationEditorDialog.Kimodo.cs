using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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

    private void InitializeKimodo(StackPanel sidebar)
    {
        var button = ActionButton("✦ Создать движение по тексту…", () => { });
        button.ToolTip = "Kimodo SOMA30 создаёт новый редактируемый клип. Установленные с редактором генератор и веса выбраны по умолчанию; исходная анимация сохраняется.";
        button.Click += async (_, _) => await Run(CreateKimodoDraft);
        sidebar.Children.Insert(0, button);
    }

    private async Task CreateKimodoDraft()
    {
        var dialog = new KimodoWindow { Owner = this, Width = 760, Height = 670 };
        var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        var advanced = new StackPanel();
        var prompt = new TextBox { Text = "A person walks slowly forward", AcceptsReturn = true, Height = 82, TextWrapping = TextWrapping.Wrap,
            ToolTip = "Одно короткое действие по-английски, например: A person walks slowly forward." };
        var executable = new TextBox { Text = _kimodoExecutable };
        var motion = new TextBox { Text = _kimodoMotion };
        var text = new TextBox { Text = _kimodoText };
        var frames = new TextBox { Text = "60" };
        var steps = new TextBox { Text = "20" };
        var seed = new TextBox { Text = "1" };
        var id = new TextBox { Text = SuggestKimodoClipId(_name, _editor.Preview().Names) };
        var title = new TextBox { Text = "Черновик движения" };
        void Field(string label, string hint, TextBox box)
        { panel.Children.Add(Paragraph(label)); panel.Children.Add(box); panel.Children.Add(Paragraph(hint)); }
        void FileField(string label, TextBox box, string filter)
        {
            advanced.Children.Add(Paragraph(label));
            var row = new DockPanel(); var browse = ActionButton("Обзор…", () => { });
            browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (picker.ShowDialog(dialog) == true) box.Text = picker.FileName; };
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box); advanced.Children.Add(row);
        }
        panel.Children.Add(Paragraph("Опишите одно действие по-английски. Результат откроется как новый черновой клип для покадровой правки. Исходная анимация не изменится."));
        Field("Действие персонажа", "Например: A person walks slowly forward. Укажите направление, темп и действие; короткий запрос обычно понятнее модели.", prompt);
        Field("ID новой анимации", "Имя для ANM и игрового ADB: до 15 латинских букв, цифр и _, уникальное. После сохранения ANM зарегистрируйте клип в игровом списке ADB.", id);
        Field("Русское название", "Подпись в списке редактора для удобного поиска; технический ID остаётся отдельным.", title);
        var numbers = new WrapPanel();
        foreach (var (label, box, hint) in new[] {
            ("Кадров", frames, "Длина клипа; 60 кадров ≈ 2 с при 30 кадр/с."),
            ("Шагов", steps, "Качество генерации; больше шагов — дольше ожидание."),
            ("Seed", seed, "Повторяемость результата при тех же настройках.") })
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
        if (dialog.ShowDialog() != true) return;

        try
        {
        if (string.IsNullOrWhiteSpace(prompt.Text) || prompt.Text.Length > 2000 || prompt.Text.IndexOf('\0') >= 0)
            throw new ArgumentException("Описание должно содержать от 1 до 2000 символов.");
        if (!File.Exists(executable.Text) || !File.Exists(motion.Text) || !(File.Exists(text.Text) || Directory.Exists(text.Text)))
            throw new FileNotFoundException("Укажите существующие kmd-generate.exe, SOMA GGUF и текстовый комплект Kimodo.");
        if (!int.TryParse(frames.Text, out int count) || count is < 2 or > 1000 ||
            !int.TryParse(steps.Text, out int diffusion) || diffusion is < 1 or > 200 ||
            !ulong.TryParse(seed.Text, out ulong randomSeed))
            throw new ArgumentException("Кадров: 2–1000; шагов: 1–200; seed: неотрицательное целое число.");
        _kimodoExecutable = executable.Text; _kimodoMotion = motion.Text; _kimodoText = text.Text;
        string newId = id.Text.Trim();
        FigureAnimationEditor.ValidateNewId(newId);
        // Validate the new ID before spending time on inference.
        var created = new FigureAnimationEditor(_editor.Preview());
        await created.CreateAnimationAsync(_name, newId, count, true);
        string temporary = Path.Combine(Path.GetTempPath(), "db-editor-kimodo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var progress = new KimodoWindow { Owner = this, Width = 570, Height = 245 };
        var progressText = Paragraph("Подготовка моделей и текста…");
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
                ? $"Kimodo готовит модели и текст · прошло {clock.Elapsed:mm\\:ss}"
                : $"Движение: {done}/{diffusion} шагов ({done * 100 / diffusion}%) · прошло {clock.Elapsed:mm\\:ss}";
            if (done > 0) { progressBar.IsIndeterminate = false; progressBar.Value = done; }
        };
        progress.Show();
        timer.Start();
        try
        {
            string promptFile = Path.Combine(temporary, "prompt.txt");
            string output = Path.Combine(temporary, "motion");
            await File.WriteAllTextAsync(promptFile, prompt.Text, new UTF8Encoding(false));
            Directory.CreateDirectory(output);
            var start = new ProcessStartInfo(executable.Text) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["KIMODO_PROFILE"] = "1";
            foreach (string arg in new[] { KimodoCompatiblePath(motion.Text), KimodoCompatiblePath(text.Text), KimodoCompatiblePath(promptFile), count.ToString(), diffusion.ToString(), randomSeed.ToString(), KimodoCompatiblePath(output) })
                start.ArgumentList.Add(arg);
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
                if (cancelled.IsCancellationRequested) throw;
                throw new TimeoutException("Kimodo не завершил генерацию за 20 минут.");
            }
            cancelled.Token.ThrowIfCancellationRequested();
            string result = await stdout, error = await stderr;
            if (process.ExitCode != 0) throw new IOException("Kimodo: " + (error.Length > 0 ? error[..Math.Min(500, error.Length)] : "код " + process.ExitCode));
            var match = Regex.Match(result, @"generated (\d+) frames with (\d+) joints");
            if (!match.Success || int.Parse(match.Groups[1].Value) != count || int.Parse(match.Groups[2].Value) != KimodoDraft.SomaJoints)
                throw new InvalidDataException("Kimodo вернул движение иной длины или не SOMA30.");
            progressText.Text = $"Движение: {diffusion}/{diffusion} шагов (100%) · прошло {clock.Elapsed:mm\\:ss}. Записываю клип…";
            progressBar.IsIndeterminate = false; progressBar.Value = diffusion;
            var draft = KimodoDraft.ReadSoma30(output, count);
            await created.ApplyKimodoDraftAsync(newId, draft);
            cancelled.Token.ThrowIfCancellationRequested();
            AnimationNames.Shared.Save(_scene.Model, newId, title.Text);
            var child = new AnimationEditorDialog(_scene with { Animations = created.Preview() }, newId) { Owner = this };
            child._saveBaseline = _saveBaseline?.ToArray();
            child._exported = Array.Empty<byte>();
            finished = true;
            timer.Stop();
            progress.Close();
            child.ShowDialog();
            await AdoptSavedChild(child);
        }
        finally
        {
            finished = true;
            timer.Stop();
            if (progress.IsVisible) progress.Close();
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        }
        catch (OperationCanceledException)
        {
            _message.Text = "Создание движения отменено. Исходная анимация не изменена.";
        }
        catch (Exception ex)
        {
            _message.Text = "Не удалось создать движение: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Создание движения не удалось", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
