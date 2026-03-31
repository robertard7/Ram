namespace RAM.Services;

public enum AgentRejectionReason
{
    None,
    TransportFailure,
    Timeout,
    EmptyResponse,
    NonJsonResponse,
    SchemaMismatch,
    EnumViolation,
    FieldMissing,
    FieldOverflow,
    ForbiddenContent,
    RuntimeException,
    UnknownProperty
}
