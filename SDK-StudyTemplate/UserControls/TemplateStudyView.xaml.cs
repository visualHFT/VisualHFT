using System;
using System.Windows;
using System.Windows.Media;
using VisualHFT.Studies.Template.ViewModel;

namespace VisualHFT.Studies.Template.UserControls
{
    /// <summary>
    /// Optional custom UI component for visualizing study results.
    /// This demonstrates how to create a custom view for your study.
    /// Remove if not needed for your study implementation.
    /// </summary>
    public partial class TemplateStudyView : System.Windows.Controls.UserControl
    {
        private TemplateStudyViewModel _viewModel;

        public TemplateStudyView()
        {
            InitializeComponent();
            
            // Initialize view model
            _viewModel = new TemplateStudyViewModel();
            this.DataContext = _viewModel;
        }

        /// <summary>
        /// Update the view with new study data
        /// </summary>
        /// <param name="value">Calculated study value</param>
        /// <param name="timestamp">Timestamp of the calculation</param>
        public void UpdateValue(double value, DateTime timestamp)
        {
            if (_viewModel != null)
            {
                _viewModel.UpdateValue(value, timestamp);
            }
        }

        /// <summary>
        /// Set the symbol being tracked
        /// </summary>
        /// <param name="symbol">Symbol name</param>
        public void SetSymbol(string symbol)
        {
            if (_viewModel != null)
            {
                _viewModel.CurrentSymbol = symbol;
            }
        }

        /// <summary>
        /// Clear all data
        /// </summary>
        public void Clear()
        {
            if (_viewModel != null)
            {
                _viewModel.Clear();
            }
        }
    }
}
