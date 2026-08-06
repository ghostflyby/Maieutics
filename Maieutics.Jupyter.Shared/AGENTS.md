# Maieutics.Jupyter.Shared instructions

Use `.agents/skills/maieutics-jupyter-protocol/SKILL.md` for cross-project protocol rules and
`.agents/skills/maieutics-dotnet-testing/SKILL.md` when changing coverage.

## Ownership

This project is the transport-independent Jupyter wire-protocol foundation. It owns classic connection-file models,
channels, headers and parent headers, wire envelopes and routing identities, multipart encoding, HMAC signing, protocol
DTOs, MIME bundles, binary buffers, JSON contracts, message IDs, cursor conversion, display IDs, protocol exceptions,
and compatibility helpers.

All Jupyter wire-format types belong here. Routing identities stay in `JupyterWireMessage`, not semantic content.

## Forbidden dependencies

- Do not reference NetMQ or create sockets.
- Do not orchestrate client requests or kernel dispatch.
- Do not launch processes.
- Do not introduce Agent, provider, tool, persistence, or notebook UI-state concepts.
- Do not add a reference to Client or Kernel.

## Protocol constraints

- Target classic Jupyter 5.5 while tolerating unknown optional fields and compatible older peer announcements.
- Use source-generated `System.Text.Json` metadata for protocol DTOs; avoid reflection serialization in hot paths.
- Prefer immutable records with explicit wire names. Preserve unknown fields where forward compatibility requires it.
- Keep binary buffers outside JSON and preserve their order.
- Validate delimiters, frame counts, required fields, signature schemes, and HMAC at the wire boundary.
- Empty keys may represent unsigned classic connections; unsupported signatures and CurveZMQ fail explicitly.
- Cursor offsets are Unicode code-point offsets. Reject out-of-range values and UTF-16 surrogate splits.
- Preserve full MIME metadata and transient dictionaries. `update_display_data` requires a valid display ID.
- Public protocol APIs require XML documentation and representative round-trip or malformed-input tests.
