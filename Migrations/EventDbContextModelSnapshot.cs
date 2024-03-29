﻿// <auto-generated />
using System;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SkyBase.Migrations
{
    [DbContext(typeof(EventDbContext))]
    partial class EventDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.0")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.MessageContainer", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("ImageLink")
                        .HasColumnType("text");

                    b.Property<string>("Link")
                        .HasColumnType("text");

                    b.Property<string>("Message")
                        .HasColumnType("text");

                    b.Property<string>("Reference")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<int?>("SetingsId")
                        .HasColumnType("integer");

                    b.Property<string>("SourceSubId")
                        .HasColumnType("text");

                    b.Property<string>("SourceType")
                        .HasColumnType("text");

                    b.Property<string>("Summary")
                        .HasColumnType("text");

                    b.Property<DateTime?>("Timestamp")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("UserId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("SetingsId");

                    b.HasIndex("Timestamp");

                    b.HasIndex("UserId");

                    b.ToTable("Messages");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.MessageSchedule", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int?>("MessageId")
                        .HasColumnType("integer");

                    b.Property<DateTime>("ScheduledTime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UserId")
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("MessageId");

                    b.HasIndex("ScheduledTime");

                    b.HasIndex("UserId");

                    b.ToTable("ScheduledMessages");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.NotificationTarget", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Name")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<string>("Target")
                        .HasColumnType("text");

                    b.Property<int>("Type")
                        .HasColumnType("integer");

                    b.Property<int>("UseCount")
                        .HasColumnType("integer");

                    b.Property<string>("UserId")
                        .HasMaxLength(36)
                        .HasColumnType("character varying(36)");

                    b.Property<int>("When")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.ToTable("NotificationTargets");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.ReceiveConfirm", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Reference")
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.ToTable("Confirms");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.Settings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<bool>("ConfirmDelivery")
                        .HasColumnType("boolean");

                    b.Property<bool>("PlaySound")
                        .HasColumnType("boolean");

                    b.Property<bool>("StoreIfOffline")
                        .HasColumnType("boolean");

                    b.HasKey("Id");

                    b.ToTable("Settings");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.Subscription", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("SourceSubId")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<string>("SourceSubIdRegex")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<string>("SourceType")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)");

                    b.Property<string>("UserId")
                        .HasMaxLength(36)
                        .HasColumnType("character varying(36)");

                    b.HasKey("Id");

                    b.HasIndex("UserId", "SourceType");

                    b.ToTable("Subscriptions");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.TargetConnection", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<bool>("IsDisabled")
                        .HasColumnType("boolean");

                    b.Property<int>("Priority")
                        .HasColumnType("integer");

                    b.Property<int?>("SubscriptionId")
                        .HasColumnType("integer");

                    b.Property<int?>("TargetId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("SubscriptionId");

                    b.HasIndex("TargetId");

                    b.ToTable("TargetConnection");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.User", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Locale")
                        .HasMaxLength(8)
                        .HasColumnType("character varying(8)");

                    b.Property<string>("UserId")
                        .HasMaxLength(36)
                        .HasColumnType("character varying(36)");

                    b.Property<string>("UserName")
                        .HasMaxLength(40)
                        .HasColumnType("character varying(40)");

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

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.MessageSchedule", b =>
                {
                    b.HasOne("Coflnet.Sky.EventBroker.Models.MessageContainer", "Message")
                        .WithMany()
                        .HasForeignKey("MessageId");

                    b.Navigation("Message");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.TargetConnection", b =>
                {
                    b.HasOne("Coflnet.Sky.EventBroker.Models.Subscription", "Subscription")
                        .WithMany("Targets")
                        .HasForeignKey("SubscriptionId");

                    b.HasOne("Coflnet.Sky.EventBroker.Models.NotificationTarget", "Target")
                        .WithMany()
                        .HasForeignKey("TargetId");

                    b.Navigation("Subscription");

                    b.Navigation("Target");
                });

            modelBuilder.Entity("Coflnet.Sky.EventBroker.Models.Subscription", b =>
                {
                    b.Navigation("Targets");
                });
#pragma warning restore 612, 618
        }
    }
}
