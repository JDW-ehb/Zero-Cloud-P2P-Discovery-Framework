using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;

namespace ZCM.ViewModels;

public class MessagingViewModel
{
    private readonly ZcspPeer _peer;
    private readonly MessagingService _messaging;

    public string TargetIp { get; set; } = "192.168.1.22";

    public ICommand HostCommand { get; }
    public ICommand ConnectCommand { get; }
    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public string OutgoingMessage { get; set; } = "";
    public ICommand SendMessageCommand { get; }




    public MessagingViewModel(ZcspPeer peer, MessagingService messaging)
    {
        _peer = peer;
        _messaging = messaging;

        HostCommand = new Command(() =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await _peer.StartHostingAsync(
                        5555,
                        name => name == _messaging.ServiceName ? _messaging : null
                    );
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HOST ERROR] {ex}"
                    );
                }
            });
        });


        ConnectCommand = new Command(async () =>
        {
            await _messaging.ConnectToPeerAsync(TargetIp, 5555);
        });

        SendMessageCommand = new Command(async () =>
        {
            if (!string.IsNullOrWhiteSpace(OutgoingMessage))
            {
                await _messaging.SendMessageAsync(OutgoingMessage);
                OutgoingMessage = "";
            }
        });

        _messaging.MessageReceived += msg =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Add(msg);
            });
        };

    }
}

