using System.Windows;
using System.Windows.Controls;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationEditorDialog
{
    private byte[]? _saveBaseline;
    internal FigureAnimationSet? SavedAnimationSet { get; private set; }
    internal string? SavedClipName { get; private set; }

    internal async Task AdoptSavedChild(AnimationEditorDialog child)
    {
        if (child.SavedAnimationSet is not { } saved) return;
        _editor.AdoptSavedBytes(saved.ExportBytes());
        _saveBaseline = saved.SaveTarget?.OriginalBytes.ToArray();
        _exported = saved.ExportBytes();
        SavedAnimationSet = saved;
        SavedClipName = child.SavedClipName;
        await Refresh();
        _message.Text = "Новый клип сохранён. Изменения доступны в родительском редакторе.";
    }

    internal async Task SaveToSourceAsync(bool backup, bool checkIssues = true)
    {
        var target = _scene.Animations?.SaveTarget ?? throw new InvalidOperationException("Источник ANM не определён.");
        if (_saveBaseline is null) throw new InvalidOperationException("Проект восстановлен, но исходный ANM изменён вне него. Сохраните отдельный ANM через экспорт; текущий источник защищён от перезаписи.");
        if (checkIssues && !await CheckBeforeExport()) return;
        _editor.NestFlatClip(_name);
        byte[] bytes = _editor.ExportBytes();
        var savedBackup = _scene.AnimationDatabase is { } actions
            ? await AnimationPairSave.SaveAsync(target, _saveBaseline, bytes, actions, actions.CreateDraft(), backup)
            : await target.SaveAsync(_saveBaseline, bytes, backup);
        _saveBaseline = bytes.ToArray(); _exported = bytes;
        SavedAnimationSet = _scene.Animations!.WithSavedBytes(bytes);
        SavedClipName = _name;
        _message.Text = "Сохранено: " + target.Description + (savedBackup is null ? " · без бэкапа." : "\nБэкап: " + savedBackup);
    }

    private void AddSaveButtons(Panel toolbar)
    {
        var target = _scene.Animations?.SaveTarget;
        _saveBaseline = target?.OriginalBytes.ToArray();
        foreach (bool backup in new[] { true, false })
        {
            var button = ActionButton(backup ? "Сохранить" : "Сохранить без бэкапа", () => { });
            button.IsEnabled = target is not null;
            button.ToolTip = target is null ? "Источник ANM не определён. Используйте экспорт." : "Записать текущий набор анимаций: " + target.Description;
            button.Click += async (_, _) => await Run(async () =>
            {
                if (target is not null) await SaveToSourceAsync(backup);
            });
            toolbar.Children.Add(button);
        }
    }
}
