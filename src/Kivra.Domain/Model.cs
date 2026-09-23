namespace Kivra.Domain;

public enum ShelfLifeUnit { Minutes, Hours, Days, Weeks, Months }
public enum Classification { NotApplicable, Veg, NonVeg, Egg }
public enum LabelStatus { Active, Consumed, Discarded, Expired, Cancelled }
public enum PrintJobStatus { Queued, Sending, Printed, Failed, Cancelled }
public enum UserRole { Administrator, Supervisor, KitchenStaff }

public abstract class Entity { public Guid Id { get; set; } = Guid.NewGuid(); public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Category : Entity { public required string Name { get; set; } public bool Active { get; set; } = true; }
public sealed class StorageLocation : Entity { public required string Name { get; set; } public bool Active { get; set; } = true; }
public sealed class User : Entity { public required string DisplayName { get; set; } public required string PinHash { get; set; } public UserRole Role { get; set; } public bool Active { get; set; } = true; }
public sealed class Item : Entity {
 public required string Name { get; set; } public string? ShortName { get; set; } public Guid CategoryId { get; set; } public Category? Category { get; set; }
 public Classification Classification { get; set; } public required string LabelType { get; set; } = "Food"; public required string DateTerminology { get; set; } = "PREPARED";
 public int ShelfLifeValue { get; set; } public ShelfLifeUnit ShelfLifeUnit { get; set; } public Guid? DefaultStorageLocationId { get; set; } public StorageLocation? DefaultStorageLocation { get; set; }
 public string DefaultLabelTemplate { get; set; } = "standard-50x30"; public bool Active { get; set; } = true;
}
public sealed class Printer : Entity { public required string Name { get; set; } public required string Model { get; set; } public required string Driver { get; set; } = "Fake"; public string IpAddress { get; set; } = "127.0.0.1"; public int TcpPort { get; set; } = 9100; public string? UsbQueueName { get; set; } public int Dpi { get; set; } = 203; public decimal LabelWidthMm { get; set; } = 50; public decimal LabelHeightMm { get; set; } = 30; public string MediaSensingMode { get; set; } = "Gap"; public decimal GapMm { get; set; } = 2; public string PrintMethod { get; set; } = "DirectThermal"; public decimal PrintSpeedIps { get; set; } = 3; public int PrintDensity { get; set; } = 8; public bool Enabled { get; set; } = true; }
public sealed class Label : Entity {
 public required string LabelCode { get; set; } public Guid ItemId { get; set; } public required string ItemNameSnapshot { get; set; } public required string CategorySnapshot { get; set; } public Classification ClassificationSnapshot { get; set; }
 public required string DateTerminologySnapshot { get; set; } public DateTimeOffset OperationalDateTime { get; set; } public DateTimeOffset ExpiryDateTime { get; set; } public required string ShelfLifeRuleSnapshot { get; set; }
 public Guid? StorageLocationId { get; set; } public required string StorageLocationSnapshot { get; set; } public decimal? Quantity { get; set; } public string? Unit { get; set; } public Guid CreatedByUserId { get; set; } public LabelStatus CurrentStatus { get; set; } = LabelStatus.Active; public Guid PrinterId { get; set; } public DateTimeOffset? LastSuccessfulPrintAt { get; set; } public int SuccessfulPrintCount { get; set; }
 public List<PrintJob> PrintJobs { get; set; } = [];
}
public sealed class PrintJob : Entity { public Guid LabelId { get; set; } public Label? Label { get; set; } public Guid PrinterId { get; set; } public Guid RequestedByUserId { get; set; } public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public PrintJobStatus Status { get; set; } = PrintJobStatus.Queued; public string? FailureReason { get; set; } public int RetryCount { get; set; } public bool IsReprint { get; set; } public string? Payload { get; set; } public string? IdempotencyKey { get; set; } }
public sealed class AuditLog : Entity { public Guid? UserId { get; set; } public required string Action { get; set; } public required string EntityName { get; set; } public Guid EntityId { get; set; } public string? PreviousValues { get; set; } public string? NewValues { get; set; } public string? DeviceIp { get; set; } }
public sealed class Notification : Entity { public required string Type { get; set; } public required string Title { get; set; } public required string Message { get; set; } public Guid? LabelId { get; set; } public DateTimeOffset? ReadAt { get; set; } }
