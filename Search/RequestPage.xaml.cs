using System;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;

namespace Gelatinarm.Search
{
    public sealed partial class RequestPage : BasePage
    {
        public RequestPage() : base(typeof(RequestPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(RequestViewModel);
        public new RequestViewModel ViewModel => (RequestViewModel)base.ViewModel;

        protected override Control InitialFocusControl => RequestButton;
    }
}
