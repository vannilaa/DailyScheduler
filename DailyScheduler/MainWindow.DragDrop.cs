using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DailyScheduler
{
    // Drag & drop row reordering for dgSchedule.
    // Requires dgSchedule.ItemsSource to be an IList (e.g. ObservableCollection<ScheduleRow>).
    public partial class MainWindow : Window
    {
        private const string DragFormat = "ScheduleRow";

        private Point _dragStartPoint;
        private object _dragItem;

        // ---- Drag source (the ⋮⋮ handle) ----

        private void Handle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragItem = ((FrameworkElement)sender).DataContext;
        }

        private void Handle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null)
                return;

            Vector diff = _dragStartPoint - e.GetPosition(null);
            if (System.Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var data = new DataObject(DragFormat, _dragItem);
            _dragItem = null;
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }

        private void Handle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragItem = null;
        }

        // ---- Drop target (each DataGridRow) ----

        private void Row_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DragFormat)) return;

            var list = dgSchedule.ItemsSource as IList;
            if (list == null) return;

            object source = e.Data.GetData(DragFormat);
            object target = ((DataGridRow)sender).Item;
            if (ReferenceEquals(source, target)) return;

            int oldIndex = list.IndexOf(source);
            int newIndex = list.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0) return;

            list.RemoveAt(oldIndex);
            list.Insert(newIndex, source);

            dgSchedule.SelectedItem = source;
            RenumberRows();
            e.Handled = true;
        }

        // Call after any reorder so the Urutan column stays 1..N.
        private void RenumberRows()
        {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Urutan = i + 1;   // ScheduleRow raises PropertyChanged, so the grid updates itself
        }
    }
}