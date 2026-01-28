using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Services.Messaging;

namespace ZCM.ViewModels;

public class MessagingViewModel : BindableObject
{
    private readonly MessagingService _messaging;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    // =====================
    // Connection inputs
    // =====================

    private string _remoteIp = "127.0.0.1";
    public string RemoteIp
    {
        get => _remoteIp;
        set { _remoteIp = value; OnPropertyChanged(); }
    }

    private int _remotePort = 5555;
    public int RemotePort
    {
        get => _remotePort;
        set { _remotePort = value; OnPropertyChanged(); }
    }

    private int _hostPort = 5555;
    public int HostPort
    {
        get => _hostPort;
        set { _hostPort = value; OnPropertyChanged(); }
    }

    // =====================
    // Messaging input
    // =====================

    private string _outgoingMessage;
    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set { _outgoingMessage = value; OnPropertyChanged(); }
    }

    // =====================
    // Commands
    // =====================

    public ICommand StartHostingCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand SendMessageCommand { get; }

    public MessagingViewModel(MessagingService messaging)
    {
        _messaging = messaging;

        _messaging.MessageReceived += msg =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
                Messages.Add(msg));
        };

        StartHostingCommand = new Command(async () =>
        {
            await _messaging.StartHostingAsync(HostPort);
        });

        ConnectCommand = new Command(async () =>
        {
            await _messaging.ConnectToPeerAsync(RemoteIp, RemotePort);
        });

        SendMessageCommand = new Command(async () =>
        {
            if (string.IsNullOrWhiteSpace(OutgoingMessage))
                return;

            await _messaging.SendMessageAsync(OutgoingMessage);
            OutgoingMessage = string.Empty;
        });
    }
}
