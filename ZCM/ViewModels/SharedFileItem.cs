using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Services.FileSharing;

namespace ZCM.ViewModels;

public sealed class SharedFileItem
{
    public Guid FileId { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public long Size { get; init; }
    public DateTime SharedSince { get; init; }

    /// <summary>True when this file belongs to the local peer.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Owner peer name (populated in aggregate views).</summary>
    public string? OwnerHostName { get; init; }

    public string SizeText
    {
        get
        {
            double mb = Size / (1024.0 * 1024.0);
            return mb >= 1024
                ? $"{mb / 1024.0:0.##} GB"
                : $"{mb:0.##} MB";
        }
    }

    public string SharedSinceText => SharedSince.ToLocalTime().ToString("dd/MM HH:mm");
}
