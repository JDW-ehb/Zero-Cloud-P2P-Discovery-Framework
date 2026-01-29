using System.Collections.ObjectModel;

using ZCL.Models;
using ZCL.API;

namespace ZCM;

public partial class DiscoveryPage : ContentPage
{
    public static string ToTimeAgo(DateTime utcTime)
    {
        var diff = DateTime.UtcNow - utcTime;

        if (diff.TotalSeconds < 60)
            return $"{Math.Max(1, (int)diff.TotalSeconds)}s ago";

        if (diff.TotalMinutes < 60)
            return $"{(int)Math.Round(diff.TotalMinutes)}m ago";

        if (diff.TotalHours < 24)
            return $"{(int)Math.Round(diff.TotalHours)}h ago";

        if (diff.TotalDays < 7)
            return $"{(int)Math.Round(diff.TotalDays)}d ago";

        if (diff.TotalDays < 30)
            return $"{(int)Math.Round(diff.TotalDays / 7)}w ago";

        if (diff.TotalDays < 365)
            return $"{(int)Math.Round(diff.TotalDays / 30)}mo ago";

        return $"{(int)Math.Round(diff.TotalDays / 365)}y ago";
    }

    public ObservableCollection<Peer> Peers { get; set; } 

    public DiscoveryPage()
	{
		InitializeComponent();

        Peers = ServiceHelper.GetService<DataStore>().Peers;

        BindingContext = this;

        foreach (var peer in Peers)
        {
            int diff = (int)(DateTime.UtcNow - peer.LastSeen).TotalSeconds;
            peer.LastSeenSeconds = ToTimeAgo(peer.LastSeen);
        }
      
    }

    private void BackButton_Clicked(object sender, EventArgs e)
    {
        Navigation.PopModalAsync();
    }
}