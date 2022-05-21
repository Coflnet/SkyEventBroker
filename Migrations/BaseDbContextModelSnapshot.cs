﻿// <auto-generated />
using System;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace SkyBase.Migrations
{
    [DbContext(typeof(EventDbContext))]
    partial class BaseDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.0")
                .HasAnnotation("Relational:MaxIdentifierLength", 64);

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.MessageContainer", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Message")
                        .HasColumnType("longtext");

                    b.Property<string>("Reference")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<int?>("SetingsId")
                        .HasColumnType("int");

                    b.Property<string>("SourceType")
                        .HasColumnType("longtext");

                    b.Property<DateTime>("Timestamp")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("datetime(6)");

                    b.Property<int?>("UserId")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.HasIndex("SetingsId");

                    b.HasIndex("Timestamp");

                    b.HasIndex("UserId");

                    b.ToTable("Messages");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.NotificationTarget", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Name")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<string>("Target")
                        .HasColumnType("longtext");

                    b.Property<int>("Type")
                        .HasColumnType("int");

                    b.Property<string>("UserId")
                        .HasMaxLength(36)
                        .HasColumnType("varchar(36)");

                    b.Property<int>("When")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.ToTable("NotificationTargets");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.ReceiveConfirm", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Reference")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<DateTime>("Timestamp")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("datetime(6)");

                    b.HasKey("Id");

                    b.ToTable("Confirms");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.Settings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<bool>("ConfirmDelivery")
                        .HasColumnType("tinyint(1)");

                    b.Property<bool>("PlaySound")
                        .HasColumnType("tinyint(1)");

                    b.Property<bool>("StoreIfOffline")
                        .HasColumnType("tinyint(1)");

                    b.HasKey("Id");

                    b.ToTable("Settings");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.User", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Locale")
                        .HasMaxLength(8)
                        .HasColumnType("varchar(8)");

                    b.Property<string>("UserId")
                        .HasMaxLength(36)
                        .HasColumnType("varchar(36)");

                    b.Property<string>("UserName")
                        .HasMaxLength(40)
                        .HasColumnType("varchar(40)");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.ToTable("User");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.MessageContainer", b =>
                {
                    b.HasOne("Coflnet.Sky.EventBroker.Models.Settings", "Setings")
                        .WithMany()
                        .HasForeignKey("SetingsId");

                    b.HasOne("Coflnet.Sky.EventBroker.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId");

                    b.Navigation("Setings");

                    b.Navigation("User");
                });
#pragma warning restore 612, 618
        }
    }
}
