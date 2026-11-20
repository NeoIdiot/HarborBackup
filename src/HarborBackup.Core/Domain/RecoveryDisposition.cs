namespace HarborBackup.Core.Domain;

// A failure describes what happened; this disposition describes what may happen next.
public enum RecoveryDisposition { None, AutomaticRetry, AwaitingConfirmation, RepairRequired, Stopped }
