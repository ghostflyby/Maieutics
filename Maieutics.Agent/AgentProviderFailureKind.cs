namespace Maieutics.Agent;

/// <summary>Provider-neutral classification of a failed model request.</summary>
public enum AgentProviderFailureKind
{
    /// <summary>No recognized provider failure information is available.</summary>
    Unknown,
    /// <summary>The provider rejected the credentials.</summary>
    Authentication,
    /// <summary>The credentials lack access to the requested resource.</summary>
    PermissionDenied,
    /// <summary>The provider rejected the request parameters or content.</summary>
    InvalidRequest,
    /// <summary>The requested model or endpoint was not found.</summary>
    NotFound,
    /// <summary>The request exceeds the model's context window.</summary>
    ContextLimit,
    /// <summary>The provider is limiting request or token throughput.</summary>
    RateLimited,
    /// <summary>The provider account has exhausted its quota or billing allowance.</summary>
    QuotaExceeded,
    /// <summary>The provider request timed out independently of caller cancellation.</summary>
    Timeout,
    /// <summary>The provider connection could not be established or was interrupted.</summary>
    Connection,
    /// <summary>The provider is unavailable or overloaded.</summary>
    Unavailable,
    /// <summary>The provider returned an invalid or incomplete response.</summary>
    InvalidResponse,
}
