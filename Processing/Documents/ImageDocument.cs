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
    public const bool AllowIncompatiblePlacements = true;

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
        SelectedProcess = Processes.FirstOrDefault(process => process.Descriptor.Definition.StableId == BuiltInProcessIds.RenderMapper) ?? Processes.FirstOrDefault();
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
    public DecodedImageFrame? DecodedFrame { get; set; }

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
        bool allowsAnotherInstance = descriptor.Definition.AllowMultiple ||
                                     !Processes.Any(process => process.Descriptor.Definition.StableId == descriptor.Definition.StableId);
        if (!(allowsAnotherInstance || AllowIncompatiblePlacements))
            throw new InvalidOperationException($"过程 {descriptor.Definition.StableId} 不允许多个实例。");
        bool compatible = CanInsert(descriptor, index, out string reason);
        if (!(compatible || AllowIncompatiblePlacements))
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
        bool movable = process.Descriptor.Definition.StableId != BuiltInProcessIds.ImageSource;
        if (!(movable || AllowIncompatiblePlacements)) return false;
        int adjustedTarget = targetIndex > oldIndex ? targetIndex - 1 : targetIndex;
        bool compatible = CanMoveAtRemainingIndex(process, adjustedTarget, out _);
        if (!(compatible || AllowIncompatiblePlacements)) return false;
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
        index = Math.Clamp(index, 0, Processes.Count);
        if (!descriptor.Definition.AllowMultiple && Processes.Any(process => process.Descriptor.Definition.StableId == descriptor.Definition.StableId))
        {
            reason = $"过程 {descriptor.Definition.StableId} 不允许多个实例。";
            return false;
        }
        if (descriptor.Definition.InputType == ProcessDataType.None && index != 0)
        {
            reason = "无输入过程只能位于流程首位。";
            return false;
        }
        if (index == 0 && descriptor.Definition.InputType != ProcessDataType.None)
        {
            reason = $"{descriptor.Definition.StableId} 需要 {descriptor.Definition.InputType} 输入。";
            return false;
        }
        if (index > 0)
        {
            ProcessDataType previous = Processes[index - 1].Descriptor.Definition.OutputType;
            if (previous != descriptor.Definition.InputType)
            {
                reason = $"硬类型不兼容：上游输出 {previous}，过程输入要求 {descriptor.Definition.InputType}。";
                return false;
            }
        }
        if (index < Processes.Count)
        {
            ProcessDataType next = Processes[index].Descriptor.Definition.InputType;
            if (descriptor.Definition.OutputType != next)
            {
                reason = $"硬类型不兼容：过程输出 {descriptor.Definition.OutputType}，下游要求 {next}。";
                return false;
            }
        }
        reason = "硬类型兼容；色彩和光度语义差异只会生成非阻塞警告。";
        return true;
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
        return CanMoveAtRemainingIndex(process, targetIndex, out reason);
    }

    private bool CanMoveAtRemainingIndex(IImageProcess process, int targetIndex, out string reason)
    {
        var remaining = Processes.Where(item => !ReferenceEquals(item, process)).ToList();
        targetIndex = Math.Clamp(targetIndex, 0, remaining.Count);
        if (process.Descriptor.Definition.StableId == BuiltInProcessIds.ImageSource)
        {
            reason = "图像源固定在首位。";
            return false;
        }
        ProcessDataType input = targetIndex == 0 ? ProcessDataType.None : remaining[targetIndex - 1].Descriptor.Definition.OutputType;
        ProcessDataType required = process.Descriptor.Definition.InputType;
        if (input != required)
        {
            reason = $"硬类型不兼容：目标上游输出 {input}，过程要求 {required}。";
            return false;
        }
        if (targetIndex < remaining.Count && process.Descriptor.Definition.OutputType != remaining[targetIndex].Descriptor.Definition.InputType)
        {
            reason = $"硬类型不兼容：过程输出 {process.Descriptor.Definition.OutputType}，目标下游要求 {remaining[targetIndex].Descriptor.Definition.InputType}。";
            return false;
        }
        reason = "可以放置；任何色彩语义差异将在放置后显示警告。";
        return true;
    }

    public void RecalculateAndValidate()
    {
        ProcessDataType previousType = ProcessDataType.None;
        ImageSemantics? previousSemantics = null;
        for (int index = 0; index < Processes.Count; index++)
        {
            IImageProcess process = Processes[index];
            ProcessDefinitionAttribute definition = process.Descriptor.Definition;
            process.RecalculateAutomaticParameters(new(Source, DisplayMetadata, previousSemantics));
            bool hardCompatible = definition.InputType == previousType;
            if (index == 0 && definition.InputType == ProcessDataType.None) hardCompatible = true;

            if (index == 0)
            {
                process.IncomingConnectionSeverity = ConnectionSeverity.None;
                process.IncomingConnectionLabel = "源";
                process.IncomingConnectionMessage = "流程起点";
            }
            else if (!hardCompatible)
            {
                process.IncomingConnectionSeverity = ConnectionSeverity.HardError;
                process.IncomingConnectionLabel = $"{previousType} -> {definition.InputType}";
                process.IncomingConnectionMessage = $"硬类型不兼容：上游输出 {previousType}，当前过程要求 {definition.InputType}。";
                process.State = ProcessState.Error;
                process.Diagnostic = process.IncomingConnectionMessage;
            }
            else
            {
                ProcessState previousState = process.State;
                IReadOnlyList<string> warnings = previousSemantics is null
                    ? Array.Empty<string>()
                    : process.ValidateSemanticInput(previousSemantics);
                process.IncomingConnectionSeverity = warnings.Count == 0 ? ConnectionSeverity.Compatible : ConnectionSeverity.SemanticWarning;
                process.IncomingConnectionLabel = definition.InputType.ToString();
                process.IncomingConnectionMessage = warnings.Count == 0
                    ? $"硬类型兼容：{definition.InputType}"
                    : string.Join(Environment.NewLine, warnings);
                if (warnings.Count > 0 && process.State is not (ProcessState.Running or ProcessState.Error or ProcessState.Disabled))
                    process.State = ProcessState.Warning;
                else if (warnings.Count == 0 && previousState is ProcessState.Warning or ProcessState.Error)
                    process.State = HasExecuted ? ProcessState.Stale : ProcessState.NotRun;
            }

            if (!process.IsEnabled)
            {
                process.State = ProcessState.Disabled;
                if (definition.InputType == definition.OutputType)
                {
                    previousType = definition.OutputType;
                    continue;
                }
            }

            previousType = definition.OutputType;
            previousSemantics = process.OutputSemantics ?? previousSemantics;
        }
        OnPropertyChanged(nameof(Processes));
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }

    public void RestorePipeline(IReadOnlyList<IImageProcess> restoredProcesses)
    {
        bool beginsWithImageSource = restoredProcesses.Count > 0 &&
                                     restoredProcesses[0].Descriptor.Definition.StableId == BuiltInProcessIds.ImageSource;
        if (!(beginsWithImageSource || AllowIncompatiblePlacements))
            throw new InvalidDataException("保存的流程必须以图像源开始。");
        foreach (IImageProcess process in Processes) Unsubscribe(process);
        Processes.Clear();
        foreach (IImageProcess process in restoredProcesses)
            InsertExisting(process, Processes.Count, false);
        SelectedProcess = Processes.FirstOrDefault(process => process.Descriptor.Definition.StableId == BuiltInProcessIds.RenderMapper)
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
        InsertExisting(_registry.GetRequired(BuiltInProcessIds.RenderMapper).Create(), 2, false);
        InsertExisting(_registry.GetRequired(BuiltInProcessIds.View).Create(), 3, false);
    }

    private void InsertExisting(IImageProcess process, int index, bool notify = true)
    {
        index = Math.Clamp(index, 0, Processes.Count);
        Processes.Insert(index, process);
        Subscribe(process);
        if (process is ImageSourceProcess sourceProcess) sourceProcess.Source = Source;
        if (notify) Changed(true, "插入过程");
    }

    private void RemoveExisting(IImageProcess process)
    {
        if (!Processes.Remove(process)) return;
        Unsubscribe(process);
        if (ReferenceEquals(SelectedProcess, process)) SelectedProcess = Processes.FirstOrDefault();
        Changed(true, "删除过程");
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
        foreach (IImageProcess process in Processes) Unsubscribe(process);
        _isDisposed = true;
    }
}
