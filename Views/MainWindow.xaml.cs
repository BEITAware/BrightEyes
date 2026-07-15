using BrightEyes.Processing.Contracts;
using BrightEyes.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using Brush = System.Windows.Media.Brush;

namespace BrightEyes.Views;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private object? _dragCandidate;

    public MainWindow()
    {
        InitializeComponent();
        ProcessRibbonCatalogBuilder.Populate(
            MainRibbon,
            ViewModel.ProcessRibbonGroups,
            ViewModel.InsertProcessCommand,
            ProcessRibbonButton_PreviewMouseLeftButtonDown,
            ProcessRibbonButton_PreviewMouseMove);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void ProcessRibbonButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as ProcessCatalogItemViewModel;
    }

    private void ProcessRibbonButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not ProcessCatalogItemViewModel item || !MovedEnough(e.GetPosition(this)))
            return;
        DragDrop.DoDragDrop((DependencyObject)sender, item.Descriptor, DragDropEffects.Copy);
        _dragCandidate = null;
    }

    private void PipelineList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        ListBoxItem? item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _dragCandidate = item?.DataContext as IImageProcess;
    }

    private void PipelineList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not IImageProcess process || !MovedEnough(e.GetPosition(this)))
            return;
        DragDrop.DoDragDrop(PipelineList, process, DragDropEffects.Move);
        _dragCandidate = null;
        ClearDropTarget();
    }

    private void PipelineList_DragOver(object sender, DragEventArgs e)
    {
        int index = GetInsertionIndex(e.GetPosition(PipelineList));
        string reason = "没有活动文档。";
        bool allowed;
        if (e.Data.GetDataPresent(typeof(ProcessDescriptor)))
        {
            var descriptor = (ProcessDescriptor)e.Data.GetData(typeof(ProcessDescriptor))!;
            allowed = ViewModel.ActiveDocument?.CanInsert(descriptor, index, out reason) == true;
        }
        else if (e.Data.GetDataPresent(typeof(IImageProcess)))
        {
            var process = (IImageProcess)e.Data.GetData(typeof(IImageProcess))!;
            allowed = ViewModel.ActiveDocument?.CanMove(process, index, out reason) == true;
        }
        else
        {
            allowed = false;
            reason = "拖动载荷不是已注册过程。";
        }

        e.Effects = allowed
            ? (e.Data.GetDataPresent(typeof(ProcessDescriptor)) ? DragDropEffects.Copy : DragDropEffects.Move)
            : DragDropEffects.None;
        PipelineList.ToolTip = reason;
        MarkDropTarget(index, allowed);
        e.Handled = true;
    }

    private void PipelineList_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    private void PipelineList_Drop(object sender, DragEventArgs e)
    {
        int index = GetInsertionIndex(e.GetPosition(PipelineList));
        if (e.Data.GetDataPresent(typeof(ProcessDescriptor)))
        {
            var descriptor = (ProcessDescriptor)e.Data.GetData(typeof(ProcessDescriptor))!;
            ViewModel.TryInsertProcess(descriptor, index, out _);
        }
        else if (e.Data.GetDataPresent(typeof(IImageProcess)))
        {
            var process = (IImageProcess)e.Data.GetData(typeof(IImageProcess))!;
            ViewModel.TryMoveProcess(process, index, out _);
        }
        ClearDropTarget();
        e.Handled = true;
    }

    private int GetInsertionIndex(Point point)
    {
        for (int index = 0; index < PipelineList.Items.Count; index++)
        {
            if (PipelineList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            Point top = item.TranslatePoint(new Point(0, 0), PipelineList);
            if (point.Y < top.Y + item.ActualHeight / 2.0) return index;
        }
        return PipelineList.Items.Count;
    }

    private void MarkDropTarget(int index, bool allowed)
    {
        ClearDropTarget();
        if (!allowed) return;
        if (index < PipelineList.Items.Count && PipelineList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            item.Tag = "DropTarget";
        else
            PipelineList.BorderBrush = (Brush)FindResource("IntermediateConnectionBrush");
        PipelineList.BorderThickness = new Thickness(0, 0, 0, 5);
    }

    private void ClearDropTarget()
    {
        foreach (object item in PipelineList.Items)
        {
            if (PipelineList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                container.ClearValue(TagProperty);
        }
        PipelineList.BorderThickness = new Thickness(0);
        PipelineList.ToolTip = null;
    }

    private bool MovedEnough(Point point) =>
        Math.Abs(point.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(point.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
