using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CortexFX.Controls
{
    public partial class ToolCard : UserControl
    {
        public ToolCard()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(ToolCard), new PropertyMetadata(string.Empty));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register("Description", typeof(string), typeof(ToolCard), new PropertyMetadata(string.Empty));

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public static readonly DependencyProperty IconDataProperty =
            DependencyProperty.Register("IconData", typeof(Geometry), typeof(ToolCard), new PropertyMetadata(null));

        public Geometry IconData
        {
            get { return (Geometry)GetValue(IconDataProperty); }
            set { SetValue(IconDataProperty, value); }
        }

        public static readonly DependencyProperty IconBackgroundProperty =
            DependencyProperty.Register("IconBackground", typeof(Brush), typeof(ToolCard), new PropertyMetadata(Brushes.Gray));

        public Brush IconBackground
        {
            get { return (Brush)GetValue(IconBackgroundProperty); }
            set { SetValue(IconBackgroundProperty, value); }
        }
    }
}