using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DependencyInjectionHelper.Vsix
{
    /// <summary>
    /// Interaction logic for ArgumentsControl.xaml
    /// </summary>
    public partial class ArgumentsControl : UserControl
    {
        public ArgumentsControl()
        {
            InitializeComponent();

            this.DataContext = this;
        }

        public List<ArgumentViewModel> Arguments { get; set; }

        public void SetArguments(ImmutableArray<Argument> arguments)
        {
            Arguments = arguments
                .Select(x => new ArgumentViewModel
                {
                    ParameterName = x.ParameterName,
                    ParameterType = x.ParameterType.GetFullName()

                })
                .ToList();
        }

        public ImmutableArray<WhatToDoWithArgument> GetResult()
        {
            return Arguments.Select(x => x.ShouldRemove ? WhatToDoWithArgument.Remove : WhatToDoWithArgument.Keep)
                .ToImmutableArray();
        }

    }
}
