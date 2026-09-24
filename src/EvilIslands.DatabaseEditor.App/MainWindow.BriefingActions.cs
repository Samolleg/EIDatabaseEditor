using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App;
public partial class MainWindow
{
    private void BriefingActions_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRecord is not { TableKey: "quests.briefings" } row || _viewModel.IsBusy) return;
        var dialog = BuildBriefingActionsDialog(row);
        dialog.Owner = this;
        ShowResizableEditorDialog(dialog);
    }

    internal Window BuildBriefingActionsDialog(RecordRowViewModel row)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        var dialog = new Window
        {
            Title = $"Действия брифинга — {row.DisplayName}", Width = 780, Height = 700,
            MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("WindowBrush"), Foreground = (Brush)FindResource("TextBrush"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Button Button(string label) => new() { Content = label, Margin = new Thickness(0, 5, 0, 5),
            Padding = new Thickness(10, 6, 10, 6), Style = (Style)FindResource("RoundedButton") };
        void Render()
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = row.DisplayName, FontSize = 20, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap,
                Text = "Параметры quests.qdb. Изменения сохраняются общей кнопкой «Сохранить»; отмена — Ctrl+Z в главном окне. Действия из скриптов карт и Lua здесь не отображаются." });
            if (row.IsTextOnlyBriefing)
            {
                panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = "Есть только текст в texts.res. Чтобы задать выдачу или изъятие, создайте запись в БД. Награды и списки действий новой записи будут пустыми." });
                var create = Button("Создать запись брифинга в БД");
                create.IsEnabled = _viewModel.IsEditMode;
                create.Click += (_, _) => { try { row = _viewModel.CreateSelectedBriefingRecord(); Render(); }
                    catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Не удалось создать запись"); } };
                panel.Children.Add(create);
            }
            else
            {
                foreach (int id in new[] { 3, 5, 1, 2, 6, 7, 8, 9, 4, 10 })
                {
                    var field = row.GetCell(id);
                    if (field is null) continue;
                    var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.Children.Add(new TextBlock { Text = field.DisplayName, VerticalAlignment = VerticalAlignment.Center });
                    var value = new TextBox { DataContext = field, IsReadOnly = field.Kind == "StringList" || !field.CanEdit,
                        MinWidth = 130, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(6),
                        Background = (Brush)FindResource("WindowBrush"), Foreground = (Brush)FindResource("TextBrush") };
                    value.SetBinding(TextBox.TextProperty, new Binding(nameof(RecordFieldViewModel.EditorText))
                        { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                    value.SetBinding(ToolTipProperty, new Binding(nameof(RecordFieldViewModel.ErrorMessage)));
                    Grid.SetColumn(value, 1); grid.Children.Add(value);
                    if (field.Kind == "StringList")
                    {
                        var edit = Button("Изменить…"); edit.IsEnabled = field.CanEdit;
                        edit.Click += (_, _) =>
                        {
                            if (!field.TryGetStringListValues(out var values)) return;
                            var list = new StructuredStringListEditorDialog(row.DisplayName, field.DisplayName, values,
                                BuildReferenceSuggestions(), updated =>
                                { bool ok = field.TrySetStringListValues(updated, out var error); return (ok, error); },
                                magicContext: id is 3 or 5 ? BuildEmbeddedMagicEditorContext() : null,
                                allowedElementKinds: id is 3 or 5 ? [StructuredElementKind.RawValue, StructuredElementKind.Weapon, StructuredElementKind.Armor] : null)
                                { Owner = dialog };
                            ShowResizableEditorDialog(list);
                        };
                        Grid.SetColumn(edit, 2); grid.Children.Add(edit);
                    }
                    panel.Children.Add(grid);
                    var errorText = new TextBlock { DataContext = field, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
                    errorText.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecordFieldViewModel.ErrorMessage)));
                    panel.Children.Add(errorText);
                }
            }
            var close = Button("Закрыть"); close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Click += (_, _) => dialog.Close(); panel.Children.Add(close);
        }
        Render();
        return dialog;
    }
}
