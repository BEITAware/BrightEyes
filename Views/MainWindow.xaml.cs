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
using GiveFeedbackEventArgs = System.Windows.GiveFeedbackEventArgs;
using IDataObject = System.Windows.IDataObject;
using FormsCursor = System.Windows.Forms.Cursor;
using WpfCursors = System.Windows.Input.Cursors;

namespace BrightEyes.Views;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private object? _dragCandidate;
    private object? _activeDragPayload;
    private bool _dropCommitted;
    private int _dropTargetIndex = -1;
    private bool? _dropTargetCompatible;

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
        RunDrag((DependencyObject)sender, item.Descriptor, DragDropEffects.Copy);
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
        RunDrag(PipelineList, process, DragDropEffects.Move);
    }

    private void RunDrag(DependencyObject source, object payload, DragDropEffects effect)
    {
        _activeDragPayload = payload;
        _dropCommitted = false;
        UpdateDropPreview(payload, GetPointerPositionInPipeline());
        try
        {
            DragDrop.DoDragDrop(source, payload, effect);
            if (!_dropCommitted)
                CommitDrop(payload, GetPointerPositionInPipeline());
        }
        finally
        {
            _activeDragPayload = null;
            _dragCandidate = null;
            ClearDropPreview();
        }
    }

    private void PipelineDragOver(object sender, DragEventArgs e)
    {
        object? payload = GetDragPayload(e.Data) ?? _activeDragPayload;
        if (payload is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        UpdateDropPreview(payload, e.GetPosition(PipelineList));
        e.Effects = payload is ProcessDescriptor ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void PipelineDragGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_activeDragPayload is not null)
            UpdateDropPreview(_activeDragPayload, GetPointerPositionInPipeline());
        e.UseDefaultCursors = false;
        Mouse.SetCursor(WpfCursors.SizeAll);
        e.Handled = true;
    }

    private void PipelineDrop(object sender, DragEventArgs e)
    {
        object? payload = GetDragPayload(e.Data) ?? _activeDragPayload;
        if (payload is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        CommitDrop(payload, e.GetPosition(PipelineList));
        e.Effects = payload is ProcessDescriptor ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void UpdateDropPreview(object payload, Point point)
    {
        int index = GetNearestInsertionIndex(point);
        bool compatible = GetPlacementCompatibility(payload, index, out string reason);
        MarkDropTarget(index, compatible);
        PipelineHint.Text = compatible
            ? $"释放后放入插槽 {index + 1}/{PipelineList.Items.Count + 1}"
            : $"仍将放入插槽 {index + 1}/{PipelineList.Items.Count + 1}；{reason}";
    }

    private void CommitDrop(object payload, Point point)
    {
        int index = GetNearestInsertionIndex(point);
        bool compatible = GetPlacementCompatibility(payload, index, out string reason);
        bool placed = payload switch
        {
            ProcessDescriptor descriptor => ViewModel.TryInsertProcess(descriptor, index, out _),
            IImageProcess process => ViewModel.TryMoveProcess(process, index, out _),
            _ => false
        };

        _dropCommitted = true;
        ClearDropPreview();
        PipelineHint.Text = placed || payload is IImageProcess
            ? compatible
                ? $"已放入插槽 {index + 1}/{PipelineList.Items.Count + 1}"
                : $"已放入插槽 {index + 1}/{PipelineList.Items.Count + 1}；错误：{reason}"
            : $"未能放入：{reason}";
    }

    private bool GetPlacementCompatibility(object payload, int index, out string reason)
    {
        if (ViewModel.ActiveDocument is null)
        {
            reason = "没有活动文档。";
            return false;
        }

        return payload switch
        {
            ProcessDescriptor descriptor => ViewModel.ActiveDocument.CanInsert(descriptor, index, out reason),
            IImageProcess process => ViewModel.ActiveDocument.CanMove(process, index, out reason),
            _ => SetUnsupportedPayloadReason(out reason)
        };
    }

    private static bool SetUnsupportedPayloadReason(out string reason)
    {
        reason = "拖动载荷不是已注册过程。";
        return false;
    }

    private int GetNearestInsertionIndex(Point point)
    {
        int nearestIndex = 0;
        double nearestDistance = double.PositiveInfinity;

        for (int index = 0; index < PipelineList.Items.Count; index++)
        {
            if (PipelineList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            Point top = item.TranslatePoint(new Point(0, 0), PipelineList);
            ConsiderSlot(index, top.Y);
            ConsiderSlot(index + 1, top.Y + item.ActualHeight);
        }

        if (double.IsPositiveInfinity(nearestDistance))
            return point.Y <= PipelineList.ActualHeight / 2.0 ? 0 : PipelineList.Items.Count;
        return nearestIndex;

        void ConsiderSlot(int index, double y)
        {
            double distance = Math.Abs(point.Y - y);
            bool isCurrentTie = Math.Abs(distance - nearestDistance) < 0.5 && index == _dropTargetIndex;
            if (distance < nearestDistance - 0.5 || isCurrentTie)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }
    }

    private Point GetPointerPositionInPipeline()
    {
        System.Drawing.Point screen = FormsCursor.Position;
        return PipelineList.PointFromScreen(new Point(screen.X, screen.Y));
    }

    private static object? GetDragPayload(IDataObject data)
    {
        if (data.GetDataPresent(typeof(ProcessDescriptor)))
            return data.GetData(typeof(ProcessDescriptor));
        if (data.GetDataPresent(typeof(IImageProcess)))
            return data.GetData(typeof(IImageProcess));
        return null;
    }

    private void MarkDropTarget(int index, bool compatible)
    {
        if (_dropTargetIndex == index && _dropTargetCompatible == compatible) return;

        ClearDropVisuals();
        _dropTargetIndex = index;
        _dropTargetCompatible = compatible;
        PipelineDropSurface.Tag = compatible ? "DropAllowed" : "DropWarning";
        if (index < PipelineList.Items.Count && PipelineList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Tag = compatible ? "DropTargetBefore" : "DropTargetBeforeWarning";
        }
        else if (PipelineList.Items.Count > 0 &&
                 PipelineList.ItemContainerGenerator.ContainerFromIndex(PipelineList.Items.Count - 1) is ListBoxItem lastItem)
        {
            lastItem.Tag = compatible ? "DropTargetAfter" : "DropTargetAfterWarning";
        }
    }

    private void ClearDropPreview()
    {
        ClearDropVisuals();
        _dropTargetIndex = -1;
        _dropTargetCompatible = null;
    }

    private void ClearDropVisuals()
    {
        foreach (object item in PipelineList.Items)
        {
            if (PipelineList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                container.ClearValue(TagProperty);
        }
        PipelineDropSurface.ClearValue(TagProperty);
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
