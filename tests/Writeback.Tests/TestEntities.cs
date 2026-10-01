using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Writeback.Tests;

public class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }

    [NotMapped]
    public string DisplayName => Name + " <" + Email + ">";
}

[Table("orders", Schema = "sales")]
public class Order
{
    [Key]
    public int OrderNumber { get; set; }

    [Column("customer_id")]
    public long CustomerId { get; set; }

    public decimal Total { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
}

public class OrderLine
{
    [Key]
    public int OrderId { get; set; }

    [Key]
    public int LineNumber { get; set; }

    public string Product { get; set; } = "";

    [ConcurrencyCheck]
    public int Version { get; set; }
}

public class Document
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    [Editable(false, AllowInitialValue = true)]
    public string CreatedBy { get; set; } = "";

    [Editable(false)]
    public string? SyncedBy { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? Slug { get; set; }
}

public class AuditLog
{
    public string Message { get; set; } = "";

    public DateTime At { get; set; }
}

public class Widget
{
    public int WidgetId { get; set; }

    public string Name { get; set; } = "";
}

public class GuidKeyed
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

public enum Status
{
    Active,
    Archived,
}

public class WithEnum
{
    public int Id { get; set; }

    public Status Status { get; set; }

    public Status? PreviousStatus { get; set; }
}

public record ProductRecord(int Id, string Name);

public class SnakeCaseEntity
{
    public int Id { get; set; }

    public string FirstName { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

// --- invalid mappings -----------------------------------------------------------------

public class WithNavigation
{
    public int Id { get; set; }

    public List<OrderLine> Lines { get; set; } = new();

    public Customer? Customer { get; set; }
}

[Table("dbo.Legacy")]
public class DottedTable
{
    public int Id { get; set; }
}

public class GeneratedWithoutSetter
{
    public int Id { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string Computed { get; } = "";
}

public class StringVersion
{
    public int Id { get; set; }

    [ConcurrencyCheck]
    public string Version { get; set; } = "";
}

public class TwoTokens
{
    public int Id { get; set; }

    [Timestamp]
    public byte[]? A { get; set; }

    [ConcurrencyCheck]
    public int B { get; set; }
}

public class CompositeIdentity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int A { get; set; }

    [Key]
    public int B { get; set; }
}

public class DuplicateColumns
{
    public int Id { get; set; }

    [Column("name")]
    public string First { get; set; } = "";

    [Column("NAME")]
    public string Second { get; set; } = "";
}

public class Weird
{
    public int Id { get; set; }

    [Column("select")]
    public string Select { get; set; } = "";

    [Column("bad]name")]
    public string Bracket { get; set; } = "";

    [Column("bad\"name")]
    public string Quote { get; set; } = "";

    [Column("bad`name")]
    public string Tick { get; set; } = "";
}

[HasTriggers]
public class Triggered
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime ModifiedAt { get; set; }
}

public class OnlyGenerated
{
    public int Id { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}

public class BaseEntity
{
    public int Id { get; set; }

    public virtual string Name { get; set; } = "";
}

public class DerivedEntity : BaseEntity
{
    public new string Name { get; set; } = "";

    public int Extra { get; set; }
}
