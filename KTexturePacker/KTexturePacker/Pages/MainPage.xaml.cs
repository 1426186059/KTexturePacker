using KTexturePacker.Models;
using KTexturePacker.PageModels;

namespace KTexturePacker.Pages
{
    public partial class MainPage : ContentPage
    {
        public MainPage(MainPageModel model)
        {
            InitializeComponent();
            BindingContext = model;
        }
    }
}