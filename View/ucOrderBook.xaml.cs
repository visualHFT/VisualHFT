using System;
using System.Windows;
using System.Windows.Controls;
using VisualHFT.Helpers;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Interaction logic for ucOrderBook.xaml
    /// </summary>
    public partial class ucOrderBook : UserControl
    {

        public ucOrderBook()
        {
            InitializeComponent();
        }
        private void butPopPriceChart_Click(object sender, RoutedEventArgs e)
        {
            //var newViewModel = new ViewModel.vmOrderBook((ViewModel.vmOrderBook)this.DataContext);
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            HelperCommon.CreateCommonPopUpWindow(chtPrice, (Button)sender, newViewModel);
        }

        private void butPopSpreadChart_Click(object sender, RoutedEventArgs e)
        {
            //var newViewModel = new ViewModel.vmOrderBook((ViewModel.vmOrderBook)this.DataContext);
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            HelperCommon.CreateCommonPopUpWindow(chtSpread, (Button)sender, newViewModel);
        }

        private void butPopSymbol_Click(object sender, RoutedEventArgs e)
        {
            //var newViewModel = new ViewModel.vmOrderBook((ViewModel.vmOrderBook)this.DataContext);
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            HelperCommon.CreateCommonPopUpWindow(grdSymbol, (Button)sender, newViewModel, "Symbol", 450, 600);
        }

        private void butDepthView1_Click(object sender, RoutedEventArgs e)
        {
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            newViewModel.SwitchView = 0;
        }

        private void butDepthView2_Click(object sender, RoutedEventArgs e)
        {
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            newViewModel.SwitchView = 1;
        }

        private void butDepthView3_Click(object sender, RoutedEventArgs e)
        {
            var newViewModel = (ViewModel.vmOrderBook)this.DataContext;
            newViewModel.SwitchView = 2;
        }


    }
}
