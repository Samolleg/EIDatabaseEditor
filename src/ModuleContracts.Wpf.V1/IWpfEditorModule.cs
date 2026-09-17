using System.Windows;
using DatabaseEditor.Modules.V1;
namespace DatabaseEditor.Modules.Wpf.V1;
public interface IWpfEditorModule : IEditorModule
{
    FrameworkElement CreateView();
}
