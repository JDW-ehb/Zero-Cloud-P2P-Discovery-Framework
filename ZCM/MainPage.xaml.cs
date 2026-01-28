using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;
using System.Diagnostics;
using System.Net;
using ZCL.API;
using ZCL.Models;
using ZCM.Pages;

namespace ZCM
{
    public class MainViewModel
    {
        public List<string> Services { get; }

        public MainViewModel(ServiceDBContext db)
        {
            Services = db.Services.Select(s => s.name).ToList();
        }
    }

    public partial class MainPage : ContentPage
    {
        int count = 0;

        public MainPage()
        {
            InitializeComponent();

            var db = ServiceHelper.GetService<ServiceDBContext>();
            db.Database.EnsureCreated();

            BindingContext = new MainViewModel(db);

            int port = Config.port;
            var multicastAddress = IPAddress.Parse(Config.multicastAddressString);
            string dbPath = db.Database.GetDbConnection().DataSource;

            Task.Run(() =>
            {
                ZCDPPeer.StartAndRun(multicastAddress, port, dbPath);
            });

        }

        private void OnCounterClicked(object? sender, EventArgs e)
        {
            count++;

            if (count == 1)
                CounterBtn.Text = $"Clicked {count} time";
            else
                CounterBtn.Text = $"Clicked {count} times";

            SemanticScreenReader.Announce(CounterBtn.Text);
        }

        private async void OnOpenMessagingClicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new MessagingPage());
        }
    }
}
