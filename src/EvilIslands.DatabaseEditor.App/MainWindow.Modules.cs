using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatabaseEditor.Modules.V1;
using DatabaseEditor.Modules.Host;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;

namespace EvilIslands.DatabaseEditor.App;
public partial class MainWindow
{
    private ModuleLoadResult? _modules;
    private LoadedModule? _activeModule;
    private bool _modulesClosed;
    private ModuleHostAdapter? _moduleHost;
    private void InitializeModules()
    {
        _moduleHost = new ModuleHostAdapter(this, _viewModel);
        _modules = ModuleLoader.Load(Path.Combine(AppContext.BaseDirectory, "Modules"), _moduleHost);
        var tabs = new TabControl { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
        foreach (var module in _modules.Modules)
        {
            try { tabs.Items.Add(new TabItem { Header = module.Manifest.ModuleId, Content = module.Instance.CreateView(), Tag = module }); }
            catch (Exception ex) { tabs.Items.Add(new TabItem { Header = module.Manifest.ModuleId, Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap } }); }
        }
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) ActivateModule((tabs.SelectedItem as TabItem)?.Tag as LoadedModule); };
        LoadedModule? singleModule = null;
        if (tabs.Items.Count == 1)
        {
            var item = (TabItem)tabs.Items[0];
            singleModule = item.Tag as LoadedModule;
            var view = item.Content;
            item.Content = null;
            ModulePane.Content = view;
        }
        else ModulePane.Content = tabs.Items.Count > 0 ? tabs : new TextBlock { Text = _modules.Errors.Count > 0 ? string.Join("\n", _modules.Errors) : "Совместимые модули не установлены. Обычные таблицы БД доступны.", Margin = new(20), TextWrapping = TextWrapping.Wrap };
        if (_modules.Errors.Count > 0 && tabs.Items.Count > 0)
            _viewModel.ReportSpreadsheetError(string.Join("\n", _modules.Errors));
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.CurrentSection))
                ActivateModule(_viewModel.CurrentSection == WorkspaceSection.Modules ? singleModule ?? (tabs.SelectedItem as TabItem)?.Tag as LoadedModule : null);
        };
    }
    private void ActivateModule(LoadedModule? module)
    {
        if (ReferenceEquals(module, _activeModule)) return;
        try { _activeModule?.Instance.Activate(false); } catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
        _activeModule = module;
        try { module?.Instance.Activate(true); } catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); }
    }
    private bool RouteModuleKey(KeyEventArgs e)
    {
        if (_viewModel.CurrentSection != WorkspaceSection.Modules || _activeModule is null) return false;
        ModuleCommand? command = e.Key == Key.F5 ? ModuleCommand.Validate : Keyboard.Modifiers == ModifierKeys.Control ? e.Key switch
        { Key.S => ModuleCommand.Save, Key.Z => ModuleCommand.Undo, Key.Y => ModuleCommand.Redo, Key.F => ModuleCommand.Find, _ => null } : null;
        if (command is null) return false;
        try { return _activeModule.Instance.Execute(command.Value); }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); return true; }
    }
    private bool CanCloseModules()
    {
        if (_modulesClosed || _modules is null) return true;
        foreach (var module in _modules.Modules)
            try { if (!module.Instance.CanClose()) return false; }
            catch (Exception ex) { _viewModel.ReportSpreadsheetError("Модуль не подтвердил закрытие: " + ex.Message); return false; }
        return true;
    }
    private void DisposeModules()
    {
        if (_modulesClosed) return;
        _modulesClosed = true;
        foreach (var module in _modules?.Modules ?? [])
            try { module.Instance.Dispose(); } catch { /* Main window must still dispose the remaining participants. */ }
    }
}
