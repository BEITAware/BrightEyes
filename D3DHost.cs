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

        public static readonly DependencyProperty MouseWheelActionProperty =
            DependencyProperty.Register(
                "MouseWheelAction",
                typeof(Action<int, int, int, bool, bool>),
                typeof(D3DHost),
                new PropertyMetadata(null));

        public Action<int, int> ResizeAction
        {
            get { return (Action<int, int>)GetValue(ResizeActionProperty); }
            set { SetValue(ResizeActionProperty, value); }
        }

        public Action<int, int, int, bool, bool> MouseWheelAction
        {
            get { return (Action<int, int, int, bool, bool>)GetValue(MouseWheelActionProperty); }
            set { SetValue(MouseWheelActionProperty, value); }
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
            _subscribedChild.MouseEnter += OnChildMouseEnter;
            _subscribedChild.MouseWheel += OnChildMouseWheel;
        }

        private void DetachChildEvents()
        {
            if (_subscribedChild is null) return;

            _subscribedChild.HandleCreated -= OnChildHandleCreated;
            _subscribedChild.Resize -= OnPanelResize;
            _subscribedChild.MouseEnter -= OnChildMouseEnter;
            _subscribedChild.MouseWheel -= OnChildMouseWheel;
            _subscribedChild = null;
        }

        private void OnChildMouseEnter(object? sender, EventArgs e)
        {
            _subscribedChild?.Focus();
        }

        private void OnChildMouseWheel(object? sender, MouseEventArgs e)
        {
            Keys modifiers = Control.ModifierKeys;
            MouseWheelAction?.Invoke(
                e.Delta,
                e.X,
                e.Y,
                modifiers.HasFlag(Keys.Control),
                modifiers.HasFlag(Keys.Shift));
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
