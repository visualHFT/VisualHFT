using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml;

namespace VisualHFT.UserControls
{
    /// <summary>
    /// Interaction logic for MetricTile.xaml
    /// </summary>
    public partial class MetricTile : UserControl
    {
        public MetricTile()
        {
            InitializeComponent();

            // Subscribe to DataContext changes to update tooltip
            DataContextChanged += MetricTile_DataContextChanged;
        }

        private void MetricTile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is VisualHFT.ViewModel.vmTile tile)
            {
                // Subscribe to property changes
                tile.PropertyChanged += Tile_PropertyChanged;

                // Update tooltip immediately with current value
                UpdateTooltip(tile.Tooltip);
            }

            if (e.OldValue is VisualHFT.ViewModel.vmTile oldTile)
            {
                // Unsubscribe from old tile
                oldTile.PropertyChanged -= Tile_PropertyChanged;
            }
        }

        private void Tile_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VisualHFT.ViewModel.vmTile.Tooltip))
            {
                var tile = sender as VisualHFT.ViewModel.vmTile;
                UpdateTooltip(tile?.Tooltip);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Update tooltip when control loads
            if (DataContext is VisualHFT.ViewModel.vmTile tile)
            {
                UpdateTooltip(tile.Tooltip);
            }
        }

        private void UpdateTooltip(string htmlText)
        {
            if (string.IsNullOrEmpty(htmlText))
            {
                txtToolTip.Inlines.Clear();
                return;
            }

            ConvertHtmlToTextBlock(htmlText, txtToolTip);
        }

        public TextBlock ConvertHtmlToTextBlock(string htmlText, TextBlock textBlock)
        {
            htmlText = XmlEscapeAmpersands(htmlText);

            textBlock.Inlines.Clear(); // Clear existing inlines
            textBlock.TextWrapping = System.Windows.TextWrapping.Wrap;
            textBlock.Width = 500;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml("<root>" + htmlText + "</root>");

                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType == XmlNodeType.Text)
                    {
                        textBlock.Inlines.Add(new Run(node.Value));
                    }
                    else if (node.Name == "br")
                    {
                        textBlock.Inlines.Add(new LineBreak());
                    }
                    else if (node.Name == "b")
                    {
                        Run run = new Run(node.InnerText);
                        run.FontWeight = System.Windows.FontWeights.Bold;
                        textBlock.Inlines.Add(run);
                    }
                    else if (node.Name == "i")
                    {
                        Run run = new Run(node.InnerText);
                        run.FontStyle = System.Windows.FontStyles.Italic;
                        textBlock.Inlines.Add(run);
                    }
                    // Add more formatting cases as needed
                }
            }
            catch (XmlException)
            {
                // If HTML parsing fails, show raw text
                textBlock.Inlines.Add(new Run(htmlText));
            }

            return textBlock;
        }

        private static string XmlEscapeAmpersands(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Replace & not starting a valid entity with &amp;
            return Regex.Replace(s, "&(?!(?:#\\d+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);)", "&amp;");
        }
    }
}
