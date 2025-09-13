using BrightEyes.Renderer;
using BrightEyes.ViewModels;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace BrightEyes.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            Debug.WriteLine("MainWindow constructor started.");
            InitializeComponent();
            Debug.WriteLine("InitializeComponent completed.");
            // DataContext is now set in XAML
            Debug.WriteLine("MainWindow constructor finished.");
        }
    }
}