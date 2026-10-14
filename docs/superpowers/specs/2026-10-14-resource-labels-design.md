# Resource labels

Add optional `IResource<T>.LabelFor(T)` and `Label<T>` registration on the resolver
and resource context. Resolve a nonblank label from the registered selector, then
the resource interface, then the existing `DisplayName:key` fallback. Key
resolution and resource equality keep their existing behavior.

Reuse `StepMessageBuilder` for titles, notes, and logs. Capture the resolved label
in `StepMessagePart` alongside its existing identity and raw JSON snapshot.
Unformatted references display the label; `:id` and `:data` remain explicit
formats. Capture labels for lifecycle messages and effects as well. The report
uses the recorded text and its existing resource links and snapshot inspector.
Later mutations cannot rewrite earlier references.

Use `UnsafeRelaxedJsonEscaping` for raw snapshots and report JSON so Unicode and
punctuation remain readable. Escape `<` at the HTML script boundary, including
when coverage enriches an existing report.

Rejected alternatives: automatic ToString labels can dump every record field;
attributes cannot configure external DTOs; adding labels to identity would change
resource equality; a second message/rendering system would duplicate the current
structured message and snapshot APIs.

Verify selector precedence, blank/default labels, external interface registrations,
unchanged identities, all formats, mutation after capture, generated titles and
notes, lifecycle effects, readable Unicode, and safe HTML embedding. Keep README
feature introductions short and use examples from the current instance step APIs.
