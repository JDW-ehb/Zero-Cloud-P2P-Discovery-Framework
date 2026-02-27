using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ZCL.Models;

[Index(nameof(ServiceName), IsUnique = true)]
public sealed class AnnouncedServiceSettingEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string ServiceName { get; set; } = ""; 

    public bool IsEnabled { get; set; } = true;
}