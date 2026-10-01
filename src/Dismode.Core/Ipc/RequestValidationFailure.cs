namespace Dismode.Core.Ipc;

public enum RequestValidationFailure
{
    None = 0,
    MessageTooLarge = 1,
    UnsupportedProtocolVersion = 2,
    CallerSidMismatch = 3,
    TimestampOutsideAllowedWindow = 4,
    SessionMismatch = 5,
    ReplayDetected = 6,
    ReplayCapacityExhausted = 7,
}

