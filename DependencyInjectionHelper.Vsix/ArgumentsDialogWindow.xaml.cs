using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;

namespace DependencyInjectionHelper.Vsix
{
    /// <summary>
    /// Interaction logic for ArgumentsDialogWindow.xaml
    /// </summary>
    public partial class ArgumentsDialogWindow : DialogWindow
    {
        public ArgumentsDialogWindow()
        {
            InitializeComponent();
        }

        private void BtnOk_OnClick(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }

        private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }



    }
}
