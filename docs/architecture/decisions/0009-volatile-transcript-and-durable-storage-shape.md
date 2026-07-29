# ADR 0009: Volatile Transcript and Durable Storage Shape

Status: Accepted

Date: 2026-07-28

## Context

The live Kernel must retain provider-neutral conversation history across model and tool iterations, including private
provider replay data that must never appear in public events. The first production tools are bounded text readers, but
future notebook output and execution targets will produce images, files, and other binary artifacts.

Serializing the complete live transcript into one JSON value on every turn retains base64-expanded binary data and
makes later branching expensive if a durable store copies complete histories. Durable persistence also introduces
migration, encryption, garbage collection, crash consistency, and retention policy that are not required for the first
read-only tool milestone.

## Decision

The first milestone keeps the canonical transcript only in the Kernel process. The committed state is an immutable
object graph of complete turns. Serialization may be used to detach and validate a newly committed turn and to measure
the existing retained-history byte budget, but serialized full-history bytes are not the canonical store.

No transcript file, database, restart recovery, or fork operation is implemented in this milestone. Process exit loses
the session. User input and tool results remain bounded text or structured JSON. Inline binary model content is rejected
before commit. Private provider replay data may remain in volatile memory when required for provider-neutral replay; it
is never exposed through public transcript values or written to durable storage.

Future durable persistence must use two cooperating stores:

- a transactional metadata store, initially SQLite, containing versioned sessions, immutable turns, parent links,
  session heads, normalized content metadata, and artifact references;
- a content-addressed blob store containing raw binary bytes keyed by SHA-256, with media type, size, integrity,
  provenance, retention, and access metadata.

Transcript payloads never inline binary bodies as base64. Provider opaque replay bytes that are eligible for persistence
use the same encrypted blob boundary or are omitted according to an explicit resume policy. A fork creates a new session
head that references an existing immutable turn; it does not copy turns or blobs. Garbage collection marks reachable
turns and blobs from live or pinned session heads before sweeping unreferenced content.

Blob publication must complete through a temporary write, integrity verification, and atomic rename before the
metadata transaction exposes its reference. A committed turn and session-head update occur in one metadata
transaction. Provider SDK objects and framework types do not enter either persisted format.

## Consequences

- The first tool milestone has no migration, recovery, or on-disk secret-retention surface.
- Live Responses replay data remains available without defining it as a public or persisted contract.
- Large and binary values have one future storage identity and are not duplicated through transcript serialization.
- Durable fork cost is constant with respect to existing history size.
- SQLite schemas, blob encryption, compaction, retention policy, and recovery APIs remain implementation work for a
  later persistence milestone.

