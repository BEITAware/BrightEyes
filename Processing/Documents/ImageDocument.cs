using BrightEyes.Processing.BuiltIn;
using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Parameters;
using BrightEyes.Processing.Execution;
using BrightEyes.Renderer;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;

namespace BrightEyes.Processing.Documents;

public sealed class PipelineChangedEventArgs : EventArgs
{
    public PipelineChangedEventArgs(bool requiresDecode, string reason)
    {
        RequiresDecode = requiresDecode;
        Reason = reason;
    }

    public bool RequiresDecode { get; }
    public string Reason { get; }
}

public sealed class ImageDocument : INotifyPropertyChanged, IDisposable
{
    private readonly ProcessRegistry _registry;
    private DisplayColorMetadata _displayMetadata;
    private IImageProcess? _selectedProcess;
    private bool _hasExecuted;
    private bool _isDirty;
    private bool _isDisposed;

    public ImageDocument(RawImage source, ProcessRegistry registry, DisplayColorMetadata displayMetadata)
    {
        Source = source;
        _registry = registry;
        _displayMetadata = displayMetadata;
        Processes = new ObservableCollection<IImageProcess>();
        Undo = new UndoManager();
        CreateDefaultPipeline();
        RecalculateAndValidate();
        SelectedProcess = Processes.FirstOrDefault(process => process.Descriptor.Definition.StableId == BuiltInProcessIds.ViewRendering) ?? Processes.FirstOrDefault();
        Undo.Clear();
        _isDirty = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PipelineChangedEventArgs>? PipelineChanged;

    public RawImage Source { get; }
    public string Title => Source.FileName;
    public ObservableCollection<IImageProcess> Processes { get; }
    public UndoManager Undo { get; }
    public CancellationTokenSource? ExecutionCancellation { get; set; }
    public long ExecutionGeneration { get; set; }
    public bool HasLastValidFrame { get; set; }
    public GpuImageFrame? DecodedGpuFrame { get; set; }
    public GpuImageFrame? FinalGpuFrame { get; set; }

    public bool HasExecuted
    {
        get => _hasExecuted;
        set => SetField(ref _hasExecuted, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetField(ref _isDirty, value);
    }

    public IImageProcess? SelectedProcess
    {
        get => _selectedProcess;
        set => SetField(ref _selectedProcess, value);
    }

    public DisplayColorMetadata DisplayMetadata
    {
        get => _displayMetadata;
        set
        {
            if (SetField(ref _displayMetadata, value))
            {
                RecalculateAndValidate();
                RaisePipelineChanged(false, "显示终端变化");
            }
        }
    }

    public IImageProcess Insert(ProcessDescriptor descriptor, int index, bool recordUndo = true)
    {
        if (!CanInsertStructurally(descriptor, index, out string structuralReason))
            throw new InvalidOperationException(structuralReason);
        if (!CanInsert(descriptor, index, out string reason))
            throw new InvalidOperationException(reason);

        IImageProcess process = descriptor.Create();
        InsertExisting(process, index);
        if (recordUndo)
        {
            Undo.PushApplied(new DelegateUndoableAction(
                "插入过程",
                () => RemoveExisting(process),
                () => InsertExisting(process, Math.Min(index, Processes.Count))));
        }
        return process;
    }

    public bool Remove(IImageProcess process, bool recordUndo = true)
    {
        if (!process.Descriptor.Definition.CanDelete) return false;
        int index = Processes.IndexOf(process);
        if (index < 0) return false;
        RemoveExisting(process);
        if (recordUndo)
        {
            Undo.PushApplied(new DelegateUndoableAction(
                "删除过程",
                () => InsertExisting(process, Math.Min(index, Processes.Count)),
                () => RemoveExisting(process)));
        }
        return true;
    }

    public bool Move(IImageProcess process, int targetIndex, bool recordUndo = true)
    {
        int oldIndex = Processes.IndexOf(process);
        if (oldIndex < 0 || oldIndex == targetIndex) return false;
        int adjustedTarget = targetIndex > oldIndex ? targetIndex - 1 : targetIndex;
        if (!CanMoveStructurally(process, adjustedTarget, out _)) return false;
        if (!CanMoveAtRemainingIndex(process, adjustedTarget, out _)) return false;
        MoveExisting(process, adjustedTarget);
        if (recordUndo)
        {
            Undo.PushApplied(new DelegateUndoableAction(
                "重排过程",
                () => MoveExisting(process, oldIndex),
                () => MoveExisting(process, adjustedTarget)));
        }
        return true;
    }

    public bool SetEnabled(IImageProcess process, bool enabled, bool recordUndo = true)
    {
        if (!process.Descriptor.Definition.CanDisable || process.IsEnabled == enabled) return false;
        bool old = process.IsEnabled;
        ApplyEnabled(process, enabled);
        if (recordUndo)
        {
            Undo.PushApplied(new DelegateUndoableAction(
                enabled ? "启用过程" : "禁用过程",
                () => ApplyEnabled(process, old),
                () => ApplyEnabled(process, enabled)));
        }
        return true;
    }

    public bool CanInsert(ProcessDescriptor descriptor, int index, out string reason)
    {
        return CanInsertStructurally(descriptor, index, out reason);
    }

    public bool CanInsertStructurally(ProcessDescriptor descriptor, int index, out string reason)
    {
        index = Math.Clamp(index, 0, Processes.Count);
        if (!descriptor.Definition.CanAdd)
        {
            reason = $"固定过程 {descriptor.Definition.StableId} 不能由目录添加。";
            return false;
        }
        if (!descriptor.Definition.AllowMultiple && Processes.Any(process =>
                process.Descriptor.Definition.StableId == descriptor.Definition.StableId))
        {
            reason = $"过程 {descriptor.Definition.StableId} 是唯一过程。";
            return false;
        }
        return CanPlaceRole(descriptor.Definition.Role, Processes, index, out reason);
    }

    public bool CanMove(IImageProcess process, int targetIndex, out string reason)
    {
        int oldIndex = Processes.IndexOf(process);
        if (oldIndex < 0)
        {
            reason = "过程不属于此文档。";
            return false;
        }
        targetIndex = targetIndex > oldIndex ? targetIndex - 1 : targetIndex;
        if (!CanMoveStructurally(process, targetIndex, out reason)) return false;
        return CanMoveAtRemainingIndex(process, targetIndex, out reason);
    }

    public bool CanMoveStructurally(IImageProcess process, int targetIndex, out string reason)
    {
        if (!Processes.Contains(process))
        {
            reason = "过程不属于此文档。";
            return false;
        }
        if (!process.Descriptor.Definition.CanMove)
        {
            reason = "这是固定过程，不能移动。";
            return false;
        }
        var remaining = Processes.Where(item => !ReferenceEquals(item, process)).ToList();
        targetIndex = Math.Clamp(targetIndex, 0, remaining.Count);
        return CanPlaceRole(process.Descriptor.Definition.Role, remaining, targetIndex, out reason);
    }

    private bool CanMoveAtRemainingIndex(IImageProcess process, int targetIndex, out string reason)
    {
        var remaining = Processes.Where(item => !ReferenceEquals(item, process)).ToList();
        targetIndex = Math.Clamp(targetIndex, 0, remaining.Count);
        if (!CanMoveStructurally(process, targetIndex, out reason)) return false;
        return CanPlaceRole(process.Descriptor.Definition.Role, remaining, targetIndex, out reason);
    }

    public void RecalculateAndValidate()
    {
        foreach (IImageProcess process in Processes)
        {
            process.RecalculateAutomaticParameters(new(Source, DisplayMetadata));
            if (!process.IsEnabled)
                process.State = ProcessState.Disabled;
        }
        OnPropertyChanged(nameof(Processes));
    }

    private static bool CanPlaceRole(
        ProcessRole role,
        IReadOnlyList<IImageProcess> pipeline,
        int index,
        out string reason)
    {
        index = Math.Clamp(index, 0, pipeline.Count);
        int decoderIndex = pipeline.ToList().FindIndex(item => item.Descriptor.Definition.Role == ProcessRole.Decoder);
        int terminalIndex = pipeline.ToList().FindIndex(item => item.Descriptor.Definition.Role == ProcessRole.Terminal);
        switch (role)
        {
            case ProcessRole.Source:
                reason = "图像源是固定的流程起点。";
                return index == 0 && pipeline.All(item => item.Descriptor.Definition.Role != ProcessRole.Source);
            case ProcessRole.Decoder:
                reason = "解码器必须紧跟图像源，并且每个文档只能有一个。";
                return index == 1 && pipeline.All(item => item.Descriptor.Definition.Role != ProcessRole.Decoder);
            case ProcessRole.Image:
                if (decoderIndex < 0 || index <= decoderIndex)
                {
                    reason = "图像过程必须位于解码器之后。";
                    return false;
                }
                if (terminalIndex >= 0 && index > terminalIndex)
                {
                    reason = "图像过程必须位于显示终端之前。";
                    return false;
                }
                reason = "图像过程独立处理统一 GPU 图像流。";
                return true;
            case ProcessRole.Observer:
                reason = "观察过程可位于解码器之后的任意采样位置。";
                return decoderIndex >= 0 && index > decoderIndex;
            case ProcessRole.Terminal:
                bool afterAllImageProcesses = !pipeline.Skip(index)
                    .Any(item => item.Descriptor.Definition.Role == ProcessRole.Image);
                reason = "显示终端必须位于全部图像过程之后，并且每个文档只能有一个。";
                return decoderIndex >= 0 && index > decoderIndex && terminalIndex < 0 && afterAllImageProcesses;
            default:
                reason = "未知过程角色。";
                return false;
        }
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }

    public void RestorePipeline(IReadOnlyList<IImageProcess> restoredProcesses)
    {
        bool valid = restoredProcesses.Count >= 3 &&
                     restoredProcesses[0].Descriptor.Definition.Role == ProcessRole.Source &&
                     restoredProcesses[1].Descriptor.Definition.Role == ProcessRole.Decoder &&
                     restoredProcesses.Count(process => process.Descriptor.Definition.Role == ProcessRole.Source) == 1 &&
                     restoredProcesses.Count(process => process.Descriptor.Definition.Role == ProcessRole.Decoder) == 1 &&
                     restoredProcesses.Count(process => process.Descriptor.Definition.Role == ProcessRole.Terminal) == 1;
        int terminalIndex = restoredProcesses.ToList().FindIndex(process =>
            process.Descriptor.Definition.Role == ProcessRole.Terminal);
        valid &= terminalIndex > 1 && restoredProcesses.Skip(terminalIndex + 1)
            .All(process => process.Descriptor.Definition.Role == ProcessRole.Observer);
        if (!valid)
            throw new InvalidDataException("保存的流程必须以唯一图像源和解码器开始，并包含唯一显示终端；终端之后只能放置观察过程。");
        foreach (IImageProcess process in Processes) Unsubscribe(process);
        Processes.Clear();
        foreach (IImageProcess process in restoredProcesses)
            InsertExisting(process, Processes.Count, false);
        SelectedProcess = Processes.FirstOrDefault(process => process.Descriptor.Definition.StableId == BuiltInProcessIds.ViewRendering)
                          ?? Processes.FirstOrDefault();
        RecalculateAndValidate();
        Undo.Clear();
        IsDirty = false;
        RaisePipelineChanged(true, "恢复已保存过程");
    }

    private void CreateDefaultPipeline()
    {
        var source = (ImageSourceProcess)_registry.GetRequired(BuiltInProcessIds.ImageSource).Create();
        source.Source = Source;
        InsertExisting(source, 0, false);
        InsertExisting(_registry.GetRequired(BuiltInProcessIds.Decoder).Create(), 1, false);
        InsertExisting(_registry.GetRequired(BuiltInProcessIds.ViewRendering).Create(), 2, false);
    }

    private void InsertExisting(IImageProcess process, int index, bool notify = true)
    {
        index = Math.Clamp(index, 0, Processes.Count);
        Processes.Insert(index, process);
        Subscribe(process);
        if (process is ImageSourceProcess sourceProcess) sourceProcess.Source = Source;
        if (notify) Changed(process.Descriptor.Definition.StableId == BuiltInProcessIds.Decoder, "插入过程");
    }

    private void RemoveExisting(IImageProcess process)
    {
        if (!Processes.Remove(process)) return;
        Unsubscribe(process);
        if (ReferenceEquals(SelectedProcess, process)) SelectedProcess = Processes.FirstOrDefault();
        Changed(process.Descriptor.Definition.StableId == BuiltInProcessIds.Decoder, "删除过程");
    }

    private void MoveExisting(IImageProcess process, int targetIndex)
    {
        int oldIndex = Processes.IndexOf(process);
        if (oldIndex < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, Processes.Count - 1);
        Processes.Move(oldIndex, targetIndex);
        Changed(false, "重排过程");
    }

    private void ApplyEnabled(IImageProcess process, bool enabled)
    {
        process.IsEnabled = enabled;
        Changed(false, enabled ? "启用过程" : "禁用过程");
    }

    private void Subscribe(IImageProcess process)
    {
        if (process is ProcessBase processBase)
            processBase.ParameterChanged += OnParameterChanged;
    }

    private void Unsubscribe(IImageProcess process)
    {
        if (process is ProcessBase processBase)
            processBase.ParameterChanged -= OnParameterChanged;
    }

    private void OnParameterChanged(object? sender, ProcessParameterChangedEventArgs e)
    {
        if (sender is not IProcessParameter parameter) return;
        IImageProcess? owner = Processes.FirstOrDefault(process => process.Parameters.Contains(parameter));
        if (owner is null) return;
        if (e.IsUserInitiated && !Undo.IsApplying)
        {
            Undo.PushApplied(new DelegateUndoableAction(
                "修改过程参数",
                () => parameter.RestoreState(e.Before, false),
                () => parameter.RestoreState(e.After, false)));
        }
        bool requiresDecode = owner.Descriptor.Definition.StableId == BuiltInProcessIds.Decoder;
        Changed(requiresDecode, "参数修改");
    }

    private void Changed(bool requiresDecode, string reason)
    {
        IsDirty = true;
        RecalculateAndValidate();
        RaisePipelineChanged(requiresDecode, reason);
    }

    private void RaisePipelineChanged(bool requiresDecode, string reason) =>
        PipelineChanged?.Invoke(this, new(requiresDecode, reason));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    public void Dispose()
    {
        if (_isDisposed) return;
        ExecutionCancellation?.Cancel();
        ExecutionCancellation?.Dispose();
        if (FinalGpuFrame is not null && !ReferenceEquals(FinalGpuFrame, DecodedGpuFrame))
            FinalGpuFrame.Dispose();
        DecodedGpuFrame?.Dispose();
        FinalGpuFrame = null;
        DecodedGpuFrame = null;
        foreach (IImageProcess process in Processes) Unsubscribe(process);
        _isDisposed = true;
    }
}
