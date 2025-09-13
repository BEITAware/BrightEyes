using System;
using System.Windows;
using System.Windows.Forms.Integration;

namespace BrightEyes
{
    public class D3DHost : WindowsFormsHost
    {
        public static readonly DependencyProperty InitializeActionProperty =
            DependencyProperty.Register("InitializeAction", typeof(Action<IntPtr, int, int>), typeof(D3DHost), new PropertyMetadata(null));

        public Action<IntPtr, int, int> InitializeAction
        {
            get { return (Action<IntPtr, int, int>)GetValue(InitializeActionProperty); }
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
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Child != null)
            {
                InitializeAction?.Invoke(Child.Handle, Child.Width, Child.Height);
                Child.Resize += OnPanelResize;
            }
        }

        private void OnPanelResize(object? sender, EventArgs e)
        {
            if (Child != null)
            {
                ResizeAction?.Invoke(Child.ClientSize.Width, Child.ClientSize.Height);
            }
        }
    }
}