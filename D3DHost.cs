using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace BrightEyes
{
    public class D3DHost : WindowsFormsHost
    {
        public static readonly DependencyProperty InitializeActionProperty =
            DependencyProperty.Register(
                "InitializeAction",
                typeof(Func<IntPtr, int, int, bool>),
                typeof(D3DHost),
                new PropertyMetadata(null, OnInitializeActionChanged));

        private Control? _subscribedChild;
        private bool _isInitialized;

        public Func<IntPtr, int, int, bool> InitializeAction
        {
            get { return (Func<IntPtr, int, int, bool>)GetValue(InitializeActionProperty); }
            set { SetValue(InitializeActionProperty, value); }
        }

        public static readonly DependencyProperty ResizeActionProperty =
            DependencyProperty.Register("ResizeAction", typeof(Action<int, int>), typeof(D3DHost), new PropertyMetadata(null));

        public Action<int, int> ResizeAction
        {
            get { return (Action<int, int>)GetValue(ResizeActionProperty); }
            set { SetValue(ResizeActionProperty, value); }
        }

        public D3DHost()
        {
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachChildEvents();
            TryInitialize();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachChildEvents();
        }

        private static void OnInitializeActionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var host = (D3DHost)dependencyObject;
            host.AttachChildEvents();
            host.TryInitialize();
        }

        private void AttachChildEvents()
        {
            if (ReferenceEquals(_subscribedChild, Child)) return;

            DetachChildEvents();
            _subscribedChild = Child;
            if (_subscribedChild is null) return;

            _subscribedChild.HandleCreated += OnChildHandleCreated;
            _subscribedChild.Resize += OnPanelResize;
        }

        private void DetachChildEvents()
        {
            if (_subscribedChild is null) return;

            _subscribedChild.HandleCreated -= OnChildHandleCreated;
            _subscribedChild.Resize -= OnPanelResize;
            _subscribedChild = null;
        }

        private void OnChildHandleCreated(object? sender, EventArgs e)
        {
            TryInitialize();
        }

        private void OnPanelResize(object? sender, EventArgs e)
        {
            if (!TryInitialize() || Child is null) return;

            int width = Child.ClientSize.Width;
            int height = Child.ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            ResizeAction?.Invoke(width, height);
        }

        private bool TryInitialize()
        {
            if (_isInitialized) return true;
            if (!IsLoaded || InitializeAction is null) return false;

            AttachChildEvents();
            if (Child is null) return false;

            Child.CreateControl();
            int width = Child.ClientSize.Width;
            int height = Child.ClientSize.Height;
            IntPtr handle = Child.Handle;
            if (handle == IntPtr.Zero || width <= 0 || height <= 0) return false;

            _isInitialized = InitializeAction(handle, width, height);
            return _isInitialized;
        }
    }
}
