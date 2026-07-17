using BrightEyes.Processing;
using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Documents;
using BrightEyes.Processing.Execution;
using BrightEyes.Processing.Parameters;
using BrightEyes.Processing.BuiltIn;
using BrightEyes.Renderer;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using Vortice.Direct3D11;

namespace BrightEyes.ViewModels;

public sealed class MainViewModel : IDisposable, INotifyPropertyChanged
{
    private static readonly TimeSpan PreviewFrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly ProcessRegistry _registry;
    private readonly MetadataPreflightService _preflight = new();
    private readonly PipelineSerializer _serializer = new();
    private readonly PipelineExecutionCoordinator _executor;
    private RendererD3D11? _renderer;
    private ImageDocument? _activeDocument;
    private IImageProcess? _selectedProcess;
    private CancellationTokenSource? _previewCancellation;
    private Task? _previewUpdateLoop;
    private ImageDocument? _previewDocument;
    private bool _previewUpdatePending;
    private bool _previewRequiresDecode;
    private bool _disposed;
    private bool _isBusy;
    private string _statusText = "打开图像以生成过程。";
    private string _inspectorText = "来源元数据将在预读后显示。";
    private float _viewZoom = 1.0f;

    public MainViewModel()
    {
        _registry = ProcessRegistry.Discover();
        _executor = new PipelineExecutionCoordinator(() => _renderer);
        Documents = new ObservableCollection<ImageDocument>();
        SelectedParameters = new ObservableCollection<ProcessParameterViewModel>();
        SelectedBasicParameters = new ObservableCollection<ProcessParameterViewModel>();
        SelectedAdvancedParameters = new ObservableCollection<ProcessParameterViewModel>();
        ActiveAnalysisProcesses = new ObservableCollection<AnalysisProcessBase>();
        ProcessRibbonGroups = _registry.Descriptors
            .Where(descriptor => descriptor.Definition.CanAdd)
            .GroupBy(descriptor => (descriptor.Definition.RibbonTab, descriptor.Definition.RibbonGroup))
            .Select(group => new ProcessRibbonGroupViewModel(
                group.Key.RibbonTab,
                group.Key.RibbonGroup,
                group.Select(descriptor => new ProcessCatalogItemViewModel(descriptor)).ToArray()))
            .ToArray();

        OpenImageCommand = new RelayCommand(_ => OpenImage(), _ => !IsBusy);
        SavePipelineCommand = new RelayCommand(_ => SavePipeline(), _ => ActiveDocument is not null);
        UndoCommand = new RelayCommand(_ => { ActiveDocument?.Undo.Undo(); RefreshCommandState(); }, _ => ActiveDocument?.Undo.CanUndo == true);
        RedoCommand = new RelayCommand(_ => { ActiveDocument?.Undo.Redo(); RefreshCommandState(); }, _ => ActiveDocument?.Undo.CanRedo == true);
        InsertProcessCommand = new RelayCommand(InsertCatalogItem, parameter => parameter is ProcessCatalogItemViewModel && ActiveDocument is not null);
        DeleteProcessCommand = new RelayCommand(DeleteProcess, parameter => parameter is IImageProcess process && process.Descriptor.Definition.CanDelete);
        ToggleProcessCommand = new RelayCommand(ToggleProcess, parameter => parameter is IImageProcess process && process.Descriptor.Definition.CanDisable);
        ResetProcessCommand = new RelayCommand(parameter => { if (parameter is IImageProcess process) process.ResetParameters(); });
        RestoreAutomaticCommand = new RelayCommand(parameter => { if (parameter is IImageProcess process) process.RestoreAutomaticParameters(); });
        CopyParametersCommand = new RelayCommand(CopyParameters, parameter => parameter is IImageProcess);
        PasteParametersCommand = new RelayCommand(PasteParameters, parameter => parameter is IImageProcess);
        ZoomInCommand = new RelayCommand(_ => ChangeViewZoom(1.2f), _ => IsRendererReady);
        ZoomOutCommand = new RelayCommand(_ => ChangeViewZoom(1.0f / 1.2f), _ => IsRendererReady);
        ResetZoomCommand = new RelayCommand(_ => ResetView(), _ => IsRendererReady);
        WindowClosingCommand = new RelayCommand(_ => Dispose());

        InitializeRendererAction = InitializeRenderer;
        ResizeRendererAction = (width, height) => _renderer?.Resize(width, height);
        PreviewMouseWheelAction = HandlePreviewMouseWheel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImageDocument> Documents { get; }
    public ObservableCollection<ProcessParameterViewModel> SelectedParameters { get; }
    public ObservableCollection<ProcessParameterViewModel> SelectedBasicParameters { get; }
    public ObservableCollection<ProcessParameterViewModel> SelectedAdvancedParameters { get; }
    public ObservableCollection<AnalysisProcessBase> ActiveAnalysisProcesses { get; }
    public IReadOnlyList<ProcessRibbonGroupViewModel> ProcessRibbonGroups { get; }
    public Func<IntPtr, int, int, bool> InitializeRendererAction { get; }
    public Action<int, int> ResizeRendererAction { get; }
    public Action<int, int, int, bool, bool> PreviewMouseWheelAction { get; }
    public ICommand WindowClosingCommand { get; }
    public ICommand OpenImageCommand { get; }
    public ICommand SavePipelineCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand InsertProcessCommand { get; }
    public ICommand DeleteProcessCommand { get; }
    public ICommand ToggleProcessCommand { get; }
    public ICommand ResetProcessCommand { get; }
    public ICommand RestoreAutomaticCommand { get; }
    public ICommand CopyParametersCommand { get; }
    public ICommand PasteParametersCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    public string ViewZoomText => $"{_viewZoom * 100.0f:0}%";

    public ImageDocument? ActiveDocument
    {
        get => _activeDocument;
        set
        {
            if (ReferenceEquals(_activeDocument, value)) return;
            if (_activeDocument is not null)
            {
                CancelPreview(_activeDocument);
                _activeDocument.PipelineChanged -= OnPipelineChanged;
            }
            _activeDocument = value;
            ResetView();
            if (_activeDocument is not null)
            {
                _activeDocument.PipelineChanged += OnPipelineChanged;
                _activeDocument.DisplayMetadata = _renderer?.DisplayColorMetadata ?? DisplayColorMetadata.SdrFallback;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(MetadataFacts));
            RebuildAnalysisProcesses();
            SelectedProcess = value?.SelectedProcess;
            StatusText = value is null
                ? "打开图像以生成过程。"
                : value.HasExecuted
                    ? "已切换到处理完成的图像。"
                    : "元数据预读与图像过程已准备，正在自动处理。";
            RefreshCommandState();
        }
    }

    public IImageProcess? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (ReferenceEquals(_selectedProcess, value)) return;
            _selectedProcess = value;
            if (ActiveDocument is not null) ActiveDocument.SelectedProcess = value;
            RebuildSelectedParameters();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProcessName));
            OnPropertyChanged(nameof(SelectedProcessDescription));
        }
    }

    public string SelectedProcessName => SelectedProcess is null
        ? "未选择过程"
        : ResourceText.Resolve(SelectedProcess.Descriptor.Definition.NameResourceKey);

    public string SelectedProcessDescription => SelectedProcess is null
        ? ""
        : ResourceText.Resolve(SelectedProcess.Descriptor.Definition.DescriptionResourceKey);

    public IReadOnlyList<MetadataFact> MetadataFacts => ActiveDocument?.Source.DetectedMetadata.Facts ?? Array.Empty<MetadataFact>();

    public bool IsRendererReady => _renderer is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value)) RefreshCommandState();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string InspectorText
    {
        get => _inspectorText;
        private set => SetField(ref _inspectorText, value);
    }

    public string DisplayColorSummary => _renderer is null
        ? "显示终端尚未初始化"
        : _renderer.DisplayColorMetadata.IsHdr
            ? $"HDR / {_renderer.DisplayColorMetadata.ColorSpace} / {_renderer.DisplayColorMetadata.EffectivePeakNits:0.#} nit"
            : $"SDR / {_renderer.DisplayColorMetadata.ColorSpace}";

    public bool TryInsertProcess(ProcessDescriptor descriptor, int index, out string reason)
    {
        if (ActiveDocument is null)
        {
            reason = "没有活动文档。";
            return false;
        }
        if (!ActiveDocument.CanInsertStructurally(descriptor, index, out reason)) return false;
        if (!ActiveDocument.CanInsert(descriptor, index, out reason)) return false;
        SelectedProcess = ActiveDocument.Insert(descriptor, index);
        RefreshCommandState();
        return true;
    }

    public bool TryMoveProcess(IImageProcess process, int index, out string reason)
    {
        if (ActiveDocument is null)
        {
            reason = "没有活动文档。";
            return false;
        }
        int oldIndex = ActiveDocument.Processes.IndexOf(process);
        int adjustedIndex = index > oldIndex ? index - 1 : index;
        if (!ActiveDocument.CanMoveStructurally(process, adjustedIndex, out reason)) return false;
        if (!ActiveDocument.CanMove(process, index, out reason)) return false;
        bool moved = ActiveDocument.Move(process, index);
        RefreshCommandState();
        return moved;
    }

    private async void OpenImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图像并预读元数据",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.avif;*.heif;*.heic;*.webp;*.gif|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        StatusText = "正在读取图像与容器级元数据。";
        try
        {
            RawImage source = await _preflight.InspectAsync(dialog.FileName, CancellationToken.None);
            var document = new ImageDocument(source, _registry, _renderer?.DisplayColorMetadata ?? DisplayColorMetadata.SdrFallback);
            string sidecar = PipelineSerializer.GetDefaultSidecarPath(source.FilePath);
            bool matchedSource = false;
            string? restoreWarning = null;
            try
            {
                matchedSource = await _serializer.TryLoadAsync(document, _registry, sidecar);
            }
            catch (Exception ex)
            {
                restoreWarning = ex.GetBaseException().Message;
            }
            Documents.Add(document);
            ActiveDocument = document;
            InspectorText = BuildMetadataSummary(source.DetectedMetadata);
            if (restoreWarning is not null)
                StatusText = "保存的过程无效，已使用元数据生成默认流程：" + restoreWarning;
            else if (File.Exists(sidecar))
                StatusText = matchedSource
                    ? "已恢复与当前文件身份匹配的保存过程。"
                    : "已恢复过程，但来源内容身份已变化；自动值已按当前元数据重算，用户覆盖保持不变。";

            await ExecuteOpenedImageAsync(document);
        }
        catch (Exception ex)
        {
            StatusText = "元数据预读失败：" + ex.GetBaseException().Message;
            Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteOpenedImageAsync(ImageDocument document)
    {
        StatusText = "图像已打开，正在自动处理。";
        try
        {
            await _executor.ExecuteAsync(document);
            IImageProcess? failedProcess = document.Processes.FirstOrDefault(process => process.State == ProcessState.Error);
            if (failedProcess is not null)
            {
                StatusText = "自动处理失败：" + failedProcess.Diagnostic;
            }
            else
            {
                StatusText = document.HasLastValidFrame
                    ? "图像处理完成；普通路径保持一次 CPU 到 GPU 上传。"
                    : "自动处理失败：过程未产生有效视图。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "自动处理失败：" + ex.GetBaseException().Message;
            Debug.WriteLine(ex);
        }
    }

    private async void SavePipeline()
    {
        if (ActiveDocument is null) return;
        string sidecar = PipelineSerializer.GetDefaultSidecarPath(ActiveDocument.Source.FilePath);
        try
        {
            await _serializer.SaveAsync(ActiveDocument, sidecar);
            StatusText = $"过程已保存：{sidecar}";
        }
        catch (Exception ex)
        {
            StatusText = "保存过程失败：" + ex.GetBaseException().Message;
        }
    }

    private void InsertCatalogItem(object? parameter)
    {
        if (parameter is not ProcessCatalogItemViewModel item || ActiveDocument is null) return;
        int terminalIndex = ActiveDocument.Processes.ToList().FindIndex(process =>
            process.Descriptor.Definition.StableId == BuiltInProcessIds.ViewRendering);
        int selectedIndex = SelectedProcess is null
            ? Math.Max(terminalIndex, 0)
            : ActiveDocument.Processes.IndexOf(SelectedProcess) + 1;
        if (terminalIndex >= 0 && item.Descriptor.Definition.Role != ProcessRole.Observer)
            selectedIndex = Math.Min(selectedIndex, terminalIndex);
        if (!TryInsertProcess(item.Descriptor, selectedIndex, out string reason)) StatusText = reason;
    }

    private void DeleteProcess(object? parameter)
    {
        if (parameter is not IImageProcess process || ActiveDocument is null) return;
        if (ActiveDocument.Remove(process))
        {
            SelectedProcess = ActiveDocument.SelectedProcess;
            RefreshCommandState();
        }
    }

    private void ToggleProcess(object? parameter)
    {
        if (parameter is not IImageProcess process || ActiveDocument is null) return;
        ActiveDocument.SetEnabled(process, !process.IsEnabled);
        RefreshCommandState();
    }

    private static void CopyParameters(object? parameter)
    {
        if (parameter is not IImageProcess process) return;
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string json = JsonSerializer.Serialize(process.Parameters.Select(item => new
        {
            id = item.Definition.StableId,
            automatic = item.IsAutomatic,
            valueType = item.ValueType.AssemblyQualifiedName,
            value = item.UserOverrideValue
        }), options);
        System.Windows.Clipboard.SetText(json);
    }

    private void PasteParameters(object? parameter)
    {
        if (parameter is not IImageProcess process || !System.Windows.Clipboard.ContainsText()) return;
        try
        {
            using JsonDocument json = JsonDocument.Parse(System.Windows.Clipboard.GetText());
            foreach (JsonElement item in json.RootElement.EnumerateArray())
            {
                string? id = item.GetProperty("id").GetString();
                IProcessParameter? target = process.Parameters.FirstOrDefault(candidate => candidate.Definition.StableId == id);
                if (target is null) continue;
                bool automatic = item.GetProperty("automatic").GetBoolean();
                if (automatic)
                {
                    target.RestoreAutomatic();
                }
                else
                {
                    object? value = item.GetProperty("value").Deserialize(target.ValueType, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                    target.SetUserValue(value);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = "剪贴板参数无效：" + ex.GetBaseException().Message;
        }
    }

    private void OnPipelineChanged(object? sender, PipelineChangedEventArgs e)
    {
        if (sender is not ImageDocument document || !ReferenceEquals(document, ActiveDocument)) return;

        if (!ReferenceEquals(SelectedProcess, document.SelectedProcess))
            SelectedProcess = document.SelectedProcess;
        RebuildAnalysisProcesses();
        RefreshCommandState();
        if (!document.HasExecuted) return;

        QueuePreview(document, e.RequiresDecode);
    }

    private void QueuePreview(ImageDocument document, bool forceDecode)
    {
        _previewUpdatePending = true;
        _previewRequiresDecode |= forceDecode;
        if (_previewUpdateLoop is { IsCompleted: false } && ReferenceEquals(_previewDocument, document))
            return;

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewDocument = document;
        _previewUpdateLoop = RunPreviewLoopAsync(document, cancellation);
    }

    private async Task RunPreviewLoopAsync(ImageDocument document, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested && ReferenceEquals(document, ActiveDocument))
            {
                await Task.Delay(PreviewFrameInterval, token);
                if (token.IsCancellationRequested || !ReferenceEquals(document, ActiveDocument) || !IsRendererReady)
                    return;

                _previewUpdatePending = false;
                bool forceDecode = _previewRequiresDecode;
                _previewRequiresDecode = false;
                await _executor.ExecuteAsync(document, forceDecode);
                if (!token.IsCancellationRequested && ReferenceEquals(document, ActiveDocument))
                    StatusText = "异步预览已更新。";

                if (!_previewUpdatePending)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusText = "异步预览失败，已保留最后有效画面：" + ex.GetBaseException().Message;
            Debug.WriteLine(ex);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                cancellation.Dispose();
                _previewCancellation = null;
                _previewUpdateLoop = null;
                _previewDocument = null;
                _previewUpdatePending = false;
                _previewRequiresDecode = false;
            }
        }
    }

    private void CancelPreview(ImageDocument? document)
    {
        CancellationTokenSource? cancellation = _previewCancellation;
        _previewCancellation = null;
        _previewUpdateLoop = null;
        _previewDocument = null;
        _previewUpdatePending = false;
        _previewRequiresDecode = false;
        cancellation?.Cancel();
        cancellation?.Dispose();
        document?.ExecutionCancellation?.Cancel();
    }

    private void RebuildSelectedParameters()
    {
        foreach (ProcessParameterViewModel parameter in SelectedParameters) parameter.Dispose();
        SelectedParameters.Clear();
        SelectedBasicParameters.Clear();
        SelectedAdvancedParameters.Clear();
        if (SelectedProcess is not null)
            foreach (IProcessParameter parameter in SelectedProcess.Parameters)
            {
                var viewModel = new ProcessParameterViewModel(parameter);
                SelectedParameters.Add(viewModel);
                (viewModel.IsAdvanced ? SelectedAdvancedParameters : SelectedBasicParameters).Add(viewModel);
            }
    }

    private void RebuildAnalysisProcesses()
    {
        AnalysisProcessBase[] analysisProcesses = ActiveDocument?.Processes.OfType<AnalysisProcessBase>().ToArray()
                                                  ?? Array.Empty<AnalysisProcessBase>();
        if (ActiveAnalysisProcesses.SequenceEqual(analysisProcesses)) return;

        ActiveAnalysisProcesses.Clear();
        foreach (AnalysisProcessBase process in analysisProcesses)
            ActiveAnalysisProcesses.Add(process);
    }

    private bool InitializeRenderer(IntPtr handle, int width, int height)
    {
        if (_disposed || handle == IntPtr.Zero || width <= 0 || height <= 0) return false;
        if (_renderer is not null) return true;

        RendererD3D11? renderer = null;
        try
        {
            renderer = new RendererD3D11(handle, width, height);
            renderer.DiagnosticRaised += OnRendererDiagnostic;
            renderer.Initialize();
            renderer.SetSdrBackgroundColor(249.0f / 255.0f, 250.0f / 255.0f, 254.0f / 255.0f, 1.0f);
            renderer.SetHdrBackgroundColor(0.58f, 0.59f, 0.62f, 1.0f);
            renderer.Start();
            _renderer = renderer;
            if (ActiveDocument is not null)
            {
                ActiveDocument.DisplayMetadata = renderer.DisplayColorMetadata;
                StatusText = "D3D11 渲染器已就绪；可执行完整解码与渲染。";
            }
            OnPropertyChanged(nameof(IsRendererReady));
            OnPropertyChanged(nameof(DisplayColorSummary));
            RefreshCommandState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "GPU 初始化失败，过程执行暂不可用：" + ex.GetBaseException().Message;
            if (renderer is not null)
            {
                renderer.DiagnosticRaised -= OnRendererDiagnostic;
                renderer.Dispose();
            }
            _renderer = null;
            OnPropertyChanged(nameof(IsRendererReady));
            OnPropertyChanged(nameof(DisplayColorSummary));
            RefreshCommandState();
            return false;
        }
    }

    private void OnRendererDiagnostic(string message)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => StatusText = message);
    }

    private static string BuildMetadataSummary(DetectedImageMetadata metadata)
    {
        string assumptions = metadata.HasAssumptions ? "包含明确标记的假设" : "无默认假设";
        string conflicts = metadata.Conflicts.Count == 0 ? "无声明冲突" : string.Join("；", metadata.Conflicts);
        return $"{metadata.ContainerFormat} / {metadata.PixelWidth} x {metadata.PixelHeight} / {metadata.BitDepth} bit\n" +
               $"{metadata.Primaries} / {metadata.Transfer} / {metadata.PhotometricModel}\n" +
               $"Reference white {metadata.ReferenceWhiteNits:0.#} nit / peak {metadata.NominalPeakNits:0.#} nit\n" +
               $"{assumptions}；{conflicts}";
    }

    private void HandlePreviewMouseWheel(int delta, int x, int y, bool control, bool shift)
    {
        if (_renderer is null || delta == 0) return;
        float wheelSteps = delta / 120.0f;
        if (control)
        {
            _renderer.PanView(0.0f, 48.0f * wheelSteps);
        }
        else if (shift)
        {
            _renderer.PanView(48.0f * wheelSteps, 0.0f);
        }
        else
        {
            SetViewZoom(_renderer.ZoomView(MathF.Pow(1.2f, wheelSteps), x, y));
        }
    }

    private void ChangeViewZoom(float factor)
    {
        if (_renderer is not null) SetViewZoom(_renderer.ZoomView(factor));
    }

    private void ResetView()
    {
        _renderer?.ResetView();
        SetViewZoom(1.0f);
    }

    private void SetViewZoom(float value)
    {
        if (Math.Abs(_viewZoom - value) < 0.0001f) return;
        _viewZoom = value;
        OnPropertyChanged(nameof(ViewZoomText));
    }

    private static void RefreshCommandState() => CommandManager.InvalidateRequerySuggested();

    public void SetExternalSharedTextureHandle(IntPtr handle, bool isRec2020PQ = true) => _renderer?.SetExternalTextureFromSharedHandle(handle, isRec2020PQ);
    public void SetExternalSharedTextureHandle(IntPtr handle, ColorSourceMetadata metadata) => _renderer?.SetExternalTextureFromSharedHandle(handle, metadata);
    public void SetExternalTexture(ID3D11Texture2D texture, bool isRec2020PQ = true) => _renderer?.SetExternalTexture(texture, isRec2020PQ);
    public void SetExternalTexture(ID3D11Texture2D texture, ColorSourceMetadata metadata) => _renderer?.SetExternalTexture(texture, metadata);
    public void ClearExternalTexture() => _renderer?.ClearExternalTexture();

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
        if (_disposed) return;
        CancelPreview(ActiveDocument);
        foreach (ImageDocument document in Documents) document.Dispose();
        if (_renderer is not null) _renderer.DiagnosticRaised -= OnRendererDiagnostic;
        _renderer?.Dispose();
        _renderer = null;
        OnPropertyChanged(nameof(IsRendererReady));
        OnPropertyChanged(nameof(DisplayColorSummary));
        RefreshCommandState();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
