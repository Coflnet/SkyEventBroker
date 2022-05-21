using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.EventBroker.Models
{
    /// <summary>
    /// <see cref="DbContext"/> For flip tracking
    /// </summary>
    public class EventDbContext : DbContext
    {
        public DbSet<MessageContainer> Messages { get; set; }
        public DbSet<ReceiveConfirm> Confirms { get; set; }
        public DbSet<NotificationTarget> NotificationTargets { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="EventDbContext"/>
        /// </summary>
        /// <param name="options"></param>
        public EventDbContext(DbContextOptions<EventDbContext> options)
        : base(options)
        {
        }

        /// <summary>
        /// Configures additional relations and indexes
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MessageContainer>(entity =>
            {
                entity.HasIndex(e => e.Timestamp);
            });
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(e => e.UserId);
            });
        }
    }
}