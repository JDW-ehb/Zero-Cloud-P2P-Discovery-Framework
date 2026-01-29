using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;
using System.Diagnostics;
using System.Net;
using ZCL.API;
using ZCL.Models;

namespace ZCM
{
    public partial class MainPage : ContentPage
    {
        int count = 0;

        public MainPage()
        {
            InitializeComponent();
        }

        private void DiscoveryPageButton_Clicked(object sender, EventArgs e)
        {
            Navigation.PushModalAsync(new DiscoveryPage());
        }
    }
}
