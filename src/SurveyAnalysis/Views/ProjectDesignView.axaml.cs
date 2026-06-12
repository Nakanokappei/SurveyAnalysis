using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SurveyAnalysis.ViewModels;

namespace SurveyAnalysis.Views;

public partial class ProjectDesignView : UserControl
{
    public ProjectDesignView() => InitializeComponent();

    // Adds a field, then moves keyboard focus to the new item's 項目名 box so the user can
    // type straight away. The container is realized on the next layout pass, so defer the focus.
    private void OnAddFieldClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectDesignViewModel vm)
            return;
        vm.AddFieldCommand.Execute(null);

        Dispatcher.UIThread.Post(() =>
        {
            var index = vm.Fields.Count - 1;
            if (index < 0)
                return;
            var container = FieldsItems.ContainerFromIndex(index);
            var nameBox = container?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            nameBox?.Focus();
        }, DispatcherPriority.Background);
    }
}
