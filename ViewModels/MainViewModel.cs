using BrightEyes.Renderer;
using System;
using System.Diagnostics;
using System.Windows.Input;

namespace BrightEyes.ViewModels
{
    public class MainViewModel : IDisposable
    {
        private RendererD3D11? _renderer;
        private bool _disposed = false;

        public Action<IntPtr, int, int> InitializeRendererAction { get; }
        public Action<int, int> ResizeRendererAction { get; }
        public ICommand WindowClosingCommand { get; }

        public MainViewModel()
        {
            WindowClosingCommand = new RelayCommand(_ => Dispose());
            Debug.WriteLine("MainViewModel constructor started.");
            InitializeRendererAction = (handle, width, height) =>
            {
                Debug.WriteLine($"InitializeRendererAction invoked with handle: {handle}, width: {width}, height: {height}");
                if (handle != IntPtr.Zero && width > 0 && height > 0)
                {
                    _renderer = new RendererD3D11(handle, width, height);
                    _renderer.Initialize();
                    _renderer.Start();
                }
                else
                {
                    Debug.WriteLine("Invalid parameters for renderer initialization.");
                }
            };

            ResizeRendererAction = (width, height) =>
            {
                Debug.WriteLine($"ResizeRendererAction invoked with width: {width}, height: {height}");
                _renderer?.Resize(width, height);
            };
            Debug.WriteLine("MainViewModel constructor finished.");
        }

        public void Dispose()
        {
            Debug.WriteLine("MainViewModel Dispose started.");
            Dispose(true);
            GC.SuppressFinalize(this);
            Debug.WriteLine("MainViewModel Dispose finished.");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _renderer?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}