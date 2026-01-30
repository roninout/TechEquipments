using Microsoft.EntityFrameworkCore;

namespace TechEquipments
{
    public class PgDbContext : DbContext
    {
        public PgDbContext(DbContextOptions<PgDbContext> options) : base(options) { }

        public DbSet<OperatorAct> OperatorActs => Set<OperatorAct>();
        public DbSet<alarm_history> AlarmHistories => Set<alarm_history>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("public");

            modelBuilder.Entity<OperatorAct>(e =>
            {
                e.ToTable("OperatorAct");                 // как у тебя было:contentReference[oaicite:1]{index=1}
                e.HasKey(x => new { x.Date, x.Hash });    // составной ключ:contentReference[oaicite:2]{index=2}

                // ВАЖНО: если в БД колонки в lowercase (чаще всего так в PostgreSQL),
                // лучше явно указать имена колонок:
                //e.Property(x => x.Date).HasColumnName("date");
                //e.Property(x => x.Type).HasColumnName("type");
                //e.Property(x => x.Client).HasColumnName("client");
                //e.Property(x => x.User).HasColumnName("user");
                //e.Property(x => x.Tag).HasColumnName("tag");
                //e.Property(x => x.Hash).HasColumnName("hash");
                //e.Property(x => x.Equip).HasColumnName("equip");
                //e.Property(x => x.Desc).HasColumnName("desc");
                //e.Property(x => x.OldV).HasColumnName("oldv");
                //e.Property(x => x.NewV).HasColumnName("newv");
            });

            modelBuilder.Entity<alarm_history>(e =>
            {
                e.ToTable("alarm_history");               // имя таблицы как в классе:contentReference[oaicite:3]{index=3}
                e.HasKey(x => x.id);

                // по аналогии можно явно задать имена колонок, если нужно:
                //e.Property(x => x.id).HasColumnName("id");
                //e.Property(x => x.desc_).HasColumnName("desc_");
                //e.Property(x => x.localtimedate).HasColumnName("localtimedate");
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
