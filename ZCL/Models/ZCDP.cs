using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Sqlite;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Reflection.Metadata;
using System.Text;

namespace ZCL.Models
{
    public class ServiceDBContext : DbContext
    {
        public DbSet<Service> Services => Set<Service>();

        public ServiceDBContext(DbContextOptions<ServiceDBContext> options)
            : base(options)
        {
        }
    }

    [Index(nameof(name), nameof(address), nameof(port), nameof(peerGuid), IsUnique = true)]
    public class Service
    {
        [Key]
        public int id { get; set; }

        public string name { get; set; }
        public string address { get; set; }
        public UInt16 port { get; set; }
        public Guid peerGuid { get; set; }
    }
}

