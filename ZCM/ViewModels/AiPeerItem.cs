using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZCL.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;


namespace ZCM.ViewModels;

public sealed class AiPeerItem
{
    public PeerNode Peer { get; set; } = null!;
    public string? Model { get; set; }

    public string Display => Peer.HostName;

    public string ModelDisplay =>
        Model ?? "unknown model";

}
public sealed class AiConversationItem : INotifyPropertyChanged
{
    private string? _summary;

    public Guid Id { get; set; }
    public Guid PeerId { get; set; }
    public string PeerName { get; set; } = "";
    public string Model { get; set; } = "";

    public string? Summary
    {
        get => _summary;
        set
        {
            if (_summary == value)
                return;

            _summary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Summary)
            ? Summary
            : Model;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}