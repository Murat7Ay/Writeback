using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Writeback.IntegrationTests;

public enum CustomerStatus
{
    Active = 1,
    Suspended = 2,
}

[Table("dx_customer")]
public class Customer
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? NameUpper { get; set; }

    [NotMapped]
    public string DisplayName => $"{Name} <{Email}>";
}

[Table("dx_doc")]
public class VersionedDoc
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("dx_order_line")]
public class OrderLine
{
    [Key]
    public int OrderId { get; set; }

    [Key]
    public int LineNumber { get; set; }

    public string Product { get; set; } = "";

    public int Quantity { get; set; }
}

/// <summary>Mapped fluently (see <see cref="IntegrationSetup"/>): renamed columns, no attributes.</summary>
public class FluentProduct
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}

[Table("order")]
public class KeywordEntity
{
    public int Id { get; set; }

    [Column("select")]
    public string Select { get; set; } = "";

    [Column("from")]
    public string? From { get; set; }
}

[Table("dx_audit")]
public class AuditEntry
{
    public string Message { get; set; } = "";

    public int Level { get; set; }
}

[Table("dx_product_record")]
public record ProductRecord(long Id, string Name);

// ---------------------------------------------------------------- SQL Server only

[Table("dx_triggered")]
[HasTriggers]
public class Triggered
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime ModifiedAt { get; set; }
}

[Table("dx_triggered")]
public class TriggeredWithoutDeclaration
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime ModifiedAt { get; set; }
}

[Table("dx_rowversioned")]
public class RowVersioned
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
